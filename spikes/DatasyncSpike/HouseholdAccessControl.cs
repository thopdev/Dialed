using System.Linq.Expressions;
using System.Security.Claims;
using CommunityToolkit.Datasync.Server;
using DatasyncSpike.Models;

namespace DatasyncSpike;

/// <summary>
/// Household scoping for every table: you only see and change data of households you belong to.
/// Note: on update the toolkit calls <see cref="IsAuthorizedAsync"/> with the EXISTING entity, so the incoming
/// entity (which could claim another household) is checked in <see cref="PreCommitHookAsync"/>.
/// </summary>
public sealed class HouseholdAccessControl<T>(IHttpContextAccessor httpContext) : AccessControlProvider<T>
    where T : HouseholdTableData<T>
{
    public const string HouseholdClaim = "household";

    private ClaimsPrincipal User => httpContext.HttpContext?.User ?? new ClaimsPrincipal();

    private string[] Households => [.. User.FindAll(HouseholdClaim).Select(claim => claim.Value)];

    public override Expression<Func<T, bool>>? GetDataView()
    {
        var households = Households;
        return entity => households.Contains(entity.HouseholdId);
    }

    public override ValueTask<bool> IsAuthorizedAsync(TableOperation operation, T? entity, CancellationToken cancellationToken = default)
    {
        if (User.Identity?.IsAuthenticated != true)
        {
            return ValueTask.FromResult(false);
        }

        // Reads and queries are limited by the data view; writes need membership of the entity's household.
        var allowed = operation is TableOperation.Query or TableOperation.Read
            || (entity is not null && Households.Contains(entity.HouseholdId));
        return ValueTask.FromResult(allowed);
    }

    public override ValueTask PreCommitHookAsync(TableOperation operation, T entity, CancellationToken cancellationToken = default)
    {
        if (!Households.Contains(entity.HouseholdId))
        {
            throw new HttpException(StatusCodes.Status403Forbidden, "Not a member of this household.");
        }

        // The toolkit calls this hook before the repository generates missing ids, so generate the packed id here.
        // (Offline clients always send their own ids.)
        if (operation == TableOperation.Create && string.IsNullOrEmpty(entity.Id))
        {
            entity.Id = HouseholdIds.New(entity.HouseholdId);
        }

        if (!HouseholdIds.TryGetHousehold(entity.Id, out var idHousehold) || idHousehold != entity.HouseholdId)
        {
            throw new HttpException(StatusCodes.Status400BadRequest, "Id must start with the entity's householdId.");
        }

        if (operation == TableOperation.Create)
        {
            entity.CreatedBy = User.FindFirstValue(ClaimTypes.NameIdentifier);
        }

        return ValueTask.CompletedTask;
    }
}
