using Microsoft.AspNetCore.Components.Routing;
using Microsoft.FluentUI.AspNetCore.Components;
using Filled = Microsoft.FluentUI.AspNetCore.Components.Icons.Filled.Size24;
using Regular = Microsoft.FluentUI.AspNetCore.Components.Icons.Regular.Size24;

namespace Dialed.UI.Navigation;

/// <summary>A top-level destination: a tab on iPhone, a sidebar entry on iPad.</summary>
/// <param name="Href">Route relative to the app base.</param>
/// <param name="TitleKey">Key in <c>Strings.resx</c>.</param>
public sealed record NavigationItem(string Href, string TitleKey, Icon IconRest, Icon IconActive, NavLinkMatch Match = NavLinkMatch.Prefix, bool ShowsLowStockBadge = false);

/// <summary>The app's five top-level sections, in display order.</summary>
public static class AppNavigation
{
    public static IReadOnlyList<NavigationItem> Items { get; } =
    [
        new("", "Nav_Home", new Regular.Home(), new Filled.Home(), NavLinkMatch.All),
        new("brew", "Nav_Brew", new Regular.DrinkCoffee(), new Filled.DrinkCoffee()),
        new("beans", "Nav_Beans", new Regular.Box(), new Filled.Box(), ShowsLowStockBadge: true),
        new("recipes", "Nav_Recipes", new Regular.BookOpen(), new Filled.BookOpen()),
        new("more", "Nav_More", new Regular.MoreHorizontal(), new Filled.MoreHorizontal()),
    ];
}
