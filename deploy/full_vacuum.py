"""
ResHog data.db 全量 VACUUM 脚本
- 执行 VACUUM 重建整个数据库
- 回收所有空闲页，重建索引（无碎片）
- 输出写入日志文件便于查看
"""
import sqlite3
import time
import os

DB_PATH = r'C:\ProgramData\ResHog\data.db'
LOG_PATH = r'C:\ProgramData\ResHog\full_vacuum.log'

def log(msg, f):
    print(msg)
    f.write(msg + '\n')
    f.flush()

with open(LOG_PATH, 'w', encoding='utf-8') as f:
    log(f"DB: {DB_PATH}", f)
    log(f"Start: {time.strftime('%Y-%m-%d %H:%M:%S')}", f)
    log("", f)

    # 清理前状态
    db_size_before = os.path.getsize(DB_PATH)
    conn = sqlite3.connect(DB_PATH, timeout=30)
    conn.execute('PRAGMA busy_timeout = 30000')
    conn.execute('PRAGMA synchronous = NORMAL')
    conn.execute('PRAGMA cache_size = -512000')
    cur = conn.cursor()

    cur.execute('PRAGMA freelist_count')
    fl_before = cur.fetchone()[0]
    cur.execute('PRAGMA page_count')
    pc_before = cur.fetchone()[0]
    cur.execute('SELECT COUNT(*) FROM samples')
    samples_before = cur.fetchone()[0]
    cur.execute('SELECT COUNT(*) FROM samples_minute')
    minute_before = cur.fetchone()[0]

    log(f"Before VACUUM:", f)
    log(f"  data.db size = {db_size_before/1024/1024/1024:.2f} GB ({db_size_before:,} bytes)", f)
    log(f"  freelist_count = {fl_before:,} pages ({fl_before*4096/1024/1024/1024:.2f} GB)", f)
    log(f"  page_count = {pc_before:,} pages ({pc_before*4096/1024/1024/1024:.2f} GB)", f)
    log(f"  samples = {samples_before:,} rows", f)
    log(f"  samples_minute = {minute_before:,} rows", f)
    log("", f)

    # 执行全量 VACUUM
    log("Executing VACUUM (全量重建数据库)...", f)
    log("  注: VACUUM 会创建临时数据库文件，需要约原文件大小的临时空间", f)
    t0 = time.time()
    cur.execute('VACUUM')
    conn.commit()
    elapsed = time.time() - t0
    log(f"  VACUUM done in {elapsed:.1f}s", f)
    log("", f)

    # 清理后状态
    db_size_after = os.path.getsize(DB_PATH)
    cur.execute('PRAGMA freelist_count')
    fl_after = cur.fetchone()[0]
    cur.execute('PRAGMA page_count')
    pc_after = cur.fetchone()[0]
    cur.execute('SELECT COUNT(*) FROM samples')
    samples_after = cur.fetchone()[0]
    cur.execute('SELECT COUNT(*) FROM samples_minute')
    minute_after = cur.fetchone()[0]

    log(f"After VACUUM:", f)
    log(f"  data.db size = {db_size_after/1024/1024/1024:.2f} GB ({db_size_after:,} bytes)", f)
    log(f"  freelist_count = {fl_after:,} pages", f)
    log(f"  page_count = {pc_after:,} pages ({pc_after*4096/1024/1024/1024:.2f} GB)", f)
    log(f"  samples = {samples_after:,} rows", f)
    log(f"  samples_minute = {minute_after:,} rows", f)
    log("", f)

    # 效果统计
    size_reduction = db_size_before - db_size_after
    size_reduction_pct = (size_reduction / db_size_before) * 100
    log(f"=== 效果 ===", f)
    log(f"  文件大小: {db_size_before/1024/1024/1024:.2f} GB -> {db_size_after/1024/1024/1024:.2f} GB", f)
    log(f"  减少: {size_reduction/1024/1024/1024:.2f} GB ({size_reduction_pct:.1f}%)", f)
    log(f"  数据完整性: samples {samples_before:,} -> {samples_after:,} (应一致)", f)
    log(f"  数据完整性: samples_minute {minute_before:,} -> {minute_after:,} (应一致)", f)

    conn.close()
    log("", f)
    log(f"End: {time.strftime('%Y-%m-%d %H:%M:%S')}", f)
    log(f"Total time: {elapsed:.1f}s", f)
