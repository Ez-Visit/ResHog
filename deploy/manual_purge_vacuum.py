"""
ResHog data.db 手动清理脚本（方案 A）
- DELETE samples 中超过 1 天的原始数据
- DELETE samples_minute 中超过 7 天的聚合数据
- DELETE alerts 中超过 7 天的告警数据
- PRAGMA incremental_vacuum 回收空闲页
"""
import sqlite3
import sys
import time
from datetime import datetime, timedelta

DB_PATH = r'C:\ProgramData\ResHog\data.db'

# cutoff 时间戳（与 SampleRepository.Format* 格式一致）
now = datetime.now()
raw_cutoff = now - timedelta(days=1)       # samples 保留 1 天
min_cutoff = now - timedelta(days=7)        # samples_minute 保留 7 天
alert_cutoff = now - timedelta(days=7)      # alerts 保留 7 天

raw_cutoff_str = raw_cutoff.strftime("%Y-%m-%dT%H:%M:%S.%f")
min_cutoff_str = min_cutoff.strftime("%Y-%m-%dT%H:%M:00")
alert_cutoff_str = alert_cutoff.strftime("%Y-%m-%dT%H:%M:%S.%f")

print(f"DB: {DB_PATH}")
print(f"now: {now.strftime('%Y-%m-%dT%H:%M:%S')}")
print(f"samples cutoff:   {raw_cutoff_str} (保留 1 天)")
print(f"minute cutoff:    {min_cutoff_str} (保留 7 天)")
print(f"alert cutoff:     {alert_cutoff_str} (保留 7 天)")
print()

# 连接（服务已停止，无锁冲突）
conn = sqlite3.connect(DB_PATH, timeout=30)
conn.execute('PRAGMA busy_timeout = 30000')
conn.execute('PRAGMA synchronous = NORMAL')
conn.execute('PRAGMA cache_size = -512000')  # 512MB cache 加速 DELETE
conn.execute('PRAGMA mmap_size = 2147418112')

cur = conn.cursor()

# 1. 清理前行数
print("=== 清理前 ===")
for table, col in [('samples', 'timestamp'), ('samples_minute', 'minute'), ('alerts', 'timestamp')]:
    cur.execute(f'SELECT COUNT(*) FROM {table}')
    print(f"  {table}: {cur.fetchone()[0]:,} rows")

# 2. 分块 DELETE samples（避免单事务过大锁库）
#    主键是 (timestamp, process_name, pid, instance_name)，timestamp 首列走聚簇索引
print()
print("=== 执行 DELETE ===")
t0 = time.time()

# samples: 分块删除（每块 50000 行，块间提交）
total_samples_deleted = 0
chunk_count = 0
while True:
    cur.execute("""
        DELETE FROM samples WHERE (timestamp, process_name, pid, instance_name) IN (
            SELECT timestamp, process_name, pid, instance_name
            FROM samples
            WHERE timestamp < ?
            LIMIT 50000
        )
    """, (raw_cutoff_str,))
    deleted = cur.rowcount
    conn.commit()
    if deleted == 0:
        break
    total_samples_deleted += deleted
    chunk_count += 1
    if chunk_count % 10 == 0:
        elapsed = time.time() - t0
        print(f"  samples: 已删除 {total_samples_deleted:,} 行 ({chunk_count} 块, {elapsed:.1f}s)")

print(f"  samples: 完成，共删除 {total_samples_deleted:,} 行 ({chunk_count} 块, {time.time()-t0:.1f}s)")

# samples_minute: 整批删除（数据量小）
t1 = time.time()
cur.execute("DELETE FROM samples_minute WHERE minute < ?", (min_cutoff_str,))
min_deleted = cur.rowcount
conn.commit()
print(f"  samples_minute: 删除 {min_deleted:,} 行 ({time.time()-t1:.1f}s)")

# alerts: 整批删除
t2 = time.time()
cur.execute("DELETE FROM alerts WHERE timestamp < ?", (alert_cutoff_str,))
alert_deleted = cur.rowcount
conn.commit()
print(f"  alerts: 删除 {alert_deleted:,} 行 ({time.time()-t2:.1f}s)")

# 3. 清理后行数
print()
print("=== 清理后 ===")
for table, col in [('samples', 'timestamp'), ('samples_minute', 'minute'), ('alerts', 'timestamp')]:
    cur.execute(f'SELECT COUNT(*) FROM {table}')
    print(f"  {table}: {cur.fetchone()[0]:,} rows")

# 4. incremental_vacuum 回收空闲页
#    检查 auto_vacuum 模式（2 = INCREMENTAL）
cur.execute('PRAGMA auto_vacuum')
av = cur.fetchone()[0]
print()
print(f"=== incremental_vacuum (auto_vacuum={av}) ===")
t3 = time.time()
# incremental_vacuum 回收所有空闲页
cur.execute('PRAGMA incremental_vacuum')
conn.commit()
print(f"  incremental_vacuum 完成 ({time.time()-t3:.1f}s)")

# 5. 检查 freelist_count（应接近 0）
cur.execute('PRAGMA freelist_count')
fl = cur.fetchone()[0]
print(f"  freelist_count: {fl:,} pages (应接近 0)")

# 6. 刷新 WAL 到主 db 文件
print()
print("=== WAL checkpoint (TRUNCATE) ===")
cur.execute('PRAGMA wal_checkpoint(TRUNCATE)')
ckpt_result = cur.fetchone()
print(f"  checkpoint: busy={ckpt_result[0]}, log_pages={ckpt_result[1]}, checkpointed={ckpt_result[2]}")

conn.close()
print()
print("=== 完成 ===")
print(f"总耗时: {time.time()-t0:.1f}s")
