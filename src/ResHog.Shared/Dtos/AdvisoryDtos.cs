namespace ResHog.Shared.Dtos;

/// <summary>
/// 健康诊断规则(健康顾问功能,2026-09-06)。
/// rules.json 持久化 + UI 规则编辑器共用;不同规则类型复用同一组字段,
/// 各类型对字段的语义解释见 docs/health-advisory-plan-2026-09-06.md §二。
/// </summary>
public class AdvisoryRule
{
    /// <summary>规则 ID(R1..R8 出厂;用户自定义规则自拟)。</summary>
    public string Id { get; set; } = "";

    /// <summary>中文说明(UI 编辑器展示)。</summary>
    public string Label { get; set; } = "";

    /// <summary>是否启用。</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>info / warning / critical。</summary>
    public string Severity { get; set; } = "warning";

    /// <summary>
    /// 阈值,按规则类型解释:R1/R2/R5=绝对值(CPU%、MB、MB/s);R3=基线倍数;
    /// R4=内存增长率(%/小时);R6=前 N 进程合计份额(%);R7=实例数。
    /// </summary>
    public double Threshold { get; set; }

    /// <summary>评估窗口(分钟)。</summary>
    public int WindowMinutes { get; set; } = 15;

    /// <summary>同 fingerprint 的提醒冷却(小时)。</summary>
    public int CooldownHours { get; set; } = 6;

    /// <summary>排除进程名(不参与评估)。</summary>
    public List<string> ExcludeProcesses { get; set; } = new();
}

/// <summary>规则集合(rules.json / PUT /api/rules 的载体)。</summary>
public record AdvisoryRulesDto(List<AdvisoryRule> Rules);

/// <summary>诊断建议(GET /api/findings;卡片与历史共用)。</summary>
public record FindingDto(
    long Id,
    string RuleId,
    string RuleLabel,
    string Severity,
    string Subject,
    string Conclusion,
    string Suggestion,
    // 人类可读证据串(如"平均 85%,阈值 50%,窗口 15 分钟")
    string Evidence,
    string FirstSeen,
    string LastSeen,
    long HitCount,
    // active / resolved
    string Status,
    bool Ignored
);

/// <summary>"不再提醒"操作结果。</summary>
public record IgnoreFindingResponseDto(bool Success);
