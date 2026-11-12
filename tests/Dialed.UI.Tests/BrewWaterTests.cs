using Bunit;
using Dialed.Client.Data;
using Dialed.Domain.Model;
using Dialed.UI.Components;
using Dialed.UI.Pages;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace Dialed.UI.Tests;

public sealed class BrewWaterTests : UiTestContext
{
    private static CancellationToken Ct => Xunit.TestContext.Current.CancellationToken;

    private static readonly EquipmentItem Tap = new() { Id = "tap", Kind = EquipmentKind.Water, Brand = "Tap", Model = "Amsterdam" };

    private IRenderedComponent<BrewForm> RenderForm(IReadOnlyList<EquipmentItem> gear, Action<Brew> onSubmit) =>
        Render<BrewForm>(p => p
            .Add(x => x.Initial, new Brew { BagId = "bag", MethodCode = "v60", DoseG = 15m, WaterG = 250m })
            .Add(x => x.Gear, gear)
            .Add(x => x.OnSubmit, EventCallback.Factory.Create<Brew>(this, onSubmit)));

    [Fact]
    public void The_water_choice_only_shows_when_there_is_water_gear_and_is_saved_with_the_brew()
    {
        RenderForm([], _ => { }).FindAll("#brew-water").ShouldBeEmpty();

        Brew? saved = null;
        var form = RenderForm([Tap], b => saved = b);
        form.Find("#brew-water").Change("tap");
        form.Find("form").Submit();

        saved!.WaterId.ShouldBe("tap");
        form.Find("#brew-water").QuerySelectorAll("option").Select(o => o.TextContent.Trim()).ShouldBe(["Not set", "Tap Amsterdam"]);
    }

    [Fact]
    public async Task The_water_page_says_how_its_brews_did_and_what_did_better()
    {
        var gear = Services.GetRequiredService<IRepository<EquipmentItem>>();
        var tap = await gear.SaveAsync(new EquipmentItem { Kind = EquipmentKind.Water, Brand = "Tap" }, Ct);
        var soft = await gear.SaveAsync(new EquipmentItem { Kind = EquipmentKind.Water, Brand = "Soft", Model = "mix" }, Ct);
        var brews = Services.GetRequiredService<IRepository<Brew>>();
        foreach (var (water, rating) in new[] { (tap.Id, 3), (tap.Id, 4), (soft.Id, 5) })
        {
            await brews.SaveAsync(new Brew { BagId = "bag", MethodCode = "v60", DoseG = 15m, WaterId = water, Rating = rating, BrewedAt = DateTimeOffset.Now }, Ct);
        }

        var page = Render<Gear>(p => p.Add(x => x.Id, tap.Id));

        var text = page.WaitForElement("#gear-water-stats").TextContent;
        text.ShouldContain("Used in 2 brews, average 3.5 stars from 2 rated.");
        text.ShouldContain("Your brews with Soft mix average 5.0.");
    }

    [Fact]
    public async Task An_unused_water_says_so()
    {
        var water = await Services.GetRequiredService<IRepository<EquipmentItem>>().SaveAsync(new EquipmentItem { Kind = EquipmentKind.Water, Brand = "Tap" }, Ct);

        Render<Gear>(p => p.Add(x => x.Id, water.Id)).WaitForElement("#gear-water-stats").TextContent.ShouldContain("Not used in a brew yet");
    }
}
