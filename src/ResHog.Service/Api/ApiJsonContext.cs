using System.Text.Json.Serialization;
using ResHog.Shared.Dtos;

namespace ResHog.Api;

/// <summary>
/// Compile-time JSON serialization context for the HTTP API.
/// Required for PublishTrimmed: without source-generated type metadata,
/// System.Text.Json falls back to reflection which is stripped by the trimmer,
/// causing NotSupportedException at runtime.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(HealthDto))]
[JsonSerializable(typeof(DashboardDto))]
[JsonSerializable(typeof(SystemOverviewDto))]
[JsonSerializable(typeof(ProcessSummaryDto))]
[JsonSerializable(typeof(List<ProcessSummaryDto>))]
[JsonSerializable(typeof(TopNResultDto))]
[JsonSerializable(typeof(List<TopNResultDto>))]
[JsonSerializable(typeof(TrendPointDto))]
[JsonSerializable(typeof(List<TrendPointDto>))]
[JsonSerializable(typeof(AlertDto))]
[JsonSerializable(typeof(List<AlertDto>))]
[JsonSerializable(typeof(ProcessDetailDto))]
[JsonSerializable(typeof(List<string>))]
[JsonSerializable(typeof(ErrorResponseDto))]
[JsonSerializable(typeof(ProcessSearchRequestDto))]
[JsonSerializable(typeof(ProcessInfoDto))]
[JsonSerializable(typeof(List<ProcessInfoDto>))]
[JsonSerializable(typeof(KillProcessRequestDto))]
[JsonSerializable(typeof(KillProcessResponseDto))]
public partial class ApiJsonContext : JsonSerializerContext
{
}
