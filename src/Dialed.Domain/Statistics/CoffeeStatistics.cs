using Dialed.Domain.Model;

namespace Dialed.Domain.Statistics;

/// <summary>Grams brewed in the week that starts on <see cref="WeekStart"/> (a Monday).</summary>
public sealed record WeeklyConsumption(DateOnly WeekStart, decimal Grams);

/// <summary>Money spent on bags in one month (bags are counted on their purchase date), per currency.</summary>
public sealed record MonthlySpend(int Year, int Month, string Currency, int Cents);

/// <summary>How often something was brewed and how it was rated on average (<c>null</c> when nothing was rated).</summary>
public sealed record Favourite(string Name, int Brews, double? AverageRating);

/// <summary>Average grind setting of a grinder in one month.</summary>
public sealed record GrindPoint(int Year, int Month, decimal AverageSetting, int Brews);

public sealed record GrinderTrend(string GrinderId, string GrinderName, IReadOnlyList<GrindPoint> Points);

public sealed record CoffeeStatistics(
    IReadOnlyList<WeeklyConsumption> Weekly,
    IReadOnlyList<MonthlySpend> Spend,
    IReadOnlyList<Favourite> Origins,
    IReadOnlyList<Favourite> Roasters,
    IReadOnlyList<Favourite> Processes,
    IReadOnlyList<GrinderTrend> GrindTrends,
    int TotalBrews)
{
    /// <summary>The tasting notes of the coffees you brewed, sorted into the branches of the flavour wheel (a note counts once per brew).</summary>
    public IReadOnlyList<Flavors.FlavorShare> Flavours { get; init; } = [];

    public const int Weeks = 12;

    public const int Top = 5;

    public bool IsEmpty => TotalBrews == 0 && Spend.Count == 0;

    /// <summary>Calculates everything the statistics screen shows from the person's own (not deleted) data. Pure: pass the data and today's date.</summary>
    public static CoffeeStatistics Calculate(
        IEnumerable<Brew> brews,
        IEnumerable<Bag> bags,
        IEnumerable<Bean> beans,
        IEnumerable<Vendor> vendors,
        IEnumerable<EquipmentItem> equipment,
        DateOnly today)
    {
        var liveBrews = brews.Where(b => !b.Deleted).ToList();
        var liveBags = bags.Where(b => !b.Deleted).ToList();
        var beanById = beans.Where(b => !b.Deleted).ToDictionary(b => b.Id);
        var vendorById = vendors.Where(v => !v.Deleted).ToDictionary(v => v.Id);
        var bagById = liveBags.ToDictionary(b => b.Id);

        return new CoffeeStatistics(
            WeeklyGrams(liveBrews, today),
            SpendPerMonth(liveBags),
            Favourites(liveBrews, bagById, beanById, bean => bean.Origin.Country),
            Favourites(liveBrews, bagById, beanById, bean => bean.RoasterId is { } id && vendorById.TryGetValue(id, out var vendor) ? vendor.Name : null),
            Favourites(liveBrews, bagById, beanById, bean => bean.Process),
            GrindTrendsOf(liveBrews, equipment.Where(e => !e.Deleted && e.Kind == EquipmentKind.Grinder)),
            liveBrews.Count)
        {
            Flavours = Flavors.FlavorWheel.Tally(liveBrews.SelectMany(b => bagById.TryGetValue(b.BagId, out var bag) && beanById.TryGetValue(bag.BeanId, out var bean) ? bean.TastingNotes : [])),
        };
    }

    public static DateOnly MondayOf(DateOnly date) => date.AddDays(-(((int)date.DayOfWeek + 6) % 7));

    private static List<WeeklyConsumption> WeeklyGrams(List<Brew> brews, DateOnly today)
    {
        var thisWeek = MondayOf(today);
        var grams = brews
            .GroupBy(b => MondayOf(DateOnly.FromDateTime(b.BrewedAt.DateTime)))
            .ToDictionary(g => g.Key, g => g.Sum(b => b.DoseG));

        return [.. Enumerable.Range(0, Weeks)
            .Select(i => thisWeek.AddDays(-7 * (Weeks - 1 - i)))
            .Select(week => new WeeklyConsumption(week, grams.GetValueOrDefault(week)))];
    }

    private static List<MonthlySpend> SpendPerMonth(List<Bag> bags) =>
        [.. bags
            .Where(b => b.PriceCents is > 0 && b.PurchasedOn is not null)
            .GroupBy(b => (b.PurchasedOn!.Value.Year, b.PurchasedOn.Value.Month, Currency: string.IsNullOrWhiteSpace(b.Currency) ? "EUR" : b.Currency.ToUpperInvariant()))
            .Select(g => new MonthlySpend(g.Key.Year, g.Key.Month, g.Key.Currency, g.Sum(b => b.PriceCents!.Value)))
            .OrderByDescending(s => s.Year).ThenByDescending(s => s.Month).ThenBy(s => s.Currency)];

    private static List<Favourite> Favourites(List<Brew> brews, Dictionary<string, Bag> bags, Dictionary<string, Bean> beans, Func<Bean, string?> selector) =>
        [.. brews
            .Select(brew => (Brew: brew, Name: bags.TryGetValue(brew.BagId, out var bag) && beans.TryGetValue(bag.BeanId, out var bean) ? selector(bean) : null))
            .Where(x => !string.IsNullOrWhiteSpace(x.Name))
            .GroupBy(x => x.Name!.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                var rated = g.Where(x => x.Brew.Rating is not null).Select(x => (double)x.Brew.Rating!.Value).ToList();
                return new Favourite(g.Key, g.Count(), rated.Count == 0 ? null : Math.Round(rated.Average(), 1));
            })
            .OrderByDescending(f => f.Brews).ThenByDescending(f => f.AverageRating ?? 0).ThenBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
            .Take(Top)];

    private static List<GrinderTrend> GrindTrendsOf(List<Brew> brews, IEnumerable<EquipmentItem> grinders) =>
        [.. grinders
            .Select(grinder => new GrinderTrend(
                grinder.Id,
                string.IsNullOrWhiteSpace(grinder.Model) ? grinder.Brand : $"{grinder.Brand} {grinder.Model}",
                [.. brews
                    .Where(b => b.GrinderId == grinder.Id && b.GrindSetting is not null)
                    .GroupBy(b => (b.BrewedAt.Year, b.BrewedAt.Month))
                    .OrderBy(g => g.Key.Year).ThenBy(g => g.Key.Month)
                    .Select(g => new GrindPoint(g.Key.Year, g.Key.Month, Math.Round(g.Average(b => b.GrindSetting!.Value), 1), g.Count()))]))
            .Where(t => t.Points.Count > 0)
            .OrderBy(t => t.GrinderName, StringComparer.OrdinalIgnoreCase)];
}
