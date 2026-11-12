using Dialed.Domain.Equipment;
using Dialed.Domain.Model;

namespace Dialed.Domain.Tests;

public sealed class WaterStatsTests
{
    private static Brew With(string? water, int? rating, bool deleted = false) =>
        new() { BagId = "bag", MethodCode = "v60", DoseG = 15m, WaterId = water, Rating = rating, Deleted = deleted, BrewedAt = DateTimeOffset.UtcNow };

    [Fact]
    public void Groups_by_water_and_ranks_by_average_rating_with_unrated_last()
    {
        var uses = WaterStats.Build([With("tap", 3), With("tap", 4), With("third", 5), With("third", null), With("soft", null), With(null, 5), With("tap", 5, deleted: true)]);

        uses.ShouldBe([new("third", 2, 1, 5.0), new("tap", 2, 2, 3.5), new("soft", 1, 0, null)]);
    }

    [Fact]
    public void Brewing_again_keeps_the_water()
    {
        var bag = new Bag { Id = "bag", BeanId = "bean", WeightG = 250m };

        BrewPrefill.Again(bag, With("tap", 4), DateTimeOffset.UtcNow).WaterId.ShouldBe("tap");
    }
}
