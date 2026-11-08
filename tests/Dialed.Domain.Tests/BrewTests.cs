using Dialed.Domain.Inventory;
using Dialed.Domain.Model;

namespace Dialed.Domain.Tests;

public sealed class BrewTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 8, 8, 0, 0, TimeSpan.Zero);

    private static readonly Bag SomeBag = new() { Id = "bag1", BeanId = "b", WeightG = 250m };

    [Fact]
    public void Prefill_from_a_recipe_copies_the_planned_parameters()
    {
        var recipe = new Recipe { Id = "r1", MethodCode = "espresso", Name = "House", GrinderId = "g1", DoseG = 18m, YieldG = 36m, GrindSetting = 12m, TempC = 93m, TimeS = 28 };

        var brew = BrewPrefill.FromRecipe(SomeBag, recipe, Now);

        brew.BagId.ShouldBe("bag1");
        brew.RecipeId.ShouldBe("r1");
        brew.MethodCode.ShouldBe("espresso");
        brew.DrinkType.ShouldBe(DrinkType.Espresso);
        brew.DoseG.ShouldBe(18m);
        brew.YieldG.ShouldBe(36m);
        brew.GrindSetting.ShouldBe(12m);
        brew.TempC.ShouldBe(93m);
        brew.TimeS.ShouldBe(28);
        brew.BrewedAt.ShouldBe(Now);
        brew.Taste.IsBalanced.ShouldBeTrue();
        brew.Rating.ShouldBeNull();
    }

    [Fact]
    public void Brew_again_keeps_the_drink_but_clears_feedback()
    {
        var previous = new Brew
        {
            BagId = "bag0", MethodCode = "espresso", DoseG = 18m, YieldG = 38m, DrinkType = DrinkType.Cappuccino, MilkMl = 120, MilkType = MilkType.Oat,
            Taste = new Taste(Acidity: 1), Rating = 4, Notes = "nice", BrewedAt = Now.AddDays(-1),
        };

        var again = BrewPrefill.Again(SomeBag, previous, Now);

        again.BagId.ShouldBe("bag1");
        again.DoseG.ShouldBe(18m);
        again.YieldG.ShouldBe(38m);
        again.DrinkType.ShouldBe(DrinkType.Cappuccino);
        again.MilkMl.ShouldBe(120);
        again.MilkType.ShouldBe(MilkType.Oat);
        again.Taste.IsBalanced.ShouldBeTrue();
        again.Rating.ShouldBeNull();
        again.Notes.ShouldBeNull();
        again.BrewedAt.ShouldBe(Now);
    }

    [Fact]
    public void Last_is_the_most_recent_undeleted_brew_of_the_bag()
    {
        Brew B(string id, string bag, int days, bool deleted = false) => new() { Id = id, BagId = bag, MethodCode = "v60", DoseG = 15m, BrewedAt = Now.AddDays(days), Deleted = deleted };

        BrewPrefill.Last("bag1", [B("1", "bag1", -3), B("2", "bag1", -1), B("3", "bag2", 0), B("4", "bag1", 0, deleted: true)])!.Id.ShouldBe("2");
        BrewPrefill.Last("bag9", [B("1", "bag1", -3)]).ShouldBeNull();
    }

    [Theory]
    [InlineData(DrinkType.Latte, true)]
    [InlineData(DrinkType.Cappuccino, true)]
    [InlineData(DrinkType.FlatWhite, true)]
    [InlineData(DrinkType.Cortado, true)]
    [InlineData(DrinkType.Espresso, false)]
    [InlineData(DrinkType.Americano, false)]
    [InlineData(DrinkType.Filter, false)]
    public void Milk_drinks(DrinkType type, bool expected) => DrinkTypes.UsesMilk(type).ShouldBe(expected);

    [Theory]
    [InlineData("espresso", DrinkType.Espresso)]
    [InlineData("cold-brew", DrinkType.ColdBrew)]
    [InlineData("v60", DrinkType.Filter)]
    [InlineData("moka-pot", DrinkType.Other)]
    public void Default_drink_per_method(string method, DrinkType expected) => DrinkTypes.DefaultFor(method).ShouldBe(expected);

    [Fact]
    public void Brew_doses_come_out_of_the_bag()
    {
        var doses = new[] { 18m, 18m, 15m };

        BagInventory.Remaining(250m, doses, []).ShouldBe(199m);
    }
}
