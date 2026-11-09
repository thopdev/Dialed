using Dialed.Client.Platform;
using Dialed.Client.Storage;

namespace Dialed.Client.Account;

/// <summary>Export your data (#36) and delete the account (#35).</summary>
public sealed class AccountDataService(AccountDataApi api, IFileExport files, IPasskeyClient passkeys, AuthSession session, TimeProvider time, ILocalStore? store = null)
{
    public async Task<AccountResult> ExportAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var bytes = await api.ExportAsync(cancellationToken);
            var name = $"dialed-export-{time.GetUtcNow():yyyy-MM-dd}.json";
            return await files.SaveAsync(name, "application/json", bytes) ? AccountResult.Ok : new AccountResult(AccountOutcome.Cancelled);
        }
        catch (AccountRefusedException exception)
        {
            return new AccountResult(AccountOutcome.Refused, exception.Message);
        }
        catch (AccountApiException exception)
        {
            return new AccountResult(AccountOutcome.Unavailable, exception.Message);
        }
    }

    /// <summary>
    /// Asks for the passkey again, deletes the account on the server and then signs out and wipes what this device holds.
    /// Nothing is wiped here unless the server confirmed the deletion.
    /// </summary>
    public async Task<AccountResult> DeleteAccountAsync(CancellationToken cancellationToken = default)
    {
        if (!await passkeys.IsSupportedAsync())
        {
            return new AccountResult(AccountOutcome.NotSupported);
        }

        try
        {
            var ceremony = await api.DeleteOptionsAsync(cancellationToken);
            var outcome = await passkeys.GetAsync(ceremony.OptionsJson);
            switch (outcome.Status)
            {
                case PasskeyStatus.Cancelled:
                    return new AccountResult(AccountOutcome.Cancelled);
                case PasskeyStatus.NotSupported:
                    return new AccountResult(AccountOutcome.NotSupported);
                case not PasskeyStatus.Success:
                    return new AccountResult(AccountOutcome.Unavailable, outcome.Error);
            }

            await api.DeleteAsync(ceremony.State, outcome.ResponseJson!, cancellationToken);
        }
        catch (AccountRefusedException exception)
        {
            return new AccountResult(AccountOutcome.Refused, exception.Message);
        }
        catch (AccountApiException exception)
        {
            return new AccountResult(AccountOutcome.Unavailable, exception.Message);
        }

        await session.ClearAsync(cancellationToken);
        if (store is not null)
        {
            await store.ClearAsync(cancellationToken);
        }

        return AccountResult.Ok;
    }
}
