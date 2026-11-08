using Dialed.Domain.Model;

namespace Dialed.Domain.Tests;

public sealed class RecipeQueryTests
{
    private static Recipe R(string id, string method, string name, string? bean = null, bool fav = false, int day = 1) =>
        new() { Id = id, MethodCode = method, Name = name, BeanId = bean, IsFavourite = fav, UpdatedAt = new DateTimeOffset(2026, 1, day, 0, 0, 0, TimeSpan.Zero) };

    private static string Name(string code) => code.ToUpperInvariant();

    private static readonly Recipe[] All =
    [
        R("1", "v60", "Morning", "b1", false, 1),
        R("2", "v60", "Evening", "b2", true, 2),
        R("3", "espresso", "House", "b1", false, 3),
        R("4", "aeropress", "Travel", null, true, 4),
    ];

    [Fact]
    public void Groups_by_method_ordered_by_name_with_favourites_first()
    {
        var groups = RecipeQuery.Group(All, new RecipeFilter(), Name);

        groups.Select(g => g.MethodCode).ShouldBe(["aeropress", "espresso", "v60"]);
        groups[2].Recipes.Select(r => r.Id).ShouldBe(["2", "1"]);
    }

    [Fact]
    public void Filters_by_bean_and_favourites()
    {
        RecipeQuery.Group(All, new RecipeFilter(BeanId: "b1"), Name).SelectMany(g => g.Recipes).Select(r => r.Id).ShouldBe(["3", "1"]);
        RecipeQuery.Group(All, new RecipeFilter(FavouritesOnly: true), Name).SelectMany(g => g.Recipes).Select(r => r.Id).ShouldBe(["4", "2"]);
        RecipeQuery.Group(All, new RecipeFilter(BeanId: "b1", FavouritesOnly: true), Name).ShouldBeEmpty();
    }

    [Fact]
    public void Filters_by_minimum_rating_using_the_brew_averages()
    {
        var ratings = new Dictionary<string, decimal> { ["1"] = 4.5m, ["2"] = 3m };

        RecipeQuery.Group(All, new RecipeFilter(MinRating: 4m), Name, ratings).SelectMany(g => g.Recipes).Select(r => r.Id).ShouldBe(["1"]);
        RecipeQuery.Group(All, new RecipeFilter(MinRating: 4m), Name).ShouldBeEmpty();
    }

    [Fact]
    public void Deleted_recipes_are_hidden()
    {
        RecipeQuery.Group([R("1", "v60", "x") with { Deleted = true }], new RecipeFilter(), Name).ShouldBeEmpty();
    }

    [Fact]
    public void Best_for_a_bean_prefers_rating_then_favourite_then_recent()
    {
        Recipe[] recipes = [R("1", "v60", "a", "b1", false, 1), R("2", "v60", "b", "b1", true, 2), R("3", "v60", "c", "b1", false, 3), R("4", "v60", "d", "b2", false, 9)];

        RecipeQuery.BestForBean("b1", recipes)!.Id.ShouldBe("2");
        RecipeQuery.BestForBean("b1", recipes, new Dictionary<string, decimal> { ["3"] = 4m })!.Id.ShouldBe("3");
        RecipeQuery.BestForBean("b1", [R("5", "v60", "e", "b1", false, 1), R("6", "v60", "f", "b1", false, 5)])!.Id.ShouldBe("6");
        RecipeQuery.BestForBean("none", recipes).ShouldBeNull();
    }
}
