using Bunit;
using Dialed.Client.Data;
using Dialed.Domain.Equipment;
using Dialed.Domain.Model;
using Dialed.UI.Pages;
using Microsoft.Extensions.DependencyInjection;

namespace Dialed.UI.Tests;

public sealed class GearShelfTests : UiTestContext
{
    private CancellationToken Ct => Xunit.TestContext.Current.CancellationToken;

    private IRepository<T> Store<T>() where T : SyncEntity => Services.GetRequiredService<IRepository<T>>();

    [Fact]
    public async Task The_shelf_shows_a_tile_per_item_with_its_service_status()
    {
        await Store<EquipmentItem>().SaveAsync(new EquipmentItem { Kind = EquipmentKind.Grinder, Brand = "Eureka", Model = "Mignon", ServiceEveryDays = 7, ServicedOn = DateOnly.FromDateTime(DateTime.Now).AddDays(-9) }, Ct);
        await Store<EquipmentItem>().SaveAsync(new EquipmentItem { Kind = EquipmentKind.Kettle, Brand = "Fellow" }, Ct);
        var page = Render<Gear>();

        page.FindAll("#gear-shelf").ShouldBeEmpty();
        page.Find("#gear-view-shelf").Click();

        var tiles = page.FindAll("#gear-shelf .dialed-shelf-tile");
        tiles.Count.ShouldBe(2);
        tiles[0].TextContent.ShouldContain("Eureka Mignon");
        tiles[0].TextContent.ShouldContain("Service due");
        page.Find("#gear-view-shelf").GetAttribute("aria-pressed").ShouldBe("true");

        page.Find("#gear-view-list").Click();
        page.FindAll("#gear-shelf").ShouldBeEmpty();
    }

    [Fact]
    public async Task A_grinder_lists_its_favourite_setting_per_bean()
    {
        var grinder = await Store<EquipmentItem>().SaveAsync(new EquipmentItem { Kind = EquipmentKind.Grinder, Brand = "Comandante", GrindScale = GrindScale.Clicks(40m) }, Ct);
        var bean = await Store<Bean>().SaveAsync(new Bean { Name = "Guji" }, Ct);
        var bag = await Store<Bag>().SaveAsync(new Bag { BeanId = bean.Id, WeightG = 250m }, Ct);
        await Store<Brew>().SaveAsync(new Brew { BagId = bag.Id, MethodCode = "v60", DoseG = 15m, GrinderId = grinder.Id, GrindSetting = 22m, Rating = 5, BrewedAt = DateTimeOffset.UtcNow }, Ct);

        var page = Render<Gear>(p => p.Add(x => x.Id, grinder.Id));

        page.WaitForElement("#gear-favorites").TextContent.ShouldContain("Guji · Hario V60: setting 22");
    }

    [Fact]
    public async Task A_grinder_without_good_brews_shows_no_favourites()
    {
        var grinder = await Store<EquipmentItem>().SaveAsync(new EquipmentItem { Kind = EquipmentKind.Grinder, Brand = "Comandante" }, Ct);

        var page = Render<Gear>(p => p.Add(x => x.Id, grinder.Id));

        page.FindAll("#gear-favorites").ShouldBeEmpty();
    }
}
