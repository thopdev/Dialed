using Bunit;
using Dialed.Client.Data;
using Dialed.Client.Preferences;
using Dialed.Domain.BrewMethods;
using Dialed.Domain.Model;
using Dialed.UI.Components;
using Dialed.UI.Pages;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace Dialed.UI.Tests;

public sealed class SettingsOnboardingTests : UiTestContext
{
    private IUserPreferences Prefs => Services.GetRequiredService<IUserPreferences>();

    private IRepository<EquipmentItem> Gear => Services.GetRequiredService<IRepository<EquipmentItem>>();

    // ---- Settings -----------------------------------------------------------------------------

    [Fact]
    public void Settings_has_every_section()
    {
        var sections = new Dictionary<string, string[]>
        {
            ["profile"] = ["profile"],
            ["preferences"] = ["units", "methods", "inventory"],
            ["household"] = ["household"],
            ["data"] = ["sync", "storage"],
            ["account"] = ["account", "passkeys"],
            ["privacy"] = ["privacy"],
            ["help"] = ["setup", "feedback"],
        };
        foreach (var (group, ids) in sections)
        {
            var page = Render<Settings>(p => p.Add(x => x.Group, group));
            foreach (var id in ids)
            {
                page.FindAll($"#settings-{id}").Count.ShouldBe(1, id);
            }
        }

        Render<Settings>().Find("#settings-version").TextContent.ShouldStartWith("Version");
        Render<Settings>(p => p.Add(x => x.Group, "help")).Find("#settings-reopen-onboarding").GetAttribute("href").ShouldBe("welcome");
    }

    [Fact]
    public void Export_and_delete_are_only_offered_to_a_signed_in_person()
    {
        Render<Settings>(p => p.Add(x => x.Group, "account")).FindAll("#data-delete").ShouldBeEmpty();
    }

    [Fact]
    public void Profile_saves_name_and_default_visibility()
    {
        var form = Render<ProfileForm>();
        form.Find("#profile-name").Change("  Thomas ");
        form.Find("#profile-visibility").Change(nameof(Visibility.Private));

        form.Find("form").Submit();

        form.WaitForAssertion(() =>
        {
            Prefs.Profile.DisplayName.ShouldBe("Thomas");
            Prefs.Profile.DefaultVisibility.ShouldBe(Visibility.Private);
        });
    }

    [Fact]
    public async Task New_beans_start_with_the_default_visibility()
    {
        await Prefs.SetProfileAsync(Prefs.Profile with { DefaultVisibility = Visibility.Friends });

        var form = Render<BeanForm>();

        form.Find("#bean-visibility").GetAttribute("value").ShouldBe(nameof(Visibility.Friends));
    }

    [Fact]
    public void Toggling_a_method_stores_it_as_favourite_and_back()
    {
        var picker = Render<FavouriteMethodsPicker>();
        var v60 = picker.FindAll("button.dialed-chip").Single(c => c.TextContent == "Hario V60");

        v60.Click();
        Prefs.Profile.FavouriteMethods.ShouldBe(["v60"]);
        picker.WaitForAssertion(() => picker.FindAll("button.dialed-chip").Single(c => c.TextContent == "Hario V60").GetAttribute("aria-pressed").ShouldBe("true"));

        picker.FindAll("button.dialed-chip").Single(c => c.TextContent == "Hario V60").Click();
        Prefs.Profile.FavouriteMethods.ShouldBeEmpty();
    }

    [Fact]
    public async Task Favourite_methods_come_first_in_the_method_pickers()
    {
        await Prefs.SetProfileAsync(Prefs.Profile with { FavouriteMethods = ["aeropress"] });

        var form = Render<RecipeForm>();

        form.FindAll("#recipe-method option").First().GetAttribute("value").ShouldBe("aeropress");
    }

    [Fact]
    public void Ordered_puts_favourites_first_and_keeps_catalog_order()
    {
        var all = BrewMethodCatalog.Default.Methods.Select(m => m.Code).ToList();
        var ordered = BrewMethodCatalog.Default.Ordered([all[3], all[1]]).Select(m => m.Code).ToList();

        ordered.Take(2).ShouldBe([all[1], all[3]]);
        ordered.Count.ShouldBe(all.Count);
        BrewMethodCatalog.Default.Ordered([]).Select(m => m.Code).ShouldBe(all);
    }

    // ---- Onboarding ---------------------------------------------------------------------------

    [Fact]
    public void First_launch_sends_the_home_page_to_setup()
    {
        Render<Home>();

        Services.GetRequiredService<NavigationManager>().Uri.ShouldEndWith("/welcome");
    }

    [Fact]
    public async Task Home_stays_once_setup_is_done()
    {
        await Prefs.SetProfileAsync(Prefs.Profile with { OnboardingDone = true });

        Render<Home>();

        Services.GetRequiredService<NavigationManager>().Uri.ShouldNotEndWith("/welcome");
    }

    [Fact]
    public void Skipping_marks_setup_done_and_goes_home()
    {
        var page = Render<Onboarding>();

        page.Find("#onboarding-skip").Click();

        Prefs.Profile.OnboardingDone.ShouldBeTrue();
        Services.GetRequiredService<NavigationManager>().Uri.ShouldNotEndWith("/welcome");
    }

    [Fact]
    public void Steps_skip_the_install_guide_when_it_is_not_needed()
    {
        var page = Render<Onboarding>();

        page.Find("#onboarding-progress").TextContent.ShouldBe("Step 1 of 3");
    }

    [Fact]
    public void Steps_include_the_install_guide_on_safari_in_a_tab()
    {
        Pwa.NeedsInstallGuide = true;

        var page = Render<Onboarding>();

        page.Find("#onboarding-progress").TextContent.ShouldBe("Step 1 of 4");
        page.FindAll(".dialed-install-guide").Count.ShouldBe(1);
    }

    [Fact]
    public async Task Finishing_saves_grinder_and_brewer()
    {
        var page = Render<Onboarding>();
        page.Find("#onboarding-next").Click();
        page.Find("#onboarding-next").Click();
        page.Find("#ob-grinder-brand").Change("Comandante");
        page.Find("#ob-grinder-model").Change("C40");
        page.Find("#ob-brewer-brand").Change("Hario");

        page.Find("#onboarding-next").Click();

        page.WaitForAssertion(() => Prefs.Profile.OnboardingDone.ShouldBeTrue());
        var items = await Gear.ListAsync(Xunit.TestContext.Current.CancellationToken);
        items.Select(i => (i.Kind, i.Brand, i.Model)).ShouldBe(
            [(EquipmentKind.Grinder, "Comandante", "C40"), (EquipmentKind.Brewer, "Hario", null)], ignoreOrder: true);
    }

    [Fact]
    public async Task Finishing_with_empty_gear_adds_nothing()
    {
        var page = Render<Onboarding>();
        page.Find("#onboarding-next").Click();
        page.Find("#onboarding-next").Click();

        page.Find("#onboarding-next").Click();

        page.WaitForAssertion(() => Prefs.Profile.OnboardingDone.ShouldBeTrue());
        (await Gear.ListAsync(Xunit.TestContext.Current.CancellationToken)).ShouldBeEmpty();
    }

    [Fact]
    public void Back_returns_to_the_previous_step()
    {
        var page = Render<Onboarding>();
        page.Find("#onboarding-next").Click();

        page.Find("#onboarding-back").Click();

        page.Find("#onboarding-progress").TextContent.ShouldBe("Step 1 of 3");
    }
}
