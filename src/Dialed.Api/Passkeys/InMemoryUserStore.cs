using Microsoft.AspNetCore.Identity;

namespace Dialed.Api.Passkeys;

/// <summary>
/// The Identity user and passkey store in memory (tests and a bare <c>dotnet run</c>; <see cref="CosmosUserStore"/> otherwise).
/// Every read returns a copy and saving a stale copy fails, like the Cosmos DB store.
/// </summary>
public sealed class InMemoryUserStore(TimeProvider time) : AccountUserStore(time)
{
    private readonly Lock gate = new();
    private readonly Dictionary<string, AccountUser> users = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> idByEmail = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> idByPasskey = new(StringComparer.Ordinal);

    public override Task<IdentityResult> CreateAsync(AccountUser user, CancellationToken ct)
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

            user.CreatedAt = Time.GetUtcNow();
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

    public override Task<IdentityResult> UpdateAsync(AccountUser user, CancellationToken ct)
    {
        lock (gate)
        {
            if (!users.TryGetValue(user.Id, out var current) || current.Version != user.Version)
            {
                return Task.FromResult(IdentityResult.Failed(new IdentityErrorDescriber().ConcurrencyFailure()));
            }

            if (!string.IsNullOrEmpty(user.NormalizedEmail) && user.NormalizedEmail != current.NormalizedEmail)
            {
                if (idByEmail.ContainsKey(user.NormalizedEmail))
                {
                    return Task.FromResult(IdentityResult.Failed(new IdentityErrorDescriber().DuplicateEmail(user.Email ?? "")));
                }

                if (current.NormalizedEmail is not null)
                {
                    idByEmail.Remove(current.NormalizedEmail);
                }

                idByEmail[user.NormalizedEmail] = user.Id;
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

    public override Task<IdentityResult> DeleteAsync(AccountUser user, CancellationToken ct)
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

    public override Task<AccountUser?> FindByIdAsync(string userId, CancellationToken ct)
    {
        lock (gate)
        {
            return Task.FromResult(users.TryGetValue(userId, out var user) ? user.Clone() : null);
        }
    }

    public override Task<AccountUser?> FindByEmailAsync(string normalizedEmail, CancellationToken ct)
    {
        lock (gate)
        {
            return Task.FromResult(idByEmail.TryGetValue(normalizedEmail, out var id) ? users[id].Clone() : null);
        }
    }

    public override Task<AccountUser?> FindByPasskeyIdAsync(byte[] credentialId, CancellationToken ct)
    {
        lock (gate)
        {
            return Task.FromResult(idByPasskey.TryGetValue(Key(credentialId), out var id) && users.TryGetValue(id, out var user) ? user.Clone() : null);
        }
    }

    protected override Task OnPasskeyRemovedAsync(AccountUser user, byte[] credentialId, CancellationToken ct)
    {
        lock (gate)
        {
            idByPasskey.Remove(Key(credentialId));
        }

        return Task.CompletedTask;
    }

    private static string Key(byte[] credentialId) => Convert.ToBase64String(credentialId);
}
