using System.Security.Claims;
using Microsoft.IdentityModel.JsonWebTokens;

namespace Dialed.Api.Friends;

public static class FriendEndpoints
{
    public static IServiceCollection AddDialedFriends(this IServiceCollection services)
    {
        services.AddSingleton<IFriendStore, InMemoryFriendStore>();
        services.AddSingleton<FriendService>();
        return services;
    }

    /// <summary>Online-only endpoints for following friends (#74). There is deliberately no way to search for people.</summary>
    public static IEndpointRouteBuilder MapDialedFriends(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/friends").RequireAuthorization();

        group.MapGet("/code", async (ClaimsPrincipal user, FriendService service, CancellationToken ct) => Results.Ok(await service.GetCodeAsync(UserId(user), ct)));
        group.MapPost("/code/rotate", async (ClaimsPrincipal user, FriendService service, CancellationToken ct) => Results.Ok(await service.RotateCodeAsync(UserId(user), ct)));

        group.MapPost("/follow", async (FollowRequest request, ClaimsPrincipal user, FriendService service, CancellationToken ct) =>
            Map(await service.FollowAsync(UserId(user), request.Code, ct)));

        group.MapGet("/requests", async (ClaimsPrincipal user, FriendService service, CancellationToken ct) => Results.Ok(await service.RequestsAsync(UserId(user), ct)));
        group.MapPost("/requests/{followerId}/accept", async (string followerId, ClaimsPrincipal user, FriendService service, CancellationToken ct) =>
            Map(await service.AcceptAsync(UserId(user), followerId, ct)));
        group.MapPost("/requests/{followerId}/decline", async (string followerId, ClaimsPrincipal user, FriendService service, CancellationToken ct) =>
            Done(await service.DeclineAsync(UserId(user), followerId, ct)));

        group.MapGet("/followers", async (ClaimsPrincipal user, FriendService service, CancellationToken ct) => Results.Ok(await service.FollowersAsync(UserId(user), ct)));
        group.MapGet("/following", async (ClaimsPrincipal user, FriendService service, CancellationToken ct) => Results.Ok(await service.FollowingAsync(UserId(user), ct)));
        group.MapGet("/following/pending", async (ClaimsPrincipal user, FriendService service, CancellationToken ct) => Results.Ok(await service.PendingFollowingAsync(UserId(user), ct)));
        group.MapDelete("/following/{followeeId}", async (string followeeId, ClaimsPrincipal user, FriendService service, CancellationToken ct) =>
            Done(await service.UnfollowAsync(UserId(user), followeeId, ct)));
        group.MapDelete("/followers/{followerId}", async (string followerId, ClaimsPrincipal user, FriendService service, CancellationToken ct) =>
            Done(await service.RemoveFollowerAsync(UserId(user), followerId, ct)));

        group.MapGet("/blocks", async (ClaimsPrincipal user, FriendService service, CancellationToken ct) => Results.Ok(await service.BlockedAsync(UserId(user), ct)));
        group.MapPost("/blocks/{otherId}", async (string otherId, ClaimsPrincipal user, FriendService service, CancellationToken ct) =>
            Done(await service.BlockAsync(UserId(user), otherId, ct)));
        group.MapDelete("/blocks/{otherId}", async (string otherId, ClaimsPrincipal user, FriendService service, CancellationToken ct) =>
            Done(await service.UnblockAsync(UserId(user), otherId, ct)));

        group.MapGet("/{ownerId}/profile", async (string ownerId, ClaimsPrincipal user, FriendService service, CancellationToken ct) =>
            await service.ProfileAsync(UserId(user), ownerId, ct) is { } profile ? Results.Ok(profile) : Results.NotFound());
        group.MapGet("/{ownerId}/shared/{table}", async (string ownerId, string table, int? skip, int? pageSize, ClaimsPrincipal user, FriendService service, CancellationToken ct) =>
            await service.SharedAsync(UserId(user), ownerId, table, skip ?? 0, pageSize ?? 20, ct) is { } page ? Results.Ok(page) : Results.NotFound());

        group.MapGet("/{ownerId}/shared/{table}/{id}", async (string ownerId, string table, string id, ClaimsPrincipal user, FriendService service, CancellationToken ct) =>
            await service.SharedItemAsync(UserId(user), ownerId, table, id, ct) is { } item ? Results.Ok(item) : Results.NotFound());

        return endpoints;
    }

    private static string UserId(ClaimsPrincipal user) => user.FindFirstValue(JwtRegisteredClaimNames.Sub) ?? "";

    private static IResult Done(bool done) => done ? Results.NoContent() : Results.NotFound();

    private static IResult Map<T>(FriendResult<T> result) => result.Outcome switch
    {
        FriendOutcome.Ok => Results.Ok(result.Value),
        FriendOutcome.Conflict => Results.Problem(detail: result.Message, statusCode: StatusCodes.Status409Conflict),
        _ => Results.Problem(detail: result.Message ?? "Not found.", statusCode: StatusCodes.Status404NotFound),
    };
}
