using Dialed.Domain.BrewMethods;

namespace Dialed.Domain.Model;

/// <summary>Builds the starting values of a new brew, so logging a repeat takes a couple of taps.</summary>
public static class BrewPrefill
{
    /// <summary>From a recipe: the planned parameters become the actual ones until the person changes them.</summary>
    public static Brew FromRecipe(Bag bag, Recipe recipe, DateTimeOffset now) => new()
    {
        BagId = bag.Id,
        MethodCode = recipe.MethodCode,
        RecipeId = recipe.Id,
        GrinderId = recipe.GrinderId,
        BrewedAt = now,
        DrinkType = DrinkTypes.DefaultFor(recipe.MethodCode),
        DoseG = recipe.DoseG ?? 0m,
        WaterG = recipe.WaterG,
        YieldG = recipe.YieldG,
        GrindSetting = recipe.GrindSetting,
        TempC = recipe.TempC,
        TimeS = recipe.TimeS,
    };

    /// <summary>"Brew again": the same drink as an earlier brew, with taste, rating and notes cleared.</summary>
    public static Brew Again(Bag bag, Brew previous, DateTimeOffset now) => new()
    {
        BagId = bag.Id,
        MethodCode = previous.MethodCode,
        RecipeId = previous.RecipeId,
        GrinderId = previous.GrinderId,
        WaterId = previous.WaterId,
        BrewedAt = now,
        DrinkType = previous.DrinkType,
        MilkMl = previous.MilkMl,
        MilkType = previous.MilkType,
        DoseG = previous.DoseG,
        WaterG = previous.WaterG,
        YieldG = previous.YieldG,
        GrindSetting = previous.GrindSetting,
        TempC = previous.TempC,
        TimeS = previous.TimeS,
    };

    /// <summary>With no recipe: the first method (or the given one), the method's default grinder and a blank dose.</summary>
    public static Brew Blank(Bag bag, string? methodCode, string? grinderId, DateTimeOffset now)
    {
        var code = methodCode ?? BrewMethodCatalog.Default.Methods[0].Code;
        return new Brew
        {
            BagId = bag.Id,
            MethodCode = code,
            GrinderId = grinderId,
            BrewedAt = now,
            DrinkType = DrinkTypes.DefaultFor(code),
            DoseG = 0m,
        };
    }

    /// <summary>The most recent brew from a bag, or <c>null</c>.</summary>
    public static Brew? Last(string bagId, IEnumerable<Brew> brews) =>
        brews.Where(b => !b.Deleted && b.BagId == bagId).OrderByDescending(b => b.BrewedAt).FirstOrDefault();
}
