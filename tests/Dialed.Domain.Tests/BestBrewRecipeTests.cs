using Dialed.Domain.Model;
using Dialed.Domain.Recipes;

namespace Dialed.Domain.Tests;

public sealed class BestBrewRecipeTests
{
    private static readonly Bean Kiambu = new() { Id = "bean", Name = "Kiambu" };

    private static readonly Bag[] Bags = [new() { Id = "bag", BeanId = "bean", WeightG = 250m }, new() { Id = "other", BeanId = "else", WeightG = 250m }];

    private static Brew Pour(int? rating, decimal dose = 15m, decimal water = 250m, decimal? grind = 22m, decimal? temp = 94m, int? time = 180, string method = "v60", string bag = "bag", string? grinder = "g1") => new()
    {
        BagId = bag,
        MethodCode = method,
        DoseG = dose,
        WaterG = water,
        GrindSetting = grind,
        TempC = temp,
        TimeS = time,
        GrinderId = grinder,
        Rating = rating,
        BrewedAt = DateTimeOffset.UtcNow,
    };

    [Fact]
    public void The_middle_of_the_best_brews_becomes_a_recipe()
    {
        Brew[] brews = [Pour(5, dose: 15m, water: 240m, time: 170), Pour(4, dose: 16m, water: 250m, time: 190), Pour(5, dose: 17m, water: 260m, time: 180), Pour(2, dose: 30m, water: 600m)];

        var result = BestBrewRecipe.Build(Kiambu, Bags, brews, []);

        result.ShouldNotBeNull();
        result.Brews.ShouldBe(3);
        result.AverageRating.ShouldBe(14.0 / 3.0, 0.001);
        var draft = result.Draft;
        (draft.MethodCode, draft.BeanId, draft.GrinderId).ShouldBe(("v60", "bean", "g1"));
        draft.Name.ShouldBe("Kiambu · Hario V60");
        (draft.DoseG, draft.WaterG, draft.GrindSetting, draft.TempC, draft.TimeS).ShouldBe((16m, 250m, 22m, 94m, 180));
    }

    [Fact]
    public void Fewer_than_two_good_brews_of_a_method_gives_nothing()
    {
        BestBrewRecipe.Build(Kiambu, Bags, [Pour(5), Pour(3), Pour(2)], []).ShouldBeNull();
        BestBrewRecipe.Build(Kiambu, Bags, [Pour(5), Pour(5, method: "espresso")], []).ShouldBeNull();
        BestBrewRecipe.Build(Kiambu, Bags, [], []).ShouldBeNull();
    }

    [Fact]
    public void Other_beans_and_deleted_brews_do_not_count()
    {
        Brew[] brews = [Pour(5), Pour(5, bag: "other"), Pour(5) with { Deleted = true }];

        BestBrewRecipe.Build(Kiambu, Bags, brews, []).ShouldBeNull();
    }

    [Fact]
    public void The_method_with_the_most_good_brews_wins_and_the_most_used_grind_is_kept()
    {
        Brew[] brews = [Pour(4, method: "espresso"), Pour(5, method: "espresso"), Pour(4, grind: 20m), Pour(4, grind: 22m), Pour(5, grind: 22m)];

        var result = BestBrewRecipe.Build(Kiambu, Bags, brews, []);

        result!.Draft.MethodCode.ShouldBe("v60");
        result.Draft.GrindSetting.ShouldBe(22m);
    }

    [Fact]
    public void Missing_numbers_stay_empty_and_the_brewer_comes_from_the_recipe_used()
    {
        var recipe = new Recipe { Id = "r1", MethodCode = "v60", Name = "x", BrewerId = "dripper" };
        Brew[] brews = [Pour(5, temp: null, time: null, grind: null, grinder: null) with { RecipeId = "r1" }, Pour(4, temp: null, time: null, grind: null, grinder: null) with { RecipeId = "r1" }];

        var draft = BestBrewRecipe.Build(Kiambu, Bags, brews, [recipe])!.Draft;

        (draft.TempC, draft.TimeS, draft.GrindSetting, draft.GrinderId).ShouldBe((null, null, null, null));
        draft.BrewerId.ShouldBe("dripper");
    }
}
