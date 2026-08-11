using System.Text.Json.Serialization;

namespace PersonalFitnessPlanner.Contracts;

public sealed record LoginRequestDto
{
    public string Email { get; init; } = string.Empty;

    public string Password { get; init; } = string.Empty;

    public string? DeviceName { get; init; }
}

public sealed record RefreshTokenRequestDto
{
    public string RefreshToken { get; init; } = string.Empty;
}

public sealed record LogoutRequestDto
{
    public string? RefreshToken { get; init; }
}

public sealed record AuthTokensDto
{
    public string AccessToken { get; init; } = string.Empty;

    public string? RefreshToken { get; init; }

    public string TokenType { get; init; } = "Bearer";

    [JsonPropertyName("expires_in")]
    public long? ExpiresInSeconds { get; init; }

    [JsonPropertyName("expires_at")]
    public long? ExpiresAtEpochSeconds { get; init; }
}
