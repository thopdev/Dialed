using System.Security.Claims;
using Microsoft.IdentityModel.JsonWebTokens;

namespace Dialed.Api.Households;

public static class HouseholdEndpoints
{
    public static IServiceCollection AddDialedHouseholds(this IServiceCollection services, IHostEnvironment environment)
    {
        services.AddSingleton<IHouseholdStore>(_ => new InMemoryHouseholdStore(environment.IsDevelopment()
            ?
            [
                new Household("dev-household", "Alice and Bob", "dev-alice"),
                new Household("dev-household-carol", "Carol's household", "dev-carol"),
            ]
            : []));
        services.AddSingleton<HouseholdService>();
        return services;
    }

    /// <summary>
    /// The household of the signed-in person. Joining, leaving and removal change which household the person belongs to; the access
    /// token carries the household, so the app must refresh (<c>POST /auth/refresh</c>) after one of them to get a token for the new one.
    /// </summary>
    public static IEndpointRouteBuilder MapDialedHouseholds(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/households/mine").RequireAuthorization();

        group.MapGet("", async (ClaimsPrincipal user, HouseholdService service, CancellationToken ct) =>
            Map(await service.GetMineAsync(UserId(user), ct)));

        group.MapPost("/invites", async (ClaimsPrincipal user, HouseholdService service, CancellationToken ct) =>
            Map(await service.CreateInviteAsync(UserId(user), ct)));

        group.MapPost("/join", async (JoinRequest request, ClaimsPrincipal user, HouseholdService service, CancellationToken ct) =>
            Map(await service.JoinAsync(UserId(user), request.Code, ct)));

        group.MapPost("/leave", async (ClaimsPrincipal user, HouseholdService service, CancellationToken ct) =>
            Map(await service.LeaveAsync(UserId(user), ct)));

        group.MapPost("/transfer", async (TransferRequest request, ClaimsPrincipal user, HouseholdService service, CancellationToken ct) =>
            Map(await service.TransferOwnershipAsync(UserId(user), request.UserId, ct)));

        group.MapDelete("/members/{memberId}", async (string memberId, ClaimsPrincipal user, HouseholdService service, CancellationToken ct) =>
            Map(await service.RemoveMemberAsync(UserId(user), memberId, ct)));

        return endpoints;
    }

    private static string UserId(ClaimsPrincipal user) => user.FindFirstValue(JwtRegisteredClaimNames.Sub) ?? "";

    private static IResult Map<T>(HouseholdResult<T> result) => result.Outcome switch
    {
        HouseholdOutcome.Ok => Results.Ok(result.Value),
        HouseholdOutcome.NotOwner => Results.Problem(detail: "Only the household owner can do that.", statusCode: StatusCodes.Status403Forbidden),
        HouseholdOutcome.InvalidInvite => Results.Problem(detail: "That invite code is not valid. Ask for a new one.", statusCode: StatusCodes.Status400BadRequest),
        HouseholdOutcome.Conflict => Results.Problem(detail: result.Message, statusCode: StatusCodes.Status409Conflict),
        _ => Results.NotFound(),
    };
}
