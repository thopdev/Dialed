namespace Dialed.Domain.Model;

/// <summary>Where a copied bean or recipe came from (#77), so the copy can say "from Carol" and the same item is not copied twice.</summary>
public sealed record CopiedFrom(string OwnerId, string OwnerName, string ItemId);

/// <summary>What a friend's bean or recipe becomes in my household: my own record, with their personal details left behind.</summary>
public static class FriendCopy
{
    /// <summary>
    /// A new bean. Photos stay with the friend (they only live on their device until uploads exist), their rating and scan are not mine,
    /// and it follows my default visibility. <paramref name="roasterId"/> is the roaster in my household (matched or created).
    /// </summary>
    public static Bean OfBean(Bean source, CopiedFrom from, string? roasterId, Visibility visibility)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(from);
        return Reset(source) with
        {
            RoasterId = roasterId,
            PhotoIds = [],
            Rating = null,
            SourceScanId = null,
            Visibility = visibility,
            CopiedFrom = from,
        };
    }

    /// <summary>
    /// A new recipe. Their grinder and brewer are not mine, so those links are dropped (the grind setting stays, on whatever scale it was);
    /// versions, favourite and template flags start fresh. <paramref name="beanId"/> is my bean of the same name, if I have one.
    /// </summary>
    public static Recipe OfRecipe(Recipe source, CopiedFrom from, string? beanId, Visibility visibility)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(from);
        return Reset(source) with
        {
            BeanId = beanId,
            GrinderId = null,
            BrewerId = null,
            ParentRecipeId = null,
            IsSuperseded = false,
            IsDefaultTemplate = false,
            IsFavourite = false,
            Visibility = visibility,
            CopiedFrom = from,
        };
    }

    /// <summary>Whether the household already holds a copy of this friend's item.</summary>
    public static bool IsCopyOf(CopiedFrom? copiedFrom, string ownerId, string itemId) =>
        copiedFrom is not null && copiedFrom.OwnerId == ownerId && copiedFrom.ItemId == itemId;

    private static T Reset<T>(T source)
        where T : SyncEntity => source with
        {
            Id = Ids.New(),
            HouseholdId = string.Empty,
            CreatedBy = string.Empty,
            UpdatedBy = string.Empty,
            CreatedAt = default,
            UpdatedAt = default,
            Version = 0,
            Deleted = false,
        };
}
