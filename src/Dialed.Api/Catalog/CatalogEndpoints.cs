using Dialed.Api.Passkeys;
using Microsoft.Extensions.Options;

namespace Dialed.Api.Catalog;

public static class CatalogEndpoints
{
    public static IServiceCollection AddDialedCatalog(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<CatalogOptions>(configuration.GetSection(CatalogOptions.Section));
        services.AddSingleton<ICatalogStore, InMemoryCatalogStore>();
        services.AddSingleton<ICatalogRulesStore, InMemoryCatalogRulesStore>();
        services.AddSingleton<CatalogSource>();
        services.AddSingleton<CatalogService>();
        services.AddHostedService<CatalogRebuildWorker>();
        return services;
    }

    public static IEndpointRouteBuilder MapDialedCatalog(this IEndpointRouteBuilder endpoints)
    {
        // Signed-in only, at most 20 results, rate limited like the friend endpoints.
        var group = endpoints.MapGroup("/catalog").RequireAuthorization();
        group.MapGet("/roasters", (string? q, CatalogService catalog) => Results.Ok(catalog.SearchRoasters(q)));
        group.MapGet("/beans", (string? q, string? roaster, CatalogService catalog) => Results.Ok(catalog.SearchBeans(q, roaster)));

        var admin = endpoints.MapGroup("/admin/catalog");
        admin.MapPost("/rebuild", async (HttpContext http, IOptions<BetaOptions> options, CatalogService catalog, ICatalogStore store, CancellationToken ct) =>
        {
            if (AdminKey.Check(http, options.Value) is { } refused)
            {
                return refused;
            }

            await catalog.RebuildAsync(ct);
            return Results.Ok(new { roasters = store.Current.Roasters.Count, beans = store.Current.Beans.Count, builtAt = store.BuiltAt });
        });
        admin.MapGet("/entries", (HttpContext http, IOptions<BetaOptions> options, CatalogService catalog, int? take) =>
            AdminKey.Check(http, options.Value) ?? Results.Ok(catalog.Entries(take ?? 100)));
        admin.MapPost("/entries/{id}/hide", async (string id, HttpContext http, IOptions<BetaOptions> options, CatalogService catalog, CancellationToken ct) =>
            AdminKey.Check(http, options.Value) ?? (await catalog.HideAsync(id, ct) ? Results.NoContent() : Results.NotFound()));
        admin.MapPost("/merge", async (CatalogMergeRequest request, HttpContext http, IOptions<BetaOptions> options, CatalogService catalog, CancellationToken ct) =>
            AdminKey.Check(http, options.Value) ?? (await catalog.MergeAsync(request.FromId, request.IntoId, ct) ? Results.NoContent() : Results.NotFound()));
        return endpoints;
    }
}
