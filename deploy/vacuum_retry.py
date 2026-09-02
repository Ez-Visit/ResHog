"""
ResHog data.db incremental_vacuum 重试脚本
将输出写入日志文件，便于提权运行后查看
"""
import sqlite3
import time
import sys

DB_PATH = r'C:\ProgramData\ResHog\data.db'
LOG_PATH = r'C:\ProgramData\ResHog\vacuum_retry.log'

def log(msg, f):
    print(msg)
    f.write(msg + '\n')
    f.flush()

with open(LOG_PATH, 'w', encoding='utf-8') as f:
    log(f"DB: {DB_PATH}", f)
    log(f"Start: {time.strftime('%Y-%m-%d %H:%M:%S')}", f)
    log("", f)

    conn = sqlite3.connect(DB_PATH, timeout=30)
    conn.execute('PRAGMA busy_timeout = 30000')
    conn.execute('PRAGMA synchronous = NORMAL')
    conn.execute('PRAGMA cache_size = -512000')
    conn.execute('PRAGMA mmap_size = 2147418112')
    cur = conn.cursor()

    # 清理前状态
    cur.execute('PRAGMA freelist_count')
    fl_before = cur.fetchone()[0]
    cur.execute('PRAGMA page_count')
    pc_before = cur.fetchone()[0]
    cur.execute('PRAGMA auto_vacuum')
    av = cur.fetchone()[0]
    log(f"Before vacuum:", f)
    log(f"  auto_vacuum = {av} (2=INCREMENTAL)", f)
    log(f"  freelist_count = {fl_before:,} pages ({fl_before*4096/1024/1024/1024:.2f} GB)", f)
    log(f"  page_count = {pc_before:,} pages ({pc_before*4096/1024/1024/1024:.2f} GB)", f)
    log("", f)

    # 执行 incremental_vacuum（不带参数 = 回收所有可回收页）
    log("Executing PRAGMA incremental_vacuum...", f)
    t0 = time.time()
    cur.execute('PRAGMA incremental_vacuum')
    conn.commit()
    log(f"  incremental_vacuum done in {time.time()-t0:.1f}s", f)
    log("", f)

    # 清理后状态
    cur.execute('PRAGMA freelist_count')
    fl_after = cur.fetchone()[0]
    cur.execute('PRAGMA page_count')
    pc_after = cur.fetchone()[0]
    log(f"After incremental_vacuum:", f)
    log(f"  freelist_count = {fl_after:,} pages ({fl_after*4096/1024/1024/1024:.2f} GB)", f)
    log(f"  page_count = {pc_after:,} pages ({pc_after*4096/1024/1024/1024:.2f} GB)", f)
    log(f"  回收页数 = {fl_before - fl_after:,} ({(fl_before-fl_after)*4096/1024/1024/1024:.2f} GB)", f)
    log("", f)

    # 如果 freelist 仍然很高，说明 incremental_vacuum 无法回收（需要全量 VACUUM）
    if fl_after > 100000:
        log(f"WARNING: freelist_count 仍很高 ({fl_after:,})", f)
        log("incremental_vacuum 无法回收这些页（空闲页散布在 B-tree 中间）", f)
        log("需要执行全量 VACUUM 才能彻底回收", f)
    else:
        log("OK: freelist_count 已清零，空间回收成功", f)

    # WAL checkpoint
    log("", f)
    log("Executing PRAGMA wal_checkpoint(TRUNCATE)...", f)
    cur.execute('PRAGMA wal_checkpoint(TRUNCATE)')
    ckpt = cur.fetchone()
    log(f"  checkpoint: busy={ckpt[0]}, log={ckpt[1]}, checkpointed={ckpt[2]}", f)

    conn.close()
    log("", f)
    log(f"End: {time.strftime('%Y-%m-%d %H:%M:%S')}", f)
