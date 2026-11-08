using Dialed.Domain.Equipment;

namespace Dialed.Domain.Tests;

public sealed class GrindScaleTests
{
    [Theory]
    [InlineData(0, GrindValidation.Valid)]
    [InlineData(18, GrindValidation.Valid)]
    [InlineData(40, GrindValidation.Valid)]
    [InlineData(-1, GrindValidation.BelowMinimum)]
    [InlineData(41, GrindValidation.AboveMaximum)]
    [InlineData(18.5, GrindValidation.NotOnStep)]
    public void Click_scales_accept_whole_clicks_in_range(decimal value, GrindValidation expected)
    {
        GrindScale.Clicks(40).Validate(value).ShouldBe(expected);
    }

    [Theory]
    [InlineData(1, GrindValidation.Valid)]
    [InlineData(5.5, GrindValidation.Valid)]
    [InlineData(11, GrindValidation.Valid)]
    [InlineData(5.25, GrindValidation.NotOnStep)]
    [InlineData(0.5, GrindValidation.BelowMinimum)]
    [InlineData(11.5, GrindValidation.AboveMaximum)]
    public void Stepped_dials_check_the_step_from_the_minimum(decimal value, GrindValidation expected)
    {
        GrindScale.Number(1m, 11m, 0.5m).Validate(value).ShouldBe(expected);
    }

    [Theory]
    [InlineData(0.1, GrindValidation.Valid)]
    [InlineData(3.14159, GrindValidation.Valid)]
    [InlineData(11.1, GrindValidation.AboveMaximum)]
    public void Stepless_dials_accept_any_decimal_in_range(decimal value, GrindValidation expected)
    {
        var scale = GrindScale.Number(0m, 11m);

        scale.IsStepped.ShouldBeFalse();
        scale.Validate(value).ShouldBe(expected);
    }

    [Fact]
    public void Steppers_move_by_one_step_and_stop_at_the_ends()
    {
        var scale = GrindScale.Clicks(40);

        scale.Increase(18m).ShouldBe(19m);
        scale.Decrease(18m).ShouldBe(17m);
        scale.Increase(40m).ShouldBe(40m);
        scale.Decrease(0m).ShouldBe(0m);
    }

    [Fact]
    public void Steppers_do_nothing_on_a_stepless_scale()
    {
        var scale = GrindScale.Number(0m, 11m);

        scale.Increase(4.2m).ShouldBe(4.2m);
        scale.Decrease(4.2m).ShouldBe(4.2m);
    }

    [Theory]
    [InlineData(18.4, 18)]
    [InlineData(18.5, 19)]
    [InlineData(-5, 0)]
    [InlineData(99, 40)]
    public void Snap_gives_the_nearest_valid_click(decimal value, decimal expected)
    {
        GrindScale.Clicks(40).Snap(value).ShouldBe(expected);
    }

    [Fact]
    public void Stepper_snaps_an_off_step_value_first()
    {
        GrindScale.Number(1m, 11m, 0.5m).Increase(5.2m).ShouldBe(5.5m);
    }

    [Theory]
    [InlineData("18", 18)]
    [InlineData(" 5.5 ", 5.5)]
    [InlineData("5,5", 5.5)]
    [InlineData("-2", -2)]
    public void TryParse_accepts_point_and_comma(string text, decimal expected)
    {
        GrindScale.TryParse(text, out var value).ShouldBeTrue();
        value.ShouldBe(expected);
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData(null)]
    [InlineData("abc")]
    [InlineData("1e3")]
    [InlineData("1,000.5")]
    public void TryParse_rejects_anything_else(string? text)
    {
        GrindScale.TryParse(text, out _).ShouldBeFalse();
    }

    [Fact]
    public void Format_drops_trailing_zeros()
    {
        GrindScale.Format(18m).ShouldBe("18");
        GrindScale.Format(5.50m).ShouldBe("5.5");
    }

    [Fact]
    public void Scales_reject_an_impossible_range_or_step()
    {
        Should.Throw<ArgumentException>(() => GrindScale.Number(5m, 5m));
        Should.Throw<ArgumentOutOfRangeException>(() => GrindScale.Number(0m, 10m, -1m));
        Should.Throw<ArgumentOutOfRangeException>(() => GrindScale.Number(0m, 10m, 11m));
    }

    [Fact]
    public void Click_scales_can_start_above_zero()
    {
        var scale = GrindScale.Clicks(max: 30m, min: 5m);

        scale.Validate(4m).ShouldBe(GrindValidation.BelowMinimum);
        scale.Decrease(5m).ShouldBe(5m);
        scale.Kind.ShouldBe(GrindSettingKind.Clicks);
    }
}
