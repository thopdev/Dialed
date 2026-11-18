using Dialed.Api.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.Azure.Cosmos;

namespace Dialed.Api.Passkeys;

/// <summary>
/// The Identity user and passkey store in Cosmos DB (ADR-0010), in the <c>identity</c> container (<c>pk = id</c>):
/// <list type="bullet">
/// <item><c>user:{userId}</c>: the user with its passkeys and recovery code hashes. Saved with its ETag (optimistic concurrency).</item>
/// <item><c>email:{sha256(normalized email)}</c>: points to the user. Creating it claims the address, so emails stay unique
/// across instances (a 409 means taken).</item>
/// <item><c>passkey:{sha256(credential id)}</c>: points to the user, for passkey sign-in. Hashed because a credential id
/// can be longer than a Cosmos DB id may be.</item>
/// </list>
/// Every lookup is a point read. Lookups are written before the user and removed after it, and a lookup is only trusted when the
/// user it points to still has that email or passkey, so a crash between two writes never lets the wrong person in.
/// </summary>
public sealed class CosmosUserStore(DialedCosmos cosmos, TimeProvider time) : AccountUserStore(time)
{
    private static readonly IdentityErrorDescriber Errors = new();

    public override async Task<IdentityResult> CreateAsync(AccountUser user, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(user.NormalizedEmail))
        {
            return IdentityResult.Failed(Errors.InvalidEmail(user.Email));
        }

        var container = await cosmos.ContainerAsync(ct);
        if (!await TryClaimEmailAsync(container, user.NormalizedEmail, user.Id, ct))
        {
            return IdentityResult.Failed(Errors.DuplicateEmail(user.Email ?? ""));
        }

        var stored = user.Clone();
        stored.CreatedAt = Time.GetUtcNow();
        stored.Version = 1;
        foreach (var passkey in stored.Passkeys)
        {
            await PointPasskeyAsync(container, passkey.CredentialId, user.Id, ct);
        }

        if (!await container.TryWriteAsync(UserDocument.From(stored), null, ct))
        {
            // The id is taken: undo the email claim (only if it is still ours).
            await ReleaseAsync(container, EmailId(user.NormalizedEmail), user.Id, ct);
            return IdentityResult.Failed(Errors.DuplicateEmail(user.Email ?? ""));
        }

