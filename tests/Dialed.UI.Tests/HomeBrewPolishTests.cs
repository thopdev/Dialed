using Bunit;
using Dialed.Client.Data;
using Dialed.Client.Preferences;
using Dialed.Domain.Inventory;
using Dialed.Domain.Model;
using Dialed.UI.Components;
using Dialed.UI.Pages;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace Dialed.UI.Tests;

/// <summary>Home and the brew flow as they look after the polish (#213): greeting, bag cards, recent brews, the bag summary and the log form.</summary>
public sealed class HomeBrewPolishTests : UiTestContext
{
    private static CancellationToken Ct => Xunit.TestContext.Current.CancellationToken;

    private static DateOnly Today => DateOnly.FromDateTime(DateTime.Now);

    private IUserPreferences Preferences => Services.GetRequiredService<IUserPreferences>();

    private IRepository<Bean> Beans => Services.GetRequiredService<IRepository<Bean>>();

    private IRepository<Bag> Bags => Services.GetRequiredService<IRepository<Bag>>();

    private IRepository<Brew> Brews => Services.GetRequiredService<IRepository<Brew>>();

    private Task Profile(string name = "") =>
        Preferences.SetProfileAsync(UserProfile.Default with { OnboardingDone = true, DisplayName = name });

    private async Task<Bag> NewBag(string name = "Gesha", decimal weight = 250m, BagStatus status = BagStatus.Open)
    {
        var bean = await Beans.SaveAsync(new Bean { Name = name }, Ct);
        return await Bags.SaveAsync(new Bag { BeanId = bean.Id, WeightG = weight, Status = status, RoastDate = Today.AddDays(-12) }, Ct);
    }

    private Task<Brew> Brewed(Bag bag, DateTimeOffset at, int? rating = null, string method = "v60") =>
        Brews.SaveAsync(new Brew { BagId = bag.Id, MethodCode = method, DoseG = 15m, WaterG = 250m, Rating = rating, BrewedAt = at }, Ct);

    // ---- Home ---------------------------------------------------------------------------------

    [Fact]
    public async Task Home_greets_by_first_name_and_counts_todays_brews()
    {
        await Profile("Thomas Hopp");
        var bag = await NewBag();
        await Brewed(bag, DateTimeOffset.Now.AddMinutes(-5));
        await Brewed(bag, DateTimeOffset.Now.AddMinutes(-30));
        await Brewed(bag, DateTimeOffset.Now.AddDays(-2));

        var page = Render<Home>();

        page.Find("h1").TextContent.ShouldEndWith(", Thomas");
        page.Find("h1").TextContent.ShouldStartWith("Good ");
        page.Find("#home-status").TextContent.ShouldStartWith("2 brews today, last at ");
    }

    [Fact]
    public async Task Home_without_brews_today_says_so()
    {
        await Profile();
        await NewBag();

        var page = Render<Home>();

        page.Find("#home-status").TextContent.ShouldBe("No brews yet today.");
        page.Find("h1").TextContent.ShouldNotContain(",");
    }

    [Fact]
    public async Task Open_bags_are_cards_with_a_meter_for_what_is_left()
    {
        await Profile();
        var bag = await NewBag(weight: 250m);
        await Brewed(bag, DateTimeOffset.Now.AddDays(-1));

        var page = Render<Home>();

        var card = page.Find("#home-open-bags .dialed-bag-card");
        card.GetAttribute("href").ShouldBe($"brew/{bag.Id}");
        card.TextContent.ShouldContain("12 days since roast");
        card.TextContent.ShouldContain("Peak");
        var meter = card.QuerySelector("[role=meter]")!;
        meter.GetAttribute("aria-label").ShouldBe("Coffee left in Gesha");
        meter.GetAttribute("aria-valuetext").ShouldBe("235 g of 250 g left");
        meter.GetAttribute("aria-valuenow").ShouldBe("94");
        page.Find("#home-inventory").GetAttribute("href").ShouldBe("inventory");
    }

    [Fact]
    public async Task Without_open_bags_home_offers_to_add_one()
    {
        await Profile();
        await NewBag(status: BagStatus.Sealed);

        var page = Render<Home>();

        page.FindAll("#home-open-bags").ShouldBeEmpty();
        page.Find("#home-new-bag").GetAttribute("href").ShouldBe("bags/new");
    }

