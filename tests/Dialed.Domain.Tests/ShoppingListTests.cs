using Dialed.Domain.Inventory;
using Dialed.Domain.Model;

namespace Dialed.Domain.Tests;

public sealed class ShoppingListTests
{
    private static BagStock Stock(string bag, string bean, string name, BagStatus status, bool low = false, decimal weight = 250m) =>
        new(new Bag { Id = bag, BeanId = bean, WeightG = weight, Status = status }, name, low ? 30m : 200m, 10, 5, Freshness.Peak, low);

    private static Brew Rated(string bag, int rating) => new() { BagId = bag, MethodCode = "v60", DoseG = 15m, Rating = rating, BrewedAt = DateTimeOffset.UtcNow };

    [Fact]
    public void An_open_bag_running_low_with_nothing_else_is_on_the_list()
    {
        var items = ShoppingList.Build([Stock("a1", "a", "Guji", BagStatus.Open, low: true)], []);

        items.ShouldBe([new ShoppingItem("a", "Guji", ShoppingReason.RunningLow, null, 250m)]);
    }

    [Fact]
    public void A_bean_you_rated_well_that_is_all_gone_is_a_buy_again()
    {
        var items = ShoppingList.Build([Stock("a1", "a", "Guji", BagStatus.Finished)], [Rated("a1", 5), Rated("a1", 4)]);

        var item = items.ShouldHaveSingleItem();
        (item.Reason, item.AverageRating).ShouldBe((ShoppingReason.BuyAgain, 4.5));
    }

    [Fact]
    public void Beans_with_a_sealed_frozen_or_healthy_open_bag_are_not_listed()
    {
        BagStock[] stock =
        [
            Stock("s1", "s", "Sealed", BagStatus.Open, low: true), Stock("s2", "s", "Sealed", BagStatus.Sealed),
            Stock("f1", "f", "Frozen", BagStatus.Finished), Stock("f2", "f", "Frozen", BagStatus.Frozen),
            Stock("h1", "h", "Healthy", BagStatus.Open),
        ];

        ShoppingList.Build(stock, [Rated("f1", 5)]).ShouldBeEmpty();
    }

    [Fact]
    public void Finished_beans_that_were_not_rated_well_are_left_off_and_the_list_is_ordered()
    {
        BagStock[] stock = [Stock("z1", "z", "Zed", BagStatus.Open, low: true), Stock("a1", "a", "Alpha", BagStatus.Open, low: true), Stock("m1", "m", "Mid", BagStatus.Finished), Stock("g1", "g", "Good", BagStatus.Finished), Stock("n1", "n", "Never rated", BagStatus.Finished)];

        var items = ShoppingList.Build(stock, [Rated("m1", 2), Rated("g1", 4)]);

        items.Select(i => (i.Name, i.Reason)).ShouldBe([("Alpha", ShoppingReason.RunningLow), ("Zed", ShoppingReason.RunningLow), ("Good", ShoppingReason.BuyAgain)]);
    }
}
