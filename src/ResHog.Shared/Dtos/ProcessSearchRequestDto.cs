namespace ResHog.Shared.Dtos;

/// <summary>
/// Request to search running processes by name or port.
/// </summary>
public record ProcessSearchRequestDto(string Query);
