-- v5 -> v6: 移除冗余 covering 索引 + 无引用告警索引(2026-09-03,P2-5 + P3-6)
--
-- 1) P2-5:idx_min_covering / idx_samples_ts_covering
--    samples 与 samples_minute 均为 WITHOUT ROWID,表本身就是按 PK(时间前缀)
--    聚簇的全量 B-tree,时间范围扫描天然"覆盖"、无回表;这两个与 PK 同序的
--    covering 索引不改变查询计划,却占 ~549MB(生产库实测:454MB + 95MB,
--    全库 ~26%)并对最热表造成近双倍写放大。
--    配套代码:TopNAnalyzer 已改 GROUP BY +process_name(走 PK 范围扫描,
--    替代 INDEXED BY);服务端 EnsureIndexes 不再创建这两个索引。
--
-- 2) P3-6:idx_alerts_name_metric_ts
--    cooldown 查询按 timestamp 范围过滤,走 idx_alerts_ts / idx_alerts_ts_severity;
--    本索引首列 process_name 无等值条件用不上,全项目无引用,仅余写放大。
--
-- ⚠ 回退方案(如需回滚到旧版二进制:旧代码 TopNAnalyzer 依赖 INDEXED BY,
--   缺索引会抛错;回滚旧版前必须先执行以下重建语句):
--   CREATE INDEX idx_min_covering ON samples_minute(minute, process_name,
--     service_name, avg_cpu, max_cpu, avg_mem_mb, avg_io_read_mb_s, avg_io_write_mb_s);
--   CREATE INDEX idx_samples_ts_covering ON samples(timestamp, process_name,
--     service_name, cpu_percent, working_set_mb, io_read_mb_s, io_write_mb_s);
--
-- 幂等:IF EXISTS 保证可重复执行。
--
-- 注意:本文件为文档参考——migrate.ps1 的迁移逻辑全部内联(v6 内联逻辑与此一致),
-- 此 SQL 文件不被执行引擎读取。

DROP INDEX IF EXISTS idx_alerts_name_metric_ts;
DROP INDEX IF EXISTS idx_min_covering;
DROP INDEX IF EXISTS idx_samples_ts_covering;

-- schema_version 记录由 migrate.ps1 写入(不在 SQL 中 INSERT)
