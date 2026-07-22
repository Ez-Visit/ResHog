-- ============================================================
-- ResHog 数据库迁移：v0 → v1
-- 描述：删除遗留索引 idx_samples_pid_ts（缺陷 #10）
-- 背景：全项目搜索未发现 WHERE pid = ? 的查询用例，
--       GetProcessDetail 的 PID 查询走 idx_samples_name_ts。
--       删除此索引可减少 BulkInsert 写放大（5 棵 B-tree → 4 棵）。
--
-- 执行方：deploy/migrations/migrate.ps1
-- 执行时机：install.ps1 部署阶段（服务启动前）
--
-- 幂等性说明：
--   本 .sql 文件仅作为迁移文档参考。
--   实际执行由 migrate.ps1 中的 Test-IndexExists + DROP INDEX 完成，
--   避免依赖 SQLite 原生 IF EXISTS 语法限制（DROP INDEX IF EXISTS 实际是支持的，
--   但为统一幂等检查方式，所有迁移都由 PowerShell 层处理）。
-- ============================================================

DROP INDEX IF EXISTS idx_samples_pid_ts;

-- schema_version 记录由 migrate.ps1 写入，不在本文件中。
