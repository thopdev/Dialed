using System.Security.Claims;
using System.Text;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Dialed.Api.Auth;

/// <summary>Creates the short-lived signed access token (a JWT with the user and household in its claims).</summary>
public sealed class TokenIssuer(IOptions<JwtOptions> options, TimeProvider time)
{
    public const string HouseholdClaim = "household";

    private readonly JwtOptions options = options.Value;

    public static SymmetricSecurityKey SigningKey(JwtOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return new SymmetricSecurityKey(Encoding.UTF8.GetBytes(options.SigningKey)) { KeyId = options.KeyId };
    }

    /// <summary>Returns the token and when it expires.</summary>
    public (string Token, DateTimeOffset ExpiresAt) IssueAccessToken(AppUser user)
    {
        ArgumentNullException.ThrowIfNull(user);

        var now = time.GetUtcNow();
        var expires = now.Add(options.AccessTokenLifetime);
        var token = new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = options.Issuer,
            Audience = options.Audience,
            IssuedAt = now.UtcDateTime,
            NotBefore = now.UtcDateTime,
            Expires = expires.UtcDateTime,
            Subject = new ClaimsIdentity(
            [
                new Claim(JwtRegisteredClaimNames.Sub, user.Id),
                new Claim(JwtRegisteredClaimNames.Email, user.Email ?? ""),
                new Claim(JwtRegisteredClaimNames.Name, user.DisplayName),
                new Claim(HouseholdClaim, user.HouseholdId),
            ]),
            SigningCredentials = new SigningCredentials(SigningKey(options), SecurityAlgorithms.HmacSha256),
        });

        return (token, expires);
    }
}
