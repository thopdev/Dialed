using Dialed.Domain.Model;
using Dialed.Domain.Statistics;

namespace Dialed.Domain.Tests;

public sealed class CupCostTests
{
    private static Bag Priced(string id = "b", int? cents = 2400, decimal weight = 250m, string? currency = "EUR") =>
        new() { Id = id, BeanId = "bean", WeightG = weight, PriceCents = cents, Currency = currency };

    private static Brew Brewed(string bag, decimal dose, int month = 11, int year = 2026, bool deleted = false) => new()
    {
        BagId = bag,
        MethodCode = "v60",
        DoseG = dose,
        Deleted = deleted,
        BrewedAt = new DateTimeOffset(year, month, 10, 12, 0, 0, TimeSpan.FromHours(1)),
    };

    [Fact]
    public void A_brew_costs_the_price_per_gram_times_its_dose()
    {
        CupCost.ForBrew(Brewed("b", 15m), Priced()).ShouldBe(144);
        CupCost.ForBrew(Brewed("b", 18m), Priced(cents: 1650, weight: 500m)).ShouldBe(59);
    }

    [Fact]
    public void Without_a_price_a_weight_or_a_dose_there_is_no_cost()
    {
        CupCost.ForBrew(Brewed("b", 15m), Priced(cents: null)).ShouldBeNull();
        CupCost.ForBrew(Brewed("b", 15m), Priced(cents: 0)).ShouldBeNull();
        CupCost.ForBrew(Brewed("b", 15m), Priced(weight: 0m)).ShouldBeNull();
        CupCost.ForBrew(Brewed("b", 0m), Priced()).ShouldBeNull();
    }

    [Fact]
    public void A_bags_usual_cup_uses_the_average_dose_of_its_brews()
    {
        CupCost.AverageForBag(Priced(), [Brewed("b", 14m), Brewed("b", 16m), Brewed("b", 99m, deleted: true), Brewed("other", 50m)]).ShouldBe(144);
        CupCost.AverageForBag(Priced(), []).ShouldBeNull();
        CupCost.AverageForBag(Priced(cents: null), [Brewed("b", 15m)]).ShouldBeNull();
    }

    [Fact]
    public void Per_month_the_average_cup_is_worked_out_per_currency_newest_first()
    {
        Bag[] bags = [Priced("eur"), Priced("usd", cents: 2000, currency: "usd"), Priced("free", cents: null)];
        Brew[] brews = [Brewed("eur", 15m), Brewed("eur", 20m), Brewed("eur", 10m, month: 10), Brewed("usd", 10m), Brewed("free", 15m), Brewed("eur", 15m, deleted: true)];

        var months = CupCost.PerMonth(brews, bags);

        months.Select(m => (m.Year, m.Month, m.Currency, m.Brews, m.TotalCents, m.AverageCents)).ShouldBe(
        [
            (2026, 11, "EUR", 2, 336, 168),
            (2026, 11, "USD", 1, 80, 80),
            (2026, 10, "EUR", 1, 96, 96),
        ]);
    }
}
