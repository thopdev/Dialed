using System.Net;
using Microsoft.AspNetCore.Identity;
using Microsoft.Azure.Cosmos;

namespace PasskeySpike.Storage;

/// <summary>
/// Identity user + passkey store on Cosmos DB. The user name is the email address, so one
/// <c>email:{hash}</c> lookup document serves both FindByName and FindByEmail and enforces uniqueness.
/// </summary>
public sealed class CosmosUserStore(IdentityCosmos cosmos, TimeProvider time) :
    IUserEmailStore<SpikeUser>,
    IUserPasskeyStore<SpikeUser>
{
    // ---- IUserStore -------------------------------------------------------------------------

    public Task<string> GetUserIdAsync(SpikeUser user, CancellationToken ct) => Task.FromResult(user.Id);

    public Task<string?> GetUserNameAsync(SpikeUser user, CancellationToken ct) => Task.FromResult(user.UserName);

    public Task SetUserNameAsync(SpikeUser user, string? userName, CancellationToken ct)
    {
        user.UserName = userName;
        return Task.CompletedTask;
    }

    public Task<string?> GetNormalizedUserNameAsync(SpikeUser user, CancellationToken ct) => Task.FromResult(user.NormalizedUserName);

    public Task SetNormalizedUserNameAsync(SpikeUser user, string? normalizedName, CancellationToken ct)
    {
        user.NormalizedUserName = normalizedName;
        return Task.CompletedTask;
    }

    public async Task<IdentityResult> CreateAsync(SpikeUser user, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(user.NormalizedEmail))
        {
            return IdentityResult.Failed(new IdentityErrorDescriber().InvalidEmail(user.Email));
        }

        user.Pk = user.Id;
        user.CreatedAt = time.GetUtcNow();

        // Claim the email first; a conflict means someone else has it (also covers a race between two sign-ups).
        var emailKey = IdentityCosmos.EmailKey(user.NormalizedEmail);
        if (!await cosmos.TryCreateLookupAsync(emailKey, user.Id, ttlSeconds: null, ct))
        {
            return IdentityResult.Failed(new IdentityErrorDescriber().DuplicateEmail(user.Email ?? ""));
        }

        try
        {
            var response = await cosmos.Container.CreateItemAsync(user, new PartitionKey(user.Pk), cancellationToken: ct);
            user.ETag = response.ETag;
        }
        catch
        {
            await cosmos.DeleteIfExistsAsync(emailKey, CancellationToken.None);
            throw;
        }

        foreach (var passkey in user.Passkeys)
        {
            await cosmos.TryCreateLookupAsync(IdentityCosmos.PasskeyKey(passkey.CredentialId), user.Id, ttlSeconds: null, ct);
        }

        return IdentityResult.Success;
    }

    public async Task<IdentityResult> UpdateAsync(SpikeUser user, CancellationToken ct)
    {
        try
        {
            var response = await cosmos.Container.ReplaceItemAsync(user, user.Id, new PartitionKey(user.Pk),
                new ItemRequestOptions { IfMatchEtag = user.ETag }, ct);
            user.ETag = response.ETag;
            return IdentityResult.Success;
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.PreconditionFailed)
        {
            return IdentityResult.Failed(new IdentityErrorDescriber().ConcurrencyFailure());
        }
    }

    public async Task<IdentityResult> DeleteAsync(SpikeUser user, CancellationToken ct)
    {
        foreach (var passkey in user.Passkeys)
        {
            await cosmos.DeleteIfExistsAsync(IdentityCosmos.PasskeyKey(passkey.CredentialId), ct);
        }

        if (user.NormalizedEmail is not null)
        {
            await cosmos.DeleteIfExistsAsync(IdentityCosmos.EmailKey(user.NormalizedEmail), ct);
        }

        await cosmos.Container.DeleteItemAsync<SpikeUser>(user.Id, new PartitionKey(user.Pk), cancellationToken: ct);
        return IdentityResult.Success;
    }

    public Task<SpikeUser?> FindByIdAsync(string userId, CancellationToken ct) => cosmos.ReadAsync<SpikeUser>(userId, ct);

    public Task<SpikeUser?> FindByNameAsync(string normalizedUserName, CancellationToken ct) => FindByLookupAsync(IdentityCosmos.EmailKey(normalizedUserName), ct);

    // ---- IUserEmailStore --------------------------------------------------------------------

    public Task SetEmailAsync(SpikeUser user, string? email, CancellationToken ct)
    {
        user.Email = email;
        return Task.CompletedTask;
    }

    public Task<string?> GetEmailAsync(SpikeUser user, CancellationToken ct) => Task.FromResult(user.Email);

    public Task<bool> GetEmailConfirmedAsync(SpikeUser user, CancellationToken ct) => Task.FromResult(user.EmailConfirmed);

    public Task SetEmailConfirmedAsync(SpikeUser user, bool confirmed, CancellationToken ct)
    {
        user.EmailConfirmed = confirmed;
        return Task.CompletedTask;
    }

    public Task<SpikeUser?> FindByEmailAsync(string normalizedEmail, CancellationToken ct) => FindByLookupAsync(IdentityCosmos.EmailKey(normalizedEmail), ct);

    public Task<string?> GetNormalizedEmailAsync(SpikeUser user, CancellationToken ct) => Task.FromResult(user.NormalizedEmail);

    public Task SetNormalizedEmailAsync(SpikeUser user, string? normalizedEmail, CancellationToken ct)
    {
        user.NormalizedEmail = normalizedEmail;
        return Task.CompletedTask;
    }

    // ---- IUserPasskeyStore ------------------------------------------------------------------
    // Like the EF store, changes are made on the user object and persisted by UserManager's UpdateAsync.

    public async Task AddOrUpdatePasskeyAsync(SpikeUser user, UserPasskeyInfo passkey, CancellationToken ct)
    {
        var existing = user.Passkeys.Find(p => p.CredentialId.AsSpan().SequenceEqual(passkey.CredentialId));
        if (existing is null)
        {
            user.Passkeys.Add(StoredPasskey.From(passkey));
            if (!string.IsNullOrEmpty(user.ETag)) // user already exists; for a new user CreateAsync adds the lookup.
            {
                await cosmos.TryCreateLookupAsync(IdentityCosmos.PasskeyKey(passkey.CredentialId), user.Id, ttlSeconds: null, ct);
            }

            return;
        }

        existing.Name = passkey.Name;
        existing.SignCount = passkey.SignCount;
        existing.IsUserVerified = passkey.IsUserVerified;
        existing.IsBackedUp = passkey.IsBackedUp;
        existing.LastUsedAt = time.GetUtcNow();
    }

    public Task<IList<UserPasskeyInfo>> GetPasskeysAsync(SpikeUser user, CancellationToken ct) =>
        Task.FromResult<IList<UserPasskeyInfo>>([.. user.Passkeys.Select(p => p.ToInfo())]);

    public Task<SpikeUser?> FindByPasskeyIdAsync(byte[] credentialId, CancellationToken ct) =>
        FindByLookupAsync(IdentityCosmos.PasskeyKey(credentialId), ct);

    public Task<UserPasskeyInfo?> FindPasskeyAsync(SpikeUser user, byte[] credentialId, CancellationToken ct) =>
        Task.FromResult(user.Passkeys.Find(p => p.CredentialId.AsSpan().SequenceEqual(credentialId))?.ToInfo());

    public async Task RemovePasskeyAsync(SpikeUser user, byte[] credentialId, CancellationToken ct)
    {
        user.Passkeys.RemoveAll(p => p.CredentialId.AsSpan().SequenceEqual(credentialId));
        await cosmos.DeleteIfExistsAsync(IdentityCosmos.PasskeyKey(credentialId), ct);
    }

    public void Dispose()
    {
    }

    private async Task<SpikeUser?> FindByLookupAsync(string key, CancellationToken ct)
    {
        var lookup = await cosmos.ReadAsync<LookupDocument>(key, ct);
        return lookup is null ? null : await cosmos.ReadAsync<SpikeUser>(lookup.UserId, ct);
    }
}
