namespace ResHog.Shared.Dtos;

/// <summary>
/// Alert record, returned by GET /api/alerts.
/// </summary>
public record AlertDto(
    long Id,
    string Timestamp,
    string ProcessName,
    int? Pid,
    string? ServiceName,
    string Metric,
    double Value,
    double Threshold,
    string Severity,
    bool Resolved
);
