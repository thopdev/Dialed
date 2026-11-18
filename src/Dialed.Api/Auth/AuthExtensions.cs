using System.Security.Claims;
using Azure.Identity;
using Dialed.Api.Data;
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
        var vaultUri = configuration["KeyVault:Uri"];
        var useKeyVault = !string.IsNullOrWhiteSpace(jwt.KeyVaultKeyName);
        if (useKeyVault)
        {
            if (string.IsNullOrWhiteSpace(vaultUri))
            {
                throw new InvalidOperationException("Jwt:KeyVaultKeyName is set but KeyVault:Uri is not.");
            }

            // Uses the managed identity in Azure (AZURE_CLIENT_ID), the developer's sign-in locally.
            services.AddSingleton<IJwtKeySource>(_ =>
                new AzureKeyVaultJwtKeySource(new Uri(vaultUri), jwt.KeyVaultKeyName, new DefaultAzureCredential()));
            services.AddSingleton<KeyVaultJwtSigningKeys>();
            services.AddSingleton<IJwtSigningKeys>(sp => sp.GetRequiredService<KeyVaultJwtSigningKeys>());
            services.AddHostedService<KeyVaultJwtKeyLoader>();
        }
        else
        {
            if (string.IsNullOrWhiteSpace(jwt.SigningKey))
            {
                if (!environment.IsDevelopment())
                {
                    throw new InvalidOperationException(
                        "No JWT signing key. Outside Development set Jwt:KeyVaultKeyName (with KeyVault:Uri) or Jwt:SigningKey.");
                }

                jwt.SigningKey = JwtOptions.DevelopmentSigningKey;
            }

            services.AddSingleton<IJwtSigningKeys>(new HmacJwtSigningKeys(jwt));
        }

        services.Configure<JwtOptions>(options =>
        {
            options.Issuer = jwt.Issuer;
            options.Audience = jwt.Audience;
            options.SigningKey = jwt.SigningKey;
            options.KeyId = jwt.KeyId;
            options.KeyVaultKeyName = jwt.KeyVaultKeyName;
            options.AccessTokenLifetime = jwt.AccessTokenLifetime;
            options.RefreshTokenLifetime = jwt.RefreshTokenLifetime;
        });

        services.AddSingleton(TimeProvider.System);
        // The test users of /auth/dev exist in Development only; the Cosmos DB directory adds them on first use.
        services.AddSingleton(new UserDirectorySeed(environment.IsDevelopment() ? DevUsers.All : []));
        services.AddCosmosOrInMemory<IUserDirectory, CosmosUserDirectory, InMemoryUserDirectory>(configuration);
        services.AddCosmosOrInMemory<IRefreshTokenStore, CosmosRefreshTokenStore, InMemoryRefreshTokenStore>(configuration);
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
                    NameClaimType = JwtRegisteredClaimNames.Name,
                    ClockSkew = TimeSpan.FromSeconds(30),
                };
            });
        services
            .AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
            .Configure<IJwtSigningKeys>((options, keys) =>
                options.TokenValidationParameters.IssuerSigningKeyResolver = (_, _, keyId, _) => keys.ValidationKeys(keyId));

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
