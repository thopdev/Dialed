using Dialed.Domain.BrewMethods;

namespace Dialed.Domain.Model;

/// <summary>What the brew history is narrowed to. A <c>null</c> member means "any".</summary>
public sealed record BrewFilter(string? BeanId = null, string? MethodCode = null, string? PersonId = null, int? MinRating = null);

/// <summary>One parameter compared between a recipe and what was actually brewed.</summary>
public sealed record ParameterDelta(BrewParameter Parameter, decimal? Planned, decimal? Actual)
{
    public bool Differs => Planned is not null && Actual is not null && Planned != Actual;
}

public static class BrewHistory
{
    /// <summary>Newest first. The bean of a brew is the bean of its bag.</summary>
    public static IReadOnlyList<Brew> Apply(IEnumerable<Brew> brews, IEnumerable<Bag> bags, BrewFilter filter)
    {
        ArgumentNullException.ThrowIfNull(brews);
        ArgumentNullException.ThrowIfNull(bags);
        ArgumentNullException.ThrowIfNull(filter);

        var beanOfBag = bags.ToDictionary(bag => bag.Id, bag => bag.BeanId);
        var query = brews.Where(brew => !brew.Deleted);

        if (filter.BeanId is not null)
        {
            query = query.Where(brew => beanOfBag.GetValueOrDefault(brew.BagId) == filter.BeanId);
        }

        if (filter.MethodCode is not null)
        {
            query = query.Where(brew => string.Equals(brew.MethodCode, filter.MethodCode, StringComparison.OrdinalIgnoreCase));
        }

        if (filter.PersonId is not null)
        {
            query = query.Where(brew => brew.CreatedBy == filter.PersonId);
        }

        if (filter.MinRating is { } min)
        {
            query = query.Where(brew => brew.Rating >= min);
        }

        return [.. query.OrderByDescending(brew => brew.BrewedAt)];
    }

    /// <summary>
    /// The recipe's values next to the actual ones, for the parameters that were planned or actually used
    /// (dose, water or yield, grind, temperature, time). Without a recipe the planned side is empty.
    /// </summary>
    public static IReadOnlyList<ParameterDelta> Compare(Brew brew, Recipe? recipe)
    {
        ArgumentNullException.ThrowIfNull(brew);

        var rows = new List<ParameterDelta>
        {
            new(BrewParameter.Dose, recipe?.DoseG, brew.DoseG),
            new(BrewParameter.Water, recipe?.WaterG, brew.WaterG),
            new(BrewParameter.Yield, recipe?.YieldG, brew.YieldG),
            new(BrewParameter.Grind, recipe?.GrindSetting, brew.GrindSetting),
            new(BrewParameter.Temperature, recipe?.TempC, brew.TempC),
            new(BrewParameter.Time, recipe?.TimeS, brew.TimeS),
        };

        return [.. rows.Where(row => row.Planned is not null || row.Actual is not null)];
    }
}
