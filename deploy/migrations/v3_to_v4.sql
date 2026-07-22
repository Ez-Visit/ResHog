-- v3 -> v4: 删除 samples_hour 表
-- 原因：
--   1. UI 已删除 30d/90d 选项，samples_hour 无查询路径
--   2. 7d 查询改走 samples_minute（retention-policy-optimization.md 设计要求）
--   3. hour 表与 minute 表保留期相同（均为 7d），hour 表无存在价值
--   4. 删除后简化架构，减少 6 个文件的维护成本
-- 幂等：IF EXISTS 保证可重复执行

DROP INDEX IF EXISTS idx_hour_trend_covering;
DROP TABLE IF EXISTS samples_hour;

-- schema_version 记录由 migrate.ps1 写入（不在 SQL 中 INSERT）
