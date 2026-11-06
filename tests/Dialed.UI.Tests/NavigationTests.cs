using Bunit;
using Dialed.UI.Layout;
using Dialed.UI.Navigation;
using Dialed.UI.Resources;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;

namespace Dialed.UI.Tests;

public sealed class NavigationTests : UiTestContext
{
    [Fact]
    public void Has_the_five_sections_in_order()
    {
        AppNavigation.Items.Select(item => item.Href).ShouldBe(["", "beans", "recipes", "people", "more"]);
    }

    [Fact]
    public void Every_section_title_is_in_the_resources()
    {
        var localizer = Services.GetRequiredService<IStringLocalizer<Strings>>();

        foreach (var item in AppNavigation.Items)
        {
            localizer[item.TitleKey].ResourceNotFound.ShouldBeFalse(item.TitleKey);
        }

        localizer["Nav_Brew"].Value.ShouldBe("Brew");
    }

    [Fact]
    public void Tab_bar_shows_every_section_with_its_label()
    {
        var tabBar = Render<TabBar>();

        var tabs = tabBar.FindAll("a.dialed-tab");
        tabs.Count.ShouldBe(5);
        tabs.Select(tab => tab.QuerySelector(".dialed-tab-label")!.TextContent).ShouldBe(["Brew", "Beans", "Recipes", "People", "More"]);
        tabBar.Find("nav").GetAttribute("aria-label").ShouldBe("Main navigation");
    }

    [Fact]
    public void Tab_bar_marks_the_current_section_as_active()
    {
        var tabBar = Render<TabBar>();

        Services.GetRequiredService<NavigationManager>().NavigateTo("beans/123");

        tabBar.WaitForAssertion(() =>
        {
            var active = tabBar.FindAll("a.dialed-tab.active");
            active.Count.ShouldBe(1);
            active[0].GetAttribute("href").ShouldBe("beans");
        });
    }

    [Fact]
    public void Brew_is_only_active_on_the_start_page()
    {
        var tabBar = Render<TabBar>();

        Services.GetRequiredService<NavigationManager>().NavigateTo("recipes");

        tabBar.WaitForAssertion(() =>
            tabBar.FindAll("a.dialed-tab.active").Single().GetAttribute("href").ShouldBe("recipes"));
    }
}
