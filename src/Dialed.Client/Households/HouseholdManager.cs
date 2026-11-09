using Dialed.Client.Account;
using Dialed.Client.Sync;

namespace Dialed.Client.Households;

/// <summary>
/// Household actions for the app. Joining or leaving changes which household the person's token and records belong to, so afterwards
/// the session is renewed, what is on this device moves into the new household and the next sync starts from scratch.
/// </summary>
public sealed class HouseholdManager(HouseholdApi api, AccountService account, SyncEngine engine, SyncCoordinator sync)
{
    public Task<HouseholdInfo> GetAsync(CancellationToken cancellationToken = default) => api.GetMineAsync(cancellationToken);

    public Task<InviteInfo> CreateInviteAsync(CancellationToken cancellationToken = default) => api.CreateInviteAsync(cancellationToken);

    public async Task<HouseholdInfo> JoinAsync(string code, CancellationToken cancellationToken = default)
    {
        var household = await api.JoinAsync(code, cancellationToken);
        await SwitchedAsync(household.Id, cancellationToken);
        return household;
    }

    public async Task<HouseholdInfo> LeaveAsync(CancellationToken cancellationToken = default)
    {
        var household = await api.LeaveAsync(cancellationToken);
        await SwitchedAsync(household.Id, cancellationToken);
        return household;
    }

    /// <summary>The owner removes someone else; nothing changes for this device.</summary>
    public Task<HouseholdInfo> RemoveMemberAsync(string userId, CancellationToken cancellationToken = default) =>
        api.RemoveMemberAsync(userId, cancellationToken);

    /// <summary>The owner hands the household to another member; nothing changes for this device's data.</summary>
    public Task<HouseholdInfo> TransferOwnershipAsync(string userId, CancellationToken cancellationToken = default) =>
        api.TransferOwnershipAsync(userId, cancellationToken);

    private async Task SwitchedAsync(string householdId, CancellationToken cancellationToken)
    {
        // The token carries the household: get one for the new household first.
        await account.RefreshSessionAsync(cancellationToken);

        // Versions and pull positions belong to the old household, what others shared there goes, and the new one is pulled in full.
        await engine.SwitchHouseholdAsync(householdId, cancellationToken);
        sync.RequestSync();
    }
}
