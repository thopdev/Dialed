using Dialed.Domain.Inventory;
using Dialed.Domain.Model;

namespace Dialed.Domain.Tests;

/// <summary>Which bags deserve a notification, and when a person is looked at (#259).</summary>
public sealed class StockAlertsTests
{
    private static readonly DateOnly Today = new(2026, 11, 11);

    private static BagStock Stock(string id, string bean, decimal remaining, BagStatus status = BagStatus.Open, int? ageDays = 14, Freshness freshness = Freshness.Peak, bool hasRoastDate = true, decimal low = 100m) =>
        new(
            new Bag { Id = id, BeanId = "b-" + id, WeightG = 250, Status = status, RoastDate = hasRoastDate ? Today.AddDays(-(ageDays ?? 0)) : null },
            bean,
            remaining,
            Cups: 5,
            ageDays,
            freshness,
            IsLow: status == BagStatus.Open && remaining < low);

    private static StockAlertResult Run(IEnumerable<BagStock> stock, StockAlertMemory? memory = null, bool low = true, bool peak = true, string household = "h1") =>
        StockAlerts.Evaluate(household, stock, memory ?? StockAlertMemory.Empty, low, peak);

    // ---- Low stock ----------------------------------------------------------------------------

    [Fact]
    public void A_bag_that_is_open_and_below_the_threshold_is_announced_once()
    {
        var first = Run([Stock("1", "Guji", 60m, freshness: Freshness.Resting)]);
        first.LowStock.ShouldBe(["Guji"]);

        var second = Run([Stock("1", "Guji", 40m, freshness: Freshness.Resting)], first.Memory);

        second.LowStock.ShouldBeEmpty();
    }

    [Fact]
    public void Topping_up_re_arms_the_alert()
    {
        var first = Run([Stock("1", "Guji", 60m, freshness: Freshness.Resting)]);
        var toppedUp = Run([Stock("1", "Guji", 300m, freshness: Freshness.Resting)], first.Memory);
        toppedUp.Memory.Low.ShouldBeEmpty();

        var again = Run([Stock("1", "Guji", 50m, freshness: Freshness.Resting)], toppedUp.Memory);

        again.LowStock.ShouldBe(["Guji"]);
    }

    [Fact]
    public void A_new_bag_of_the_same_bean_is_its_own_alert()
    {
        var first = Run([Stock("1", "Guji", 60m, freshness: Freshness.Resting)]);

        var next = Run([Stock("1", "Guji", 60m, freshness: Freshness.Resting), Stock("2", "Guji", 20m, freshness: Freshness.Resting)], first.Memory);

        next.LowStock.ShouldBe(["Guji"]);
        next.Memory.Low.ShouldBe(["h1/1", "h1/2"], ignoreOrder: true);
    }

    [Theory]
    [InlineData(BagStatus.Finished)]
    [InlineData(BagStatus.Frozen)]
    [InlineData(BagStatus.Sealed)]
    public void Only_open_bags_can_be_low(BagStatus status)
    {
        Run([Stock("1", "Guji", 10m, status, freshness: Freshness.Resting)]).LowStock.ShouldBeEmpty();
    }

    [Fact]
    public void A_deleted_bag_is_skipped()
    {
        var bag = Stock("1", "Guji", 10m, freshness: Freshness.Resting);

        Run([bag with { Bag = bag.Bag with { Deleted = true } }]).LowStock.ShouldBeEmpty();
    }

    // ---- Peak ---------------------------------------------------------------------------------

    [Fact]
    public void A_bag_entering_its_peak_window_is_announced_once_and_not_again_while_it_stays()
    {
        var resting = Run([Stock("1", "Kiambu", 200m, freshness: Freshness.Resting, ageDays: 3)]);
        resting.Peak.ShouldBeEmpty();

        var peak = Run([Stock("1", "Kiambu", 200m)], resting.Memory);
        peak.Peak.ShouldBe(["Kiambu"]);

        Run([Stock("1", "Kiambu", 200m)], peak.Memory).Peak.ShouldBeEmpty();
    }

    [Theory]
    [InlineData(BagStatus.Finished)]
    [InlineData(BagStatus.Frozen)]
    public void Finished_or_frozen_bags_never_peak(BagStatus status)
    {
        Run([Stock("1", "Kiambu", 200m, status)]).Peak.ShouldBeEmpty();
    }

