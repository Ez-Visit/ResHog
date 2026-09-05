namespace ResHog.Shared.Dtos;

/// <summary>
/// Top-N ranking result for a single process, returned by GET /api/topn.
/// </summary>
public record TopNResultDto(
    int Rank,
    string ProcessName,
    string? ServiceName,
    double AvgValue,
    double MaxValue,
    double SecondaryMetric,
    string Unit,
    string MetricName,
    // 任务管理器同款友好名;null=旧服务未提供(Top-N 富化 DISP-9)
    string? DisplayName = null
);
