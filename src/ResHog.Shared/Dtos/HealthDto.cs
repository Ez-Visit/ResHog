namespace ResHog.Shared.Dtos;

/// <summary>
/// Health check response, returned by GET /api/health.
/// </summary>
public record HealthDto(
    string Status,
    DateTime StartTime,
    long UptimeSeconds,
    long SampleCount,
    int MonitoredProcesses,
    string Version
);
