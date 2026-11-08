using Dialed.Domain.Equipment;

namespace Dialed.Domain.Tests;

public sealed class GrindScaleInputTests
{
    [Fact]
    public void Clicks_from_zero_by_default()
    {
        GrindScaleInput.TryCreate(GrindSettingKind.Clicks, null, 40m, null, out var scale, out var error).ShouldBeTrue();
        error.ShouldBeNull();
        scale!.Kind.ShouldBe(GrindSettingKind.Clicks);
        scale.Min.ShouldBe(0m);
        scale.Max.ShouldBe(40m);
        scale.Step.ShouldBe(1m);
    }

    [Fact]
    public void Stepped_and_stepless_dials()
    {
        GrindScaleInput.TryCreate(GrindSettingKind.Number, 1m, 10m, 0.5m, out var stepped, out _).ShouldBeTrue();
        stepped!.IsStepped.ShouldBeTrue();
        GrindScaleInput.TryCreate(GrindSettingKind.Number, 1m, 10m, null, out var stepless, out _).ShouldBeTrue();
        stepless!.IsStepped.ShouldBeFalse();
    }

    [Theory]
    [InlineData(null, null, GrindScaleInput.ErrorMissing)]
    [InlineData(0, 0, GrindScaleInput.ErrorRange)]
    [InlineData(5, 3, GrindScaleInput.ErrorRange)]
    [InlineData(-1, 3, GrindScaleInput.ErrorRange)]
    public void Rejects_a_bad_range(int? min, int? max, string key)
    {
        GrindScaleInput.TryCreate(GrindSettingKind.Number, min, max, null, out var scale, out var error).ShouldBeFalse();
        scale.ShouldBeNull();
        error.ShouldBe(key);
    }

    [Fact]
    public void Rejects_a_step_bigger_than_the_range()
    {
        GrindScaleInput.TryCreate(GrindSettingKind.Number, 1m, 3m, 5m, out _, out var error).ShouldBeFalse();
        error.ShouldBe(GrindScaleInput.ErrorStep);
    }

    [Fact]
    public void Describes_each_kind()
    {
        GrindScaleInput.Describe(GrindScale.Clicks(40m)).ShouldBe("0–40 clicks");
        GrindScaleInput.Describe(GrindScale.Number(1m, 10m, 0.5m)).ShouldBe("1–10, step 0.5");
        GrindScaleInput.Describe(GrindScale.Number(1m, 10m)).ShouldBe("1–10, stepless");
    }
}
