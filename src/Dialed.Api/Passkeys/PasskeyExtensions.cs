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
        auth.MapPost("/signin/options", (HttpContext http, PasskeyCeremonies ceremonies) => ceremonies.SignInOptionsAsync(http));
        auth.MapPost("/signin", (HttpContext http, CeremonyCompleteRequest request, PasskeyCeremonies ceremonies) => ceremonies.SignInAsync(http, request));
        return endpoints;
    }
}