    [Fact]
    public void A_bag_without_a_roast_date_is_skipped_for_the_peak_but_not_for_low_stock()
    {
        var result = Run([Stock("1", "Mystery", 20m, hasRoastDate: false, freshness: Freshness.Unknown, ageDays: null)]);

        result.Peak.ShouldBeEmpty();
        result.LowStock.ShouldBe(["Mystery"]);
    }

    [Fact]
    public void A_sealed_bag_inside_its_window_counts_for_the_peak()
    {
        Run([Stock("1", "Kiambu", 250m, BagStatus.Sealed)]).Peak.ShouldBe(["Kiambu"]);
    }

    // ---- Switches and combining ---------------------------------------------------------------

    [Fact]
    public void A_switch_that_is_off_announces_nothing_and_remembers_nothing()
    {
        var result = Run([Stock("1", "Guji", 20m)], low: false, peak: false);

        result.LowStock.ShouldBeEmpty();
        result.Peak.ShouldBeEmpty();
        result.Memory.Low.ShouldBeEmpty();
        result.Memory.Peak.ShouldBeEmpty();
    }

    [Fact]
    public void Several_bags_come_back_together_by_name()
    {
        var result = Run([Stock("1", "Zeta", 20m), Stock("2", "alpha", 30m), Stock("3", "Mid", 40m)], peak: false);

        result.LowStock.ShouldBe(["alpha", "Mid", "Zeta"]);
    }

    [Fact]
    public void Another_households_memory_is_kept()
    {
        var memory = new StockAlertMemory(new HashSet<string> { "h2/9" }, new HashSet<string>());

        var result = Run([Stock("1", "Guji", 20m)], memory, peak: false);

        result.Memory.Low.ShouldBe(["h1/1", "h2/9"], ignoreOrder: true);
    }

    // ---- When ---------------------------------------------------------------------------------

    private static readonly TimeZoneInfo Amsterdam = TimeZoneInfo.FindSystemTimeZoneById("Europe/Amsterdam");

    [Theory]
    [InlineData("2026-11-11T06:59:00Z", false)] // 07:59 in Amsterdam
    [InlineData("2026-11-11T07:00:00Z", true)] // 08:00
    [InlineData("2026-11-11T19:59:00Z", true)] // 20:59
    [InlineData("2026-11-11T20:00:00Z", false)] // 21:00: never at night
    [InlineData("2026-11-11T02:00:00Z", false)]
    public void A_person_is_looked_at_between_eight_and_nine_in_the_evening_in_their_own_time(string utc, bool due)
    {
        StockAlerts.IsDue(DateTimeOffset.Parse(utc), Amsterdam, lastHandled: null).ShouldBe(due);
    }

    [Fact]
    public void Someone_handled_today_is_not_looked_at_again_but_is_tomorrow()
    {
        var morning = DateTimeOffset.Parse("2026-11-11T07:30:00Z");
        var handled = StockAlerts.LocalDate(morning, Amsterdam);

        StockAlerts.IsDue(DateTimeOffset.Parse("2026-11-11T12:00:00Z"), Amsterdam, handled).ShouldBeFalse();
        StockAlerts.IsDue(DateTimeOffset.Parse("2026-11-12T07:30:00Z"), Amsterdam, handled).ShouldBeTrue();
    }

    [Fact]
    public void Daylight_saving_moves_the_utc_time_but_not_the_local_one()
    {
        // The clocks in Amsterdam went forward on 2026-03-29: 08:00 local is 07:00 UTC before and 06:00 UTC after.
        StockAlerts.IsDue(DateTimeOffset.Parse("2026-03-28T06:30:00Z"), Amsterdam, null).ShouldBeFalse(); // 07:30
        StockAlerts.IsDue(DateTimeOffset.Parse("2026-03-28T07:00:00Z"), Amsterdam, null).ShouldBeTrue(); // 08:00
        StockAlerts.IsDue(DateTimeOffset.Parse("2026-03-30T05:30:00Z"), Amsterdam, null).ShouldBeFalse(); // 07:30 CEST
        StockAlerts.IsDue(DateTimeOffset.Parse("2026-03-30T06:00:00Z"), Amsterdam, null).ShouldBeTrue(); // 08:00 CEST
    }

    [Fact]
    public void The_local_date_can_differ_from_the_utc_date()
    {
        var auckland = TimeZoneInfo.FindSystemTimeZoneById("Pacific/Auckland");

        StockAlerts.LocalDate(DateTimeOffset.Parse("2026-11-11T20:00:00Z"), auckland).ShouldBe(new DateOnly(2026, 11, 12));
    }
}