    [Fact]
    public async Task Recent_brews_show_the_last_three_with_method_and_stars_and_link_to_history()
    {
        await Profile();
        var bag = await NewBag();
        for (var i = 0; i < 4; i++)
        {
            await Brewed(bag, DateTimeOffset.Now.AddHours(-i), rating: 4, method: "espresso");
        }

        var page = Render<Home>();

        var rows = page.FindAll("#home-recent .dialed-row");
        rows.Count.ShouldBe(3);
        rows[0].GetAttribute("href").ShouldStartWith("history/");
        rows[0].QuerySelector(".dialed-row-subtitle")!.TextContent.ShouldStartWith("Today ");
        rows[0].QuerySelector(".dialed-method-icon").ShouldNotBeNull();
        rows[0].QuerySelector("[role=img]")!.GetAttribute("aria-label").ShouldBe("4 of 5 stars");
        page.Find("#home-history").GetAttribute("href").ShouldBe("history");
    }

    [Fact]
    public async Task Brew_again_shows_amounts_with_an_arrow_and_stars_instead_of_a_fraction()
    {
        await Profile();
        var bag = await NewBag();
        await Brewed(bag, DateTimeOffset.Now, rating: 5);

        var row = Render<Home>().Find("#home-brew-again .dialed-row");

        row.QuerySelector(".dialed-row-subtitle")!.TextContent.ShouldBe("15 g → 250 g · Hario V60");
        row.TextContent.ShouldNotContain("5/5");
        row.QuerySelector("[role=img]")!.GetAttribute("aria-label").ShouldBe("5 of 5 stars");
    }

    // ---- Brew picker and bag ------------------------------------------------------------------

    [Fact]
    public async Task Picker_lists_open_bags_before_sealed_ones_with_a_thumbnail_and_status()
    {
        await NewBag("Aaa sealed", status: BagStatus.Sealed);
        await NewBag("Zzz open");
        await NewBag("Frozen", status: BagStatus.Frozen);

        var page = Render<BrewHome>();

        var rows = page.FindAll("#brew-bags .dialed-row");
        rows.Select(r => r.QuerySelector(".dialed-row-title")!.TextContent).ShouldBe(["Zzz open", "Aaa sealed"]);
        rows[1].QuerySelector(".dialed-chip")!.TextContent.Trim().ShouldBe("Sealed");
        rows.ShouldAllBe(r => r.QuerySelector(".dialed-thumb") != null);
    }

    [Fact]
    public async Task Bag_screen_summarises_what_is_left_and_how_old_it_is()
    {
        var bag = await NewBag(weight: 250m);

        var page = Render<BrewHome>(p => p.Add(x => x.BagId, bag.Id));

        var summary = page.Find("#brew-bag-summary");
        summary.QuerySelectorAll(".dialed-stat-value").Select(v => v.TextContent).ShouldBe(["250 g", "12 d"]);
        summary.QuerySelector("[role=meter]")!.GetAttribute("aria-valuetext").ShouldBe("250 g of 250 g left");
    }

    [Fact]
    public void Picker_without_bags_shows_an_empty_state_with_an_action()
    {
        var page = Render<BrewHome>();

        page.Find(".dialed-empty .dialed-empty-icon").ShouldNotBeNull();
        page.Find(".dialed-empty a[href='bags/new']").ShouldNotBeNull();
    }

    // ---- Log form -----------------------------------------------------------------------------

    private IRenderedComponent<BrewForm> RenderForm(Brew initial) =>
        Render<BrewForm>(p => p
            .Add(x => x.Initial, initial)
            .Add(x => x.OnSubmit, EventCallback.Factory.Create<Brew>(this, _ => { })));

    [Fact]
    public void Log_form_is_grouped_in_titled_sections_with_the_save_bar_last()
    {
        var form = RenderForm(new Brew { BagId = "bag", MethodCode = "espresso", DoseG = 18m, YieldG = 36m });

        form.FindAll(".dialed-form-section h2").Select(h => h.TextContent).ShouldBe(["Method and drink", "Numbers", "How did it taste?", "Photos and notes"]);
        form.FindAll(".dialed-form-section").ShouldAllBe(s => s.GetAttribute("aria-labelledby") != null);
        form.Find("form > .dialed-sticky-actions button[type=submit]").ShouldNotBeNull();
    }

