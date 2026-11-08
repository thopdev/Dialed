using Dialed.Domain.Inventory;
using Dialed.Domain.Model;

namespace Dialed.Domain.Tests;

public sealed class BagLifecycleTests
{
    private static readonly DateOnly Roasted = new(2026, 1, 1);

    private static Bag NewBag() => new() { BeanId = "b", WeightG = 250m, RoastDate = Roasted };

    private static DateOnly D(int day) => new(2026, 1, day);

    [Fact]
    public void Open_sets_status_and_date_once()
    {
        BagLifecycle.TryOpen(NewBag(), D(5), out var opened).ShouldBeTrue();
        opened.Status.ShouldBe(BagStatus.Open);
        opened.OpenedOn.ShouldBe(D(5));
        BagLifecycle.TryOpen(opened, D(6), out _).ShouldBeFalse();
    }

    [Fact]
    public void Freeze_works_from_sealed_and_open_and_moves_to_the_freezer()
    {
        BagLifecycle.TryFreeze(NewBag(), D(3), out var frozen).ShouldBeTrue();
        frozen.Status.ShouldBe(BagStatus.Frozen);
        frozen.FrozenOn.ShouldBe(D(3));
        frozen.Storage.ShouldBe(BagStorage.Freezer);
        BagLifecycle.TryFreeze(frozen, D(4), out _).ShouldBeFalse();
    }

    [Fact]
    public void Thaw_adds_the_frozen_days_and_restores_the_status()
    {
        BagLifecycle.TryFreeze(NewBag(), D(3), out var frozen);
        BagLifecycle.TryThaw(frozen, D(13), out var thawed).ShouldBeTrue();

        thawed.Status.ShouldBe(BagStatus.Sealed);
        thawed.FrozenOn.ShouldBeNull();
        thawed.FrozenDays.ShouldBe(10);
        thawed.Storage.ShouldBe(BagStorage.Pantry);
    }

    [Fact]
    public void Thaw_of_an_opened_bag_returns_to_open()
    {
        BagLifecycle.TryOpen(NewBag(), D(2), out var opened);
        BagLifecycle.TryFreeze(opened, D(3), out var frozen);

        BagLifecycle.TryThaw(frozen, D(4), out var thawed).ShouldBeTrue();
        thawed.Status.ShouldBe(BagStatus.Open);
    }

    [Fact]
    public void Thaw_is_refused_when_not_frozen_or_before_the_freeze()
    {
        BagLifecycle.TryThaw(NewBag(), D(3), out _).ShouldBeFalse();
        BagLifecycle.TryFreeze(NewBag(), D(5), out var frozen);
        BagLifecycle.TryThaw(frozen, D(4), out _).ShouldBeFalse();
    }

    [Fact]
    public void Frozen_days_do_not_count_toward_age()
    {
        BagLifecycle.TryFreeze(NewBag(), D(3), out var frozen);
        BagLifecycle.TryThaw(frozen, D(13), out var thawed);

        // Roasted day 1, today day 20: 19 days, 10 of them frozen.
        BagLifecycle.AgeDays(thawed, D(20)).ShouldBe(9);
    }

    [Fact]
    public void Age_stands_still_while_frozen()
    {
        BagLifecycle.TryFreeze(NewBag(), D(3), out var frozen);

        BagLifecycle.AgeDays(frozen, D(3)).ShouldBe(2);
        BagLifecycle.AgeDays(frozen, D(25)).ShouldBe(2);
    }

    [Fact]
    public void Age_is_unknown_without_a_roast_date_and_never_negative()
    {
        (NewBag() with { RoastDate = null }).Let(b => BagLifecycle.AgeDays(b, D(5))).ShouldBeNull();
        BagLifecycle.AgeDays(NewBag(), new DateOnly(2025, 12, 1)).ShouldBe(0);
    }

    [Fact]
    public void Finish_stops_the_age_and_closes_an_open_freeze()
    {
        BagLifecycle.TryFreeze(NewBag(), D(3), out var frozen);
        BagLifecycle.TryFinish(frozen, D(10), out var finished).ShouldBeTrue();

        finished.Status.ShouldBe(BagStatus.Finished);
        finished.FinishedOn.ShouldBe(D(10));
        finished.FrozenOn.ShouldBeNull();
        finished.FrozenDays.ShouldBe(7);
        BagLifecycle.AgeDays(finished, D(30)).ShouldBe(2);
        BagLifecycle.TryFinish(finished, D(11), out _).ShouldBeFalse();
    }

    [Fact]
    public void Bought_again_copies_the_purchase_but_starts_fresh()
    {
        var last = NewBag() with { SellerId = "s", PriceCents = 1450, Currency = "EUR", Status = BagStatus.Finished, OpenedOn = D(2), FinishedOn = D(9), FrozenDays = 4 };

        var again = BagLifecycle.BoughtAgain(last, D(20));

        again.BeanId.ShouldBe("b");
        again.SellerId.ShouldBe("s");
        again.PriceCents.ShouldBe(1450);
        again.WeightG.ShouldBe(250m);
        again.PurchasedOn.ShouldBe(D(20));
        again.Status.ShouldBe(BagStatus.Sealed);
        again.RoastDate.ShouldBeNull();
        again.OpenedOn.ShouldBeNull();
        again.FinishedOn.ShouldBeNull();
        again.FrozenDays.ShouldBe(0);
    }
}

internal static class LetExtensions
{
    public static TResult Let<T, TResult>(this T value, Func<T, TResult> map) => map(value);
}
