using Dialed.Client.Sync;

namespace Dialed.Client.Account;

/// <summary>Gives sync the access token of the signed-in person and renews it shortly before it expires.</summary>
public sealed class SessionTokenProvider(AuthSession session, AccountApi api, TimeProvider time) : IAccessTokenProvider
{
    private static readonly TimeSpan RenewBefore = TimeSpan.FromMinutes(1);

    private readonly SemaphoreSlim gate = new(1, 1);

    public string? HouseholdId => session.Tokens?.HouseholdId;

    public Task<string?> GetAccessTokenAsync(CancellationToken cancellationToken = default) => GetAsync(refusedToken: null, cancellationToken);

    /// <summary>The server refused this token (it expired during a long sync, or the clock is off): renew now, unless someone already did.</summary>
    public Task<string?> RenewAccessTokenAsync(string? refusedToken, CancellationToken cancellationToken = default) => GetAsync(refusedToken, cancellationToken);

    private async Task<string?> GetAsync(string? refusedToken, CancellationToken cancellationToken)
    {
        if (session.Tokens is not { } tokens)
        {
            return null;
        }

        if (IsUsable(tokens, refusedToken))
        {
            return tokens.AccessToken;
        }

        await gate.WaitAsync(cancellationToken);
        try
        {
            // Another caller may have renewed it while this one waited.
            tokens = session.Tokens;
            if (tokens is null)
            {
                return null;
            }

            if (IsUsable(tokens, refusedToken))
            {
                return tokens.AccessToken;
            }

            try
            {
                var renewed = await api.RefreshAsync(tokens.RefreshToken, cancellationToken);
                if (renewed is null)
                {
                    // The refresh token is no longer valid (signed out elsewhere, expired or reused): sign in again.
                    await session.ClearAsync(cancellationToken);
                    return null;
                }

                await session.SetAsync(renewed, cancellationToken);
                return renewed.AccessToken;
            }
            catch (AccountApiException)
            {
                // Offline or server trouble: keep the sign-in, the sync fails now and tries again later.
                throw new SyncTransportException("Could not renew the sign-in.");
            }
        }
        finally
        {
            gate.Release();
        }
    }

    // Fresh enough, and not the token the server just refused.
    private bool IsUsable(SessionTokens tokens, string? refusedToken) =>
        tokens.AccessToken != refusedToken && tokens.AccessTokenExpiresAt - RenewBefore > time.GetUtcNow();
}
