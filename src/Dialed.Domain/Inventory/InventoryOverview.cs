using Dialed.Domain.Model;

namespace Dialed.Domain.Inventory;

/// <summary>The inventory settings the overview needs.</summary>
public sealed record InventorySettings(decimal LowStockG, RestWindows RestWindows)
{
    public const decimal DefaultLowStockG = 100m;

    public static InventorySettings Default { get; } = new(DefaultLowStockG, RestWindows.Default);
}

/// <summary>One bag with everything the inventory shows, calculated from the bag and its brews.</summary>
public sealed record BagStock(Bag Bag, string BeanName, decimal RemainingG, int? Cups, int? AgeDays, Freshness Freshness, bool IsLow);

public static class InventoryOverview
{
    /// <summary>Doses used when a bag has no brews yet and neither has the household.</summary>
    public const decimal FallbackDoseG = 18m;

    public static IReadOnlyList<BagStock> Build(IEnumerable<Bag> bags, IEnumerable<Bean> beans, IEnumerable<Brew> brews, DateOnly today, InventorySettings settings)
    {
        ArgumentNullException.ThrowIfNull(bags);
        ArgumentNullException.ThrowIfNull(beans);
        ArgumentNullException.ThrowIfNull(brews);
        ArgumentNullException.ThrowIfNull(settings);

        var beanById = beans.Where(b => !b.Deleted).ToDictionary(b => b.Id);
        var liveBrews = brews.Where(b => !b.Deleted).ToList();
        var dosesByBag = liveBrews.GroupBy(b => b.BagId).ToDictionary(g => g.Key, g => g.Select(b => b.DoseG).ToList());
        var overallDose = liveBrews.Count > 0 ? liveBrews.Average(b => b.DoseG) : FallbackDoseG;

        return
        [
            .. bags.Where(b => !b.Deleted).Select(bag =>
            {
                var doses = dosesByBag.GetValueOrDefault(bag.Id) ?? [];
                var remaining = BagInventory.Display(BagInventory.Remaining(bag.WeightG, doses, []));
                var averageDose = doses.Count > 0 ? doses.Average() : overallDose;
                var cups = averageDose > 0m ? (int?)decimal.Floor(remaining / averageDose) : null;
                beanById.TryGetValue(bag.BeanId, out var bean);
                var age = BagLifecycle.AgeDays(bag, today);
                var freshness = FreshnessRules.Classify(age, settings.RestWindows.For(bean?.RoastLevel), bag.RestDaysOverride);
                var low = bag.Status == BagStatus.Open && remaining < settings.LowStockG;
                return new BagStock(bag, bean?.Name ?? "", remaining, cups, age, freshness, low);
            }),
        ];
    }

    /// <summary>Open bags below the low-stock threshold: the number on the navigation badge.</summary>
    public static int LowStockCount(IEnumerable<BagStock> stock) => stock.Count(s => s.IsLow);
}
