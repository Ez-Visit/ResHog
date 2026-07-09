namespace ResHog.Shared.Dtos;

/// <summary>
/// Result of a process kill operation.
/// </summary>
public record KillProcessResponseDto(bool Success, string Message);
