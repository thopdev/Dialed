using System.Security.Claims;
using Dialed.Api.Friends;
using Dialed.Api.Passkeys;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;

namespace Dialed.Api.PublicProfiles;

public sealed record PublicProfileRequest(bool Enabled);

public static class PublicProfileEndpoints
{
    public static IServiceCollection AddDialedPublicProfiles(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<ReportOptions>(configuration.GetSection(ReportOptions.Section));
        services.AddSingleton<IPublicProfileStore, InMemoryPublicProfileStore>();
        services.AddSingleton<PublicProfileService>();
        return services;
    }

    public static IEndpointRouteBuilder MapDialedPublicProfiles(this IEndpointRouteBuilder endpoints)
    {
        var profile = endpoints.MapGroup("/profile/public").RequireAuthorization();
        profile.MapGet("", async (ClaimsPrincipal user, PublicProfileService service, CancellationToken ct) => Results.Ok(await service.GetAsync(UserId(user), ct)));
        profile.MapPut("", async (PublicProfileRequest request, ClaimsPrincipal user, PublicProfileService service, CancellationToken ct) =>
            Results.Ok(await service.SetEnabledAsync(UserId(user), request.Enabled, ct)));

        // Find people by name: only public profiles, never more than 20, never anyone blocked.
        endpoints.MapGet("/people/search", async (string? q, ClaimsPrincipal user, PublicProfileService service, CancellationToken ct) =>
            Results.Ok(await service.SearchAsync(UserId(user), q, ct))).RequireAuthorization();

        endpoints.MapPost("/reports", async (ReportRequest request, ClaimsPrincipal user, PublicProfileService service, CancellationToken ct) =>
        {
            var result = await service.ReportAsync(UserId(user), request, ct);
            return result.Outcome switch
            {
                PublicOutcome.Ok => Results.NoContent(),
                PublicOutcome.Invalid => Results.ValidationProblem(new Dictionary<string, string[]> { ["report"] = [result.Message ?? "That report is not complete."] }),
                _ => Results.NotFound(),
            };
        }).RequireAuthorization();

        // Moderation by the owner of the service, with the same admin key as the beta invites.
        var admin = endpoints.MapGroup("/admin");
        admin.MapPost("/users/{id}/suspend", async (string id, HttpContext http, IOptions<BetaOptions> options, PublicProfileService service, CancellationToken ct) =>
            AdminKey.Check(http, options.Value) ?? (await service.SetSuspendedAsync(id, suspended: true, ct) ? Results.NoContent() : Results.NotFound()));
        admin.MapPost("/users/{id}/restore", async (string id, HttpContext http, IOptions<BetaOptions> options, PublicProfileService service, CancellationToken ct) =>
            AdminKey.Check(http, options.Value) ?? (await service.SetSuspendedAsync(id, suspended: false, ct) ? Results.NoContent() : Results.NotFound()));
        admin.MapGet("/users/suspended", async (HttpContext http, IOptions<BetaOptions> options, PublicProfileService service, CancellationToken ct) =>
            AdminKey.Check(http, options.Value) ?? Results.Ok(await service.SuspendedAsync(ct)));
        admin.MapGet("/reports", async (HttpContext http, IOptions<BetaOptions> options, PublicProfileService service, CancellationToken ct) =>
            AdminKey.Check(http, options.Value) ?? Results.Ok(await service.ReportsAsync(ct)));

        return endpoints;
    }

    private static string UserId(ClaimsPrincipal user) => user.FindFirstValue(JwtRegisteredClaimNames.Sub) ?? "";
}
