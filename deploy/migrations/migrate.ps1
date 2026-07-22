<#
.SYNOPSIS
    ResHog 数据库独立迁移脚本。

.DESCRIPTION
    一次性 schema 变更（DROP COLUMN / DROP INDEX 老库清理 / ALTER TABLE 等）
    不放服务启动代码路径，由本脚本在 install.ps1 部署阶段顺序执行。

    设计原则：
    - 服务启动代码只做 CREATE IF NOT EXISTS（幂等且开销极小）
    - 一次性 schema 变更走独立迁移脚本，由部署流程显式调用
    - 基于 schema_version 表追踪已应用的迁移版本
    - 幂等：已执行的版本跳过，部分执行的版本能继续

    v3 重构（缺陷 #9）：三张数据表改用 WITHOUT ROWID
    - 此迁移是"清库重建"：备份旧库 + 删除数据库文件
    - 服务启动时 SchemaSql 会以 v3 schema 创建新库
    - 历史数据丢失（用户已确认可接受）

    数据库连接：改用 Python sqlite3 模块（v3 重构后）
    - 原因：self-contained 发布的 ResHog.Service.exe 不提供 Microsoft.Data.Sqlite.dll
    - Python 3.x 内置 sqlite3 模块，无需额外安装
    - 跨平台兼容（未来若迁移到 Linux 也能用）

.PARAMETER DbPath
    SQLite 数据库文件路径（必填）

.PARAMETER MigrationDir
    迁移 SQL 文件目录，默认为脚本所在目录

.EXAMPLE
    .\migrate.ps1 -DbPath "C:\ProgramData\ResHog\data.db"
#>

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$DbPath,

    [string]$MigrationDir = (Split-Path -Parent $MyInvocation.MyCommand.Path)
)

$ErrorActionPreference = "Stop"

# ============================================================
# 日志输出函数（必须最先定义，后续步骤都要用）
# ============================================================
function Write-MigrateLog {
    param([string]$Message, [string]$Level = "INF")
    $color = switch ($Level) {
        "INF" { "White" }
        "WRN" { "Yellow" }
        "ERR" { "Red" }
        "OK"  { "Green" }
    }
    $timestamp = Get-Date -Format "yyyy-MM-dd HH:mm:ss.fff"
    Write-Host "[$timestamp] [$Level] [MIGRATE] $Message" -ForegroundColor $color
}

# ============================================================
# Python 可用性检查（v3 重构后必需，替代 Microsoft.Data.Sqlite.dll）
# ============================================================
try {
    $pyVersion = python --version 2>&1
    if ($LASTEXITCODE -ne 0) { throw "Python not found" }
    Write-MigrateLog "Python available: $pyVersion"
} catch {
    Write-MigrateLog "Python is required for migration but not found. Please install Python 3.x." "ERR"
    Write-MigrateLog "Download from: https://www.python.org/downloads/" "ERR"
    exit 3
}

if (-not (Test-Path $DbPath)) {
    Write-MigrateLog "Database file not found: $DbPath" "WRN"
    Write-MigrateLog "New install — service will create database with v3 schema on first startup." "OK"
    Write-MigrateLog "Note: schema_version table will be empty until next migrate.ps1 run initializes v3." "INF"
    exit 0
}