    [Fact]
    public void Dose_and_yield_are_large_side_by_side_fields_with_a_live_ratio()
    {
        var form = RenderForm(new Brew { BagId = "bag", MethodCode = "espresso", DoseG = 18m, YieldG = 36m });

        var pair = form.Find(".dialed-field-row");
        pair.QuerySelectorAll(".dialed-field-large input").Select(i => i.Id).ShouldBe(["brew-dose", "brew-liquid"]);
        pair.QuerySelectorAll("input").ShouldAllBe(i => i.GetAttribute("inputmode") == "decimal");
        form.Find("#brew-ratio").TextContent.ShouldBe("Ratio 1:2.0");

        form.Find("#brew-liquid").Change("45");

        form.Find("#brew-ratio strong").TextContent.ShouldBe("1:2.5");
    }

    [Fact]
    public void A_missing_dose_is_explained_next_to_the_field()
    {
        var form = RenderForm(new Brew { BagId = "bag", MethodCode = "v60", DoseG = 0m });

        form.Find("form").Submit();

        form.Find("#brew-dose").GetAttribute("aria-describedby").ShouldBe("brew-dose-error");
        form.Find("#brew-dose-error").TextContent.ShouldBe("Enter the dose.");
        form.FindAll("#brew-ratio").ShouldBeEmpty();
    }

    // ---- Grind drift --------------------------------------------------------------------------

    private async Task Ground(Bag bag, int daysAgo, decimal setting) =>
        await Brews.SaveAsync(new Brew { BagId = bag.Id, MethodCode = "v60", DoseG = 15m, GrindSetting = setting, BrewedAt = DateTimeOffset.Now.AddDays(-daysAgo) }, Ct);

    [Fact]
    public async Task A_grind_that_keeps_moving_finer_shows_a_drift_note_on_the_bag_screen()
    {
        var bag = await NewBag();
        await Ground(bag, 12, 20m);
        await Ground(bag, 8, 19m);
        await Ground(bag, 4, 18m);

        var page = Render<BrewHome>(p => p.Add(x => x.BagId, bag.Id));

        var note = page.Find("#brew-grind-drift");
        note.QuerySelector("h2")!.TextContent.ShouldBe("Your grind is drifting finer");
        note.TextContent.ShouldContain("from 20 to 18 in 8 days");
        page.Find("#brew-grind-drift-try").TextContent.ShouldContain("Today the trend points to");
    }

    [Fact]
    public async Task Without_a_drift_the_bag_screen_has_no_note()
    {
        var bag = await NewBag();
        await Ground(bag, 8, 18m);
        await Ground(bag, 4, 18m);

        Render<BrewHome>(p => p.Add(x => x.BagId, bag.Id)).FindAll("#brew-grind-drift").ShouldBeEmpty();
    }

    // ---- Roast age tip ------------------------------------------------------------------------

    [Fact]
    public async Task The_bag_screen_says_where_the_bag_is_compared_with_the_age_of_your_best_brews()
    {
        var bean = await Beans.SaveAsync(new Bean { Name = "Kiambu" }, Ct);
        var bag = await Bags.SaveAsync(new Bag { BeanId = bean.Id, WeightG = 250m, RoastDate = Today.AddDays(-4) }, Ct);
        foreach (var daysAgo in new[] { 1, 2, 3 })
        {
            await Brews.SaveAsync(new Brew { BagId = bag.Id, MethodCode = "v60", DoseG = 15m, Rating = 5, BrewedAt = DateTimeOffset.Now.AddDays(-daysAgo) }, Ct);
        }

        var page = Render<BrewHome>(p => p.Add(x => x.BagId, bag.Id));

        var tip = page.WaitForElement("#brew-roast-tip");
        tip.TextContent.ShouldContain("This bag is older than your best brews of this bean");
        tip.TextContent.ShouldContain("1 to 3 days after roasting");
    }

    [Fact]
    public async Task Without_enough_good_brews_there_is_no_roast_tip()
    {
        var bag = await NewBag();
        await Brewed(bag, DateTimeOffset.Now.AddDays(-1), rating: 5);

        Render<BrewHome>(p => p.Add(x => x.BagId, bag.Id)).FindAll("#brew-roast-tip").ShouldBeEmpty();
    }
}
