using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using ResHog.Collectors;
using ResHog.Services;
using ResHog.Shared.Dtos;
using ResHog.Storage;

namespace ResHog.Advisory;

/// <summary>
/// 离线健康诊断规则引擎(health-advisory,2026-09-06)。
///
/// 纯本地:仅基于 samples_minute(7 天分钟聚合)与 samples(raw 1 天)评估,不联网;
/// 只产出建议(findings 表),不执行任何 kill/服务动作。
/// 由 ResHogWorker 在每分钟聚合完成后调用 Evaluate();单次评估预算低于 100ms。
///
/// 规则语义见 docs/health-advisory-plan-2026-09-06.md §二:
///   R1 持续高 CPU / R2 持续高内存 / R3 CPU 较自身 7 天同时段基线异常 /
///   R4 内存线性增长(泄漏嫌疑)/ R5 磁盘写入热点 / R6 资源集中度(前 3 份额)/
///   R7 同名进程聚合(进程树叶子归并)/ R8 服务主机 CPU 归因(经 ServiceMapper)
///
/// 去重与冷却:fingerprint = hash(ruleId|subject);冷却期内命中仅累计 hit_count,
/// 超过冷却时长才生成新卡片;连续 30 分钟未命中自动 status=resolved。
/// </summary>
public class AdvisoryRuleEngine
{
    private readonly SampleRepository _repository;
    private readonly RuleStore _ruleStore;
    private readonly ServiceMapper _serviceMapper;
    private readonly ProcessManager _processManager;
    private readonly ILogger<AdvisoryRuleEngine> _logger;

    /// <summary>R9 上次评估时间(降频 30 分钟;真伪基准变化极慢)。</summary>
    private DateTime _lastR9Run = default;

    /// <summary>活动建议连续未命中超过该时长自动 resolved。</summary>
    private static readonly TimeSpan AutoResolveAfter = TimeSpan.FromMinutes(30);

    public AdvisoryRuleEngine(
        SampleRepository repository, RuleStore ruleStore,
        ServiceMapper serviceMapper, ProcessManager processManager,
        ILogger<AdvisoryRuleEngine> logger)
    {
        _repository = repository;
        _ruleStore = ruleStore;
        _serviceMapper = serviceMapper;
        _processManager = processManager;
        _logger = logger;
    }

    public void Evaluate()
    {
        try
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            using var conn = _repository.OpenConnection();
            AutoResolveStale(conn);

            foreach (var rule in _ruleStore.Current)
            {
                if (!rule.Enabled) continue;
                try
                {
                    switch (rule.Id)
                    {
                        case "R1": EvaluateSustainedHigh(conn, rule, "cpu"); break;
                        case "R2": EvaluateSustainedHigh(conn, rule, "memory"); break;
                        case "R3": EvaluateBaselineAnomaly(conn, rule); break;
                        case "R4": EvaluateMemoryGrowth(conn, rule); break;
                        case "R5": EvaluateSustainedHigh(conn, rule, "io_write"); break;
                        case "R6": EvaluateConcentration(conn, rule); break;
                        case "R7": EvaluateInstanceGroup(conn, rule); break;
                        case "R8": EvaluateServiceHost(conn, rule); break;
                        case "R9": EvaluateSvchostAuthenticity(conn, rule); break;
                    }
                }
                catch (Exception ex)
                {
                    // 单条规则失败不影响其余规则
                    _logger.LogWarning(ex, "Advisory rule {RuleId} evaluation failed", rule.Id);
                }
            }

            sw.Stop();
            if (sw.ElapsedMilliseconds > 200)
                _logger.LogWarning("Advisory evaluation took {Ms}ms (slow)", sw.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Advisory evaluation failed");
        }
    }

    // ============================ 查询帮助 ============================

    public sealed record WindowRow(
        string Process, string? Service, double AvgCpu, double AvgMem,
        double AvgIoWrite, double Instances, double PeakCpu);

