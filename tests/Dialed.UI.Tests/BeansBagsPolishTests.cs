using Bunit;
using Dialed.Client.Data;
using Dialed.Domain.Inventory;
using Dialed.Domain.Model;
using Dialed.UI.Components;
using Dialed.UI.Pages;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace Dialed.UI.Tests;

/// <summary>Beans, bags and inventory as a collection (#214): thumbnails and chips in lists, the bean and bag detail, the freshness timeline and grouped forms.</summary>
public sealed class BeansBagsPolishTests : UiTestContext
{
    private static CancellationToken Ct => Xunit.TestContext.Current.CancellationToken;

    private static DateOnly Today => DateOnly.FromDateTime(DateTime.Now);

    private IRepository<Bean> Beans => Services.GetRequiredService<IRepository<Bean>>();

    private IRepository<Bag> Bags => Services.GetRequiredService<IRepository<Bag>>();

    private IRepository<Brew> Brews => Services.GetRequiredService<IRepository<Brew>>();

    private IRepository<Vendor> Vendors => Services.GetRequiredService<IRepository<Vendor>>();

    private Task<Bean> Bean(string name = "Kenya", RoastLevel? level = RoastLevel.Light, int? rating = null, bool decaf = false) =>
        Beans.SaveAsync(new Bean { Name = name, RoastLevel = level, Rating = rating, IsDecaf = decaf, Process = "Washed", AltitudeM = 1800, TastingNotes = ["Blackcurrant"] }, Ct);

    private Task<Bag> Bag(Bean bean, BagStatus status = BagStatus.Open, int ageDays = 12, decimal weight = 250m) =>
        Bags.SaveAsync(new Bag { BeanId = bean.Id, WeightG = weight, Status = status, RoastDate = Today.AddDays(-ageDays) }, Ct);

    // ---- Beans list ---------------------------------------------------------------------------

    [Fact]
    public async Task Bean_rows_have_a_roast_tinted_thumbnail_chips_and_a_rating()
    {
        var bean = await Bean(rating: 4, decaf: true);
        await Bag(bean);
        await Bag(bean, BagStatus.Sealed);
        await Bag(bean, BagStatus.Finished);

        var row = Render<Beans>().Find($"[data-bean='{bean.Id}']");

        row.QuerySelector(".dialed-roast-light .dialed-thumb").ShouldNotBeNull();
        row.QuerySelectorAll(".dialed-row-chips .dialed-chip").Select(c => c.TextContent).ShouldBe(["Light", "Decaf", "2 bags"]);
        row.QuerySelector("[role=img]")!.GetAttribute("aria-label").ShouldBe("4 of 5 stars");
    }

    [Fact]
    public async Task Filters_are_folded_away_until_asked_for_and_count_what_is_active()
    {
        var roaster = await Vendors.SaveAsync(new Vendor { Name = "Friedhats", Kinds = VendorKinds.Roaster }, Ct);
        await Beans.SaveAsync(new Bean { Name = "Kenya", RoasterId = roaster.Id }, Ct);
        var page = Render<Beans>();

        var toggle = page.Find("#bean-filters-toggle");
        toggle.GetAttribute("aria-expanded").ShouldBe("false");
        toggle.GetAttribute("aria-controls").ShouldBe("bean-filters");
        page.Find("#bean-filters").HasAttribute("hidden").ShouldBeTrue();
        page.Find("#bean-search").GetAttribute("placeholder").ShouldBe("Search beans");

        toggle.Click();
        page.Find("#bean-filters").HasAttribute("hidden").ShouldBeFalse();
        page.Find("#bean-filter-roaster").Change(roaster.Id);

        page.Find("#bean-filters-toggle").GetAttribute("aria-expanded").ShouldBe("true");
        page.Find("#bean-filters-toggle").TextContent.Trim().ShouldBe("Filters (1)");
    }

    // ---- Bean detail --------------------------------------------------------------------------

