using Dialed.Domain.Inventory;
using Dialed.Domain.Model;

namespace Dialed.Domain.Tests;

public sealed class BeanQueryTests
{
    private static Bean B(string id, string name, string? roaster = null, string? process = null, RoastLevel? level = null, int? rating = null, int day = 1) =>
        new() { Id = id, Name = name, RoasterId = roaster, Process = process, RoastLevel = level, Rating = rating, CreatedAt = new DateTimeOffset(2026, 1, day, 0, 0, 0, TimeSpan.Zero) };

    private static Bag Bg(string beanId, BagStatus status = BagStatus.Sealed) => new() { BeanId = beanId, WeightG = 250m, Status = status };

    private static readonly Bean[] Beans =
    [
        B("1", "Gesha Village", "r1", "Washed", RoastLevel.Light, 5, 1),
        B("2", "Brazil Santos", "r2", "Natural", RoastLevel.Medium, 3, 2),
        B("3", "Ethiopia Guji", "r1", "natural", RoastLevel.Light, null, 3),
    ];

    [Fact]
    public void Sorts_by_name_by_default() =>
        BeanQuery.Apply(Beans, [], new BeanFilter(), BeanSort.Name).Select(b => b.Id).ShouldBe(["2", "3", "1"]);

    [Fact]
    public void Sorts_by_rating_then_name() =>
        BeanQuery.Apply(Beans, [], new BeanFilter(), BeanSort.Rating).Select(b => b.Id).ShouldBe(["1", "2", "3"]);

    [Fact]
    public void Sorts_newest_first() =>
        BeanQuery.Apply(Beans, [], new BeanFilter(), BeanSort.Newest).Select(b => b.Id).ShouldBe(["3", "2", "1"]);

    [Fact]
    public void Filters_by_roaster_process_and_level()
    {
        BeanQuery.Apply(Beans, [], new BeanFilter(RoasterId: "r1"), BeanSort.Name).Select(b => b.Id).ShouldBe(["3", "1"]);
        BeanQuery.Apply(Beans, [], new BeanFilter(Process: "Natural"), BeanSort.Name).Select(b => b.Id).ShouldBe(["2", "3"]);
        BeanQuery.Apply(Beans, [], new BeanFilter(RoastLevel: RoastLevel.Light), BeanSort.Name).Select(b => b.Id).ShouldBe(["3", "1"]);
        BeanQuery.Apply(Beans, [], new BeanFilter(RoasterId: "r1", RoastLevel: RoastLevel.Light, Process: "washed"), BeanSort.Name).Select(b => b.Id).ShouldBe(["1"]);
    }

    [Fact]
    public void In_stock_means_a_bag_that_is_not_finished()
    {
        Bag[] bags = [Bg("1"), Bg("2", BagStatus.Finished), Bg("3", BagStatus.Finished), Bg("3", BagStatus.Frozen)];

        BeanQuery.Apply(Beans, bags, new BeanFilter(InStock: true), BeanSort.Name).Select(b => b.Id).ShouldBe(["3", "1"]);
        BeanQuery.Apply(Beans, bags, new BeanFilter(InStock: false), BeanSort.Name).Select(b => b.Id).ShouldBe(["2"]);
    }

    [Fact]
    public void Search_ranks_forgivingly_and_combines_with_filters()
    {
        BeanQuery.Apply(Beans, [], new BeanFilter(Search: "gesa"), BeanSort.Name).Select(b => b.Id).ShouldBe(["1"]);
        BeanQuery.Apply(Beans, [], new BeanFilter(Search: "gesha", RoasterId: "r2"), BeanSort.Name).ShouldBeEmpty();
    }

    [Fact]
    public void Deleted_beans_are_hidden() =>
        BeanQuery.Apply([B("1", "A") with { Deleted = true }], [], new BeanFilter(), BeanSort.Name).ShouldBeEmpty();

    [Fact]
    public void Known_processes_are_recognised_ignoring_case()
    {
        BeanQuery.IsKnownProcess("washed").ShouldBeTrue();
        BeanQuery.IsKnownProcess("Yeast ferment").ShouldBeFalse();
        BeanQuery.IsKnownProcess(null).ShouldBeFalse();
    }

    [Fact]
    public void AddUnique_trims_and_ignores_duplicates_and_blanks()
    {
        IReadOnlyList<string> list = ["Bourbon"];

        BeanQuery.AddUnique(list, " Gesha ").ShouldBe(["Bourbon", "Gesha"]);
        BeanQuery.AddUnique(list, "bourbon").ShouldBeSameAs(list);
        BeanQuery.AddUnique(list, "  ").ShouldBeSameAs(list);
        BeanQuery.AddUnique(list, null).ShouldBeSameAs(list);
    }
}
