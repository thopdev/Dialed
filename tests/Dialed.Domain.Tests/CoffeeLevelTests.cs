using Dialed.Domain.Statistics;

namespace Dialed.Domain.Tests;

public sealed class CoffeeLevelTests
{
    [Theory]
    [InlineData(0, 0, 10)]
    [InlineData(9, 0, 10)]
    [InlineData(10, 1, 50)]
    [InlineData(49, 1, 50)]
    [InlineData(50, 2, 100)]
    [InlineData(250, 4, 500)]
    public void A_level_starts_at_its_threshold_and_names_the_next_one(int brews, int index, int next)
    {
        var level = CoffeeLevel.For(brews);

        level.Index.ShouldBe(index);
        level.NextAt.ShouldBe(next);
        level.IsTop.ShouldBeFalse();
    }

    [Theory]
    [InlineData(500)]
    [InlineData(5000)]
    public void The_top_level_has_no_next_step(int brews)
    {
        var level = CoffeeLevel.For(brews);

        level.Index.ShouldBe(CoffeeLevel.Thresholds.Count - 1);
        level.IsTop.ShouldBeTrue();
        level.ToGo.ShouldBe(0);
        level.Fraction.ShouldBe(1);
    }

    [Fact]
    public void Progress_runs_from_the_start_of_the_level_to_the_next_one()
    {
        CoffeeLevel.For(10).Fraction.ShouldBe(0);
        CoffeeLevel.For(30).Fraction.ShouldBe(0.5);
        CoffeeLevel.For(30).ToGo.ShouldBe(20);
    }

    [Fact]
    public void A_negative_count_is_treated_as_zero()
    {
        CoffeeLevel.For(-3).Index.ShouldBe(0);
    }
}
