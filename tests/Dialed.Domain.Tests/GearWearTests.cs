using Dialed.Domain.Equipment;
using Dialed.Domain.Model;

namespace Dialed.Domain.Tests;

public sealed class GearWearTests
{
    private static readonly DateOnly Today = new(2026, 11, 11);

    private static EquipmentItem Grinder(int? everyBrews = null, int? everyKg = null, DateOnly? servicedOn = null) =>
        new() { Id = "g1", Kind = EquipmentKind.Grinder, Brand = "Eureka", ServiceEveryBrews = everyBrews, ServiceEveryKg = everyKg, ServicedOn = servicedOn };

    private static Brew Brewed(DateOnly day, string? grinder = "g1", string? recipe = null, decimal dose = 18m, bool deleted = false) => new()
    {
        BagId = "bag",
        MethodCode = "espresso",
        DoseG = dose,
        GrinderId = grinder,
        RecipeId = recipe,
        Deleted = deleted,
        BrewedAt = new DateTimeOffset(day.ToDateTime(new TimeOnly(8, 0)), TimeZoneInfo.Local.GetUtcOffset(day.ToDateTime(new TimeOnly(8, 0)))),
    };

    private static List<Brew> Many(int n, DateOnly start) => [.. Enumerable.Range(0, n).Select(i => Brewed(start.AddDays(i)))];

    [Fact]
    public void Gear_without_an_interval_has_no_nudge()
    {
        GearWear.Evaluate(Grinder(), Many(500, Today.AddDays(-600)), []).State.ShouldBe(WearState.None);
    }

    [Fact]
    public void Brews_against_the_interval_go_from_ok_to_soon_to_due()
    {
        GearWear.Evaluate(Grinder(everyBrews: 100), Many(50, Today.AddDays(-60)), []).State.ShouldBe(WearState.Ok);
        GearWear.Evaluate(Grinder(everyBrews: 100), Many(90, Today.AddDays(-100)), []).State.ShouldBe(WearState.Soon);

        var due = GearWear.Evaluate(Grinder(everyBrews: 100), Many(100, Today.AddDays(-120)), []);
        due.State.ShouldBe(WearState.Due);
        due.Brews.ShouldBe(100);
    }

    [Fact]
    public void Kilograms_of_coffee_count_too_and_whichever_comes_first_wins()
    {
        // 60 brews of 18 g is about 1.1 kg: a 1 kg interval is due long before 500 brews.
        var status = GearWear.Evaluate(Grinder(everyBrews: 500, everyKg: 1), Many(60, Today.AddDays(-70)), []);

        status.State.ShouldBe(WearState.Due);
        status.Kilograms.ShouldBe(1.08m);
    }

    [Fact]
    public void Only_brews_after_the_service_date_count()
    {
        var serviced = Today.AddDays(-10);
        var brews = new List<Brew> { Brewed(serviced.AddDays(-5)), Brewed(serviced), Brewed(serviced.AddDays(1)), Brewed(serviced.AddDays(2)) };

        GearWear.Evaluate(Grinder(everyBrews: 100, servicedOn: serviced), brews, []).Brews.ShouldBe(2);
    }

    [Fact]
    public void A_grinder_counts_its_own_brews_and_a_brewer_counts_through_the_recipe()
    {
        var brewer = new EquipmentItem { Id = "m1", Kind = EquipmentKind.EspressoMachine, Brand = "Lelit", ServiceEveryBrews = 10 };
        Recipe[] recipes = [new() { Id = "r1", MethodCode = "espresso", Name = "Daily", BrewerId = "m1" }, new() { Id = "r2", MethodCode = "espresso", Name = "Other", BrewerId = "m2" }];
        var brews = new List<Brew> { Brewed(Today, grinder: "g1", recipe: "r1"), Brewed(Today, grinder: "g2", recipe: "r1"), Brewed(Today, grinder: "g1", recipe: "r2"), Brewed(Today, grinder: "g1"), Brewed(Today, recipe: "r1", deleted: true) };

        GearWear.Evaluate(brewer, brews, recipes).Brews.ShouldBe(2); // via recipe r1, not deleted
        GearWear.Evaluate(Grinder(everyBrews: 10), brews, recipes).Brews.ShouldBe(3); // own grinder id, deleted one not counted (it has g1 too)
    }
}
