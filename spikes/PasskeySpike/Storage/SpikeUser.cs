using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Identity;

namespace PasskeySpike.Storage;

/// <summary>Identity user document. Passkeys are embedded: a user has a handful at most.</summary>
public sealed class SpikeUser
{
    public string Id { get; set; } = "";
    public string Pk { get; set; } = "";
    public string Type { get; set; } = "user";
    public string? UserName { get; set; }
    public string? NormalizedUserName { get; set; }
    public string? Email { get; set; }
    public string? NormalizedEmail { get; set; }
    public bool EmailConfirmed { get; set; }
    public string DisplayName { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public List<StoredPasskey> Passkeys { get; set; } = [];

    [JsonPropertyName("_etag")]
    public string? ETag { get; set; }
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
}
