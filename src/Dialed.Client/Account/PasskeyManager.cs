using Dialed.Client.Platform;

namespace Dialed.Client.Account;

/// <summary>What the person can do with their passkeys and recovery codes once signed in (#112).</summary>
public sealed class PasskeyManager(AccountDataApi api, IPasskeyClient passkeys, AccountService account)
{
    /// <summary>Asked of the server when the list loads: the account looks safe once it has a second passkey or a synced one.</summary>
    public static bool NeedsSecondPasskey(RecoveryStatus status) => status.Passkeys < 2 && !status.HasBackedUpPasskey;

    public async Task<(IReadOnlyList<PasskeyInfo> Passkeys, RecoveryStatus? Status, AccountResult Result)> LoadAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var list = await api.ListPasskeysAsync(cancellationToken);
            var status = await api.RecoveryStatusAsync(cancellationToken);
            return (list, status, AccountResult.Ok);
        }
        catch (AccountRefusedException exception) when (exception.Status == 404)
        {
            return ([], null, new AccountResult(AccountOutcome.NoAccount));
        }
        catch (Exception exception) when (exception is AccountApiException or AccountRefusedException)
        {
            return ([], null, Failed(exception));
        }
    }

    public Task<AccountResult> RenameAsync(string id, string name, CancellationToken cancellationToken = default) =>
        RunAsync(() => api.RenamePasskeyAsync(id, name, cancellationToken));

    public Task<AccountResult> RemoveAsync(string id, CancellationToken cancellationToken = default) =>
        RunAsync(() => api.RemovePasskeyAsync(id, cancellationToken));

    /// <summary>Makes another passkey on this device (or the one the platform picks, such as a phone or a security key).</summary>
    public async Task<AccountResult> AddAsync(string name, CancellationToken cancellationToken = default)
    {
        if (!await passkeys.IsSupportedAsync())
        {
            return new AccountResult(AccountOutcome.NotSupported);
        }

        try
        {
            var ceremony = await api.AddPasskeyOptionsAsync(cancellationToken);
            var outcome = await passkeys.CreateAsync(ceremony.OptionsJson);
            switch (outcome.Status)
            {
                case PasskeyStatus.Cancelled:
                    return new AccountResult(AccountOutcome.Cancelled);
                case PasskeyStatus.NotSupported:
                    return new AccountResult(AccountOutcome.NotSupported);
                case not PasskeyStatus.Success:
                    return new AccountResult(AccountOutcome.Unavailable, outcome.Error);
            }

            await api.AddPasskeyAsync(ceremony.State, outcome.ResponseJson!, name, cancellationToken);
            return AccountResult.Ok;
        }
        catch (Exception exception) when (exception is AccountApiException or AccountRefusedException)
        {
            return Failed(exception);
        }
    }

    /// <summary>Replaces all recovery codes; the new ones are shown once through <see cref="AccountService.PendingRecoveryCodes"/>.</summary>
    public async Task<AccountResult> RegenerateCodesAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            account.ShowRecoveryCodes(await api.RegenerateRecoveryCodesAsync(cancellationToken));
            return AccountResult.Ok;
        }
        catch (Exception exception) when (exception is AccountApiException or AccountRefusedException)
        {
            return Failed(exception);
        }
    }

    private static async Task<AccountResult> RunAsync(Func<Task> action)
    {
        try
        {
            await action();
            return AccountResult.Ok;
        }
        catch (Exception exception) when (exception is AccountApiException or AccountRefusedException)
        {
            return Failed(exception);
        }
    }

    private static AccountResult Failed(Exception exception) =>
        exception is AccountRefusedException
            ? new AccountResult(AccountOutcome.Refused, exception.Message)
            : new AccountResult(AccountOutcome.Unavailable, exception.Message);
}
