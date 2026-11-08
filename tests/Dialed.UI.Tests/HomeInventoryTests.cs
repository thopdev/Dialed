using Bunit;
using Dialed.Client.Data;
using Dialed.Client.Preferences;
using Dialed.Domain.Inventory;
using Dialed.Domain.Model;
using Dialed.UI.Components;
using Dialed.UI.Pages;
using Microsoft.Extensions.DependencyInjection;

namespace Dialed.UI.Tests;

public sealed class HomeInventoryTests : UiTestContext
{
    public HomeInventoryTests() => Services.GetRequiredService<IUserPreferences>()
        .SetProfileAsync(UserProfile.Default with { OnboardingDone = true }).GetAwaiter().GetResult();

    private static CancellationToken Ct => Xunit.TestContext.Current.CancellationToken;

    private static DateOnly Today => DateOnly.FromDateTime(DateTime.Now);

    private IRepository<Bean> Beans => Services.GetRequiredService<IRepository<Bean>>();

    private IRepository<Bag> Bags => Services.GetRequiredService<IRepository<Bag>>();

    private IRepository<Brew> Brews => Services.GetRequiredService<IRepository<Brew>>();

    private async Task<Bag> OpenBag(string name = "Gesha", decimal weight = 250m, BagStatus status = BagStatus.Open)
    {
        var bean = await Beans.SaveAsync(new Bean { Name = name }, Ct);
        return await Bags.SaveAsync(new Bag { BeanId = bean.Id, WeightG = weight, Status = status, RoastDate = Today.AddDays(-10) }, Ct);
    }

    private Task<Brew> Rated(Bag bag, int rating, int minutesAgo = 0) =>
        Brews.SaveAsync(new Brew { BagId = bag.Id, MethodCode = "v60", DoseG = 18m, Rating = rating, BrewedAt = DateTimeOffset.Now.AddMinutes(-minutesAgo) }, Ct);

    // ---- Home ---------------------------------------------------------------------------------

    [Fact]
    public void New_users_are_guided_to_add_a_first_bean()
    {
        var page = Render<Home>();

        page.Find("#home-first-bean").GetAttribute("href").ShouldBe("beans/new");
        page.FindAll("#home-log-brew").ShouldBeEmpty();
    }

    [Fact]
    public async Task Home_shows_open_bags_with_remaining_cups_and_age()
    {
        await OpenBag("Gesha", 250m);
        await OpenBag("Sealed one", 250m, BagStatus.Sealed);

        var page = Render<Home>();

        var rows = page.FindAll("#home-open-bags .dialed-row");
        rows.Count.ShouldBe(1);
        rows[0].TextContent.ShouldContain("Gesha");
        rows[0].TextContent.ShouldContain("250 g left");
        rows[0].TextContent.ShouldContain("10 days since roast");
        rows[0].TextContent.ShouldContain("cups");
        page.Find("#home-log-brew").GetAttribute("href").ShouldBe("brew");
        page.Find("#home-scan-bag").GetAttribute("href").ShouldBe("bags/new");
    }

    [Fact]
    public async Task Brew_again_lists_the_last_three_brews_rated_four_or_more()
    {
        var bag = await OpenBag();
        await Rated(bag, 5, minutesAgo: 40);
        var newest = await Rated(bag, 4, minutesAgo: 1);
        await Rated(bag, 3, minutesAgo: 2);
        await Rated(bag, 4, minutesAgo: 10);
        await Rated(bag, 5, minutesAgo: 20);

        var page = Render<Home>();

        var cards = page.FindAll("#home-brew-again .dialed-row");
        cards.Count.ShouldBe(3);
        cards[0].GetAttribute("href").ShouldBe($"brew/{bag.Id}/log/again/{newest.Id}");
        cards.ShouldAllBe(c => !c.TextContent.Contains("3/5"));
    }

    [Fact]
    public async Task Brew_again_skips_finished_bags()
    {
        var finished = await OpenBag("Old", status: BagStatus.Finished);
        await Rated(finished, 5);

        Render<Home>().FindAll("#home-brew-again").ShouldBeEmpty();
    }

    // ---- Inventory ----------------------------------------------------------------------------

    [Fact]
    public async Task Inventory_groups_bags_by_status_and_hides_finished()
    {
        await OpenBag("A", status: BagStatus.Open);
        await OpenBag("B", status: BagStatus.Sealed);
        await OpenBag("C", status: BagStatus.Frozen);
        await OpenBag("D", status: BagStatus.Finished);

        var page = Render<Stock>();

        page.FindAll("section").Select(s => s.GetAttribute("data-group")).ShouldBe(["open", "sealed", "frozen"]);
        page.Markup.ShouldNotContain(">D<");
    }

    [Fact]
    public async Task Inventory_marks_low_stock_and_freshness()
    {
        await OpenBag("Tiny", weight: 60m);

        var page = Render<Stock>();

        page.Markup.ShouldContain("Low");
        page.Markup.ShouldContain("Peak");
    }

    [Fact]
    public void Empty_inventory_points_to_a_new_bag()
    {
        Render<Stock>().Markup.ShouldContain("No bags yet.");
    }

    // ---- Badge and settings -------------------------------------------------------------------

    [Fact]
    public async Task Badge_counts_open_bags_below_the_threshold()
    {
        Render<LowStockBadge>().FindAll(".dialed-badge").ShouldBeEmpty();
        await OpenBag("Tiny", weight: 60m);
        await OpenBag("Big", weight: 500m);

        var badge = Render<LowStockBadge>();

        badge.Find(".dialed-badge").TextContent.ShouldBe("1");
    }

    [Fact]
    public async Task Badge_follows_the_threshold_setting()
    {
        await OpenBag("Big", weight: 500m);
        var badge = Render<LowStockBadge>();
        badge.FindAll(".dialed-badge").ShouldBeEmpty();

        await Services.GetRequiredService<IUserPreferences>().SetInventoryAsync(InventorySettings.Default with { LowStockG = 600m });

        badge.WaitForAssertion(() => badge.Find(".dialed-badge").TextContent.ShouldBe("1"));
    }

    [Fact]
    public void Settings_form_saves_threshold_and_windows()
    {
        var form = Render<InventorySettingsForm>();
        form.Find("#stock-low").Change("75");
        form.Find("[data-field=dark-rest]").Change("4");
        form.Find("[data-field=dark-peak]").Change("10");

        form.Find("form").Submit();

        form.WaitForAssertion(() =>
        {
            var saved = Services.GetRequiredService<IUserPreferences>().Inventory;
            saved.LowStockG.ShouldBe(75m);
            saved.RestWindows.Dark.ShouldBe(new RestWindow(4, 10));
            saved.RestWindows.Light.ShouldBe(RestWindows.Default.Light);
        });
    }
}
