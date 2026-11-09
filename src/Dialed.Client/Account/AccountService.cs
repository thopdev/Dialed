using Dialed.Client.Platform;
using Dialed.Client.Sync;

namespace Dialed.Client.Account;

/// <summary>Sign-up, sign-in and sign-out with passkeys: runs the ceremony between the platform and the API.</summary>
public sealed class AccountService(AccountApi api, IPasskeyClient passkeys, AuthSession session, SyncEngine engine, SyncCoordinator sync)
{
    public async Task<AccountResult> SignUpAsync(string email, string displayName, string? inviteCode = null, CancellationToken cancellationToken = default)
    {
        if (!await passkeys.IsSupportedAsync())
        {
            return new AccountResult(AccountOutcome.NotSupported);
        }

        return await RunAsync(
            () => api.SignUpOptionsAsync(email, displayName, inviteCode, cancellationToken),
            options => passkeys.CreateAsync(options),
            (state, credential) => api.SignUpAsync(state, credential, cancellationToken),
            cancellationToken);
    }

    public async Task<AccountResult> SignInAsync(CancellationToken cancellationToken = default)
    {
        if (!await passkeys.IsSupportedAsync())
        {
            return new AccountResult(AccountOutcome.NotSupported);
        }

        return await RunAsync(
            () => api.SignInOptionsAsync(cancellationToken),
            options => passkeys.GetAsync(options),
            (state, credential) => api.SignInAsync(state, credential, cancellationToken),
            cancellationToken);
    }

    /// <summary>Gets a new token pair now (for example after the household changed, since the token carries it). Signs out when the server no longer accepts the session.</summary>
    public async Task RefreshSessionAsync(CancellationToken cancellationToken = default)
    {
        if (session.Tokens is not { } tokens)
        {
            return;
        }

        var renewed = await api.RefreshAsync(tokens.RefreshToken, cancellationToken);
        if (renewed is null)
        {
            await session.ClearAsync(cancellationToken);
            return;
        }

        await session.SetAsync(renewed, cancellationToken);
    }

    /// <summary>Signs out of this device. The server is told on a best-effort basis; local sign-out always happens.</summary>
    public async Task SignOutAsync(CancellationToken cancellationToken = default)
    {
        var refreshToken = session.Tokens?.RefreshToken;
        await session.ClearAsync(cancellationToken);
        if (refreshToken is null)
        {
            return;
        }

        try
        {
            await api.SignOutAsync(refreshToken, cancellationToken);
        }
        catch (Exception exception) when (exception is AccountApiException or AccountRefusedException)
        {
            // Already signed out here; the refresh token expires on its own.
        }
    }

    private async Task<AccountResult> RunAsync(
        Func<Task<AccountApi.Ceremony>> start,
        Func<string, Task<PasskeyOutcome>> perform,
        Func<string, string, Task<SessionTokens>> complete,
        CancellationToken cancellationToken)
    {
        try
        {
            var ceremony = await start();
            var outcome = await perform(ceremony.OptionsJson);
            switch (outcome.Status)
            {
                case PasskeyStatus.Cancelled:
                    return new AccountResult(AccountOutcome.Cancelled);
                case PasskeyStatus.NotSupported:
                    return new AccountResult(AccountOutcome.NotSupported);
                case not PasskeyStatus.Success:
                    return new AccountResult(AccountOutcome.Unavailable, outcome.Error);
            }

            var tokens = await complete(ceremony.State, outcome.ResponseJson!);
            await session.SetAsync(tokens, cancellationToken);

            // What was made before signing in now belongs to this person's household, and goes up with the next sync.
            await engine.AdoptLocalRecordsAsync(cancellationToken);
            sync.RequestSync();
            return AccountResult.Ok;
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
}
