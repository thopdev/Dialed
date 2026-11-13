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

    private void Audit<TPage>(Action<ComponentParameterCollectionBuilder<TPage>>? parameters = null) where TPage : IComponent
    {
        var screen = parameters is null ? Render<TPage>() : Render(parameters);
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

    [Fact]
    public void Upload_states_are_labeled()
    {
        Uploads.Set("p1", Dialed.Client.Uploads.UploadStatus.Waiting);
        Uploads.Set("p2", Dialed.Client.Uploads.UploadStatus.Failed);
        var strip = Render<PhotoStrip>(p => p.Add(x => x.PhotoIds, ["p1", "p2"]));
        var status = Render<UploadQueueStatus>();

        strip.FindAll("button.photo-upload-retry").ShouldHaveSingleItem();
        status.FindAll("#uploads-retry").ShouldHaveSingleItem();
        Problems(strip).Concat(Problems(status)).Distinct().ToList().ShouldBeEmpty();
    }

    [Fact]
    public void The_dial_in_assistant_is_labeled()
    {
        var brew = new Brew { BagId = "b", MethodCode = "espresso", DoseG = 18m, YieldG = 36m, GrindSetting = 12m, TempC = 93m, TimeS = 28, Taste = new Taste(Acidity: -2) };
        var card = Render<DialInAdviceCard>(p => p.Add(x => x.Brew, brew).Add(x => x.Scale, Dialed.Domain.Equipment.GrindScale.Clicks(40)));
        Problems(card).Distinct().ToList().ShouldBeEmpty();

        card.Find("#dialin-ask").Click();

        card.FindAll("#dialin-ai").ShouldNotBeEmpty();
        Problems(card).Distinct().ToList().ShouldBeEmpty();
    }

    [Fact]
    public async Task Offline_scans_are_labeled()
    {
        Uploads.Set("p1", Dialed.Client.Uploads.UploadStatus.Waiting);
        ScanQueue.Set("p1", Dialed.Client.Scan.PendingScanStatus.Ready, new Dialed.Client.Scan.ScannedLabel(
            new(" Guji", 0.9), new("Friedhats", 0.9), new(null, 0), new(null, 0), new(null, 0), new(null, 0), new(null, 0), new(null, 0), new(null, 0), new(null, 0), new(null, 0), "v", "m"));
        ScanQueue.Set("p2", Dialed.Client.Scan.PendingScanStatus.Failed, problem: Dialed.Client.Scan.ScanProblem.QuotaUsed);
        await Services.GetRequiredService<Dialed.Client.Account.AuthSession>().SetAsync(
            new Dialed.Client.Account.SessionTokens("a", DateTimeOffset.MaxValue, "r", "u1", "Thomas", "h-u1"), Xunit.TestContext.Current.CancellationToken);

        var page = Render<Scan>();
        var banner = Render<PendingScansNote>();

        page.FindAll(".scan-pending-row").Count.ShouldBe(2);
        banner.Find("#scans-ready").ShouldNotBeNull();
        Problems(page).Concat(Problems(banner)).Distinct().ToList().ShouldBeEmpty();
    }

    [Fact] public void Recipes_is_labeled() => Audit<Recipes>();

    [Fact] public void Equipment_is_labeled() => Audit<Gear>();

    [Fact] public void History_is_labeled() => Audit<History>();

    [Fact] public void Vendors_is_labeled() => Audit<Vendors>();

    [Fact] public void Inventory_is_labeled() => Audit<Stock>();

    [Fact] public void People_is_labeled() => Audit<People>();

    [Fact] public void Friends_activity_is_labeled() => Audit<Activity>();

    [Fact] public void Settings_is_labeled() => Audit<Settings>();
    [Theory]
    [InlineData("profile")]
    [InlineData("preferences")]
    [InlineData("household")]
    [InlineData("data")]
    [InlineData("account")]
    [InlineData("privacy")]
    [InlineData("help")]
    public void Settings_groups_are_labeled(string group) => Audit<Settings>(p => p.Add(x => x.Group, group));

    [Fact] public void Statistics_is_labeled() => Audit<Statistics>();
    [Fact] public void HistoryCompare_is_labeled() => Audit<HistoryCompare>();

    [Fact] public void More_is_labeled() => Audit<More>();

    [Fact] public void Import_is_labeled() => Audit<Import>();

    [Fact] public void Scan_is_labeled() => Audit<Scan>();
    [Fact] public void Admin_is_labeled() => Audit<Admin>();
    [Fact] public void Catalog_suggestions_are_labeled() => Audit<Dialed.UI.Components.CatalogSuggestions>();
    [Fact] public void Notifications_panel_is_labeled() => Audit<Dialed.UI.Components.NotificationsPanel>();
    [Fact] public void BarcodeScan_is_labeled() => Audit<BarcodeScan>();
    [Fact] public void Public_profile_panel_is_labeled() => Audit<Dialed.UI.Components.PublicProfilePanel>();
    [Fact] public void Find_people_is_labeled() => Audit<Dialed.UI.Components.FindPeople>();

    [Fact] public void Onboarding_is_labeled() => Audit<Onboarding>();
    [Fact] public void SignIn_is_labeled() => Audit<SignIn>();

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

    // ---- Round 3 screens with data: bean page extras, drift note, gear upkeep, suggestions, notification switches ----

    private static CancellationToken Ct => Xunit.TestContext.Current.CancellationToken;

    private async Task<(Bean Bean, Bag Bag)> BeanWithOffBrews()
    {
        var bean = await Services.GetRequiredService<Dialed.Client.Data.IRepository<Bean>>().SaveAsync(new Bean { Name = "Kiambu", Origin = new BeanOrigin(Country: "Kenya"), TastingNotes = ["blackcurrant"] }, Ct);
        var bag = await Services.GetRequiredService<Dialed.Client.Data.IRepository<Bag>>().SaveAsync(new Bag { BeanId = bean.Id, WeightG = 250m, RoastDate = DateOnly.FromDateTime(DateTime.Now).AddDays(-12) }, Ct);
        var brews = Services.GetRequiredService<Dialed.Client.Data.IRepository<Brew>>();
        foreach (var (days, grind, rating) in new[] { (12, 26m, 5), (8, 24m, 4), (4, 23m, 4), (1, 22m, 5) })
        {
            await brews.SaveAsync(new Brew { BagId = bag.Id, MethodCode = "v60", DoseG = 15m, WaterG = 250m, GrindSetting = grind, Rating = rating, Taste = new Taste(Acidity: -1), BrewedAt = DateTimeOffset.Now.AddDays(-days) }, Ct);
        }

        return (bean, bag);
    }

    [Fact]
    public async Task The_bean_page_with_its_journey_taste_profile_and_recipe_offer_is_labeled()
    {
        var (bean, _) = await BeanWithOffBrews();
        var page = Render<Beans>(p => p.Add(x => x.Id, bean.Id));
        page.WaitForElement("#bean-recipe-from-best");

        page.FindAll("#bean-share-card").ShouldHaveSingleItem().GetAttribute("aria-label").ShouldNotBeNullOrWhiteSpace();
        page.FindAll("#bean-taste-profile [role=img]").ShouldAllBe(r => !string.IsNullOrWhiteSpace(r.GetAttribute("aria-label")));
        Problems(page).Distinct().ToList().ShouldBeEmpty();
    }

    [Fact]
    public async Task The_grind_drift_note_is_labeled_and_has_a_heading()
    {
        var (_, bag) = await BeanWithOffBrews();
        var page = Render<BrewHome>(p => p.Add(x => x.BagId, bag.Id));

        var note = page.WaitForElement("#brew-grind-drift");
        note.QuerySelector("h2").ShouldNotBeNull();
        Problems(page).Distinct().ToList().ShouldBeEmpty();
    }

    [Fact]
    public async Task Gear_upkeep_on_the_list_the_detail_and_the_form_is_labeled()
    {
        var gear = Services.GetRequiredService<Dialed.Client.Data.IRepository<EquipmentItem>>();
        var grinder = await gear.SaveAsync(new EquipmentItem { Kind = EquipmentKind.Grinder, Brand = "Eureka", Model = "Mignon", ServiceEveryDays = 7, ServicedOn = DateOnly.FromDateTime(DateTime.Now).AddDays(-9) }, Ct);

        var list = Render<Gear>();
        list.WaitForElement(".dialed-chip-warning");
        var detail = Render<Gear>(p => p.Add(x => x.Id, grinder.Id));
        detail.WaitForElement("#gear-upkeep-status");
        var form = Render<EquipmentForm>(p => p.Add(x => x.Item, grinder));

        Problems(list).Concat(Problems(detail)).Concat(Problems(form)).Distinct().ToList().ShouldBeEmpty();
        form.Find("#gear-every-days").ShouldNotBeNull();
    }

    [Fact]
    public void Tasting_note_suggestions_are_a_labeled_list_of_buttons()
    {
        var input = Render<ChipInput>(p => p.Add(x => x.Label, "Tasting notes").Add(x => x.SuggestFlavors, true));
        input.Find("input").Input("blu");

        input.Find(".dialed-chip-suggestions").GetAttribute("aria-label").ShouldNotBeNullOrWhiteSpace();
        Problems(input).Distinct().ToList().ShouldBeEmpty();
    }

    [Fact]
    public async Task The_notification_switches_including_service_due_are_labeled()
    {
        await Services.GetRequiredService<Dialed.Client.Account.AuthSession>().SetAsync(new Dialed.Client.Account.SessionTokens("a", DateTimeOffset.MaxValue, "r", "u1", "Thomas", "h-u1"), Ct);
        var panel = Render<NotificationsPanel>();
        panel.Find("#push-enable").Click();
        panel.WaitForElement("#push-service");

        Problems(panel).Distinct().ToList().ShouldBeEmpty();
    }
}
