using Bunit;
using Dialed.Domain.Equipment;
using Dialed.UI.Components;

namespace Dialed.UI.Tests;

public sealed class GrindSettingInputTests : UiTestContext
{
    private static readonly GrindScale Clicks = GrindScale.Clicks(40);
    private static readonly GrindScale Stepless = GrindScale.Number(0m, 11m);

    private IRenderedComponent<GrindSettingInput> RenderInput(GrindScale scale, decimal? value, Action<decimal?>? changed = null) =>
        Render<GrindSettingInput>(p => p
            .Add(x => x.Scale, scale)
            .Add(x => x.Value, value)
            .Add(x => x.Label, "Grind")
            .Add(x => x.ValueChanged, Microsoft.AspNetCore.Components.EventCallback.Factory.Create<decimal?>(this, v => changed?.Invoke(v))));

    [Fact]
    public void Click_grinders_get_steppers_and_a_unit()
    {
        var input = RenderInput(Clicks, 18m);

        input.FindAll(".dialed-grind-step").Count.ShouldBe(2);
        input.Find("input").GetAttribute("value").ShouldBe("18");
        input.Find(".dialed-grind-unit").TextContent.ShouldBe("clicks");
    }

    [Fact]
    public void Stepless_grinders_only_get_a_decimal_field()
    {
        var input = RenderInput(Stepless, 4.5m);

        input.FindAll(".dialed-grind-step").ShouldBeEmpty();
        input.FindAll(".dialed-grind-unit").ShouldBeEmpty();
    }

    [Fact]
    public void Coarser_and_finer_buttons_change_the_value_by_one_click()
    {
        decimal? latest = null;
        var input = RenderInput(Clicks, 18m, v => latest = v);

        input.Find("button[aria-label=Coarser]").Click();
        latest.ShouldBe(19m);
        input.Find("input").GetAttribute("value").ShouldBe("19");

        input.Find("button[aria-label=Finer]").Click();
        input.Find("button[aria-label=Finer]").Click();
        latest.ShouldBe(17m);
    }

    [Fact]
    public void Buttons_are_disabled_at_the_ends_of_the_range()
    {
        RenderInput(Clicks, 0m).Find("button[aria-label=Finer]").HasAttribute("disabled").ShouldBeTrue();
        RenderInput(Clicks, 40m).Find("button[aria-label=Coarser]").HasAttribute("disabled").ShouldBeTrue();
    }

    [Fact]
    public void An_empty_field_starts_at_the_lowest_setting_when_stepped()
    {
        decimal? latest = null;
        var input = RenderInput(Clicks, null, v => latest = v);

        input.Find("button[aria-label=Coarser]").Click();

        latest.ShouldBe(0m);
    }

    [Fact]
    public void Typing_a_valid_value_reports_it()
    {
        decimal? latest = null;
        var input = RenderInput(Stepless, null, v => latest = v);

        input.Find("input").Change("5,5");

        latest.ShouldBe(5.5m);
        input.FindAll(".dialed-grind-error").ShouldBeEmpty();
    }

    [Theory]
    [InlineData("12", "The highest setting is 11.")]
    [InlineData("-1", "The lowest setting is 0.")]
    [InlineData("abc", "Enter a number.")]
    public void Typing_an_invalid_value_shows_an_error_and_reports_nothing(string typed, string message)
    {
        var reported = false;
        var input = RenderInput(Stepless, 4m, _ => reported = true);

        input.Find("input").Change(typed);

        input.Find(".dialed-grind-error").TextContent.ShouldBe(message);
        input.Find("input").GetAttribute("aria-invalid").ShouldBe("true");
        reported.ShouldBeFalse();
    }

    [Fact]
    public void A_value_between_clicks_is_rejected()
    {
        var input = RenderInput(Clicks, 18m);

        input.Find("input").Change("18.5");

        input.Find(".dialed-grind-error").TextContent.ShouldBe("Use steps of 1.");
    }

    [Fact]
    public void Clearing_the_field_reports_no_value()
    {
        decimal? latest = 1m;
        var input = RenderInput(Stepless, 4m, v => latest = v);

        input.Find("input").Change(string.Empty);

        latest.ShouldBeNull();
    }

    [Fact]
    public void Outside_changes_update_the_field()
    {
        var input = RenderInput(Clicks, 18m);

        input.Render(p => p.Add(x => x.Value, 22m));

        input.Find("input").GetAttribute("value").ShouldBe("22");
    }
}
