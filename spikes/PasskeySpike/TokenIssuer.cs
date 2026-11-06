using System.Security.Claims;
using System.Text;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using PasskeySpike.Storage;

namespace PasskeySpike;

/// <summary>Issues the short-lived JWT access token (refresh tokens are #31).</summary>
public sealed class TokenIssuer(IOptions<JwtOptions> options, TimeProvider time)
{
    private readonly JwtOptions options = options.Value;

    public static SymmetricSecurityKey SigningKey(JwtOptions options) => new(Encoding.UTF8.GetBytes(options.SigningKey));

    public TokenResponse Issue(SpikeUser user)
    {
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
            ]),
            SigningCredentials = new SigningCredentials(SigningKey(options), SecurityAlgorithms.HmacSha256),
        });

        return new TokenResponse(token, expires, user.Id, user.DisplayName);
    }
}
