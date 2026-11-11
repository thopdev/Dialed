using Bunit;
using Dialed.Client.Account;
using Dialed.Client.Data;
using Dialed.Client.Preferences;
using Dialed.Domain.Inventory;
using Dialed.Domain.Model;
using Dialed.UI.Components;
using Dialed.UI.Pages;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace Dialed.UI.Tests;

/// <summary>The stock alert switches, what the server is told, and the filtered inventory a tapped notification opens (#259).</summary>
public sealed class StockAlertUiTests : UiTestContext
{
    private static CancellationToken Ct => Xunit.TestContext.Current.CancellationToken;

    private async Task<IRenderedComponent<NotificationsPanel>> PanelOn()
    {
        await Services.GetRequiredService<AuthSession>().SetAsync(new SessionTokens("a", DateTimeOffset.MaxValue, "r", "u1", "Thomas", "h-u1"), Ct);
        var panel = Render<NotificationsPanel>();
        panel.Find("#push-enable").Click();
        panel.WaitForElement("#push-low");
        return panel;
    }

    [Fact]
    public async Task The_stock_switches_start_off()
    {
        var panel = await PanelOn();

        panel.Find("#push-low").HasAttribute("checked").ShouldBeFalse();
        panel.Find("#push-peak").HasAttribute("checked").ShouldBeFalse();
        panel.Find("#push-switches").TextContent.ShouldContain("once a day");
    }

    [Fact]
    public async Task Switching_low_stock_on_tells_the_server_the_thresholds_and_the_time_zone()
    {
        await Services.GetRequiredService<IUserPreferences>().SetInventoryAsync(new InventorySettings(150m, RestWindows.Default));
        var panel = await PanelOn();

        panel.Find("#push-low").Change(true);

        panel.WaitForAssertion(() => PushServer.LowStock.ShouldBeTrue());
        PushServer.Peak.ShouldBeFalse();
        PushServer.Inventory!.LowStockG.ShouldBe(150m);
        PushServer.SentTimeZone.ShouldBe("Europe/Amsterdam");
        panel.Find("#push-peak").Change(true);
        panel.WaitForAssertion(() => PushServer.Peak.ShouldBeTrue());
        PushServer.LowStock.ShouldBeTrue();
    }

    [Fact]
    public async Task The_follow_and_household_switches_keep_the_stock_ones_as_they_are()
    {
        var panel = await PanelOn();
        panel.Find("#push-peak").Change(true);
        panel.WaitForAssertion(() => PushServer.Peak.ShouldBeTrue());

        panel.Find("#push-follows").Change(false);

        panel.WaitForAssertion(() => PushServer.Follows.ShouldBeFalse());
        PushServer.Peak.ShouldBeTrue();
    }

    [Fact]
    public async Task Changing_the_inventory_settings_updates_the_server_while_a_stock_alert_is_on()
    {
        var panel = await PanelOn();
        panel.Find("#push-low").Change(true);
        panel.WaitForAssertion(() => PushServer.LowStock.ShouldBeTrue());
        var saves = PushServer.SaveCalls;
        var form = Render<InventorySettingsForm>();

        form.Find("#stock-low").Change("75");
        form.Find("form").Submit();

        form.WaitForAssertion(() => PushServer.SaveCalls.ShouldBe(saves + 1));
        PushServer.Inventory!.LowStockG.ShouldBe(75m);
    }

    [Fact]
    public async Task Changing_the_inventory_settings_tells_the_server_nothing_when_no_stock_alert_is_on()
    {
        await Services.GetRequiredService<AuthSession>().SetAsync(new SessionTokens("a", DateTimeOffset.MaxValue, "r", "u1", "Thomas", "h-u1"), Ct);
        var form = Render<InventorySettingsForm>();

        form.Find("form").Submit();

        form.WaitForAssertion(() => form.Find("[role=status]"));
        PushServer.SaveCalls.ShouldBe(0);
    }

    [Fact]
    public void Changing_the_inventory_settings_works_signed_out()
    {
        var form = Render<InventorySettingsForm>();

        form.Find("form").Submit();

        form.WaitForAssertion(() => form.Find("[role=status]"));
        PushServer.SaveCalls.ShouldBe(0);
    }

    // ---- The inventory a tapped notification opens --------------------------------------------

    private async Task Bags()
    {
        var today = DateOnly.FromDateTime(DateTime.Now);
        var beans = Services.GetRequiredService<IRepository<Bean>>();
        var bags = Services.GetRequiredService<IRepository<Bag>>();
        var low = await beans.SaveAsync(new Bean { Name = "Tiny" }, Ct);
        var big = await beans.SaveAsync(new Bean { Name = "Plenty" }, Ct);
        var fresh = await beans.SaveAsync(new Bean { Name = "Fresh" }, Ct);
        await bags.SaveAsync(new Bag { BeanId = low.Id, WeightG = 40m, Status = BagStatus.Open, RoastDate = today.AddDays(-60) }, Ct);
        await bags.SaveAsync(new Bag { BeanId = big.Id, WeightG = 250m, Status = BagStatus.Open, RoastDate = today.AddDays(-14) }, Ct);
        await bags.SaveAsync(new Bag { BeanId = fresh.Id, WeightG = 250m, Status = BagStatus.Open, RoastDate = today.AddDays(-1) }, Ct);
    }

    private IRenderedComponent<Stock> StockFor(string? show)
    {
        Services.GetRequiredService<NavigationManager>().NavigateTo(show is null ? "inventory" : $"inventory?show={show}");
        return Render<Stock>();
    }

    private static string[] Names(IRenderedComponent<Stock> page) => [.. page.FindAll("[data-group] .dialed-row-title").Select(t => t.TextContent.Trim()).Order()];

    [Fact]
    public async Task The_low_filter_shows_only_the_bags_running_low_with_a_way_back()
    {
        await Bags();

        var page = StockFor("low");

        page.Find("#stock-filter").TextContent.ShouldContain("running low");
        page.Find("#stock-filter-clear").GetAttribute("href").ShouldBe("inventory");
        Names(page).ShouldBe(["Tiny"]);
    }

    [Fact]
    public async Task The_peak_filter_shows_only_the_bags_at_their_best()
    {
        await Bags();

        var page = StockFor("peak");

        page.Find("#stock-filter").TextContent.ShouldContain("at their peak");
        Names(page).ShouldBe(["Plenty"]);
    }

    [Fact]
    public async Task Without_a_filter_everything_shows_and_an_unknown_filter_is_ignored()
    {
        await Bags();

        Names(StockFor(null)).ShouldBe(["Fresh", "Plenty", "Tiny"]);
        StockFor("nonsense").FindAll("#stock-filter").ShouldBeEmpty();
    }

    [Fact]
    public async Task A_filter_that_matches_nothing_says_so()
    {
        await Services.GetRequiredService<IRepository<Bean>>().SaveAsync(new Bean { Name = "Only" }, Ct);
        var bean = (await Services.GetRequiredService<IRepository<Bean>>().ListAsync(Ct)).Single();
        await Services.GetRequiredService<IRepository<Bag>>().SaveAsync(new Bag { BeanId = bean.Id, WeightG = 250m, Status = BagStatus.Open, RoastDate = DateOnly.FromDateTime(DateTime.Now) }, Ct);

        StockFor("low").Find(".dialed-empty, [class*=empty]").TextContent.ShouldContain("None of your bags");
    }
}