    /// <summary>窗口内按进程聚合(分钟表一行/进程):均值 + 实例数(分钟样本数均值)。</summary>
    private List<WindowRow> WindowAggregates(SqliteConnection conn, int minutes)
    {
        var since = SampleRepository.FormatMinute(DateTime.Now.AddMinutes(-minutes));
        var rows = new List<WindowRow>();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT process_name, MAX(service_name),
                   AVG(avg_cpu), AVG(avg_mem_mb), AVG(avg_io_write_mb_s),
                   AVG(sample_count), MAX(avg_cpu)
            FROM samples_minute WHERE minute >= @since GROUP BY process_name
            """;
        cmd.Parameters.AddWithValue("@since", since);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            rows.Add(new WindowRow(
                reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1),
                reader.GetDouble(2), reader.GetDouble(3), reader.GetDouble(4),
                reader.GetDouble(5), reader.GetDouble(6)));
        }
        return rows;
    }

    /// <summary>
    /// 系统基础设施进程:不参与"关闭/重启应用"类建议(2026-09-07)。
    /// Memory Compression 是 Windows 10/11 的核心系统进程,内存随系统负载上下波动,
    /// 系统自动管理、用户无法关闭——R4 曾把它当"泄漏嫌疑"误报(6 小时 +11%/小时);
    /// svchost 同理(服务宿主,已由 R8/R9 专属);内置于各规则的排除判断中。
    /// </summary>
    private static readonly HashSet<string> SystemProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "memory compression", "system", "idle", "registry", "smss",
        "csrss", "wininit", "winlogon", "services", "lsass", "svchost",
        "dwm", "explorer", "audiodg", "fontdrvhost", "spoolsv",
        "taskmgr", "mmc", "dllhost", "conhost", "wudfhost"
    };

    private static bool Excluded(AdvisoryRule rule, string process) =>
        SystemProcesses.Contains(process) ||
        process.StartsWith("ResHog", StringComparison.OrdinalIgnoreCase) ||
        rule.ExcludeProcesses.Contains(process, StringComparer.OrdinalIgnoreCase);

    /// <summary>进程近 7 天"同时段(小时)"CPU 均值基线(R3)。</summary>
    private double HourBaseline(SqliteConnection conn, string process, int hour)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT AVG(avg_cpu) FROM samples_minute
            WHERE process_name = @p AND minute >= @since
              AND CAST(substr(minute, 12, 2) AS INTEGER) = @hour
            """;
        cmd.Parameters.AddWithValue("@p", process);
        cmd.Parameters.AddWithValue("@since", SampleRepository.FormatMinute(DateTime.Now.AddDays(-7)));
        cmd.Parameters.AddWithValue("@hour", hour);
        var r = cmd.ExecuteScalar();
        return r is null || r == DBNull.Value ? 0 : Convert.ToDouble(r);
    }

    // ============================ 规则实现 ============================

    private void EvaluateSustainedHigh(SqliteConnection conn, AdvisoryRule rule, string metric)
    {
        // 内存口径修正(2026-09-07,用户对比任务管理器发现虚高):改用私有工作集
        // (working_set_private_mb,查 raw 表)——工作集含与其它进程共享的 DLL/映射页,
        // 任务管理器"内存"列默认也是私有工作集,口径对齐后数值直接可比。
        // CPU/磁盘写仍走分钟表(samples_minute 无 private 列;窗口 ≤1 天受 raw 保留期约束)。
        if (metric == "memory")
        {
            if (rule.WindowMinutes > 24 * 60) return;
            var since = SampleRepository.FormatTimestamp(DateTime.Now.AddMinutes(-rule.WindowMinutes));
            var memRows = new List<(string Process, double AvgPriv, string? Service)>();
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = """
                    SELECT process_name, AVG(working_set_private_mb), MAX(service_name)
                    FROM samples WHERE timestamp >= @since GROUP BY process_name
                    """;
                cmd.Parameters.AddWithValue("@since", since);
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                    memRows.Add((reader.GetString(0), reader.GetDouble(1),
                                 reader.IsDBNull(2) ? null : reader.GetString(2)));
            }
            foreach (var r in memRows)
            {
                if (Excluded(rule, r.Process)) continue;
                if (r.AvgPriv < rule.Threshold || r.AvgPriv < 256) continue;
                var svc = r.Service is null ? "" : $"(服务: {r.Service})";
                UpsertFinding(conn, rule, r.Process,
                    conclusion: $"{r.Process} 过去 {rule.WindowMinutes} 分钟平均私有内存 {r.AvgPriv:F0} MB," +
                                $"超过阈值 {rule.Threshold:F0} MB。",
                    suggestion: "如该程序当前非必需,可关闭以释放内存;长期偏高可考虑升级内存或更换更轻量的替代软件。",
                    evidence: $"窗口 {rule.WindowMinutes} 分钟,平均私有内存 {r.AvgPriv:F0} MB," +
                              $"阈值 {rule.Threshold:F0} MB{svc}");
            }
            return;
        }

