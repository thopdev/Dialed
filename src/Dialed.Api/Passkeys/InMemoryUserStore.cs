using Microsoft.AspNetCore.Identity;

namespace Dialed.Api.Passkeys;

/// <summary>
/// Identity user and passkey store in memory (the Cosmos DB store from the passkey spike replaces it). Every read returns a copy and
/// saving a stale copy fails, like the real store. The user name is the email address, so one email index serves both lookups.
/// </summary>
public sealed class InMemoryUserStore(TimeProvider time) :
    IUserEmailStore<AccountUser>,
    IUserPasskeyStore<AccountUser>
{
    private readonly Lock gate = new();
    private readonly Dictionary<string, AccountUser> users = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> idByEmail = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> idByPasskey = new(StringComparer.Ordinal);

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

    public Task<IdentityResult> CreateAsync(AccountUser user, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(user.NormalizedEmail))
        {
            return Task.FromResult(IdentityResult.Failed(new IdentityErrorDescriber().InvalidEmail(user.Email)));
        }

        lock (gate)
        {
            if (idByEmail.ContainsKey(user.NormalizedEmail) || users.ContainsKey(user.Id))
            {
                return Task.FromResult(IdentityResult.Failed(new IdentityErrorDescriber().DuplicateEmail(user.Email ?? "")));
            }

            user.CreatedAt = time.GetUtcNow();
            user.Version = 1;
            users[user.Id] = user.Clone();
            idByEmail[user.NormalizedEmail] = user.Id;
            foreach (var passkey in user.Passkeys)
            {
                idByPasskey[Key(passkey.CredentialId)] = user.Id;
            }
        }

        return Task.FromResult(IdentityResult.Success);
    }

    public Task<IdentityResult> UpdateAsync(AccountUser user, CancellationToken ct)
    {
        lock (gate)
        {
            if (!users.TryGetValue(user.Id, out var current) || current.Version != user.Version)
            {
                return Task.FromResult(IdentityResult.Failed(new IdentityErrorDescriber().ConcurrencyFailure()));
            }

            user.Version++;
            users[user.Id] = user.Clone();
            foreach (var stale in idByPasskey.Where(p => p.Value == user.Id).Select(p => p.Key).ToList())
            {
                idByPasskey.Remove(stale);
            }

            foreach (var passkey in user.Passkeys)
            {
                idByPasskey[Key(passkey.CredentialId)] = user.Id;
            }
        }

        return Task.FromResult(IdentityResult.Success);
    }

    public Task<IdentityResult> DeleteAsync(AccountUser user, CancellationToken ct)
    {
        lock (gate)
        {
            if (users.Remove(user.Id, out var removed))
            {
                if (removed.NormalizedEmail is not null)
                {
                    idByEmail.Remove(removed.NormalizedEmail);
                }

                foreach (var passkey in removed.Passkeys)
                {
                    idByPasskey.Remove(Key(passkey.CredentialId));
                }
            }
        }

        return Task.FromResult(IdentityResult.Success);
    }

    public Task<AccountUser?> FindByIdAsync(string userId, CancellationToken ct)
    {
        lock (gate)
        {
            return Task.FromResult(users.TryGetValue(userId, out var user) ? user.Clone() : null);
        }
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

    public Task<AccountUser?> FindByEmailAsync(string normalizedEmail, CancellationToken ct)
    {
        lock (gate)
        {
            return Task.FromResult(idByEmail.TryGetValue(normalizedEmail, out var id) ? users[id].Clone() : null);
        }
    }

    public Task<string?> GetNormalizedEmailAsync(AccountUser user, CancellationToken ct) => Task.FromResult(user.NormalizedEmail);

    public Task SetNormalizedEmailAsync(AccountUser user, string? normalizedEmail, CancellationToken ct)
    {
        user.NormalizedEmail = normalizedEmail;
        return Task.CompletedTask;
    }

    // ---- IUserPasskeyStore ------------------------------------------------------------------
    // Changes are made on the user object and saved by UserManager's UpdateAsync, like the EF store.

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

    public Task<AccountUser?> FindByPasskeyIdAsync(byte[] credentialId, CancellationToken ct)
    {
        lock (gate)
        {
            return Task.FromResult(idByPasskey.TryGetValue(Key(credentialId), out var id) && users.TryGetValue(id, out var user) ? user.Clone() : null);
        }
    }

    public Task<UserPasskeyInfo?> FindPasskeyAsync(AccountUser user, byte[] credentialId, CancellationToken ct) =>
        Task.FromResult(user.Passkeys.Find(p => p.CredentialId.AsSpan().SequenceEqual(credentialId))?.ToInfo());

    public Task RemovePasskeyAsync(AccountUser user, byte[] credentialId, CancellationToken ct)
    {
        user.Passkeys.RemoveAll(p => p.CredentialId.AsSpan().SequenceEqual(credentialId));
        lock (gate)
        {
            idByPasskey.Remove(Key(credentialId));
        }

        return Task.CompletedTask;
    }

    public void Dispose()
    {
    }

    private static string Key(byte[] credentialId) => Convert.ToBase64String(credentialId);
}
