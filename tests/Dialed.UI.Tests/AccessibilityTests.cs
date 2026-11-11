using AngleSharp.Dom;
using Bunit;
using Dialed.Domain.Model;
using Dialed.UI.Components;
using Dialed.UI.Pages;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace Dialed.UI.Tests;

/// <summary>
/// Static accessibility audit (#49): what VoiceOver reads depends on every control having an accessible name. This cannot replace a pass
/// with VoiceOver on a phone (see docs/accessibility.md) but it catches unlabeled controls on every main screen.
/// </summary>
public sealed class AccessibilityTests : UiTestContext
{
    private static bool HasText(IElement element) => !string.IsNullOrWhiteSpace(element.TextContent);

    private static bool Hidden(IElement element) =>
        element.GetAttribute("aria-hidden") == "true" || element.GetAttribute("type") == "hidden" || element.HasAttribute("hidden");

    private static string Describe(IElement element) => element.OuterHtml.Length > 140 ? element.OuterHtml[..140] : element.OuterHtml;

    private static IEnumerable<string> Problems<T>(IRenderedComponent<T> screen) where T : IComponent
    {
        var labelled = screen.FindAll("label[for]").Select(l => l.GetAttribute("for")).ToHashSet();

        foreach (var field in screen.FindAll("input, select, textarea").Where(f => !Hidden(f)))
        {
            var id = field.Id;
            var named = field.HasAttribute("aria-label") || field.HasAttribute("aria-labelledby")
                || field.Closest("label") is not null
                || (!string.IsNullOrEmpty(id) && labelled.Contains(id));
            if (!named)
            {
                yield return "field without label: " + Describe(field);
            }
        }

        foreach (var control in screen.FindAll("button, a[href], [role=button], [role=link]").Where(c => !Hidden(c)))
        {
            var named = HasText(control) || control.HasAttribute("aria-label") || control.HasAttribute("aria-labelledby") || control.HasAttribute("title")
                || control.QuerySelector("img[alt]:not([alt=''])") is not null;
            if (!named)
            {
                yield return "control without name: " + Describe(control);
            }
        }

        foreach (var image in screen.FindAll("img"))
        {
            if (!image.HasAttribute("alt"))
            {
                yield return "image without alt: " + Describe(image);
            }
        }

        foreach (var svg in screen.FindAll("svg").Where(s => s.Closest("button, a") is null && !Hidden(s)))
        {
            if (!svg.HasAttribute("aria-label") && !svg.HasAttribute("role") && svg.Closest("[aria-hidden=true]") is null)
            {
                yield return "icon that is neither hidden nor labeled: " + Describe(svg);
            }
        }
    }

    private void Audit<TPage>() where TPage : IComponent
    {
        var screen = Render<TPage>();
        try
        {
            screen.WaitForAssertion(() => screen.Markup.ShouldNotContain("dialed-skeleton"), TimeSpan.FromSeconds(1));
        }
        catch (Exception)
        {
            // an empty or still-loading screen is audited as it is
        }

        Problems(screen).Distinct().ToList().ShouldBeEmpty(typeof(TPage).Name);
    }

    [Fact] public void Home_is_labeled() => Audit<Home>();

    [Fact] public void Brew_is_labeled() => Audit<BrewHome>();

    [Fact] public void Beans_is_labeled() => Audit<Beans>();

    [Fact]
    public void Recipe_suggestions_are_labeled()
    {
        var panel = Render<RecipeSuggestionPanel>(p => p.Add(x => x.Bean, new Bean { Name = "Gesha" }));
        panel.Find("#suggest-go").Click();

        panel.FindAll("#suggest-result").ShouldNotBeEmpty();
        Problems(panel).Distinct().ToList().ShouldBeEmpty();
    }

    [Fact] public void Recipes_is_labeled() => Audit<Recipes>();

    [Fact] public void Equipment_is_labeled() => Audit<Gear>();

    [Fact] public void History_is_labeled() => Audit<History>();

    [Fact] public void Vendors_is_labeled() => Audit<Vendors>();

    [Fact] public void Inventory_is_labeled() => Audit<Stock>();

    [Fact] public void People_is_labeled() => Audit<People>();

    [Fact] public void Friends_activity_is_labeled() => Audit<Activity>();

    [Fact] public void Settings_is_labeled() => Audit<Settings>();

    [Fact] public void Statistics_is_labeled() => Audit<Statistics>();

    [Fact] public void More_is_labeled() => Audit<More>();

    [Fact] public void Import_is_labeled() => Audit<Import>();

    [Fact] public void Scan_is_labeled() => Audit<Scan>();

    [Fact] public void Onboarding_is_labeled() => Audit<Onboarding>();

    [Fact] public void Tab_bar_is_labeled() => Audit<Dialed.UI.Layout.TabBar>();

    [Fact]
    public async Task Household_create_rename_and_delete_are_labeled()
    {
        Tokens.Value = "tkn";
        await Services.GetRequiredService<Dialed.Client.Account.AuthSession>().SetAsync(new Dialed.Client.Account.SessionTokens("a", DateTimeOffset.MaxValue, "r", "u1", "You", "h1"), Xunit.TestContext.Current.CancellationToken);
        HouseholdServer.Households = [("h1", "Home"), ("h2", "Office")];
        var panel = Render<HouseholdPanel>();
        panel.WaitForElement("#household-delete");
        Problems(panel).Distinct().ToList().ShouldBeEmpty("household panel");

        panel.Find("#household-rename").Click();
        Problems(panel).Distinct().ToList().ShouldBeEmpty("rename form");
        panel.Find("#household-rename-cancel").Click();

        panel.Find("#household-create").Click();
        Problems(panel).Distinct().ToList().ShouldBeEmpty("create form");
        panel.Find("#household-create-cancel").Click();

        panel.Find("#household-delete").Click();
        Problems(panel).Distinct().ToList().ShouldBeEmpty("delete confirmation");
    }
}
