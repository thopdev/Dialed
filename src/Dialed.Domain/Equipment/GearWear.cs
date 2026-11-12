using Dialed.Domain.Model;

namespace Dialed.Domain.Equipment;

public enum WearState
{
    /// <summary>No service interval set for this gear.</summary>
    None,
    Ok,
    /// <summary>At least 90 percent of an interval is used.</summary>
    Soon,
    Due,
}

/// <summary>How much a piece of gear has been used since it was last serviced, against its interval.</summary>
public sealed record GearWearStatus(WearState State, int Brews, decimal Kilograms, double Fraction, int Days = 0)
{
    public static GearWearStatus NoInterval { get; } = new(WearState.None, 0, 0m, 0);
}

/// <summary>
/// Upkeep nudges: counts the brews made with a piece of gear since its service date (a grinder through the brew, a brewer or machine through the
/// recipe it was brewed with) and compares them with the interval the person set in brews, kilograms of coffee and/or days (7 for a weekly clean). Pure.
/// </summary>
public static class GearWear
{
    public const double SoonFraction = 0.9;

    public static GearWearStatus Evaluate(EquipmentItem item, IEnumerable<Brew> brews, IEnumerable<Recipe> recipes, DateOnly? today = null)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(brews);
        ArgumentNullException.ThrowIfNull(recipes);

        if (item.ServiceEveryBrews is not > 0 && item.ServiceEveryKg is not > 0 && item.ServiceEveryDays is not > 0)
        {
            return GearWearStatus.NoInterval;
        }

        var recipeBrewer = recipes.Where(r => !r.Deleted && r.BrewerId is not null).ToDictionary(r => r.Id, r => r.BrewerId!);
        var used = brews.Where(b => !b.Deleted && UsesItem(item, b, recipeBrewer)
                && (item.ServicedOn is not { } since || DateOnly.FromDateTime(b.BrewedAt.LocalDateTime) > since))
            .ToList();
        var count = used.Count;
        var kilograms = used.Sum(b => b.DoseG) / 1000m;

        // Time counts from the service date, or from the day the gear was added when it has never been serviced.
        var start = item.ServicedOn ?? (item.CreatedAt == default ? (DateOnly?)null : DateOnly.FromDateTime(item.CreatedAt.LocalDateTime));
        var days = start is { } from ? Math.Max(0, (today ?? DateOnly.FromDateTime(DateTime.Now)).DayNumber - from.DayNumber) : 0;

        var fraction = Math.Max(
            Math.Max(
                item.ServiceEveryBrews is > 0 and var everyBrews ? (double)count / everyBrews : 0,
                item.ServiceEveryKg is > 0 and var everyKg ? (double)(kilograms / everyKg) : 0),
            item.ServiceEveryDays is > 0 and var everyDays ? (double)days / everyDays : 0);
        var state = fraction >= 1 ? WearState.Due : fraction >= SoonFraction ? WearState.Soon : WearState.Ok;
        return new GearWearStatus(state, count, Math.Round(kilograms, 2), fraction, days);
    }

    private static bool UsesItem(EquipmentItem item, Brew brew, Dictionary<string, string> recipeBrewer) =>
        item.Kind == EquipmentKind.Grinder
            ? brew.GrinderId == item.Id
            : brew.RecipeId is { } recipeId && recipeBrewer.TryGetValue(recipeId, out var brewerId) && brewerId == item.Id;
}
