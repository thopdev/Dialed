namespace Dialed.Domain.Model;

/// <summary>
/// Who can read an item: only its creator, the household, accepted followers of the creator too, or everyone who uses Dialed (#106).
/// <see cref="Public"/> is only for beans, recipes and vendors, and only counts while the creator's public profile is switched on.
/// </summary>
public enum Visibility
{
    Private,
    Household,
    Friends,
    Public,
}

/// <summary>
/// Base of everything that is stored and synced (architecture §2). Ids are created on the device so records can be made offline.
/// Records are immutable: change one with <c>with</c> and save it.
/// </summary>
public abstract record SyncEntity
{
    /// <summary>Client-generated GUID string.</summary>
    public string Id { get; init; } = Ids.New();

    public string HouseholdId { get; init; } = string.Empty;

    public string CreatedBy { get; init; } = string.Empty;

    public string UpdatedBy { get; init; } = string.Empty;

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset UpdatedAt { get; init; }

    /// <summary>Raised on every save by the store; used to detect that an edit started from an outdated copy.</summary>
    public int Version { get; init; }

    /// <summary>Soft delete: the record stays so the deletion can sync (purged after 30 days, #43).</summary>
    public bool Deleted { get; init; }
}

public static class Ids
{
    /// <summary>A new time-ordered GUID (v7) as text.</summary>
    public static string New() => Guid.CreateVersion7().ToString();
}
