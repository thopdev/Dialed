using Dialed.Api.Configuration;
using Microsoft.AspNetCore.Identity;

namespace Dialed.Api.Passkeys;

public static class PasskeyExtensions
{
    /// <summary>Passkey sign-up and sign-in: Identity's passkey handler over our own user store and cookie-less ceremony state.</summary>
    public static IServiceCollection AddDialedPasskeys(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<PasskeyOptions>(configuration.GetSection(PasskeyOptions.SectionName));
        services.AddSingleton(sp => new OriginPolicy(
            AllowedOrigins.From(configuration),
            AllowedOrigins.Patterns(configuration),
            sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<PasskeyOptions>>()));
        services.AddSingleton<ICeremonyNonces, InMemoryCeremonyNonces>();
        services.AddScoped<PasskeyCeremonies>();
        services.AddScoped<RecoveryService>();
        // Lockout, send cooldown and email codes must hold across restarts and instances (#197): Cosmos DB when it is configured.
        if (CosmosConnection.From(configuration) is { } cosmos)
        {
            services.AddSingleton(_ => CosmosConnection.CreateClient(cosmos.ConnectionString));
            services.AddSingleton(sp => new CosmosRecoveryContainer(sp.GetRequiredService<Microsoft.Azure.Cosmos.CosmosClient>(), cosmos.Database));
            services.AddSingleton<IEmailRecoveryStore, CosmosEmailRecoveryStore>();
        }
        else
        {
            services.AddSingleton<IEmailRecoveryStore, InMemoryEmailRecoveryStore>();
        }

        services.AddSingleton<OutboxEmailSender>();
        services.AddSingleton<IEmailSender>(sp => sp.GetRequiredService<OutboxEmailSender>());

        // In Azure the key ring must be shared and persisted (Blob Storage + Key Vault), or ceremony state
        // minted by one replica cannot be opened by another.
        services.AddDataProtection();

        // One shared in-memory store (Identity would otherwise create one per request).
        services.AddSingleton<InMemoryUserStore>();
        services.AddIdentityCore<AccountUser>(options => options.User.RequireUniqueEmail = true);
        services.AddSingleton<IUserStore<AccountUser>>(sp => sp.GetRequiredService<InMemoryUserStore>());
        return services;
    }

    /// <summary>Maps the ceremony endpoints under <c>/auth</c>.</summary>
    public static IEndpointRouteBuilder MapDialedPasskeys(this IEndpointRouteBuilder endpoints)
    {
        var auth = endpoints.MapGroup("/auth");
        auth.MapPost("/signup/options", (HttpContext http, SignUpOptionsRequest request, PasskeyCeremonies ceremonies) => ceremonies.SignUpOptionsAsync(http, request));
        auth.MapPost("/signup", (HttpContext http, CeremonyCompleteRequest request, PasskeyCeremonies ceremonies) => ceremonies.SignUpAsync(http, request));
        auth.MapPost("/recover/email", async (RecoverEmailRequest request, RecoveryService recovery, HttpContext http) =>
        {
            // Always the same answer, so this cannot be used to find out who has an account.
            await recovery.SendEmailCodeAsync(request.Email ?? "", http.RequestAborted);
            return Results.NoContent();
        });
        auth.MapPost("/recover/options", (HttpContext http, RecoverOptionsRequest request, PasskeyCeremonies ceremonies) => ceremonies.RecoverOptionsAsync(http, request));
        auth.MapPost("/recover", (HttpContext http, CeremonyCompleteRequest request, PasskeyCeremonies ceremonies) => ceremonies.RecoverAsync(http, request));
        auth.MapPost("/signin/options", (HttpContext http, PasskeyCeremonies ceremonies) => ceremonies.SignInOptionsAsync(http));
        auth.MapPost("/signin", (HttpContext http, CeremonyCompleteRequest request, PasskeyCeremonies ceremonies) => ceremonies.SignInAsync(http, request));
        return endpoints;
    }
}
