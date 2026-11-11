using Dialed.Client.Account;
using Dialed.Client.Sync;

namespace Dialed.Client.Households;

/// <summary>
/// Household actions for the app. Joining or leaving changes which household the person's token and records belong to, so afterwards
/// the session is renewed, what is on this device moves into the new household and the next sync starts from scratch.
/// </summary>
public sealed class HouseholdManager(HouseholdApi api, AccountService account, SyncEngine engine, SyncCoordinator sync)
{
    /// <summary>Raised when the active household or its membership changed through this manager, so every view of it can reload.</summary>
    public event Action? Changed;

    public Task<HouseholdInfo> GetAsync(CancellationToken cancellationToken = default) => api.GetMineAsync(cancellationToken);

    /// <summary>Every household the person belongs to, the active one marked.</summary>
    public Task<IReadOnlyList<HouseholdSummary>> ListAsync(CancellationToken cancellationToken = default) => api.ListAsync(cancellationToken);

    /// <summary>
    /// Makes another household the active one: what is waiting is sent first (this device only holds one household at a time),
    /// then the session is renewed for the new household and its data is pulled in.
    /// </summary>
    public async Task<HouseholdInfo> SwitchAsync(string householdId, CancellationToken cancellationToken = default)
    {
        await SendPendingAsync(cancellationToken);
        var household = await api.SwitchAsync(householdId, cancellationToken);
        await SwitchedAsync(household.Id, cancellationToken);
        return household;
    }

    /// <summary>Creates a household owned by the person and moves to it, like <see cref="SwitchAsync"/> (what is waiting is sent first).</summary>
    public async Task<HouseholdInfo> CreateAsync(string name, CancellationToken cancellationToken = default)
    {
        await SendPendingAsync(cancellationToken);
        var household = await api.CreateAsync(name, cancellationToken);
        await SwitchedAsync(household.Id, cancellationToken);
        return household;
    }

    /// <summary>The owner renames the active household; nothing changes for this device's data.</summary>
    public async Task<HouseholdInfo> RenameAsync(string name, CancellationToken cancellationToken = default)
    {
        var household = await api.RenameAsync(name, cancellationToken);
        Changed?.Invoke();
        return household;
    }

    /// <summary>The owner deletes the active household with its data; this device moves to the household that is active now and drops the deleted one's records.</summary>
    public async Task<HouseholdInfo> DeleteAsync(CancellationToken cancellationToken = default)
    {
        var household = await api.DeleteAsync(cancellationToken);
        await SwitchedAsync(household.Id, cancellationToken);
        return household;
    }

    public Task<InviteInfo> CreateInviteAsync(int maxUses = 1, int? daysValid = null, CancellationToken cancellationToken = default) =>
        api.CreateInviteAsync(maxUses, daysValid, cancellationToken);

    /// <summary>The owner's open invites (#71).</summary>
    public Task<IReadOnlyList<InviteInfo>> ListInvitesAsync(CancellationToken cancellationToken = default) => api.ListInvitesAsync(cancellationToken);

    /// <summary>The owner withdraws an open invite (#71).</summary>
    public Task RevokeInviteAsync(string code, CancellationToken cancellationToken = default) => api.RevokeInviteAsync(code, cancellationToken);

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
    public async Task<HouseholdInfo> TransferOwnershipAsync(string userId, CancellationToken cancellationToken = default)
    {
        var household = await api.TransferOwnershipAsync(userId, cancellationToken);
        Changed?.Invoke();
        return household;
    }

    /// <summary>This device holds one household at a time, so what is waiting must reach the server before it moves to another.</summary>
    private async Task SendPendingAsync(CancellationToken cancellationToken)
    {
        await sync.SyncNowAsync(cancellationToken);
        if (await engine.PendingCountAsync(cancellationToken) > 0)
        {
            throw new AccountApiException("Some changes have not been sent yet. Wait until they are synced, then switch.");
        }
    }

    private async Task SwitchedAsync(string householdId, CancellationToken cancellationToken)
    {
        // The token carries the household: get one for the new household first.
        await account.RefreshSessionAsync(cancellationToken);

        // Versions and pull positions belong to the old household, what others shared there goes, and the new one is pulled in full.
        await engine.SwitchHouseholdAsync(householdId, cancellationToken);
        sync.RequestSync();
        Changed?.Invoke();
    }
}
