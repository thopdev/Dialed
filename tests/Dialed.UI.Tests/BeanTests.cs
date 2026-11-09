using Bunit;
using Dialed.Client.Data;
using Dialed.Domain.Inventory;
using Dialed.Domain.Model;
using Dialed.UI.Components;
using Dialed.UI.Pages;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace Dialed.UI.Tests;

public sealed class BeanTests : UiTestContext
{
    private static CancellationToken Ct => Xunit.TestContext.Current.CancellationToken;

    private IRepository<Vendor> Vendors => Services.GetRequiredService<IRepository<Vendor>>();

    private IRepository<Bean> Beans => Services.GetRequiredService<IRepository<Bean>>();

    private IRepository<Bag> Bags => Services.GetRequiredService<IRepository<Bag>>();

    // ---- ChipInput ----------------------------------------------------------------------------

    [Fact]
    public void ChipInput_adds_values_and_ignores_duplicates()
    {
        IReadOnlyList<string> values = [];
        var input = Render<ChipInput>(p => p
            .Add(x => x.Label, "Varieties")
            .Add(x => x.Values, values)
            .Add(x => x.ValuesChanged, EventCallback.Factory.Create<IReadOnlyList<string>>(this, v => values = v)));

        input.Find("input").Input("Gesha");
        input.Find("button.dialed-button").Click();

        values.ShouldBe(["Gesha"]);
    }

    [Fact]
    public void ChipInput_removes_a_value()
    {
        IReadOnlyList<string> values = ["Gesha", "Bourbon"];
        var input = Render<ChipInput>(p => p
            .Add(x => x.Label, "Varieties")
            .Add(x => x.Values, values)
            .Add(x => x.ValuesChanged, EventCallback.Factory.Create<IReadOnlyList<string>>(this, v => values = v)));

        input.Find("button[aria-label='Remove Gesha']").Click();

        values.ShouldBe(["Bourbon"]);
    }

    // ---- Form ---------------------------------------------------------------------------------

    [Fact]
    public void Form_requires_a_name()
    {
        Bean? submitted = null;
        var form = Render<BeanForm>(p => p.Add(x => x.OnSubmit, EventCallback.Factory.Create<Bean>(this, b => submitted = b)));

        form.Find("form").Submit();

        form.Markup.ShouldContain("Enter a name.");
        submitted.ShouldBeNull();
    }

    [Fact]
    public void Form_submits_all_fields()
    {
        Bean? submitted = null;
        var form = Render<BeanForm>(p => p.Add(x => x.OnSubmit, EventCallback.Factory.Create<Bean>(this, b => submitted = b)));
        form.Find("#bean-name").Input("  Gesha Village ");
        form.Find("#bean-country").Change("Ethiopia");
        form.Find("#bean-region").Change("Bench Maji");
        form.Find("#bean-farm").Change("Gesha Village");
        form.Find("#bean-producer").Change("Adam Overton");
        form.Find("#bean-process").Change("Washed");
        form.Find("#bean-altitude").Change("1950");
        form.Find("#bean-roast-level").Change("MediumLight");
        form.Find("#bean-roast-style").Change("Filter");
        form.Find("#bean-decaf").Change(true);
        form.Find("#bean-notes").Change(" Floral ");
        form.Find("#bean-visibility").Change("Private");
        form.Find("[role=radio][aria-label='4 of 5 stars']").Click();

        form.Find("form").Submit();

        submitted.ShouldNotBeNull();
        submitted.Name.ShouldBe("Gesha Village");
        submitted.Origin.ShouldBe(new BeanOrigin("Ethiopia", "Bench Maji", "Gesha Village", "Adam Overton"));
        submitted.Process.ShouldBe("Washed");
        submitted.AltitudeM.ShouldBe(1950);
        submitted.RoastLevel.ShouldBe(RoastLevel.MediumLight);
        submitted.RoastStyle.ShouldBe(RoastStyle.Filter);
        submitted.IsDecaf.ShouldBeTrue();
        submitted.Notes.ShouldBe("Floral");
        submitted.Visibility.ShouldBe(Visibility.Private);
        submitted.Rating.ShouldBe(4);
    }

