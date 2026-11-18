using Microsoft.AspNetCore.Identity;

namespace Dialed.Api.Passkeys;

/// <summary>
/// What every Identity user store shares: the property accessors and the passkey changes on the user object, which
/// UserManager then saves with <see cref="UpdateAsync"/> (like the EF store). The user name is the email address, so one
/// email lookup serves both. Subclasses only keep the users: <see cref="InMemoryUserStore"/> and <see cref="CosmosUserStore"/>.
/// </summary>
public abstract class AccountUserStore(TimeProvider time) :
    IUserEmailStore<AccountUser>,
    IUserPasskeyStore<AccountUser>
{
    protected TimeProvider Time => time;

    // ---- Saving and finding (per store) -----------------------------------------------------

    public abstract Task<IdentityResult> CreateAsync(AccountUser user, CancellationToken ct);

    /// <summary>Saves the user; a stale copy (another save happened since it was read) fails with a concurrency failure.</summary>
    public abstract Task<IdentityResult> UpdateAsync(AccountUser user, CancellationToken ct);

    public abstract Task<IdentityResult> DeleteAsync(AccountUser user, CancellationToken ct);

    public abstract Task<AccountUser?> FindByIdAsync(string userId, CancellationToken ct);

    public abstract Task<AccountUser?> FindByEmailAsync(string normalizedEmail, CancellationToken ct);

    public abstract Task<AccountUser?> FindByPasskeyIdAsync(byte[] credentialId, CancellationToken ct);

    /// <summary>The passkey was taken off the user object; drop its lookup right away.</summary>
    protected abstract Task OnPasskeyRemovedAsync(AccountUser user, byte[] credentialId, CancellationToken ct);

    // ---- IUserStore -------------------------------------------------------------------------

    public Task<string> GetUserIdAsync(AccountUser user, CancellationToken ct) => Task.FromResult(user.Id);

    public Task<string?> GetUserNameAsync(AccountUser user, CancellationToken ct) => Task.FromResult(user.UserName);

    public Task SetUserNameAsync(AccountUser user, string? userName, CancellationToken ct)
    {
        user.UserName = userName;
        return Task.CompletedTask;
    }

    public Task<string?> GetNormalizedUserNameAsync(AccountUser user, CancellationToken ct) => Task.FromResult(user.NormalizedUserName);

    public Task SetNormalizedUserNameAsync(AccountUser user, string? normalizedName, CancellationToken ct)
    {
        user.NormalizedUserName = normalizedName;
        return Task.CompletedTask;
    }

    public Task<AccountUser?> FindByNameAsync(string normalizedUserName, CancellationToken ct) => FindByEmailAsync(normalizedUserName, ct);

    // ---- IUserEmailStore --------------------------------------------------------------------

    public Task SetEmailAsync(AccountUser user, string? email, CancellationToken ct)
    {
        user.Email = email;
        return Task.CompletedTask;
    }

    public Task<string?> GetEmailAsync(AccountUser user, CancellationToken ct) => Task.FromResult(user.Email);

    public Task<bool> GetEmailConfirmedAsync(AccountUser user, CancellationToken ct) => Task.FromResult(user.EmailConfirmed);

    public Task SetEmailConfirmedAsync(AccountUser user, bool confirmed, CancellationToken ct)
    {
        user.EmailConfirmed = confirmed;
        return Task.CompletedTask;
    }

    public Task<string?> GetNormalizedEmailAsync(AccountUser user, CancellationToken ct) => Task.FromResult(user.NormalizedEmail);

    public Task SetNormalizedEmailAsync(AccountUser user, string? normalizedEmail, CancellationToken ct)
    {
        user.NormalizedEmail = normalizedEmail;
        return Task.CompletedTask;
    }

    // ---- IUserPasskeyStore ------------------------------------------------------------------

    public Task AddOrUpdatePasskeyAsync(AccountUser user, UserPasskeyInfo passkey, CancellationToken ct)
    {
        var existing = user.Passkeys.Find(p => p.CredentialId.AsSpan().SequenceEqual(passkey.CredentialId));
        if (existing is null)
        {
            user.Passkeys.Add(StoredPasskey.From(passkey));
        }
        else
        {
            existing.Name = passkey.Name;
            existing.SignCount = passkey.SignCount;
            existing.IsUserVerified = passkey.IsUserVerified;
            existing.IsBackedUp = passkey.IsBackedUp;
            existing.LastUsedAt = time.GetUtcNow();
        }

        return Task.CompletedTask;
    }

    public Task<IList<UserPasskeyInfo>> GetPasskeysAsync(AccountUser user, CancellationToken ct) =>
        Task.FromResult<IList<UserPasskeyInfo>>([.. user.Passkeys.Select(p => p.ToInfo())]);

    public Task<UserPasskeyInfo?> FindPasskeyAsync(AccountUser user, byte[] credentialId, CancellationToken ct) =>
        Task.FromResult(user.Passkeys.Find(p => p.CredentialId.AsSpan().SequenceEqual(credentialId))?.ToInfo());

    public Task RemovePasskeyAsync(AccountUser user, byte[] credentialId, CancellationToken ct)
    {
        user.Passkeys.RemoveAll(p => p.CredentialId.AsSpan().SequenceEqual(credentialId));
        return OnPasskeyRemovedAsync(user, credentialId, ct);
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
    }

    protected static bool HasPasskey(AccountUser user, byte[] credentialId) =>
        user.Passkeys.Exists(p => p.CredentialId.AsSpan().SequenceEqual(credentialId));
}
