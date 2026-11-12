using Dialed.Domain.Equipment;
using Dialed.Domain.Model;

namespace Dialed.Domain.Tests;

public sealed class FavoriteGrindsTests
{
    private static Brew B(string bag, decimal? grind, int? rating, int day, string grinder = "g1", string method = "v60") => new()
    {
        BagId = bag,
        MethodCode = method,
        DoseG = 18m,
        GrinderId = grinder,
        GrindSetting = grind,
        Rating = rating,
        BrewedAt = new DateTimeOffset(2026, 1, day, 8, 0, 0, TimeSpan.Zero),
    };

    private static readonly Bag[] Bags = [new() { Id = "bag1", BeanId = "bean1", WeightG = 250m }, new() { Id = "bag2", BeanId = "bean1", WeightG = 250m }];

    [Fact]
    public void The_best_rated_setting_per_bean_and_method_wins_across_bags()
    {
        var result = FavoriteGrinds.For("g1", [B("bag1", 20m, 4, 1), B("bag2", 18m, 5, 2), B("bag2", 17m, 3, 3)], Bags);

        var favorite = result.Single();
        favorite.BeanId.ShouldBe("bean1");
        favorite.Setting.ShouldBe(18m);
        favorite.Rating.ShouldBe(5);
        favorite.Brews.ShouldBe(3);
    }

    [Fact]
    public void Ties_go_to_the_newest_brew() =>
        FavoriteGrinds.For("g1", [B("bag1", 20m, 5, 1), B("bag1", 19m, 5, 2)], Bags).Single().Setting.ShouldBe(19m);

    [Fact]
    public void Other_grinders_unrated_low_and_methods_are_kept_apart()
    {
        var result = FavoriteGrinds.For("g1", [B("bag1", 20m, 3, 1), B("bag1", 10m, 5, 2, grinder: "g2"), B("bag1", null, 5, 3), B("bag1", 9m, 4, 4, method: "espresso")], Bags);

        result.Single().MethodCode.ShouldBe("espresso");
    }
}
