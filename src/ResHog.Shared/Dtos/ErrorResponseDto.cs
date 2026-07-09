namespace ResHog.Shared.Dtos;

/// <summary>
/// Standard error response returned by API endpoints on failure.
/// Replaces anonymous types which break under PublishTrimmed.
/// </summary>
public record ErrorResponseDto(string Error);
