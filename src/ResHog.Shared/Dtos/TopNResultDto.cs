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
    string MetricName
);
