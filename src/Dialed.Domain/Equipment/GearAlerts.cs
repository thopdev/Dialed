using Dialed.Domain.Model;

namespace Dialed.Domain.Equipment;

/// <summary>The gear to tell a person about now, and the keys to remember so each service is announced once.</summary>
public sealed record GearAlertResult(IReadOnlyList<string> Due, IReadOnlySet<string> Memory);

/// <summary>
/// Which pieces of gear deserve a "service due" notification: the same <see cref="GearWear"/> judgement the app shows, so the server and the app
/// cannot disagree. A piece is announced once per service: its key holds the service date, so marking it serviced re-arms it. Pure.
/// </summary>
public static class GearAlerts
{
    public static GearAlertResult Evaluate(
        string householdId,
        IEnumerable<EquipmentItem> gear,
        IEnumerable<Brew> brews,
        IEnumerable<Recipe> recipes,
        DateOnly today,
        IReadOnlySet<string> previous)
    {
        ArgumentNullException.ThrowIfNull(gear);
        ArgumentNullException.ThrowIfNull(brews);
        ArgumentNullException.ThrowIfNull(recipes);
        ArgumentNullException.ThrowIfNull(previous);

        var brewList = brews.ToList();
        var recipeList = recipes.ToList();
        var dueNow = gear
            .Where(g => !g.Deleted && GearWear.Evaluate(g, brewList, recipeList, today).State == WearState.Due)
            .ToList();

        string Key(EquipmentItem g) => $"{householdId}/{g.Id}/{g.ServicedOn:yyyy-MM-dd}";

        // Only this household's keys are replaced; other households' memory is carried over untouched.
        var prefix = householdId + "/";
        var kept = previous.Where(k => !k.StartsWith(prefix, StringComparison.Ordinal));
        var memory = new HashSet<string>(kept.Concat(dueNow.Select(Key)), StringComparer.Ordinal);

        var names = dueNow
            .Where(g => !previous.Contains(Key(g)))
            .Select(Name)
            .Order(StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        return new GearAlertResult(names, memory);
    }

    private static string Name(EquipmentItem item)
    {
        var name = $"{item.Brand} {item.Model}".Trim();
        return name.Length > 0 ? name : item.Kind.ToString();
    }
}