        user.CreatedAt = stored.CreatedAt;
        user.Version = stored.Version;
        return IdentityResult.Success;
    }

    public override async Task<IdentityResult> UpdateAsync(AccountUser user, CancellationToken ct)
    {
        var container = await cosmos.ContainerAsync(ct);
        var (current, etag) = await container.ReadAsync<UserDocument>(UserDocument.IdFor(user.Id), ct);
        if (current is null || current.User.Version != user.Version)
        {
            return IdentityResult.Failed(Errors.ConcurrencyFailure());
        }

        var oldEmail = current.User.NormalizedEmail;
        var emailChanged = !string.IsNullOrEmpty(user.NormalizedEmail) && user.NormalizedEmail != oldEmail;
        if (emailChanged && !await TryClaimEmailAsync(container, user.NormalizedEmail!, user.Id, ct))
        {
            return IdentityResult.Failed(Errors.DuplicateEmail(user.Email ?? ""));
        }

        foreach (var passkey in user.Passkeys.Where(p => !HasPasskey(current.User, p.CredentialId)))
        {
            await PointPasskeyAsync(container, passkey.CredentialId, user.Id, ct);
        }

        var next = user.Clone();
        next.Version = user.Version + 1;
        if (!await container.TryWriteAsync(UserDocument.From(next), etag, ct))
        {
            if (emailChanged)
            {
                await ReleaseAsync(container, EmailId(user.NormalizedEmail!), user.Id, ct);
            }

            return IdentityResult.Failed(Errors.ConcurrencyFailure());
        }

        user.Version = next.Version;
        if (emailChanged && !string.IsNullOrEmpty(oldEmail))
        {
            await ReleaseAsync(container, EmailId(oldEmail), user.Id, ct);
        }

        foreach (var removed in current.User.Passkeys.Where(p => !HasPasskey(user, p.CredentialId)))
        {
            await ReleaseAsync(container, PasskeyId(removed.CredentialId), user.Id, ct);
        }

        return IdentityResult.Success;
    }

    public override async Task<IdentityResult> DeleteAsync(AccountUser user, CancellationToken ct)
    {
        var container = await cosmos.ContainerAsync(ct);
        var (current, _) = await container.ReadAsync<UserDocument>(UserDocument.IdFor(user.Id), ct);
        if (current is null)
        {
            return IdentityResult.Success;
        }

        await container.TryDeleteAsync(current.Id, null, ct);
        if (!string.IsNullOrEmpty(current.User.NormalizedEmail))
        {
            await ReleaseAsync(container, EmailId(current.User.NormalizedEmail), user.Id, ct);
        }

        foreach (var passkey in current.User.Passkeys)
        {
            await ReleaseAsync(container, PasskeyId(passkey.CredentialId), user.Id, ct);
        }

        return IdentityResult.Success;
    }

    public override async Task<AccountUser?> FindByIdAsync(string userId, CancellationToken ct)
    {
        var container = await cosmos.ContainerAsync(ct);
        var (document, _) = await container.ReadAsync<UserDocument>(UserDocument.IdFor(userId), ct);
        return document?.User;
    }

    public override async Task<AccountUser?> FindByEmailAsync(string normalizedEmail, CancellationToken ct)
    {
        var container = await cosmos.ContainerAsync(ct);
        var (lookup, _) = await container.ReadAsync<LookupDocument>(EmailId(normalizedEmail), ct);
        var user = lookup is null ? null : await FindByIdAsync(lookup.UserId, ct);
        return user?.NormalizedEmail == normalizedEmail ? user : null;
    }

    public override async Task<AccountUser?> FindByPasskeyIdAsync(byte[] credentialId, CancellationToken ct)
    {
        var container = await cosmos.ContainerAsync(ct);
        var (lookup, _) = await container.ReadAsync<LookupDocument>(PasskeyId(credentialId), ct);
        var user = lookup is null ? null : await FindByIdAsync(lookup.UserId, ct);
        return user is not null && HasPasskey(user, credentialId) ? user : null;
    }

    protected override async Task OnPasskeyRemovedAsync(AccountUser user, byte[] credentialId, CancellationToken ct)
    {
        var container = await cosmos.ContainerAsync(ct);
        await ReleaseAsync(container, PasskeyId(credentialId), user.Id, ct);
    }

    private static string EmailId(string normalizedEmail) => "email:" + CosmosIdentity.Hash(normalizedEmail);

    private static string PasskeyId(byte[] credentialId) => "passkey:" + CosmosIdentity.Hash(credentialId);

    /// <summary>
    /// Creates the email lookup for the user. Taken when it points to someone who still has the address; a lookup left behind
    /// by a sign-up that never finished (no such user, or the user changed address) is taken over with its ETag.
    /// </summary>
    private async Task<bool> TryClaimEmailAsync(Container container, string normalizedEmail, string userId, CancellationToken ct)
    {
        var claim = new LookupDocument { Id = EmailId(normalizedEmail), Type = "email", UserId = userId };
        for (var attempt = 0; attempt < CosmosIdentity.MaxRetries; attempt++)
        {
            var (existing, etag) = await container.ReadAsync<LookupDocument>(claim.Id, ct);
            if (existing is not null)
            {
                if (existing.UserId == userId)
                {
                    return true;
                }

                var owner = await FindByIdAsync(existing.UserId, ct);
                if (owner?.NormalizedEmail == normalizedEmail)
                {
                    return false;
                }
            }

            if (await container.TryWriteAsync(claim, etag, ct))
            {
                return true;
            }
        }

        return false;
    }

    private static Task PointPasskeyAsync(Container container, byte[] credentialId, string userId, CancellationToken ct)
    {
        var lookup = new LookupDocument { Id = PasskeyId(credentialId), Type = "passkey", UserId = userId };
        return container.UpsertItemAsync(lookup, new PartitionKey(lookup.Pk), cancellationToken: ct);
    }

    /// <summary>Removes the lookup when it still points to this user.</summary>
    private static async Task ReleaseAsync(Container container, string id, string userId, CancellationToken ct)
    {
        var (lookup, etag) = await container.ReadAsync<LookupDocument>(id, ct);
        if (lookup?.UserId == userId)
        {
            await container.TryDeleteAsync(id, etag, ct);
        }
    }

    internal sealed record UserDocument : IdentityDocument
    {
        public AccountUser User { get; init; } = new();

        public static string IdFor(string userId) => "user:" + userId;

        public static UserDocument From(AccountUser user) => new() { Id = IdFor(user.Id), Type = "user", User = user };
    }

    internal sealed record LookupDocument : IdentityDocument
    {
        public string UserId { get; init; } = "";
    }
}
