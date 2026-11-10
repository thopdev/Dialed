using System.Text.Json;
using Dialed.Client.Data;
using Dialed.Client.Preferences;
using Dialed.Domain.Model;
using Dialed.Domain.Text;

namespace Dialed.Client.Friends;

public enum CopyOutcome
{
    /// <summary>A new bean or recipe is in my household.</summary>
    Copied,

    /// <summary>I already had a copy of this item; nothing new was made.</summary>
    AlreadyCopied,

    /// <summary>The item is gone or no longer shared with me.</summary>
    Unavailable,
}

/// <param name="Table"><c>beans</c> or <c>recipes</c>.</param>
/// <param name="Id">The copy's id (or the earlier copy's), <c>null</c> when unavailable.</param>
public sealed record CopyResult(CopyOutcome Outcome, string Table, string? Id, string? Name);

/// <summary>
/// Copies a friend's bean or recipe into my household (#77). The copy is my own record saved through the repositories, so it syncs like
/// anything else. A bean brings its roaster (matched by name to one I have, else added); a recipe links to my bean of the same name if I
/// have one. Photos stay with the friend until uploads exist (#68).
/// </summary>
public sealed class FriendCopyService(
    FriendsApi friends,
    IRepository<Bean> beans,
    IRepository<Recipe> recipes,
    IRepository<Vendor> vendors,
    IUserPreferences preferences)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>The earlier copy of this item, if my household already has one (no network needed).</summary>
    public async Task<CopyResult?> ExistingAsync(string ownerId, string table, string itemId, CancellationToken cancellationToken = default)
    {
        if (table == "beans")
        {
            var bean = (await beans.ListAsync(cancellationToken)).FirstOrDefault(b => FriendCopy.IsCopyOf(b.CopiedFrom, ownerId, itemId));
            return bean is null ? null : new CopyResult(CopyOutcome.AlreadyCopied, table, bean.Id, bean.Name);
        }

        var recipe = (await recipes.ListAsync(cancellationToken)).FirstOrDefault(r => FriendCopy.IsCopyOf(r.CopiedFrom, ownerId, itemId));
        return recipe is null ? null : new CopyResult(CopyOutcome.AlreadyCopied, table, recipe.Id, recipe.Name);
    }

    /// <exception cref="Dialed.Client.Account.AccountApiException">The server could not be reached.</exception>
    public async Task<CopyResult> CopyAsync(string ownerId, string table, string itemId, CancellationToken cancellationToken = default)
    {
        if (table is not ("beans" or "recipes"))
        {
            throw new ArgumentOutOfRangeException(nameof(table), table, "Only beans and recipes can be copied.");
        }

        if (await ExistingAsync(ownerId, table, itemId, cancellationToken) is { } existing)
        {
            return existing;
        }

        if (await friends.SharedItemAsync(ownerId, table, itemId, cancellationToken) is not { } shared)
        {
            return new CopyResult(CopyOutcome.Unavailable, table, null, null);
        }

        var from = new CopiedFrom(ownerId, shared.OwnerName, itemId);
        var visibility = preferences.Profile.DefaultVisibility;
        if (table == "beans")
        {
            var source = shared.Item.Deserialize<Bean>(Json) ?? throw new JsonException("The shared bean could not be read.");
            var saved = await beans.SaveAsync(FriendCopy.OfBean(source, from, await RoasterAsync(shared.RoasterName, cancellationToken), visibility), cancellationToken);
            return new CopyResult(CopyOutcome.Copied, table, saved.Id, saved.Name);
        }

        var recipe = shared.Item.Deserialize<Recipe>(Json) ?? throw new JsonException("The shared recipe could not be read.");
        var beanId = shared.BeanName is { Length: > 0 } beanName
            ? (await beans.ListAsync(cancellationToken)).FirstOrDefault(b => string.Equals(b.Name.Trim(), beanName.Trim(), StringComparison.OrdinalIgnoreCase))?.Id
            : null;
        var savedRecipe = await recipes.SaveAsync(FriendCopy.OfRecipe(recipe, from, beanId, visibility), cancellationToken);
        return new CopyResult(CopyOutcome.Copied, table, savedRecipe.Id, savedRecipe.Name);
    }

    /// <summary>My roaster with this name (close matches count), or a new one.</summary>
    private async Task<string?> RoasterAsync(string? name, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        var mine = await vendors.ListAsync(cancellationToken);
        if (LabelMatcher.Find(name, null, null, mine, []).BestRoaster is { } match)
        {
            return match.Item.Id;
        }

        return (await vendors.SaveAsync(new Vendor { Name = name.Trim(), Kinds = VendorKinds.Roaster }, cancellationToken)).Id;
    }
}
