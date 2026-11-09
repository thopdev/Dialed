using Dialed.Client.Account;
using Dialed.Client.Sync;

namespace Dialed.Client.Households;

/// <summary>
/// Household actions for the app. Joining or leaving changes which household the person's token and records belong to, so afterwards
/// the session is renewed, what is on this device moves into the new household and the next sync starts from scratch.
/// </summary>
public sealed class HouseholdManager(HouseholdApi api, AccountService account, SyncEngine engine, ISyncState state, SyncCoordinator sync)
{
    public Task<HouseholdInfo> GetAsync(CancellationToken cancellationToken = default) => api.GetMineAsync(cancellationToken);

    public Task<InviteInfo> CreateInviteAsync(CancellationToken cancellationToken = default) => api.CreateInviteAsync(cancellationToken);

    public async Task<HouseholdInfo> JoinAsync(string code, CancellationToken cancellationToken = default)
    {
        var household = await api.JoinAsync(code, cancellationToken);
        await SwitchedAsync(cancellationToken);
        return household;
    }

    public async Task<HouseholdInfo> LeaveAsync(CancellationToken cancellationToken = default)
    {
        var household = await api.LeaveAsync(cancellationToken);
        await SwitchedAsync(cancellationToken);
        return household;
    }

    /// <summary>The owner removes someone else; nothing changes for this device.</summary>
    public Task<HouseholdInfo> RemoveMemberAsync(string userId, CancellationToken cancellationToken = default) =>
        api.RemoveMemberAsync(userId, cancellationToken);

    private async Task SwitchedAsync(CancellationToken cancellationToken)
    {
        // The token carries the household: get one for the new household first.
        await account.RefreshSessionAsync(cancellationToken);

        // Versions and pull positions belong to the old household, so the new one is pulled in full.
        await state.ResetServerStateAsync(cancellationToken);
        await engine.AdoptLocalRecordsAsync(cancellationToken);
        sync.RequestSync();
    }
}
