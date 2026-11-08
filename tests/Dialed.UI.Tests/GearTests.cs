using Bunit;
using Dialed.Client.Data;
using Dialed.Domain.Equipment;
using Dialed.Domain.Model;
using Dialed.UI.Components;
using Dialed.UI.Pages;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace Dialed.UI.Tests;

public sealed class GearTests : UiTestContext
{
    private static CancellationToken Ct => Xunit.TestContext.Current.CancellationToken;

    private IRepository<EquipmentItem> Gear => Services.GetRequiredService<IRepository<EquipmentItem>>();

    private IRepository<MethodDefault> Defaults => Services.GetRequiredService<IRepository<MethodDefault>>();

    // ---- Form ---------------------------------------------------------------------------------

    [Fact]
    public void Form_requires_a_brand()
    {
        EquipmentItem? submitted = null;
        var form = Render<EquipmentForm>(p => p.Add(x => x.OnSubmit, EventCallback.Factory.Create<EquipmentItem>(this, i => submitted = i)));

        form.Find("form").Submit();

        form.Markup.ShouldContain("Enter a brand.");
        submitted.ShouldBeNull();
    }

    [Fact]
    public void Grinder_needs_a_usable_scale()
    {
        var form = Render<EquipmentForm>();
        form.Find("#gear-brand").Input("Comandante");

        form.Find("form").Submit();

        form.Markup.ShouldContain("Enter the highest setting.");
    }

    [Fact]
    public void Form_creates_a_click_grinder()
    {
        EquipmentItem? submitted = null;
        var form = Render<EquipmentForm>(p => p.Add(x => x.OnSubmit, EventCallback.Factory.Create<EquipmentItem>(this, i => submitted = i)));
        form.Find("#gear-brand").Input("Comandante");
        form.Find("#gear-model").Change("C40");
        form.Find("#gear-scale-max").Change("40");

        form.Find("form").Submit();

        submitted.ShouldNotBeNull();
        submitted.Kind.ShouldBe(EquipmentKind.Grinder);
        submitted.Brand.ShouldBe("Comandante");
        submitted.Model.ShouldBe("C40");
        submitted.GrindScale!.Kind.ShouldBe(GrindSettingKind.Clicks);
        submitted.GrindScale.Max.ShouldBe(40m);
    }

    [Fact]
    public void Form_creates_a_numbered_stepped_grinder()
    {
        EquipmentItem? submitted = null;
        var form = Render<EquipmentForm>(p => p.Add(x => x.OnSubmit, EventCallback.Factory.Create<EquipmentItem>(this, i => submitted = i)));
        form.Find("#gear-brand").Input("Niche");
        form.Find("#gear-scale-number").Change(true);
        form.Find("#gear-scale-min").Change("0");
        form.Find("#gear-scale-max").Change("50");
        form.Find("#gear-scale-step").Change("1");

        form.Find("form").Submit();

        submitted!.GrindScale!.Kind.ShouldBe(GrindSettingKind.Number);
        submitted.GrindScale.Step.ShouldBe(1m);
    }

    [Fact]
    public void Other_gear_has_no_scale()
    {
        EquipmentItem? submitted = null;
        var form = Render<EquipmentForm>(p => p.Add(x => x.OnSubmit, EventCallback.Factory.Create<EquipmentItem>(this, i => submitted = i)));
        form.Find("#gear-kind").Change("Kettle");
        form.Find("#gear-brand").Input("Fellow");

        form.FindAll("#gear-scale-max").ShouldBeEmpty();
        form.Find("form").Submit();

        submitted!.Kind.ShouldBe(EquipmentKind.Kettle);
        submitted.GrindScale.ShouldBeNull();
    }

    // ---- Page ---------------------------------------------------------------------------------

    [Fact]
    public void Empty_list_invites_the_first_item()
    {
        var page = Render<Gear>();

        page.Markup.ShouldContain("No equipment yet");
        page.Find("a[href='equipment/new']").ShouldNotBeNull();
    }

