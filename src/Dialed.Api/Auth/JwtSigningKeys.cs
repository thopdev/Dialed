using System.Text;
using Microsoft.IdentityModel.Tokens;

namespace Dialed.Api.Auth;

/// <summary>
/// Where access tokens are signed and which keys validate them. Development and tests use a shared HMAC key;
/// Azure signs with an RSA key that never leaves Key Vault (<see cref="KeyVaultJwtSigningKeys"/>, #30).
/// </summary>
public interface IJwtSigningKeys
{
    /// <summary>Credentials for a new token; the key id (<c>kid</c>) goes in the token header.</summary>
    SigningCredentials SigningCredentials { get; }

    /// <summary>Keys that may have signed a token with this <c>kid</c> (all known keys when it is missing).</summary>
    IEnumerable<SecurityKey> ValidationKeys(string? keyId);
}

/// <summary>Shared-secret signing (HS256) from <c>Jwt:SigningKey</c>, or the fixed development key.</summary>
public sealed class HmacJwtSigningKeys : IJwtSigningKeys
{
    private readonly SymmetricSecurityKey key;

    public HmacJwtSigningKeys(JwtOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (Encoding.UTF8.GetByteCount(options.SigningKey) < 32)
        {
            throw new InvalidOperationException("Jwt:SigningKey must be at least 32 bytes.");
        }

        key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(options.SigningKey)) { KeyId = options.KeyId };
        SigningCredentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);
    }

    public SigningCredentials SigningCredentials { get; }

    public IEnumerable<SecurityKey> ValidationKeys(string? keyId) => [key];
}