    [Fact]
    public async Task Bean_detail_shows_a_header_key_facts_and_notes_as_chips()
    {
        var bean = await Bean(rating: 5);

        var page = Render<Beans>(p => p.Add(x => x.Id, bean.Id));

        page.Find("#bean-hero .dialed-roast-light").ShouldNotBeNull();
        page.Find("#bean-hero [role=img]").GetAttribute("aria-label").ShouldBe("5 of 5 stars");
        page.FindAll("#bean-facts .dialed-stat-label").Select(l => l.TextContent).ShouldBe(["Process", "Roast level", "Altitude"]);
        page.FindAll("#bean-facts .dialed-stat-value").Select(v => v.TextContent).ShouldBe(["Washed", "Light", "1800 m"]);
        page.FindAll("#bean-notes .dialed-chip").Select(c => c.TextContent).ShouldBe(["Blackcurrant"]);
        page.Find("#bean-edit").GetAttribute("aria-label").ShouldBe("Edit");
        page.Find("#bean-delete").GetAttribute("aria-label").ShouldBe("Delete");
    }

    [Fact]
    public async Task Bean_detail_lists_bags_with_roast_date_and_meter_and_its_recent_brews()
    {
        var bean = await Bean();
        var bag = await Bag(bean, ageDays: 12);
        await Brews.SaveAsync(new Brew { BagId = bag.Id, MethodCode = "v60", DoseG = 15m, WaterG = 250m, Rating = 4, BrewedAt = DateTimeOffset.Now }, Ct);

        var page = Render<Beans>(p => p.Add(x => x.Id, bean.Id));

        var row = page.Find($"#bean-bags [data-bag='{bag.Id}']");
        row.QuerySelector(".dialed-row-title")!.TextContent.ShouldBe($"Roasted {Today.AddDays(-12):d MMM yyyy}");
        row.QuerySelector(".dialed-row-subtitle")!.TextContent.ShouldBe("235 g of 250 g left · Open");
        row.QuerySelector("[role=meter]")!.GetAttribute("aria-valuenow").ShouldBe("94");
        row.QuerySelector(".dialed-chip")!.TextContent.ShouldBe("Peak");
        var brew = page.Find("#bean-brews .dialed-row");
        brew.QuerySelector(".dialed-row-title")!.TextContent.ShouldBe("15 g → 250 g");
        brew.QuerySelector(".dialed-method-icon").ShouldNotBeNull();
        page.Find("#bean-add-bag").GetAttribute("href").ShouldBe($"bags/new?beanId={bean.Id}");
    }

    // ---- Bag detail ---------------------------------------------------------------------------

    [Fact]
    public async Task Bag_detail_summarises_with_tiles_a_meter_and_readable_dates()
    {
        var bean = await Bean();
        var bag = await Bags.SaveAsync(new Bag { BeanId = bean.Id, WeightG = 250m, Status = BagStatus.Open, PriceCents = 1850, Currency = "EUR", RoastDate = new DateOnly(2026, 10, 28) }, Ct);

        var page = Render<Bags>(p => p.Add(x => x.Id, bag.Id));

        page.Find("#bag-status").TextContent.ShouldBe("Open");
        page.FindAll("#bag-summary .dialed-stat-label").Select(l => l.TextContent).ShouldBe(["Left", "Age", "Price"]);
        page.Find("#bag-summary [role=meter]").GetAttribute("aria-valuetext").ShouldBe("250 g of 250 g left");
        page.Find("#bag-facts").TextContent.ShouldContain("28 Oct 2026");
        page.Find("#bag-facts").TextContent.ShouldNotContain("2026-10-28");
        page.Find("#bag-edit").GetAttribute("aria-label").ShouldBe("Edit");
    }

    [Fact]
    public async Task Bag_detail_shows_where_the_bag_is_on_its_freshness_timeline()
    {
        var bean = await Bean(level: RoastLevel.Light);
        var bag = await Bag(bean, ageDays: 12);

        var timeline = Render<Bags>(p => p.Add(x => x.Id, bag.Id)).Find("#bag-freshness");

        timeline.GetAttribute("role").ShouldBe("img");
        timeline.GetAttribute("aria-label").ShouldBe("12 days since roast: at its peak. Ready from day 10, fades after day 30.");
    }

    [Theory]
    [InlineData(3, "still resting", "7.5")]
    [InlineData(45, "past its peak", "97.83")]
    public void Freshness_timeline_places_today_on_the_bar(int age, string state, string left)
    {
        var timeline = Render<FreshnessTimeline>(p => p.Add(x => x.AgeDays, age).Add(x => x.Window, new RestWindow(10, 30)));

        timeline.Find("[role=img]").GetAttribute("aria-label")!.ShouldContain(state);
        timeline.Find(".dialed-freshness-today").GetAttribute("style").ShouldBe($"left: {left}%");
    }

