using Dialed.Domain.Model;
using Dialed.Domain.Statistics;

namespace Dialed.Domain.Tests;

public sealed class BeanTasteProfileTests
{
    private static Brew Brewed(Taste taste, string bag = "bag", bool deleted = false) =>
        new() { BagId = bag, MethodCode = "v60", DoseG = 15m, Taste = taste, Deleted = deleted, BrewedAt = DateTimeOffset.UtcNow };

    [Fact]
    public void Averages_each_axis_over_the_brews_that_were_off()
    {
        var result = BeanTasteProfile.Build(["bag"], [Brewed(new Taste(Acidity: -2, Body: -1)), Brewed(new Taste(Acidity: -1, Sweetness: 1)), Brewed(new Taste())]);

        result.ShouldNotBeNull();
        result.Brews.ShouldBe(2);
        result.Axes.Select(a => (a.Axis, a.Average)).ShouldBe([(TasteAxis.Acidity, -1.5), (TasteAxis.Strength, 0.0), (TasteAxis.Body, -0.5), (TasteAxis.Sweetness, 0.5)]);
        result.Axes.Select(a => a.IsNoticeable).ShouldBe([true, false, true, true]);
    }

    [Fact]
    public void Needs_two_brews_that_were_off()
    {
        BeanTasteProfile.Build(["bag"], [Brewed(new Taste(Acidity: 1)), Brewed(new Taste())]).ShouldBeNull();
        BeanTasteProfile.Build(["bag"], []).ShouldBeNull();
    }

    [Fact]
    public void Other_bags_and_deleted_brews_do_not_count()
    {
        BeanTasteProfile.Build(["bag"], [Brewed(new Taste(Body: 1)), Brewed(new Taste(Body: 1), bag: "other"), Brewed(new Taste(Body: 1), deleted: true)]).ShouldBeNull();
    }
}
