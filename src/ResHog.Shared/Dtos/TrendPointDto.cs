namespace ResHog.Shared.Dtos;

/// <summary>
/// A single data point in a trend time series, returned by GET /api/trend.
/// </summary>
public record TrendPointDto(
    string Timestamp,
    double Value
);