    [Fact]
    public async Task Bag_list_rows_show_a_meter_and_freshness_but_finished_bags_do_not()
    {
        var bean = await Bean();
        var open = await Bag(bean, ageDays: 12);
        var finished = await Bag(bean, BagStatus.Finished);
        var page = Render<Bags>();
        page.FindAll(".dialed-chip").First(c => c.TextContent == "All").Click();

        page.Find($"[data-bag='{open.Id}'] [role=meter]").ShouldNotBeNull();
        page.Find($"[data-bag='{open.Id}'] .dialed-row-subtitle").TextContent.ShouldBe("250 g · Open · 12 days old");
        page.Find($"[data-bag='{finished.Id}']").QuerySelector("[role=meter]").ShouldBeNull();
    }

    // ---- Inventory ----------------------------------------------------------------------------

    [Fact]
    public async Task Inventory_groups_have_headers_with_a_count_and_rows_with_a_meter()
    {
        var bean = await Bean(level: RoastLevel.Dark);
        await Bag(bean);
        await Bag(bean);

        var page = Render<Stock>();

        var open = page.Find("section[data-group=open]");
        open.QuerySelector(".dialed-section-header h2")!.TextContent.ShouldBe("Open");
        open.QuerySelector(".dialed-count")!.GetAttribute("aria-label").ShouldBe("2 bags");
        open.QuerySelectorAll("[role=meter]").Length.ShouldBe(2);
        open.QuerySelector(".dialed-roast-dark").ShouldNotBeNull();
    }

    // ---- Forms --------------------------------------------------------------------------------

    [Fact]
    public void Bean_form_is_grouped_and_checks_the_name_when_leaving_the_field()
    {
        var form = Render<BeanForm>(p => p.Add(x => x.OnSubmit, EventCallback.Factory.Create<Bean>(this, _ => { })));

        form.FindAll(".dialed-form-section h2").Select(h => h.TextContent).ShouldBe(["Bean", "Origin", "Roast", "Tasting", "Sharing"]);
        form.FindAll("#bean-name-error").ShouldBeEmpty();

        form.Find("#bean-name").Blur();

        form.Find("#bean-name").GetAttribute("aria-describedby").ShouldBe("bean-name-error");
        form.Find("#bean-name-error").TextContent.ShouldNotBeEmpty();
        form.Find("#bean-country").GetAttribute("autocapitalize").ShouldBe("words");
        form.Find("#bean-altitude").GetAttribute("inputmode").ShouldBe("numeric");
    }

    [Fact]
    public void Bean_form_rejects_an_altitude_out_of_range()
    {
        Bean? saved = null;
        var form = Render<BeanForm>(p => p.Add(x => x.OnSubmit, EventCallback.Factory.Create<Bean>(this, b => saved = b)));
        form.Find("#bean-name").Input("Kenya");
        form.Find("#bean-altitude").Change("9000");

        form.Find("form").Submit();

        saved.ShouldBeNull();
        form.Find("#bean-altitude-error").TextContent.ShouldBe("Enter an altitude between 0 and 5000 m.");
        form.Find("#bean-altitude").GetAttribute("aria-invalid").ShouldBe("true");
    }

    [Fact]
    public async Task Bag_form_is_grouped_and_checks_the_weight_when_leaving_the_field()
    {
        var bean = await Bean();
        var form = Render<BagForm>(p => p
            .Add(x => x.Beans, [bean])
            .Add(x => x.Today, Today)
            .Add(x => x.OnSubmit, EventCallback.Factory.Create<Bag>(this, _ => { })));

        form.FindAll(".dialed-form-section h2").Select(h => h.TextContent).ShouldBe(["Bag", "Purchase", "Storage"]);

        form.Find("#bag-weight").Blur();

        form.Find("#bag-weight").GetAttribute("aria-describedby").ShouldBe("bag-weight-error");
        form.Find("#bag-weight").GetAttribute("inputmode").ShouldBe("decimal");
        form.Find("form > .dialed-sticky-actions button[type=submit]").ShouldNotBeNull();
    }
}
