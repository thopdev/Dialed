using Dialed.Domain.DialIn;
using Dialed.Domain.Equipment;
using Dialed.Domain.Model;

namespace Dialed.Domain.Tests;

public sealed class GrindDriftTests
{
    private static readonly DateOnly Today = new(2026, 11, 20);

    private static Brew Brewed(int day, decimal? setting, string method = "v60", string? grinder = "g1", string bag = "bag") => new()
    {
        BagId = bag,
        MethodCode = method,
        DoseG = 15m,
        GrinderId = grinder,
        GrindSetting = setting,
        BrewedAt = new DateTimeOffset(2026, 11, day, 12, 0, 0, TimeSpan.Zero),
    };

    private static readonly GrindScale Clicks = GrindScale.Clicks(40);

    [Fact]
    public void A_setting_that_keeps_getting_finer_is_a_drift_with_a_trend_for_today()
    {
        var note = GrindDrift.Detect("bag", [Brewed(1, 20), Brewed(5, 19), Brewed(9, 18), Brewed(13, 17)], Clicks, Today);

        note.ShouldNotBeNull();
        note.Finer.ShouldBeTrue();
        (note.From, note.To, note.Days, note.Brews).ShouldBe((20m, 17m, 12, 4));
        note.Suggested.ShouldBe(15m);
    }

    [Fact]
    public void A_setting_that_gets_coarser_is_reported_as_coarser()
    {
        var note = GrindDrift.Detect("bag", [Brewed(1, 10), Brewed(6, 11), Brewed(12, 12)], Clicks, new DateOnly(2026, 11, 12));

        note!.Finer.ShouldBeFalse();
        note.Suggested.ShouldBeNull();
    }

    [Fact]
    public void Too_few_brews_or_too_short_a_time_is_not_a_drift()
    {
        GrindDrift.Detect("bag", [Brewed(1, 20), Brewed(12, 18)], Clicks, Today).ShouldBeNull();
        GrindDrift.Detect("bag", [Brewed(1, 20), Brewed(2, 19), Brewed(4, 18)], Clicks, Today).ShouldBeNull();
    }

    [Fact]
    public void Going_back_and_forth_is_not_a_drift()
    {
        GrindDrift.Detect("bag", [Brewed(1, 20), Brewed(5, 17), Brewed(9, 19), Brewed(13, 16)], Clicks, Today).ShouldBeNull();
    }

    [Fact]
    public void A_setting_that_did_not_move_is_not_a_drift()
    {
        GrindDrift.Detect("bag", [Brewed(1, 20), Brewed(5, 20), Brewed(13, 20)], Clicks, Today).ShouldBeNull();
    }

    [Fact]
    public void Other_methods_grinders_and_bags_are_left_out()
    {
        Brew[] brews = [Brewed(1, 20), Brewed(5, 12, method: "espresso"), Brewed(9, 18), Brewed(10, 5, grinder: "g2"), Brewed(13, 17), Brewed(14, 3, bag: "other")];

        var note = GrindDrift.Detect("bag", brews.Where(b => b.GrinderId == "g1" || b.MethodCode != "v60"), Clicks, Today);

        // The newest brew with a setting for this bag is the V60 on g1, so espresso is ignored.
        note.ShouldNotBeNull();
        (note.From, note.To, note.Brews).ShouldBe((20m, 17m, 3));
    }

    [Fact]
    public void Deleted_brews_and_brews_without_a_setting_do_not_count()
    {
        Brew[] brews = [Brewed(1, 20), Brewed(5, null), Brewed(9, 19) with { Deleted = true }, Brewed(13, 18)];

        GrindDrift.Detect("bag", brews, Clicks, Today).ShouldBeNull();
    }

    [Fact]
    public void Without_a_scale_the_trend_is_rounded_to_one_decimal()
    {
        var note = GrindDrift.Detect("bag", [Brewed(1, 5.0m), Brewed(5, 4.8m), Brewed(9, 4.6m), Brewed(13, 4.4m)], null, Today);

        note!.Finer.ShouldBeTrue();
        note.Suggested.ShouldBe(4.1m);
    }
}
