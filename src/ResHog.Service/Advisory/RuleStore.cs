using System.Text.Json;
using ResHog.Api;
using ResHog.Shared.Dtos;

namespace ResHog.Advisory;

/// <summary>
/// 规则持久化:ProgramData\ResHog\rules.json(与 data.db 同目录)。
/// 文件由服务持有写入权——UI(普通用户权限)经 PUT /api/rules 修改,
/// 服务端落盘,规避 ProgramData 下 SYSTEM 创建文件的 ACL 问题。
/// 缺文件时写入出厂预设(8 条,见 <see cref="CreateDefaultRules"/>)。
/// 线程安全:Current 快照 + Save 全量替换(评估线程读、API 线程写)。
/// 序列化使用 ApiJsonContext(源生成)——服务端已禁用反射序列化,
/// 普通 JsonSerializerOptions 会抛 "Reflection-based serialization has been disabled"
/// (2026-09-07 实测 PUT /api/rules 500 的真因)。
/// </summary>
public class RuleStore
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        TypeInfoResolver = ApiJsonContext.Default
    };

    private readonly string _path;
    private readonly object _lock = new();
    private IReadOnlyList<AdvisoryRule> _current;

    public RuleStore(string dbPath)
    {
        var dir = Path.GetDirectoryName(dbPath);
        if (string.IsNullOrEmpty(dir)) dir = AppContext.BaseDirectory;
        _path = Path.Combine(dir, "rules.json");
        _current = Load();
    }

    /// <summary>当前生效规则(评估线程每次评估读取)。</summary>
    public IReadOnlyList<AdvisoryRule> Current
    {
        get { lock (_lock) return _current; }
    }

    public string FilePath => _path;

    /// <summary>全量替换规则集并落盘(UI 保存)。</summary>
    public void Save(IReadOnlyList<AdvisoryRule> rules)
    {
        lock (_lock)
        {
            _current = rules;
            File.WriteAllText(_path, JsonSerializer.Serialize(new AdvisoryRulesDto(rules.ToList()), JsonOpts));
        }
    }

    private IReadOnlyList<AdvisoryRule> Load()
    {
        try
        {
            if (File.Exists(_path))
            {
                var dto = JsonSerializer.Deserialize<AdvisoryRulesDto>(File.ReadAllText(_path), JsonOpts);
                if (dto?.Rules is { Count: > 0 }) return dto.Rules;
            }
        }
        catch
        {
            // 损坏/不可读 → 回落出厂预设并重写
        }

        var defaults = CreateDefaultRules();
        try { File.WriteAllText(_path, JsonSerializer.Serialize(new AdvisoryRulesDto(defaults), JsonOpts)); }
        catch { /* 首启目录权限异常时内存默认仍生效 */ }
        return defaults;
    }

    /// <summary>出厂预设 8 条(阈值与 docs/health-advisory-plan §二 P0 一致,用户可改)。</summary>
    public static List<AdvisoryRule> CreateDefaultRules() =>
    [
        new() { Id = "R1", Label = "持续高 CPU", Enabled = true, Severity = "warning",
                Threshold = 50, WindowMinutes = 15, CooldownHours = 6,
                ExcludeProcesses = ["ResHog.Service"] },
        new() { Id = "R2", Label = "持续高内存", Enabled = true, Severity = "warning",
                Threshold = 1536, WindowMinutes = 15, CooldownHours = 6,
                ExcludeProcesses = ["ResHog.Service"] },
        new() { Id = "R3", Label = "CPU 较自身基线异常", Enabled = true, Severity = "warning",
                Threshold = 3, WindowMinutes = 15, CooldownHours = 6,
                ExcludeProcesses = ["ResHog.Service"] },
        new() { Id = "R4", Label = "内存增长(泄漏嫌疑)", Enabled = true, Severity = "warning",
                Threshold = 10, WindowMinutes = 360, CooldownHours = 12,
                ExcludeProcesses = ["ResHog.Service"] },
        new() { Id = "R5", Label = "磁盘写入热点", Enabled = true, Severity = "warning",
                Threshold = 20, WindowMinutes = 10, CooldownHours = 6,
                ExcludeProcesses = ["ResHog.Service"] },
        new() { Id = "R6", Label = "资源集中度", Enabled = true, Severity = "warning",
                Threshold = 70, WindowMinutes = 15, CooldownHours = 6,
                ExcludeProcesses = ["ResHog.Service"] },
        new() { Id = "R7", Label = "同名进程聚合", Enabled = true, Severity = "info",
                Threshold = 10, WindowMinutes = 15, CooldownHours = 12,
                ExcludeProcesses = ["ResHog.Service"] },
        new() { Id = "R8", Label = "服务主机 CPU 归因", Enabled = true, Severity = "info",
                Threshold = 30, WindowMinutes = 15, CooldownHours = 6,
                ExcludeProcesses = ["ResHog.Service"] },
        new() { Id = "R9", Label = "svchost 真伪校验(伪装检测)", Enabled = true, Severity = "critical",
                Threshold = 1, WindowMinutes = 0, CooldownHours = 12,
                ExcludeProcesses = ["ResHog.Service"] }
    ];
}
