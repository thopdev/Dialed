using Dialed.Domain.Equipment;
using Dialed.Domain.Model;

namespace Dialed.Domain.Tests;

public sealed class GearAlertsTests
{
    private static readonly DateOnly Today = new(2026, 11, 11);

    private static readonly IReadOnlySet<string> Nothing = new HashSet<string>();

    private static EquipmentItem Grinder(string id, string brand, DateOnly? servicedOn, int everyDays = 7, bool deleted = false) =>
        new() { Id = id, Kind = EquipmentKind.Grinder, Brand = brand, Model = "Mignon", ServiceEveryDays = everyDays, ServicedOn = servicedOn, Deleted = deleted };

    [Fact]
    public void Due_gear_is_named_in_order_and_remembered()
    {
        var result = GearAlerts.Evaluate("h", [Grinder("b", "Eureka", Today.AddDays(-8)), Grinder("a", "Baratza", Today.AddDays(-9)), Grinder("c", "Fresh", Today.AddDays(-1))], [], [], Today, Nothing);

        result.Due.ShouldBe(["Baratza Mignon", "Eureka Mignon"]);
        result.Memory.Count.ShouldBe(2);
    }

    [Fact]
    public void Gear_already_told_is_not_announced_again_until_it_is_serviced()
    {
        var item = Grinder("a", "Eureka", Today.AddDays(-8));
        var first = GearAlerts.Evaluate("h", [item], [], [], Today, Nothing);

        GearAlerts.Evaluate("h", [item], [], [], Today.AddDays(1), first.Memory).Due.ShouldBeEmpty();

        var serviced = item with { ServicedOn = Today };
        var later = GearAlerts.Evaluate("h", [serviced], [], [], Today.AddDays(1), first.Memory);
        later.Due.ShouldBeEmpty();
        later.Memory.ShouldBeEmpty();

        GearAlerts.Evaluate("h", [serviced], [], [], Today.AddDays(8), later.Memory).Due.ShouldBe(["Eureka Mignon"]);
    }

    [Fact]
    public void Deleted_gear_and_gear_without_an_interval_are_ignored_and_other_households_keep_their_memory()
    {
        var plain = new EquipmentItem { Id = "p", Kind = EquipmentKind.Kettle, Brand = "Fellow" };
        var other = new HashSet<string> { "other/x/2026-10-01" };

        var result = GearAlerts.Evaluate("h", [plain, Grinder("d", "Old", Today.AddDays(-20), deleted: true)], [], [], Today, other);

        result.Due.ShouldBeEmpty();
        result.Memory.ShouldBe(other);
    }

    [Fact]
    public void Gear_without_a_name_falls_back_to_its_kind()
    {
        var item = new EquipmentItem { Id = "g", Kind = EquipmentKind.Grinder, Brand = "", ServiceEveryDays = 1, ServicedOn = Today.AddDays(-3) };

        GearAlerts.Evaluate("h", [item], [], [], Today, Nothing).Due.ShouldBe(["Grinder"]);
    }
}
