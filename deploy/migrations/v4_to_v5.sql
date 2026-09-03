-- v4 -> v5: 清理 config 表中已废弃的 retention_hour_days 记录(2026-09-03)
--
-- v4 重构删除了 samples_hour 表和 HourAggregationDays 配置项,但老版本
-- SchemaSql 的 config INSERT 仍写入 retention_hour_days。本迁移清理老库中
-- 残留的该记录,避免误导维护者。
-- 幂等:DELETE 不存在的行无副作用。
--
-- 注意:本文件为文档参考——migrate.ps1 的迁移逻辑全部内联(v5 内联逻辑与此一致),
-- 此 SQL 文件不被执行引擎读取。

DELETE FROM config WHERE key = 'retention_hour_days';

-- schema_version 记录由 migrate.ps1 写入(不在 SQL 中 INSERT)
