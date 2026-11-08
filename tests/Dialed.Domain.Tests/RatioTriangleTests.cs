using Dialed.Domain.Model;

namespace Dialed.Domain.Tests;

public sealed class RatioTriangleTests
{
    [Fact]
    public void Dose_and_liquid_give_the_ratio()
    {
        var t = new RatioTriangle(dose: 15m, liquid: 240m);
        t.Ratio.ShouldBe(16m);
    }

    [Fact]
    public void Dose_then_ratio_gives_the_liquid()
    {
        var t = new RatioTriangle();
        t.Set(RatioField.Dose, 18m);
        t.Set(RatioField.Ratio, 16.5m);

        t.Liquid.ShouldBe(297m);
    }

    [Fact]
    public void Liquid_then_ratio_gives_the_dose()
    {
        var t = new RatioTriangle();
        t.Set(RatioField.Liquid, 250m);
        t.Set(RatioField.Ratio, 16m);

        t.Dose.ShouldBe(15.6m);
    }

    [Fact]
    public void Changing_the_dose_keeps_the_ratio_and_moves_the_liquid()
    {
        var t = new RatioTriangle(dose: 15m, liquid: 240m);

        // Editing the ratio recalculates the field typed longest ago, the dose.
        t.Set(RatioField.Ratio, 15m);
        t.Dose.ShouldBe(16m);
        t.Liquid.ShouldBe(240m);

        // Typing the dose again: the liquid is now the oldest, so it follows.
        t.Set(RatioField.Dose, 20m);
        t.Ratio.ShouldBe(15m);
        t.Liquid.ShouldBe(300m);
    }

    [Fact]
    public void Editing_the_liquid_after_dose_and_ratio_keeps_dose_and_changes_the_ratio()
    {
        var t = new RatioTriangle();
        t.Set(RatioField.Ratio, 2m);
        t.Set(RatioField.Dose, 18m);
        t.Liquid.ShouldBe(36m);

        t.Set(RatioField.Liquid, 45m);

        t.Dose.ShouldBe(18m);
        t.Ratio.ShouldBe(2.5m);
    }

    [Fact]
    public void Nothing_is_calculated_with_one_value_or_a_zero_divisor()
    {
        var t = new RatioTriangle();
        t.Set(RatioField.Dose, 18m);
        t.Liquid.ShouldBeNull();
        t.Ratio.ShouldBeNull();

        var zero = new RatioTriangle(dose: 0m, liquid: 100m);
        zero.Ratio.ShouldBeNull();
    }

    [Fact]
    public void Clearing_a_field_stops_it_counting_as_typed()
    {
        var t = new RatioTriangle(dose: 15m, liquid: 240m);
        t.Set(RatioField.Dose, null);
        t.Dose.ShouldBeNull();

        t.Set(RatioField.Dose, 20m);
        t.Ratio.ShouldBe(12m);
    }

    [Fact]
    public void Recipe_ratio_uses_water_or_yield()
    {
        new Recipe { MethodCode = "v60", Name = "x", DoseG = 15m, WaterG = 240m }.Ratio.ShouldBe(16m);
        new Recipe { MethodCode = "espresso", Name = "x", DoseG = 18m, YieldG = 36m }.Ratio.ShouldBe(2m);
        new Recipe { MethodCode = "v60", Name = "x", DoseG = 15m }.Ratio.ShouldBeNull();
        new Recipe { MethodCode = "v60", Name = "x", WaterG = 240m }.Ratio.ShouldBeNull();
    }
}