# ============================================================
# 通过 Python sqlite3 执行 SQL（替代 Microsoft.Data.Sqlite.dll）
#
# 重要：数据库可能因服务刚停止而仍有文件锁残留（SQLite checkpoint 未完成）
# Invoke-SqliteQuery 内置重试逻辑，最多重试 5 次，每次间隔递增（1s/2s/4s/8s/16s）
# ============================================================
function Invoke-SqliteQuery {
    param(
        [string]$Query,
        [switch]$Scalar
    )

    # 将 SQL 语句通过 stdin 传给 Python，避免命令行参数转义问题
    $pythonCode = @"
import sqlite3
import sys
import json
import time

db_path = r'$DbPath'

# 重试逻辑：服务刚停止时 SQLite 可能仍持有文件锁
# 每次重试间隔翻倍：1s, 2s, 4s, 8s, 16s（总最多 31s）
max_retries = 5
retry_delays = [1, 2, 4, 8, 16]

for attempt in range(max_retries + 1):
    try:
        conn = sqlite3.connect(db_path, timeout=15)
        conn.execute('PRAGMA busy_timeout = 15000')
        conn.execute('PRAGMA synchronous = NORMAL')
        conn.execute('PRAGMA cache_size = -512000')
        conn.execute('PRAGMA mmap_size = 2147418112')
        conn.execute('PRAGMA wal_autocheckpoint = 200')
        break
    except Exception as e:
        if attempt < max_retries:
            delay = retry_delays[attempt]
            print(f'RETRY:{attempt+1}/{max_retries} connect failed: {e}, waiting {delay}s', file=sys.stderr)
            time.sleep(delay)
        else:
            print(f'FATAL: failed to connect after {max_retries} retries: {e}', file=sys.stderr)
            sys.exit(2)

cur = conn.cursor()
sql = sys.stdin.read()
cur.execute(sql)

if ${Scalar}:
    row = cur.fetchone()
    print(row[0] if row and row[0] is not None else '')
else:
    cols = [desc[0] for desc in cur.description] if cur.description else []
    rows = cur.fetchall()
    # 输出为 JSON 行格式，PowerShell 层解析
    for row in rows:
        obj = dict(zip(cols, row))
        print(json.dumps(obj, default=str))

conn.close()
"@

    $result = $Query | python -c $pythonCode 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "Python sqlite3 query failed (exit $LASTEXITCODE): $result"
    }
    if ($Scalar) { return $result | Select-Object -First 1 }
    return $result | ForEach-Object {
        try { $_ | ConvertFrom-Json } catch { $_ }
    }
}

function Invoke-SqliteNonQuery {
    param([string]$Sql)
    Invoke-SqliteQuery -Query $Sql -Scalar | Out-Null
}

function Test-ColumnExists {
    param(
        [string]$TableName,
        [string]$ColumnName
    )
    $rows = Invoke-SqliteQuery -Query "PRAGMA table_info($TableName);"
    foreach ($row in $rows) {
        if ($row.name -ieq $ColumnName) {
            return $true
        }
    }
    return $false
}

function Test-IndexExists {
    param([string]$IndexName)
    $rows = Invoke-SqliteQuery -Query "PRAGMA index_list('samples');"
    foreach ($row in $rows) {
        if ($row.name -ieq $IndexName) {
            return $true
        }
    }
    return $false
}

function Get-CurrentSchemaVersion {
    try {
        $result = Invoke-SqliteQuery -Query "SELECT MAX(version) FROM schema_version;" -Scalar
        if ($result) {
            return [int]$result
        }
    } catch {
        # schema_version 表不存在（极老版本），返回 0
    }
    return 0
}

# ============================================================
# 主流程
# ============================================================

Write-MigrateLog "Migration script started"
Write-MigrateLog "Database: $DbPath"
Write-MigrateLog "Migration dir: $MigrationDir"

$currentVersion = Get-CurrentSchemaVersion
Write-MigrateLog "Current schema version: $currentVersion"

# ============================================================
# 特殊场景：数据库存在但 schema_version 表为空（新库）
# 服务首次启动 SchemaSql 创建了空 schema_version 表（v3 重构后不再插入版本记录）
# migrate.ps1 需要检测：若数据库是 v3 schema（有 WITHOUT ROWID）但 schema_version 为空，
# 直接写入 v3 记录即可，无需执行 v0->v1->v2->v3 迁移
# ============================================================
if ($currentVersion -eq 0) {
    # 检查 samples 表是否已经是 v3 schema（WITHOUT ROWID）
    $samplesSchema = Invoke-SqliteQuery -Query "SELECT sql FROM sqlite_master WHERE name='samples';"
    $isV3Schema = $false
    if ($samplesSchema) {
        $sqlText = $samplesSchema | ForEach-Object {
            if ($_ -is [string]) { $_ } else { $_.sql }
        } | Select-Object -First 1
        if ($sqlText -and $sqlText -match "WITHOUT ROWID") {
            $isV3Schema = $true
        }
    }

    if ($isV3Schema) {
        Write-MigrateLog "Database has v3 schema (WITHOUT ROWID) but schema_version is empty." "INF"
        Write-MigrateLog "Initializing schema_version to v3 (new database created by service)" "OK"
        $appliedAt = (Get-Date).ToString("yyyy-MM-ddTHH:mm:ss.fffffff")
        $insertSql = "INSERT OR IGNORE INTO schema_version (version, applied_at, description) VALUES (3, '$appliedAt', 'WITHOUT ROWID schema (initialized by migrate.ps1 for new database)');"
        Invoke-SqliteNonQuery -Sql $insertSql
        $currentVersion = 3
        Write-MigrateLog "Schema version initialized to v3" "OK"
    } else {
        Write-MigrateLog "Database has old schema (with AUTOINCREMENT id) and schema_version is empty." "WRN"
        Write-MigrateLog "Will apply full migration chain v0 -> v1 -> v2 -> v3" "INF"
    }
}

