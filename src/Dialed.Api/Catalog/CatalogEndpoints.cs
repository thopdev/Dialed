using Dialed.Api.Data;
using Dialed.Api.Passkeys;
using Microsoft.Extensions.Options;

namespace Dialed.Api.Catalog;

public static class CatalogEndpoints
{
    public static IServiceCollection AddDialedCatalog(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<CatalogOptions>(configuration.GetSection(CatalogOptions.Section));
        services.AddCosmosOrInMemory<ICatalogStore, CosmosCatalogStore, InMemoryCatalogStore>(configuration);
        services.AddCosmosOrInMemory<ICatalogRulesStore, CosmosCatalogRulesStore, InMemoryCatalogRulesStore>(configuration);
        services.AddSingleton<CatalogSource>();
        services.AddSingleton<CatalogService>();
        services.AddHostedService<CatalogRebuildWorker>();
        return services;
    }

    public static IEndpointRouteBuilder MapDialedCatalog(this IEndpointRouteBuilder endpoints)
    {
        // Signed-in only, at most 20 results, rate limited like the friend endpoints.
        var group = endpoints.MapGroup("/catalog").RequireAuthorization();
        group.MapGet("/roasters", async (string? q, CatalogService catalog, CancellationToken ct) => Results.Ok(await catalog.SearchRoastersAsync(q, ct)));
        group.MapGet("/beans", async (string? q, string? roaster, CatalogService catalog, CancellationToken ct) => Results.Ok(await catalog.SearchBeansAsync(q, roaster, ct)));
        group.MapGet("/barcode", async (string? code, CatalogService catalog, CancellationToken ct) => Results.Ok(await catalog.FindByBarcodeAsync(code, ct)));

        var admin = endpoints.MapGroup("/admin/catalog");
        admin.MapPost("/rebuild", async (HttpContext http, IOptions<BetaOptions> options, CatalogService catalog, ICatalogStore store, CancellationToken ct) =>
        {
            if (AdminKey.Check(http, options.Value) is { } refused)
            {
                return refused;
            }

            await catalog.RebuildAsync(ct);
            var build = await store.GetAsync(ct);
            return Results.Ok(new { roasters = build.Catalog.Roasters.Count, beans = build.Catalog.Beans.Count, builtAt = build.BuiltAt });
        });
        admin.MapGet("/entries", async (HttpContext http, IOptions<BetaOptions> options, CatalogService catalog, int? take, CancellationToken ct) =>
            AdminKey.Check(http, options.Value) ?? Results.Ok(await catalog.EntriesAsync(take ?? 100, ct)));
        admin.MapPost("/entries/{id}/hide", async (string id, HttpContext http, IOptions<BetaOptions> options, CatalogService catalog, CancellationToken ct) =>
            AdminKey.Check(http, options.Value) ?? (await catalog.HideAsync(id, ct) ? Results.NoContent() : Results.NotFound()));
        admin.MapPost("/merge", async (CatalogMergeRequest request, HttpContext http, IOptions<BetaOptions> options, CatalogService catalog, CancellationToken ct) =>
            AdminKey.Check(http, options.Value) ?? (await catalog.MergeAsync(request.FromId, request.IntoId, ct) ? Results.NoContent() : Results.NotFound()));
        admin.MapGet("/hidden", async (HttpContext http, IOptions<BetaOptions> options, CatalogService catalog, CancellationToken ct) =>
            AdminKey.Check(http, options.Value) ?? Results.Ok(await catalog.HiddenEntriesAsync(ct)));
        admin.MapPost("/entries/{id}/unhide", async (string id, HttpContext http, IOptions<BetaOptions> options, CatalogService catalog, CancellationToken ct) =>
            AdminKey.Check(http, options.Value) ?? (await catalog.UnhideAsync(id, ct) ? Results.NoContent() : Results.NotFound()));
        admin.MapGet("/rules", async (HttpContext http, IOptions<BetaOptions> options, CatalogService catalog, CancellationToken ct) =>
            AdminKey.Check(http, options.Value) ?? Results.Ok(await catalog.RulesAsync(ct)));
        admin.MapPost("/unmerge", async (CatalogUnmergeRequest request, HttpContext http, IOptions<BetaOptions> options, CatalogService catalog, CancellationToken ct) =>
            AdminKey.Check(http, options.Value) ?? (await catalog.UnmergeAsync(request.KeyA, request.KeyB, ct) ? Results.NoContent() : Results.NotFound()));
        return endpoints;
    }
}
