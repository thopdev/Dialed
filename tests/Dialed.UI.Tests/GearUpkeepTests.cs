using Bunit;
using Dialed.Client.Data;
using Dialed.Client.Preferences;
using Dialed.Domain.Equipment;
using Dialed.Domain.Model;
using Dialed.UI.Components;
using Dialed.UI.Pages;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace Dialed.UI.Tests;

public sealed class GearUpkeepTests : UiTestContext
{
    private static CancellationToken Ct => Xunit.TestContext.Current.CancellationToken;

    private IRepository<EquipmentItem> Gear => Services.GetRequiredService<IRepository<EquipmentItem>>();

    private async Task<EquipmentItem> GrinderWithBrews(int brews, int? everyBrews = 10, int? everyKg = null, DateOnly? servicedOn = null)
    {
        var grinder = await Gear.SaveAsync(new EquipmentItem { Kind = EquipmentKind.Grinder, Brand = "Eureka", Model = "Mignon", ServiceEveryBrews = everyBrews, ServiceEveryKg = everyKg, ServicedOn = servicedOn }, Ct);
        var bean = await Services.GetRequiredService<IRepository<Bean>>().SaveAsync(new Bean { Name = "Guji" }, Ct);
        var bag = await Services.GetRequiredService<IRepository<Bag>>().SaveAsync(new Bag { BeanId = bean.Id, WeightG = 250m }, Ct);
        var store = Services.GetRequiredService<IRepository<Brew>>();
        for (var i = 0; i < brews; i++)
        {
            await store.SaveAsync(new Brew { BagId = bag.Id, MethodCode = "espresso", DoseG = 18m, GrinderId = grinder.Id, BrewedAt = DateTimeOffset.Now.AddMinutes(-i - 1) }, Ct);
        }

        return grinder;
    }

    [Fact]
    public void The_form_saves_the_service_intervals_and_leaves_them_out_when_empty()
    {
        var saved = new List<EquipmentItem>();
        var form = Render<EquipmentForm>(p => p.Add(x => x.OnSubmit, EventCallback.Factory.Create<EquipmentItem>(this, saved.Add)));
        form.Find("#gear-kind").Change(nameof(EquipmentKind.Kettle));
        form.Find("#gear-brand").Input("Fellow");

        form.Find("form").Submit();
        form.Find("#gear-every-brews").Change("100");
        form.Find("#gear-every-kg").Change("250");
        form.Find("form").Submit();

        saved[0].ServiceEveryBrews.ShouldBeNull();
        saved[0].ServiceEveryKg.ShouldBeNull();
        saved[1].ServiceEveryBrews.ShouldBe(100);
        saved[1].ServiceEveryKg.ShouldBe(250);
    }

    [Fact]
    public async Task The_detail_shows_the_usage_and_that_service_is_due()
    {
        var grinder = await GrinderWithBrews(12, everyBrews: 10);

        var page = Render<Gear>(p => p.Add(x => x.Id, grinder.Id));

        page.WaitForAssertion(() => page.Find("#gear-upkeep-status"));
        page.Find("#gear-upkeep-usage").TextContent.ShouldContain("12 brews");
        page.Find("#gear-upkeep-usage").TextContent.ShouldContain("Service due");
        page.Find("#gear-upkeep-status [role=meter]").GetAttribute("aria-valuenow").ShouldBe("100");
    }

    [Fact]
    public async Task Marking_it_serviced_starts_the_count_again()
    {
        var grinder = await GrinderWithBrews(12, everyBrews: 10);
        var page = Render<Gear>(p => p.Add(x => x.Id, grinder.Id));
        page.WaitForAssertion(() => page.Find("#gear-mark-serviced"));

        page.Find("#gear-mark-serviced").Click();

        page.WaitForAssertion(() => page.Find("#gear-upkeep-usage").TextContent.ShouldNotContain("Service due"));
        (await Gear.GetAsync(grinder.Id, Ct))!.ServicedOn.ShouldBe(DateOnly.FromDateTime(DateTime.Now));
        page.Find("#gear-upkeep-usage").TextContent.ShouldContain("0 brews");
    }

    [Fact]
    public async Task Gear_without_an_interval_shows_no_upkeep()
    {
        var plain = await Gear.SaveAsync(new EquipmentItem { Kind = EquipmentKind.Kettle, Brand = "Fellow" }, Ct);

        var page = Render<Gear>(p => p.Add(x => x.Id, plain.Id));

        page.WaitForAssertion(() => page.Find(".dialed-detail-pane h1"));
        page.FindAll("#gear-upkeep-status").ShouldBeEmpty();
    }

    private Task FinishSetup() =>
        Services.GetRequiredService<IUserPreferences>().SetProfileAsync(UserProfile.Default with { OnboardingDone = true });

    [Fact]
    public async Task The_list_and_Home_nudge_when_service_is_due()
    {
        await FinishSetup();
        await GrinderWithBrews(12, everyBrews: 10);

        var list = Render<Gear>();
        list.WaitForAssertion(() => list.Markup.ShouldContain("Service due"));

        var home = Render<Home>();
        home.WaitForAssertion(() => home.Find("#home-gear-due").TextContent.ShouldContain("Eureka Mignon"));
    }

    [Fact]
    public async Task Gear_that_is_fine_gets_no_nudge()
    {
        await FinishSetup();
        await GrinderWithBrews(3, everyBrews: 100);

        var home = Render<Home>();

        home.WaitForAssertion(() => home.Find("#home-status"));
        home.FindAll("#home-gear-due").ShouldBeEmpty();
    }
}