        var rows = WindowAggregates(conn, rule.WindowMinutes);
        foreach (var r in rows)
        {
            if (Excluded(rule, r.Process)) continue;

            double value, floor;
            string unit, metricLabel;
            switch (metric)
            {
                case "cpu":
                    value = r.AvgCpu; floor = 5; unit = "%"; metricLabel = "CPU";
                    break;
                default: // io_write
                    value = r.AvgIoWrite; floor = 1.0; unit = "MB/s"; metricLabel = "磁盘写入";
                    break;
            }
            if (value < rule.Threshold || value < floor) continue;

            var svc = r.Service is null ? "" : $"(服务: {r.Service})";
            UpsertFinding(conn, rule, r.Process,
                conclusion: $"{r.Process} 过去 {rule.WindowMinutes} 分钟平均{metricLabel} {value:F1}{unit}," +
                            $"超过阈值 {rule.Threshold}{unit}。",
                suggestion: metric switch
                {
                    "cpu" => "建议检查该程序是否有后台任务/卡死循环,不使用时可暂时关闭以降低 CPU 负载。",
                    "memory" => "如该程序当前非必需,可关闭以释放内存;长期偏高可考虑升级内存或更换更轻量的替代软件。",
                    _ => "持续大量写盘可能来自下载/编译/同步类任务;机械硬盘上会拖慢整机,建议错峰执行。"
                },
                evidence: $"窗口 {rule.WindowMinutes} 分钟,平均 {value:F1}{unit},阈值 {rule.Threshold}{unit}{svc}");
        }
    }

    private void EvaluateBaselineAnomaly(SqliteConnection conn, AdvisoryRule rule)
    {
        var rows = WindowAggregates(conn, rule.WindowMinutes);
        var hour = DateTime.Now.Hour;
        foreach (var r in rows.Where(r => r.AvgCpu >= 10).OrderByDescending(r => r.AvgCpu).Take(10))
        {
            if (Excluded(rule, r.Process)) continue;
            var baseline = HourBaseline(conn, r.Process, hour);
            if (baseline < 1) continue;                       // 历史数据不足/历史空闲,基线无意义
            var ratio = r.AvgCpu / baseline;
            if (ratio < rule.Threshold) continue;

            UpsertFinding(conn, rule, r.Process,
                conclusion: $"{r.Process} 当前平均 CPU {r.AvgCpu:F1}%,为其近 7 天同时段均值" +
                            $"({baseline:F1}%)的 {ratio:F1} 倍。",
                suggestion: "明显高于历史水平:可能正在执行重负载任务,也可能是异常行为;建议打开趋势页对比确认后再处理。",
                evidence: $"当前 {r.AvgCpu:F1}% vs 7 天同时段均值 {baseline:F1}%,倍数 {ratio:F1}(阈值 {rule.Threshold}×)");
        }
    }

    private void EvaluateMemoryGrowth(SqliteConnection conn, AdvisoryRule rule)
    {
        // 取窗口内(默认 6h)每分钟序列,在 C# 侧做线性回归,避免 SQL 实现斜率
        var since = SampleRepository.FormatMinute(DateTime.Now.AddMinutes(-rule.WindowMinutes));
        var series = new Dictionary<string, List<(int T, double Mem)>>(256);
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                SELECT process_name, avg_mem_mb FROM samples_minute
                WHERE minute >= @since ORDER BY process_name, minute
                """;
            cmd.Parameters.AddWithValue("@since", since);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var p = reader.GetString(0);
                if (!series.TryGetValue(p, out var list))
                    series[p] = list = new List<(int, double)>();
                list.Add((series[p].Count, reader.GetDouble(1)));
            }
        }

        foreach (var (process, points) in series)
        {
            if (Excluded(rule, process)) continue;
            if (points.Count < 60) continue;                  // 至少 1 小时数据
            var last = points[^1].Item2;
            if (last < 500) continue;                         // 绝对量过小无意义

            var mean = points.Average(p => p.Item2);
            if (mean < 1) continue;
            var cov = points.Sum(p => (p.T - (points.Count - 1) / 2.0) * (p.Item2 - mean));
            var varT = points.Sum(p => Math.Pow(p.T - (points.Count - 1) / 2.0, 2));
            if (varT < 1) continue;
            var slopePerMin = cov / varT;                     // MB per minute
            var pctPerHour = slopePerMin * 60 / mean * 100;   // 相对均值的 %/小时
            if (pctPerHour < rule.Threshold) continue;

            UpsertFinding(conn, rule, process,
                conclusion: $"{process} 内存近 {rule.WindowMinutes / 60.0:F0} 小时持续增长" +
                            $"(约 {pctPerHour:F0}%/小时,当前 {last:F0} MB),存在资源泄漏嫌疑。",
                suggestion: "建议在方便时保存工作并重启该程序;若重启后很快再次增长,可向软件厂商反馈。",
                evidence: $"窗口 {rule.WindowMinutes} 分钟,斜率 {pctPerHour:F0}%/小时(阈值 {rule.Threshold}),当前 {last:F0} MB");
        }
    }

    private void EvaluateConcentration(SqliteConnection conn, AdvisoryRule rule)
    {
        var rows = WindowAggregates(conn, rule.WindowMinutes);
        if (rows.Count == 0) return;
        var loads = rows.Where(r => !Excluded(rule, r.Process))
                        .Select(r => (r.Process, Load: r.AvgCpu * r.Instances))
                        .OrderByDescending(x => x.Load)
                        .ToList();
        var total = loads.Sum(x => x.Load);
        if (total < 5) return;                                // 整机空闲无诊断意义

        var top3 = loads.Take(3).ToList();
        var share = top3.Sum(x => x.Load) / total * 100;
        if (share < rule.Threshold) return;

        var detail = string.Join("、", top3.Select(x => $"{x.Process}({x.Load / total * 100:F0}%)"));
        UpsertFinding(conn, rule, "(系统整体)",
            conclusion: $"系统 CPU 的 {share:F0}% 集中在前 3 个进程:{detail}。",
            suggestion: "整体卡顿主要来自以上进程;逐个确认其必要性,优先处理占比最高的。",
            evidence: $"窗口 {rule.WindowMinutes} 分钟,前 3 进程合计 {share:F0}%(阈值 {rule.Threshold}%)");
    }

    /// <summary>R7 存活口径:"最近 60 秒内仍有样本"视为存活(与任务管理器"活着的进程"对齐)。</summary>
    private static readonly TimeSpan AliveWindow = TimeSpan.FromSeconds(60);

    private void EvaluateInstanceGroup(SqliteConnection conn, AdvisoryRule rule)
    {
        // 口径修正 v2(2026-09-07,用户对比任务管理器发现):
        // ① 实例数改"存活口径"——只统计最近 60 秒内仍有样本的 pid。此前的
        //    COUNT(DISTINCT pid)(15 分钟窗口)把 wps 这类"启动器"型程序拉起即退出的
        //    短命进程也计入(99 vs 任务管理器 9);② 内存改用私有工作集
        //    (working_set_private_mb)——工作集合计重复计入共享 DLL 页
        //    (4484MB vs 任务管理器 ~226MB 的第二个主因)。两个口径均与任务管理器直接可比。
        //    (2026-09-07 补:当前建议视图此前用 15s 内必有新样本的 ALIVE_CUTOFF;
        //     现统一为 60s,与历史视图一致,避免同进程不同数字的困惑。)
        // svchost 不在本规则内(服务宿主多实例是系统设计,归 R8/R9)。
        if (rule.WindowMinutes > 24 * 60) return;   // raw 仅保留 1 天
        var since = SampleRepository.FormatTimestamp(DateTime.Now.AddMinutes(-rule.WindowMinutes));
        var aliveCutoff = SampleRepository.FormatTimestamp(DateTime.Now - AliveWindow);
        var perPid = new List<(string Process, double PrivMem, string? Service)>();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                SELECT process_name, AVG(working_set_private_mb), MAX(service_name)
                FROM samples WHERE timestamp >= @since
                GROUP BY process_name, pid
                HAVING MAX(timestamp) >= @alive
                """;
            cmd.Parameters.AddWithValue("@since", since);
            cmd.Parameters.AddWithValue("@alive", aliveCutoff);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                perPid.Add((reader.GetString(0), reader.GetDouble(1),
                            reader.IsDBNull(2) ? null : reader.GetString(2)));
            }
        }

        foreach (var grp in perPid.GroupBy(x => x.Process))
        {
            if (Excluded(rule, grp.Key)) continue;
            if (grp.Key.Equals("svchost", StringComparison.OrdinalIgnoreCase)) continue;

            var instances = grp.Count();
            var totalMem = grp.Sum(x => x.PrivMem);          // 每 pid 私有内存均值求和 = 存活合计
            if (instances < rule.Threshold || totalMem < 1024) continue;

            var service = grp.Select(x => x.Service).FirstOrDefault(s => !string.IsNullOrEmpty(s));
            var svc = service is null ? "" : $"(服务: {service})";
            var parentNote = "";
            try
            {
                var live = LiveInstances(grp.Key);
                var parents = live.Select(p => p.ParentPid).Where(p => p.HasValue).Distinct().ToList();
                if (live.Count > 0 && parents.Count == 1)
                    parentNote = $",同父进程 PID {parents[0]}";
            }
            catch { /* 增强信息失败不阻塞主流程 */ }

            UpsertFinding(conn, rule, grp.Key,
                conclusion: $"{grp.Key} 共 {instances} 个存活实例,合计私有内存 {totalMem:F0} MB{parentNote}。",
                suggestion: "多进程为该类程序(如浏览器/编辑器)的正常架构;如需释放内存,可关闭空闲的窗口/标签页。",
                evidence: $"存活实例 {instances}(阈值 {rule.Threshold}),私有内存合计 {totalMem:F0} MB" +
                          $"(口径:最近 {rule.WindowMinutes} 分钟均值,仅存活进程)");
        }
    }

    private void EvaluateServiceHost(SqliteConnection conn, AdvisoryRule rule)
    {
        // raw 表(1 天保留)带 pid:窗口内 svchost 按 pid 聚合找主要贡献者。
        // 内存证据用 private_bytes_mb:svchost 实例间共享大量系统 DLL,工作集合计
        // 会重复计入共享页;跨实例聚合按私有字节记账是行业惯例(resmon"提交"列)。
        var since = SampleRepository.FormatTimestamp(DateTime.Now.AddMinutes(-rule.WindowMinutes));
        int? topPid = null;
        double topCpu = 0, topMem = 0;
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                SELECT pid, AVG(cpu_percent), AVG(private_bytes_mb) FROM samples
                WHERE process_name = 'svchost' AND timestamp >= @since
                GROUP BY pid ORDER BY 2 DESC LIMIT 1
                """;
            cmd.Parameters.AddWithValue("@since", since);
            using var reader = cmd.ExecuteReader();
            if (reader.Read())
            {
                topPid = reader.GetInt32(0);
                topCpu = reader.GetDouble(1);
                topMem = reader.IsDBNull(2) ? 0 : reader.GetDouble(2);
            }
        }
        if (topPid is null || topCpu < rule.Threshold) return;

        _serviceMapper.RefreshIfNeeded();
        var display = _serviceMapper.GetServiceDisplayName(topPid.Value)
                      ?? _serviceMapper.GetServiceName(topPid.Value);
        var subject = string.IsNullOrEmpty(display)
            ? $"服务主机(PID {topPid})"
            : $"服务主机: {display}";

        UpsertFinding(conn, rule, subject,
            conclusion: $"{subject} 过去 {rule.WindowMinutes} 分钟平均 CPU {topCpu:F1}%" +
                        $"(阈值 {rule.Threshold}%),私有内存 {topMem:F0} MB。",
            suggestion: "可通过「设置 → Windows 更新/服务」暂停对应服务观察;ResHog 不代为执行任何操作。",
            evidence: $"主要贡献 PID {topPid},窗口平均 CPU {topCpu:F1}%,私有内存 {topMem:F0} MB");
    }

    /// <summary>
    /// R9 svchost 真伪校验(2026-09-06):行业惯例(Process Explorer/System Informer)——
    /// 合法 svchost 恒满足:①镜像路径 = %SystemRoot%\System32\svchost.exe;
    /// ②父进程 = services.exe(SCM 启动)。任何已知信息偏离 → critical 建议。
    /// 未知的路径/父进程(权限不足、快照间隙)不算违规,避免误报;
    /// 账户校验(token 查询)成本高,列二期。纯提醒,不执行任何动作。
    /// </summary>
    private void EvaluateSvchostAuthenticity(SqliteConnection conn, AdvisoryRule rule)
    {
        // 降频(2026-09-07):R9 每 30 分钟评估一次——真伪基准变化极慢,且评估只用只读缓存。
        // (原随 Engine 每 15s;现改为 Engine 侧"每 30 分钟才跑 R9 体",其余规则照常。)
        if (_lastR9Run != default && (DateTime.Now - _lastR9Run) < TimeSpan.FromMinutes(30))
            return;

        var all = LiveAllProcesses();   // 只读缓存,不触发刷新
        if (all.Count == 0) return;     // 缓存为空(刚启动)→ 本轮跳过,不触发刷新

        var byPid = new Dictionary<int, ResHog.Shared.Dtos.ProcessInfoDto>(all.Count);
        foreach (var p in all) byPid[p.Pid] = p;

        foreach (var p in all)
        {
            if (!p.ProcessName.Equals("svchost", StringComparison.OrdinalIgnoreCase)) continue;

            var path = (p.CommandLine ?? "").Replace('/', '\\');
            var pathKnown = path.Length > 0;
            var pathOk = pathKnown &&
                         path.EndsWith("\\windows\\system32\\svchost.exe",
                             StringComparison.OrdinalIgnoreCase);

            var parentKnown = false;
            var parentOk = false;
            var parentName = "未知";
            if (p.ParentPid.HasValue && byPid.TryGetValue(p.ParentPid.Value, out var parent))
            {
                parentKnown = true;
                parentOk = parent.ProcessName.Equals("services", StringComparison.OrdinalIgnoreCase);
                parentName = parent.ProcessName;
            }

            var pathViolation = pathKnown && !pathOk;
            var parentViolation = parentKnown && !parentOk;
            if (!pathViolation && !parentViolation) continue;

            var pathShown = pathKnown ? path : "未知";
            var pathPart = pathViolation
                ? $"镜像路径为 {path}(合法应为 System32\\svchost.exe);"
                : "镜像路径未知(无法读取);";
            var parentPart = parentViolation
                ? $"父进程为 {parentName}(合法应为 services.exe)。"
                : "";

            UpsertFinding(conn, rule, $"可疑 svchost (PID {p.Pid})",
                conclusion: "发现不符合系统 svchost 特征的进程:" + pathPart + parentPart,
                suggestion: "合法 svchost 只应运行自 System32。建议用杀毒软件全盘扫描,并可向安全厂商提交该文件分析。ResHog 仅提醒,不执行任何处理。",
                evidence: $"PID {p.Pid};路径 {pathShown};父进程 {parentName}" +
                          (p.ParentPid.HasValue ? $"(PID {p.ParentPid})" : ""));
        }
        _lastR9Run = DateTime.Now;
    }

    // ============================ findings 存储 ============================

    private static string Fingerprint(string ruleId, string subject)
    {
        var hash = MD5.HashData(Encoding.UTF8.GetBytes($"{ruleId}|{subject}"));
        return Convert.ToHexString(hash);
    }

    /// <summary>写建议:冷却期内命中→累计;否则新卡。返回是否新建。</summary>
    private void UpsertFinding(
        SqliteConnection conn, AdvisoryRule rule, string subject,
        string conclusion, string suggestion, string evidence)
    {
        var fp = Fingerprint(rule.Id, subject);
        var now = SampleRepository.FormatTimestamp(DateTime.Now);

        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                SELECT id, last_seen FROM findings
                WHERE fingerprint = @fp AND status = 'active' AND ignored = 0
                ORDER BY last_seen DESC LIMIT 1
                """;
            cmd.Parameters.AddWithValue("@fp", fp);
            using var reader = cmd.ExecuteReader();
            if (reader.Read())
            {
                var lastSeen = reader.GetString(1);
                var cooldown = TimeSpan.FromHours(rule.CooldownHours);
                if (DateTime.TryParse(lastSeen, out var last) &&
                    DateTime.Now - last < cooldown)
                {
                    reader.Close();
                    using var up = conn.CreateCommand();
                    up.CommandText = """
                        UPDATE findings SET last_seen = @now, hit_count = hit_count + 1,
                               evidence = @evidence, conclusion = @conclusion
                        WHERE fingerprint = @fp AND status = 'active' AND ignored = 0
                        """;
                    up.Parameters.AddWithValue("@now", now);
                    up.Parameters.AddWithValue("@evidence", evidence);
                    up.Parameters.AddWithValue("@conclusion", conclusion);
                    up.Parameters.AddWithValue("@fp", fp);
                    up.ExecuteNonQuery();
                    return;
                }
            }
        }

        using var ins = conn.CreateCommand();
        ins.CommandText = """
            INSERT INTO findings (rule_id, severity, subject, fingerprint, evidence,
                                  conclusion, suggestion, first_seen, last_seen, hit_count, ignored, status)
            VALUES (@rule, @sev, @subject, @fp, @evidence,
                    @conclusion, @suggestion, @now, @now, 1, 0, 'active')
            """;
        ins.Parameters.AddWithValue("@rule", rule.Id);
        ins.Parameters.AddWithValue("@sev", rule.Severity);
        ins.Parameters.AddWithValue("@subject", subject);
        ins.Parameters.AddWithValue("@fp", fp);
        ins.Parameters.AddWithValue("@evidence", evidence);
        ins.Parameters.AddWithValue("@conclusion", conclusion);
        ins.Parameters.AddWithValue("@suggestion", suggestion);
        ins.Parameters.AddWithValue("@now", now);
        ins.ExecuteNonQuery();
    }

    private void AutoResolveStale(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE findings SET status = 'resolved'
            WHERE status = 'active' AND last_seen < @stale
            """;
        cmd.Parameters.AddWithValue("@stale",
            SampleRepository.FormatTimestamp(DateTime.Now - AutoResolveAfter));
        cmd.ExecuteNonQuery();
    }

    public List<FindingDto> GetFindings(string status)
    {
        var list = new List<FindingDto>();
        var isHistory = string.Equals(status, "history", StringComparison.OrdinalIgnoreCase);
        using var conn = _repository.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = isHistory
            ? "SELECT * FROM findings ORDER BY last_seen DESC LIMIT 200"
            : "SELECT * FROM findings WHERE ignored = 0 AND status = 'active' ORDER BY last_seen DESC LIMIT 50";
        using var reader = cmd.ExecuteReader();
        var labels = _ruleStore.Current.ToDictionary(r => r.Id, r => r.Label);
        while (reader.Read())
        {
            var ruleId = reader.GetString(1);
            list.Add(new FindingDto(
                reader.GetInt64(0),
                ruleId,
                labels.TryGetValue(ruleId, out var label) ? label : ruleId,
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(6),
                reader.GetString(7),
                reader.GetString(5),
                reader.GetString(8),
                reader.GetString(9),
                reader.GetInt64(10),
                reader.GetString(12),
                reader.GetInt64(11) != 0
            ));
        }
        return list;
    }

    public bool IgnoreFinding(long id)
    {
        using var conn = _repository.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE findings SET ignored = 1 WHERE id = @id";
        cmd.Parameters.AddWithValue("@id", id);
        return cmd.ExecuteNonQuery() > 0;
    }

    // ============================ 实时进程树(增强) ============================

    /// <summary>
    /// R9 与 R7 用的实时进程列表(2026-09-07 修正):
    /// 改用 ProcessManager.TryGetCachedProcessList()——**只读缓存不触发刷新**。
    /// 背景:此前用 SearchProcesses("") 每次评估都可能触发后台刷新(3s TTL),
    /// R9 每 15s 一次 → 刷新→枚举(2~4s)→R9 再触发,实测把评估拖到 6~34 秒。
    /// 缓存为空(服务刚启动)时返回空,本轮跳过,下轮再试,不触发刷新。
    /// </summary>
    private List<ResHog.Shared.Dtos.ProcessInfoDto> LiveInstances(string processName)
    {
        try
        {
            return _processManager.TryGetCachedProcessList()
                .Where(p => p.ProcessName.Equals(processName, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }
        catch
        {
            return new List<ResHog.Shared.Dtos.ProcessInfoDto>();
        }
    }

    /// <summary>R9 评估的实时全部进程(只读缓存,不触发刷新)。</summary>
    private List<ResHog.Shared.Dtos.ProcessInfoDto> LiveAllProcesses()
    {
        try
        {
            return _processManager.TryGetCachedProcessList();
        }
        catch
        {
            return new List<ResHog.Shared.Dtos.ProcessInfoDto>();
        }
    }
}
