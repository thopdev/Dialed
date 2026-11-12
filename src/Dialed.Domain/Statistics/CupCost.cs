using Dialed.Domain.Model;

namespace Dialed.Domain.Statistics;

/// <summary>What the coffee of the brews of one month cost, per currency: how many brews, the total and the average per cup, in cents.</summary>
public sealed record CupCostMonth(int Year, int Month, string Currency, int Brews, int TotalCents, int AverageCents);

/// <summary>
/// What a cup costs: the price of the bag divided by its weight, times the dose of the brew (milk, water and the machine are not counted).
/// Only brews from bags with a price and a weight can be priced. Pure.
/// </summary>
public static class CupCost
{
    /// <summary>The coffee in one brew, in cents (to the nearest cent), or <c>null</c> when its bag has no price or weight.</summary>
    public static int? ForBrew(Brew brew, Bag bag)
    {
        ArgumentNullException.ThrowIfNull(brew);
        ArgumentNullException.ThrowIfNull(bag);
        return bag.PriceCents is > 0 && bag.WeightG > 0m && brew.DoseG > 0m
            ? (int)Math.Round(bag.PriceCents.Value * brew.DoseG / bag.WeightG, MidpointRounding.AwayFromZero)
            : null;
    }

    /// <summary>The usual cup from one bag: its price per gram times the average dose of its brews, or <c>null</c> without brews or a price.</summary>
    public static int? AverageForBag(Bag bag, IEnumerable<Brew> brews)
    {
        ArgumentNullException.ThrowIfNull(bag);
        ArgumentNullException.ThrowIfNull(brews);
        var doses = brews.Where(b => !b.Deleted && b.BagId == bag.Id && b.DoseG > 0m).Select(b => b.DoseG).ToList();
        return doses.Count == 0 ? null : ForBrew(new Brew { BagId = bag.Id, MethodCode = "", DoseG = doses.Average() }, bag);
    }

    /// <summary>Per month (newest first) and currency: the cost of the brews made that month with priced bags.</summary>
    public static IReadOnlyList<CupCostMonth> PerMonth(IEnumerable<Brew> brews, IEnumerable<Bag> bags)
    {
        ArgumentNullException.ThrowIfNull(brews);
        ArgumentNullException.ThrowIfNull(bags);
        var byId = bags.Where(b => !b.Deleted).ToDictionary(b => b.Id);
        return
        [
            .. brews
                .Where(b => !b.Deleted && byId.ContainsKey(b.BagId))
                .Select(b => (Brew: b, Bag: byId[b.BagId], Cents: ForBrew(b, byId[b.BagId])))
                .Where(x => x.Cents is not null)
                .GroupBy(x => (x.Brew.BrewedAt.LocalDateTime.Year, x.Brew.BrewedAt.LocalDateTime.Month, Currency: string.IsNullOrWhiteSpace(x.Bag.Currency) ? "EUR" : x.Bag.Currency.ToUpperInvariant()))
                .Select(g => new CupCostMonth(g.Key.Year, g.Key.Month, g.Key.Currency, g.Count(), g.Sum(x => x.Cents!.Value), (int)Math.Round(g.Average(x => (double)x.Cents!.Value), MidpointRounding.AwayFromZero)))
                .OrderByDescending(m => m.Year).ThenByDescending(m => m.Month).ThenBy(m => m.Currency),
        ];
    }
}
