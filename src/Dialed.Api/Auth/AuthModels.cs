namespace Dialed.Api.Auth;

/// <summary>A signed-in person. Every user belongs to a household (a personal one is created at sign-up).</summary>
public sealed record AppUser(string Id, string? Email, string DisplayName, string HouseholdId);

/// <summary>What a successful sign-in or refresh returns to the app.</summary>
public sealed record TokenResponse(string AccessToken, DateTimeOffset AccessTokenExpiresAt, string RefreshToken, string UserId, string DisplayName, string HouseholdId);

public sealed record RefreshRequest(string RefreshToken);

public sealed record DevLoginRequest(string User);

public sealed class JwtOptions
{
    public const string SectionName = "Jwt";

    public string Issuer { get; set; } = "dialed-api";

    public string Audience { get; set; } = "dialed-app";

    /// <summary>At least 32 bytes. Production needs it configured (later from Key Vault); Development falls back to a fixed local key.</summary>
    public string SigningKey { get; set; } = "";

    /// <summary>Id of the signing key, put in the token header so keys can be rotated.</summary>
    public string KeyId { get; set; } = "local-1";

    public TimeSpan AccessTokenLifetime { get; set; } = TimeSpan.FromMinutes(30);

    public TimeSpan RefreshTokenLifetime { get; set; } = TimeSpan.FromDays(60);

    public const string DevelopmentSigningKey = "development-only-signing-key-change-me-0123456789";
}
