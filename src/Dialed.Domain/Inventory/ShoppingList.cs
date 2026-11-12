using Dialed.Domain.Model;

namespace Dialed.Domain.Inventory;

public enum ShoppingReason
{
    /// <summary>The only bag in stock is open and running low.</summary>
    RunningLow,

    /// <summary>Nothing left of a bean you rated well (4 stars or more on average).</summary>
    BuyAgain,
}

/// <summary>One bean to buy: why, how you rated it and the size of the last bag, as a hint for how much to buy.</summary>
public sealed record ShoppingItem(string BeanId, string Name, ShoppingReason Reason, double? AverageRating, decimal? LastBagWeightG);

/// <summary>
/// What to buy next, from the same stock figures the inventory shows. A bean is on the list when everything of it that is left is an open bag
/// under the low-stock threshold, or when it is all gone and you rated its brews 4 stars or more on average. Beans that still have a sealed,
/// frozen or healthy open bag are not on it. Pure.
/// </summary>
public static class ShoppingList
{
    public const double GoodRating = 4.0;

    public static IReadOnlyList<ShoppingItem> Build(IEnumerable<BagStock> stock, IEnumerable<Brew> brews)
    {
        ArgumentNullException.ThrowIfNull(stock);
        ArgumentNullException.ThrowIfNull(brews);

        var ratingByBag = brews.Where(b => !b.Deleted && b.Rating is not null)
            .GroupBy(b => b.BagId)
            .ToDictionary(g => g.Key, g => g.Select(b => (double)b.Rating!.Value).ToList());
        var items = new List<ShoppingItem>();
        foreach (var bean in stock.Where(s => !s.Bag.Deleted && s.BeanName.Length > 0).GroupBy(s => s.Bag.BeanId))
        {
            var bags = bean.ToList();
            var hasSupply = bags.Any(s => s.Bag.Status is BagStatus.Sealed or BagStatus.Frozen || (s.Bag.Status == BagStatus.Open && !s.IsLow));
            if (hasSupply)
            {
                continue;
            }

            var ratings = bags.SelectMany(s => ratingByBag.GetValueOrDefault(s.Bag.Id) ?? []).ToList();
            double? average = ratings.Count == 0 ? null : ratings.Average();
            var last = bags.OrderByDescending(s => s.Bag.RoastDate ?? s.Bag.PurchasedOn ?? DateOnly.MinValue).First();
            if (bags.Any(s => s.Bag.Status == BagStatus.Open && s.IsLow))
            {
                items.Add(new ShoppingItem(bean.Key, last.BeanName, ShoppingReason.RunningLow, average, last.Bag.WeightG));
            }
            else if (average >= GoodRating)
            {
                items.Add(new ShoppingItem(bean.Key, last.BeanName, ShoppingReason.BuyAgain, average, last.Bag.WeightG));
            }
        }

        return [.. items.OrderBy(i => i.Reason).ThenBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase)];
    }
}
