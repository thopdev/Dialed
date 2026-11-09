using System.Security.Claims;
using System.Text.Json.Nodes;
using Dialed.Api.Auth;
using Microsoft.IdentityModel.JsonWebTokens;

namespace Dialed.Api.Sync;

/// <summary>A record as sent over the wire: the entity JSON and the server's version token.</summary>
public sealed record SyncRecordDto(JsonObject Entity, string Version);

public sealed record SyncPageDto(IReadOnlyList<SyncRecordDto> Records, bool HasMore);

public static class SyncEndpoints
{
    /// <summary>Tables the app may sync. Anything else is a 404, so typos and probing do not create data.</summary>
    public static readonly HashSet<string> Tables = new(StringComparer.Ordinal)
    {
        "beans", "bags", "brews", "recipes", "equipment", "vendors", "photos", "methoddefaults",
    };

    public static IServiceCollection AddDialedSync(this IServiceCollection services)
    {
        services.AddSingleton<ISyncStore, InMemorySyncStore>();
        services.AddSingleton<SyncPurgeWorker>();
        services.AddHostedService(sp => sp.GetRequiredService<SyncPurgeWorker>());
        return services;
    }

    /// <summary>
    /// <c>GET /sync/{table}</c> pulls changes after a watermark; <c>PUT /sync/{table}/{id}</c> pushes one record with <c>If-Match</c>.
    /// Everything is scoped to the household in the caller's token: there is no way to name another one.
    /// </summary>
    public static IEndpointRouteBuilder MapDialedSync(this IEndpointRouteBuilder endpoints)
    {
        var sync = endpoints.MapGroup("/sync").RequireAuthorization();

        sync.MapGet("/{table}", async (string table, ClaimsPrincipal user, ISyncStore store, DateTimeOffset? afterUpdatedAt, string? afterId, int? pageSize, CancellationToken ct) =>
        {
            if (!Tables.Contains(table) || Household(user) is not { } household)
            {
                return Results.NotFound();
            }

            var viewer = user.FindFirstValue(JwtRegisteredClaimNames.Sub) ?? "";
            var (rows, hasMore) = await store.PullAsync(household, viewer, table, afterUpdatedAt, afterId, pageSize ?? 100, ct);
            return Results.Ok(new SyncPageDto([.. rows.Select(ToDto)], hasMore));
        });

        sync.MapPut("/{table}/{id}", async (string table, string id, JsonObject entity, ClaimsPrincipal user, ISyncStore store, HttpRequest request, CancellationToken ct) =>
        {
            if (!Tables.Contains(table) || Household(user) is not { } household)
            {
                return Results.NotFound();
            }

            if (SyncPayloadValidator.Validate(id, entity) is { Count: > 0 } problems)
            {
                return Results.ValidationProblem(problems);
            }

            var ifMatch = request.Headers.IfMatch.ToString().Trim('"');
            var userId = user.FindFirstValue(JwtRegisteredClaimNames.Sub) ?? "";
            var result = await store.PushAsync(household, userId, table, id, entity, string.IsNullOrEmpty(ifMatch) ? null : ifMatch, ct);

            return result.Outcome switch
            {
                StoreOutcome.Stored => Results.Ok(ToDto(result.Row!)),
                StoreOutcome.VersionMismatch => Results.Json(ToDto(result.Row!), statusCode: StatusCodes.Status409Conflict),
                StoreOutcome.Forbidden => Results.Json(new { error = result.Message }, statusCode: StatusCodes.Status403Forbidden),
                _ => Results.BadRequest(new { error = result.Message }),
            };
        });

        return endpoints;
    }

    private static string? Household(ClaimsPrincipal user) =>
        user.FindFirstValue(TokenIssuer.HouseholdClaim) is { Length: > 0 } h ? h : null;

    private static SyncRecordDto ToDto(SyncRow row) => new(row.Entity, row.Version.ToString());
}
