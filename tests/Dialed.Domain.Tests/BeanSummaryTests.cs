using Dialed.Domain.Model;
using Dialed.Domain.Statistics;

namespace Dialed.Domain.Tests;

public sealed class BeanSummaryTests
{
    private static Brew In(string bag, string method, int? rating, Taste? taste = null, bool deleted = false, int day = 1) => new()
    {
        BagId = bag,
        MethodCode = method,
        DoseG = 15m,
        Rating = rating,
        Taste = taste ?? new Taste(),
        Deleted = deleted,
        BrewedAt = new DateTimeOffset(2026, 11, day, 8, 0, 0, TimeSpan.Zero),
    };

    [Fact]
    public void Adds_up_the_brews_of_all_bags_of_the_bean()
    {
        Bag[] bags = [new() { Id = "a1", BeanId = "a", WeightG = 250m }, new() { Id = "a2", BeanId = "a", WeightG = 250m }, new() { Id = "b1", BeanId = "b", WeightG = 250m }];
        Brew[] brews = [In("a1", "v60", 5, new Taste(Acidity: -2), day: 3), In("a2", "v60", 4, new Taste(Acidity: -1)), In("a1", "espresso", null), In("a1", "v60", 1, deleted: true), In("b1", "v60", 2)];

        var summary = BeanSummaries.Build("a", bags, brews);

        (summary.Bags, summary.Brews, summary.Rated, summary.AverageRating, summary.FourStarOrBetter, summary.FavouriteMethod).ShouldBe((2, 3, 2, 4.5, 2, "v60"));
        summary.LastBrewed.ShouldBe(new DateTimeOffset(2026, 11, 3, 8, 0, 0, TimeSpan.Zero));
        summary.Taste!.Axes.First(x => x.Axis == TasteAxis.Acidity).Average.ShouldBe(-1.5);
    }

    [Fact]
    public void A_bean_without_brews_is_empty()
    {
        var summary = BeanSummaries.Build("a", [new Bag { Id = "a1", BeanId = "a", WeightG = 250m }], []);

        (summary.Brews, summary.AverageRating, summary.FavouriteMethod, summary.LastBrewed, summary.Taste).ShouldBe((0, null, null, null, null));
    }
}
