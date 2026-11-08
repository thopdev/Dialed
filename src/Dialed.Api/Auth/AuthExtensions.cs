using System.Security.Claims;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Dialed.Api.Auth;

public static class AuthExtensions
{
    /// <summary>Registers token issuing, refresh tokens and JWT bearer validation.</summary>
    public static IServiceCollection AddDialedAuth(this IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        var jwt = configuration.GetSection(JwtOptions.SectionName).Get<JwtOptions>() ?? new JwtOptions();
        if (string.IsNullOrWhiteSpace(jwt.SigningKey))
        {
            if (!environment.IsDevelopment())
            {
                throw new InvalidOperationException("Jwt:SigningKey is not set. Outside Development the signing key must be configured.");
            }

            jwt.SigningKey = JwtOptions.DevelopmentSigningKey;
        }

        if (System.Text.Encoding.UTF8.GetByteCount(jwt.SigningKey) < 32)
        {
            throw new InvalidOperationException("Jwt:SigningKey must be at least 32 bytes.");
        }

        services.Configure<JwtOptions>(options =>
        {
            options.Issuer = jwt.Issuer;
            options.Audience = jwt.Audience;
            options.SigningKey = jwt.SigningKey;
            options.KeyId = jwt.KeyId;
            options.AccessTokenLifetime = jwt.AccessTokenLifetime;
            options.RefreshTokenLifetime = jwt.RefreshTokenLifetime;
        });

        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<IUserDirectory>(_ => new InMemoryUserDirectory(environment.IsDevelopment() ? DevUsers.All : []));
        services.AddSingleton<InMemoryRefreshTokenStore>();
        services.AddSingleton<IRefreshTokenStore>(sp => sp.GetRequiredService<InMemoryRefreshTokenStore>());
        services.AddSingleton<TokenIssuer>();
        services.AddSingleton<RefreshTokenService>();

        services
            .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(options =>
            {
                options.MapInboundClaims = false;
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidIssuer = jwt.Issuer,
                    ValidAudience = jwt.Audience,
                    IssuerSigningKey = TokenIssuer.SigningKey(jwt),
                    NameClaimType = JwtRegisteredClaimNames.Name,
                    ClockSkew = TimeSpan.FromSeconds(30),
                };
            });
        services.AddAuthorization();
        return services;
    }

    /// <summary>Maps <c>/auth/refresh</c>, <c>/auth/signout</c> and <c>/auth/me</c>; <c>/auth/dev</c> only in Development.</summary>
    public static IEndpointRouteBuilder MapDialedAuth(this IEndpointRouteBuilder endpoints, IHostEnvironment environment)
    {
        var auth = endpoints.MapGroup("/auth");

        auth.MapPost("/refresh", async (RefreshRequest request, RefreshTokenService refresh, CancellationToken ct) =>
            await refresh.RotateAsync(request.RefreshToken, ct) is { } tokens ? Results.Ok(tokens) : Results.Unauthorized());

        auth.MapPost("/signout", async (RefreshRequest request, RefreshTokenService refresh, CancellationToken ct) =>
        {
            await refresh.RevokeAsync(request.RefreshToken, ct);
            return Results.NoContent();
        });

        auth.MapGet("/me", async (ClaimsPrincipal principal, IUserDirectory users, CancellationToken ct) =>
            await users.FindByIdAsync(principal.FindFirstValue(JwtRegisteredClaimNames.Sub) ?? "", ct) is { } user
                ? Results.Ok(user)
                : Results.NotFound()).RequireAuthorization();

        if (environment.IsDevelopment())
        {
            // Test login for local development and end-to-end tests (#33). Never mapped outside Development.
            auth.MapPost("/dev", async (DevLoginRequest request, RefreshTokenService refresh, CancellationToken ct) =>
                DevUsers.Find(request.User) is { } user
                    ? Results.Ok(await refresh.SignInAsync(user, ct))
                    : Results.BadRequest(new { error = "Unknown test user. Use alice, bob or carol." }));
        }

        return endpoints;
    }
}