    [Fact]
    public void Form_supports_an_other_process()
    {
        Bean? submitted = null;
        var form = Render<BeanForm>(p => p.Add(x => x.OnSubmit, EventCallback.Factory.Create<Bean>(this, b => submitted = b)));
        form.Find("#bean-name").Input("Test");
        form.Find("#bean-process").Change("__other");
        form.Find("#bean-process-other").Change("Yeast ferment");

        form.Find("form").Submit();

        submitted!.Process.ShouldBe("Yeast ferment");
    }

    [Fact]
    public void Form_starts_from_the_bean_being_edited()
    {
        var bean = new Bean { Name = "Santos", Process = "Yeast ferment", Rating = 3, Origin = new BeanOrigin(Country: "Brazil") };

        var form = Render<BeanForm>(p => p.Add(x => x.Bean, bean));

        form.Find("#bean-name").GetAttribute("value").ShouldBe("Santos");
        form.Find("#bean-country").GetAttribute("value").ShouldBe("Brazil");
        form.Find("#bean-process-other").GetAttribute("value").ShouldBe("Yeast ferment");
    }

    // ---- Page ---------------------------------------------------------------------------------

    [Fact]
    public void Empty_list_invites_the_first_bean()
    {
        var page = Render<Beans>();

        page.Markup.ShouldContain("No beans yet");
        page.Find("a[href='beans/new']").ShouldNotBeNull();
    }

    [Fact]
    public async Task List_searches_filters_and_sorts()
    {
        var roaster = await Vendors.SaveAsync(new Vendor { Name = "Friedhats" }, Ct);
        var gesha = await Beans.SaveAsync(new Bean { Name = "Gesha", RoasterId = roaster.Id, Process = "Washed", RoastLevel = RoastLevel.Light, Rating = 5 }, Ct);
        await Beans.SaveAsync(new Bean { Name = "Santos", Process = "Natural", RoastLevel = RoastLevel.Medium, Rating = 3 }, Ct);
        await Bags.SaveAsync(new Bag { BeanId = gesha.Id, WeightG = 250m }, Ct);
        var page = Render<Beans>();

        page.FindAll(".dialed-row-title").Select(t => t.TextContent).ShouldBe(["Gesha", "Santos"]);

        page.Find("#bean-filter-roaster").Change(roaster.Id);
        page.FindAll(".dialed-row-title").Select(t => t.TextContent).ShouldBe(["Gesha"]);
        page.Find("#bean-filter-roaster").Change("");

        page.Find("#bean-filter-process").Change("Natural");
        page.FindAll(".dialed-row-title").Select(t => t.TextContent).ShouldBe(["Santos"]);
        page.Find("#bean-filter-process").Change("");

        page.Find("#bean-filter-roast").Change("Light");
        page.FindAll(".dialed-row-title").Select(t => t.TextContent).ShouldBe(["Gesha"]);
        page.Find("#bean-filter-roast").Change("");

        page.FindAll(".dialed-chip").First(c => c.TextContent == "In stock").Click();
        page.FindAll(".dialed-row-title").Select(t => t.TextContent).ShouldBe(["Gesha"]);
        page.FindAll(".dialed-chip").First(c => c.TextContent == "All").Click();

        page.Find("#bean-sort").Change("Rating");
        page.FindAll(".dialed-row-title").Select(t => t.TextContent).ShouldBe(["Gesha", "Santos"]);

        page.Find("#bean-search").Input("zzzz");
        page.Markup.ShouldContain("Nothing matches.");
    }

