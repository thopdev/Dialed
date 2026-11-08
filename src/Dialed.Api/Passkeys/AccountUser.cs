using Microsoft.AspNetCore.Identity;

namespace Dialed.Api.Passkeys;

/// <summary>The Identity user: who can sign in and with which passkeys. Households and profile live elsewhere (<c>AppUser</c>).</summary>
public sealed class AccountUser
{
    public string Id { get; set; } = "";

    public string? UserName { get; set; }

    public string? NormalizedUserName { get; set; }

    public string? Email { get; set; }

    public string? NormalizedEmail { get; set; }

    public bool EmailConfirmed { get; set; }

    public string DisplayName { get; set; } = "";

    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>Changes on every save; a save of a stale copy is refused (optimistic concurrency).</summary>
    public int Version { get; set; }

    public List<StoredPasskey> Passkeys { get; set; } = [];

    public AccountUser Clone()
    {
        var copy = (AccountUser)MemberwiseClone();
        copy.Passkeys = [.. Passkeys.Select(p => p.Clone())];
        return copy;
    }
}

public sealed class StoredPasskey
{
    public byte[] CredentialId { get; set; } = [];

    public byte[] PublicKey { get; set; } = [];

    public string? Name { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset? LastUsedAt { get; set; }

    public uint SignCount { get; set; }

    public string[]? Transports { get; set; }

    public bool IsUserVerified { get; set; }

    public bool IsBackupEligible { get; set; }

    public bool IsBackedUp { get; set; }

    public byte[] AttestationObject { get; set; } = [];

    public byte[] ClientDataJson { get; set; } = [];

    public static StoredPasskey From(UserPasskeyInfo info) => new()
    {
        CredentialId = info.CredentialId,
        PublicKey = info.PublicKey,
        Name = info.Name,
        CreatedAt = info.CreatedAt,
        SignCount = info.SignCount,
        Transports = info.Transports,
        IsUserVerified = info.IsUserVerified,
        IsBackupEligible = info.IsBackupEligible,
        IsBackedUp = info.IsBackedUp,
        AttestationObject = info.AttestationObject,
        ClientDataJson = info.ClientDataJson,
    };

    public UserPasskeyInfo ToInfo() =>
        new(CredentialId, PublicKey, CreatedAt, SignCount, Transports, IsUserVerified, IsBackupEligible, IsBackedUp, AttestationObject, ClientDataJson)
        {
            Name = Name,
        };

    public StoredPasskey Clone() => (StoredPasskey)MemberwiseClone();
}
