using System.Text.Json;
using System.Text.Json.Serialization;
using ResHog.Shared.Dtos;

namespace ResHog.UI.Services;

/// <summary>
/// Compile-time JSON serialization context for trim-safe deserialization.
/// Avoids reflection-based serialization which breaks under PublishTrimmed.
/// </summary>
[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(HealthDto))]
[JsonSerializable(typeof(DashboardDto))]
[JsonSerializable(typeof(List<TopNResultDto>))]
[JsonSerializable(typeof(List<TrendPointDto>))]
[JsonSerializable(typeof(List<AlertDto>))]
[JsonSerializable(typeof(List<string>))]
[JsonSerializable(typeof(ProcessDetailDto))]
[JsonSerializable(typeof(ProcessSearchRequestDto))]
[JsonSerializable(typeof(List<ProcessInfoDto>))]
[JsonSerializable(typeof(KillProcessRequestDto))]
[JsonSerializable(typeof(KillProcessResponseDto))]
public partial class MonitorJsonContext : JsonSerializerContext
{
}