    [Fact]
    public async Task Detail_shows_the_bean_its_roaster_and_bags()
    {
        var roaster = await Vendors.SaveAsync(new Vendor { Name = "Friedhats" }, Ct);
        var bean = await Beans.SaveAsync(new Bean
        {
            Name = "Gesha",
            RoasterId = roaster.Id,
            Origin = new BeanOrigin("Ethiopia", "Bench Maji"),
            Process = "Washed",
            RoastLevel = RoastLevel.Light,
            Varieties = ["Gesha"],
            TastingNotes = ["Jasmine", "Peach"],
            Rating = 4,
        }, Ct);
        await Bags.SaveAsync(new Bag { BeanId = bean.Id, WeightG = 250m, Status = BagStatus.Open }, Ct);

        var page = Render<Beans>(p => p.Add(x => x.Id, bean.Id));

        page.Find(".dialed-detail-pane h1").TextContent.ShouldBe("Gesha");
        page.Find($"a[href='vendors/{roaster.Id}']").TextContent.ShouldBe("Friedhats");
        page.Markup.ShouldContain("Ethiopia, Bench Maji");
        page.FindAll("#bean-notes .dialed-chip").Select(c => c.TextContent).ShouldBe(["Jasmine", "Peach"]);
        page.Markup.ShouldContain("Light");
        page.Markup.ShouldContain("250 g");
        page.Markup.ShouldContain("Open");
    }

    [Fact]
    public void Unknown_bean_shows_a_not_found_message()
    {
        Render<Beans>(p => p.Add(x => x.Id, "nope")).Markup.ShouldContain("no longer exists");
    }

    [Fact]
    public async Task Creating_a_bean_saves_it_and_opens_it()
    {
        var page = Render<Beans>(p => p.Add(x => x.Id, "new"));
        page.Find("#bean-name").Input("Gesha");

        page.Find("form").Submit();

        var beans = await Beans.ListAsync(Ct);
        beans.Select(b => b.Name).ShouldBe(["Gesha"]);
        Services.GetRequiredService<NavigationManager>().Uri.ShouldEndWith($"beans/{beans[0].Id}");
    }

    [Fact]
    public async Task Editing_a_bean_saves_the_change()
    {
        var bean = await Beans.SaveAsync(new Bean { Name = "Gesha" }, Ct);
        var page = Render<Beans>(p => p.Add(x => x.Id, bean.Id));

        page.FindAll(".dialed-page-actions button").First(b => b.GetAttribute("aria-label") == "Edit").Click();
        page.Find("#bean-name").Input("Gesha Village");
        page.Find("form").Submit();

        (await Beans.GetAsync(bean.Id, Ct))!.Name.ShouldBe("Gesha Village");
        page.WaitForAssertion(() => page.Find(".dialed-detail-pane h1").TextContent.ShouldBe("Gesha Village"));
    }

    [Fact]
    public async Task A_stale_edit_shows_a_conflict_message()
    {
        var bean = await Beans.SaveAsync(new Bean { Name = "Gesha" }, Ct);
        var page = Render<Beans>(p => p.Add(x => x.Id, bean.Id));
        page.FindAll(".dialed-page-actions button").First(b => b.GetAttribute("aria-label") == "Edit").Click();

        await Beans.SaveAsync(bean with { Name = "Changed elsewhere" }, Ct);
        page.Find("#bean-name").Input("My edit");
        page.Find("form").Submit();

        page.WaitForAssertion(() => page.Markup.ShouldContain("changed elsewhere"));
    }

    [Fact]
    public async Task Deleting_a_bean_also_removes_its_bags()
    {
        var bean = await Beans.SaveAsync(new Bean { Name = "Gesha" }, Ct);
        var bag = await Bags.SaveAsync(new Bag { BeanId = bean.Id, WeightG = 250m }, Ct);
        var page = Render<Beans>(p => p.Add(x => x.Id, bean.Id));

        page.FindAll(".dialed-page-actions button").First(b => b.GetAttribute("aria-label") == "Delete").Click();
        page.Find(".dialed-confirm").TextContent.ShouldContain("also removes its 1 bag(s)");
        page.Find(".dialed-confirm .dialed-button-danger").Click();

        (await Beans.GetAsync(bean.Id, Ct)).ShouldBeNull();
        (await Bags.GetAsync(bag.Id, Ct)).ShouldBeNull();
    }
}