    [Fact]
    public async Task List_filters_by_kind()
    {
        await Gear.SaveAsync(new EquipmentItem { Kind = EquipmentKind.Grinder, Brand = "Comandante", Model = "C40", GrindScale = GrindScale.Clicks(40m) }, Ct);
        await Gear.SaveAsync(new EquipmentItem { Kind = EquipmentKind.Kettle, Brand = "Fellow" }, Ct);
        var page = Render<Gear>();

        page.FindAll(".dialed-row-title").Select(t => t.TextContent).ShouldBe(["Comandante C40", "Fellow"]);

        page.Find("#gear-filter").Change("Kettle");
        page.FindAll(".dialed-row-title").Select(t => t.TextContent).ShouldBe(["Fellow"]);
    }

    [Fact]
    public async Task Detail_shows_the_grind_scale()
    {
        var grinder = await Gear.SaveAsync(new EquipmentItem { Kind = EquipmentKind.Grinder, Brand = "Comandante", GrindScale = GrindScale.Clicks(40m) }, Ct);

        var page = Render<Gear>(p => p.Add(x => x.Id, grinder.Id));

        page.Find(".dialed-detail-pane h1").TextContent.ShouldBe("Comandante");
        page.Markup.ShouldContain("0–40 clicks");
    }

    [Fact]
    public async Task Creating_gear_saves_it_and_opens_it()
    {
        var page = Render<Gear>(p => p.Add(x => x.Id, "new"));
        page.Find("#gear-kind").Change("Scale");
        page.Find("#gear-brand").Input("Acaia");

        page.Find("form").Submit();

        var items = await Gear.ListAsync(Ct);
        items.Select(i => i.Brand).ShouldBe(["Acaia"]);
        Services.GetRequiredService<NavigationManager>().Uri.ShouldEndWith($"equipment/{items[0].Id}");
    }

    [Fact]
    public async Task Editing_gear_saves_the_change()
    {
        var item = await Gear.SaveAsync(new EquipmentItem { Kind = EquipmentKind.Kettle, Brand = "Fellow" }, Ct);
        var page = Render<Gear>(p => p.Add(x => x.Id, item.Id));

        page.FindAll(".dialed-page-actions button").First(b => b.TextContent == "Edit").Click();
        page.Find("#gear-model").Change("Stagg EKG");
        page.Find("form").Submit();

        (await Gear.GetAsync(item.Id, Ct))!.Model.ShouldBe("Stagg EKG");
    }

    [Fact]
    public async Task Default_grinder_and_brewer_per_method_can_be_set_and_cleared()
    {
        var grinder = await Gear.SaveAsync(new EquipmentItem { Kind = EquipmentKind.Grinder, Brand = "Comandante", GrindScale = GrindScale.Clicks(40m) }, Ct);
        var brewer = await Gear.SaveAsync(new EquipmentItem { Kind = EquipmentKind.Brewer, Brand = "Hario", Model = "V60" }, Ct);
        var page = Render<Gear>(p => p.Add(x => x.Id, "defaults"));

        page.Find("#default-grinder-v60").Change(grinder.Id);
        page.Find("#default-brewer-v60").Change(brewer.Id);

        var row = (await Defaults.ListAsync(Ct)).Single();
        row.MethodCode.ShouldBe("v60");
        row.GrinderId.ShouldBe(grinder.Id);
        row.BrewerId.ShouldBe(brewer.Id);

        page.Find("#default-grinder-v60").Change("");
        page.Find("#default-brewer-v60").Change("");
        (await Defaults.ListAsync(Ct)).ShouldBeEmpty();
    }

    [Fact]
    public async Task Deleting_gear_also_clears_it_as_a_default()
    {
        var grinder = await Gear.SaveAsync(new EquipmentItem { Kind = EquipmentKind.Grinder, Brand = "Comandante", GrindScale = GrindScale.Clicks(40m) }, Ct);
        await Defaults.SaveAsync(new MethodDefault { MethodCode = "v60", GrinderId = grinder.Id }, Ct);
        var page = Render<Gear>(p => p.Add(x => x.Id, grinder.Id));
        page.Markup.ShouldContain("Hario V60");

        page.FindAll(".dialed-page-actions button").First(b => b.TextContent == "Delete").Click();
        page.Find(".dialed-confirm .dialed-button-danger").Click();

        (await Gear.GetAsync(grinder.Id, Ct)).ShouldBeNull();
        (await Defaults.ListAsync(Ct)).ShouldBeEmpty();
    }
}