# 迁移脚本清单（按版本顺序）
# v2_to_v3 是"清库重建"特殊迁移：不执行 SQL，而是删除数据库文件让服务以新 schema 重建
$migrations = @(
    @{ From = 0; To = 1; File = "v0_to_v1.sql"; Description = "Drop legacy idx_samples_pid_ts index" },
    @{ From = 1; To = 2; File = "v1_to_v2.sql"; Description = "Drop unused p95_cpu / p95_mem_mb columns from samples_minute" },
    @{ From = 2; To = 3; File = "v2_to_v3.sql"; Description = "WITHOUT ROWID rebuild: backup old db, delete file, service recreates with v3 schema" },
    @{ From = 3; To = 4; File = "v3_to_v4.sql"; Description = "Drop samples_hour table (no query path, superseded by samples_minute for 7d range)" }
)

$appliedCount = 0

foreach ($mig in $migrations) {
    if ($currentVersion -ge $mig.To) {
        Write-MigrateLog "Version $($mig.To) already applied (current=$currentVersion), skipping" "INF"
        continue
    }

    $sqlFile = Join-Path $MigrationDir $mig.File
    if (-not (Test-Path $sqlFile) -and $mig.To -ne 3) {
        # v2_to_v3.sql 是文档参考，实际逻辑在 PowerShell 层，不需要文件存在
        Write-MigrateLog "Migration file not found: $sqlFile" "WRN"
        continue
    }

    Write-MigrateLog "Applying migration v$($mig.From) -> v$($mig.To): $($mig.Description)"

    $appliedAt = (Get-Date).ToString("yyyy-MM-ddTHH:mm:ss.fffffff")

    try {
        # ============================================================
        # v0 -> v1: DROP INDEX idx_samples_pid_ts（幂等）
        # ============================================================
        if ($mig.To -eq 1) {
            if (Test-IndexExists -IndexName "idx_samples_pid_ts") {
                Invoke-SqliteNonQuery -Sql "DROP INDEX idx_samples_pid_ts;"
                Write-MigrateLog "Dropped index idx_samples_pid_ts"
            } else {
                Write-MigrateLog "Index idx_samples_pid_ts not present (already dropped or fresh install)" "INF"
            }
        }

        # ============================================================
        # v1 -> v2: DROP COLUMN p95_cpu / p95_mem_mb（幂等）
        # ============================================================
        if ($mig.To -eq 2) {
            $colsToDrop = @("p95_cpu", "p95_mem_mb")
            foreach ($col in $colsToDrop) {
                if (Test-ColumnExists -TableName "samples_minute" -ColumnName $col) {
                    Invoke-SqliteNonQuery -Sql "ALTER TABLE samples_minute DROP COLUMN $col;"
                    Write-MigrateLog "Dropped column $col from samples_minute"
                } else {
                    Write-MigrateLog "Column $col not present on samples_minute (already dropped or fresh schema)" "INF"
                }
            }
        }

        # ============================================================
        # v2 -> v3: WITHOUT ROWID 重构（清库重建）
        # ============================================================
        # 特殊迁移：不执行 SQL，而是：
        # 1. 备份旧库到 data.db.v2backup（以防万一）
        # 2. 删除 data.db / data.db-wal / data.db-shm
        # 3. 服务启动时 SchemaSql 会以 v3 schema 创建新库
        # 4. schema_version 表由 SchemaSql 初始化为 v3
        #
        # 历史数据丢失（用户已确认可接受）
        # ============================================================
        if ($mig.To -eq 3) {
            Write-MigrateLog "v2_to_v3: WITHOUT ROWID rebuild — backing up and deleting old database" "WRN"

            # 1. 备份旧库（若备份已存在则跳过，避免覆盖）
            $backupPath = "$DbPath.v2backup"
            if (-not (Test-Path $backupPath)) {
                Copy-Item $DbPath $backupPath -Force
                Write-MigrateLog "Backed up old database to $backupPath"
            } else {
                Write-MigrateLog "Backup already exists at $backupPath, skipping backup" "WRN"
            }

            # 2. 删除数据库文件（含 WAL/SHM）
            #    服务已停止（由 install.ps1 调用本脚本前确保），无锁冲突
            Remove-Item $DbPath -Force -ErrorAction SilentlyContinue
            Remove-Item "$DbPath-wal" -Force -ErrorAction SilentlyContinue
            Remove-Item "$DbPath-shm" -Force -ErrorAction SilentlyContinue
            Write-MigrateLog "Deleted old database files (data.db, data.db-wal, data.db-shm)"

            # 3. 不需要写入 schema_version v3 记录
            #    服务启动时 SchemaSql 的 INSERT OR IGNORE 会初始化 v3 记录
            Write-MigrateLog "Service will recreate database with v3 schema (WITHOUT ROWID) on startup" "OK"
            Write-MigrateLog "Historical data has been cleared (user-approved)" "WRN"

            $appliedCount++
            # 跳过后续的 schema_version 记录写入（数据库已删除，无法写入）
            continue
        }

        # ============================================================
        # v3 -> v4: DROP TABLE samples_hour（幂等）
        # ============================================================
        # samples_hour 表已无查询路径（UI 删除 30d/90d，7d 改走 samples_minute）
        # 迁移脚本直接执行 v3_to_v4.sql 中的 DROP TABLE IF EXISTS 语句
        # ============================================================
        if ($mig.To -eq 4) {
            # Python sqlite3 的 execute() 只支持单条语句，必须逐条执行
            # v3_to_v4.sql 仅作为文档参考，实际逻辑在此内联执行
            Invoke-SqliteNonQuery -Sql "DROP INDEX IF EXISTS idx_hour_trend_covering;"
            Write-MigrateLog "Dropped index idx_hour_trend_covering (if existed)"
            Invoke-SqliteNonQuery -Sql "DROP TABLE IF EXISTS samples_hour;"
            Write-MigrateLog "Dropped table samples_hour (if existed)"
        }

        # 记录 schema_version（INSERT OR IGNORE 保证幂等）
        # 注意：v2_to_v3 迁移已 continue 跳过此步骤
        $insertSql = "INSERT OR IGNORE INTO schema_version (version, applied_at, description) VALUES ($($mig.To), '$appliedAt', '$($mig.Description)');"
        Invoke-SqliteNonQuery -Sql $insertSql

        $appliedCount++
        Write-MigrateLog "Migration v$($mig.From) -> v$($mig.To) completed" "OK"

    } catch {
        Write-MigrateLog "Migration v$($mig.From) -> v$($mig.To) FAILED: $_" "ERR"
        Write-MigrateLog "Database left in previous state (v$currentVersion). Manual intervention required." "ERR"
        exit 1
    }
}

# 最终版本检查
# 注意：v2_to_v3 后数据库已删除，Get-CurrentSchemaVersion 会返回 0
# 实际版本由服务启动后 SchemaSql 初始化为 v3
if (Test-Path $DbPath) {
    $finalVersion = Get-CurrentSchemaVersion
    Write-MigrateLog "Final schema version: $finalVersion"
} else {
    Write-MigrateLog "Database deleted (v2_to_v3 rebuild), service will initialize v3 on startup"
}
Write-MigrateLog "Applied: $appliedCount migration(s)"
Write-MigrateLog "Migration script completed" "OK"
exit 0
