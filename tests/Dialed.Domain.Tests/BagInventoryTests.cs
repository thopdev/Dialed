using Dialed.Domain.Inventory;

namespace Dialed.Domain.Tests;

public sealed class BagInventoryTests
{
    private static InventoryAdjustment Adjustment(decimal amountG, AdjustmentReason reason = AdjustmentReason.Other, string id = "a") =>
        new(id, "bag-1", amountG, reason, DateTimeOffset.UnixEpoch);

    [Fact]
    public void Remaining_is_weight_minus_brews_minus_adjustments()
    {
        var remaining = BagInventory.Remaining(
            250m,
            [18m, 18m, 17.5m],
            [Adjustment(5m, AdjustmentReason.Spilled), Adjustment(10m, AdjustmentReason.Gifted, "b")]);

        remaining.ShouldBe(181.5m);
    }

    [Fact]
    public void A_fresh_bag_has_its_full_weight_left()
    {
        BagInventory.Remaining(340m, [], []).ShouldBe(340m);
    }

    [Fact]
    public void A_negative_correction_puts_coffee_back()
    {
        BagInventory.Remaining(250m, [50m], [Adjustment(-10m, AdjustmentReason.Correction)]).ShouldBe(210m);
    }

    [Fact]
    public void Brews_logged_on_two_devices_are_both_counted_after_a_sync()
    {
        // Each device only knew its own brews; the sum is the same whichever order they arrive in.
        decimal[] phoneBrews = [18m, 18m];
        decimal[] tabletBrews = [20m];

        var merged = BagInventory.Remaining(250m, phoneBrews.Concat(tabletBrews), []);
        var reversed = BagInventory.Remaining(250m, tabletBrews.Concat(phoneBrews), []);

        merged.ShouldBe(194m);
        reversed.ShouldBe(merged);
    }

    [Fact]
    public void Remaining_can_be_negative_but_is_displayed_as_zero()
    {
        var remaining = BagInventory.Remaining(250m, [260m], []);

        remaining.ShouldBe(-10m);
        BagInventory.Display(remaining).ShouldBe(0m);
        BagInventory.Display(42m).ShouldBe(42m);
    }

    [Theory]
    [InlineData(250, 125, 0.5)]
    [InlineData(250, 0, 0)]
    [InlineData(250, -5, 0)]
    [InlineData(250, 300, 1)]
    [InlineData(0, 10, 0)]
    public void FractionLeft_stays_between_zero_and_one(decimal weight, decimal remaining, decimal expected)
    {
        BagInventory.FractionLeft(weight, remaining).ShouldBe(expected);
    }

    [Theory]
    [InlineData(BagStatus.Open, 0, true)]
    [InlineData(BagStatus.Open, -3, true)]
    [InlineData(BagStatus.Frozen, 0, true)]
    [InlineData(BagStatus.Open, 0.5, false)]
    [InlineData(BagStatus.Finished, 0, false)]
    public void Finish_is_offered_when_nothing_is_left(BagStatus status, decimal remaining, bool expected)
    {
        BagInventory.ShouldOfferFinish(status, remaining).ShouldBe(expected);
    }

    [Fact]
    public void Null_inputs_are_rejected()
    {
        Should.Throw<ArgumentNullException>(() => BagInventory.Remaining(1m, null!, []));
        Should.Throw<ArgumentNullException>(() => BagInventory.Remaining(1m, [], null!));
    }
}
