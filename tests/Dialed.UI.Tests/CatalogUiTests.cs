using Bunit;
using Dialed.Client.Catalog;
using Dialed.Client.Data;
using Dialed.Client.Platform;
using Dialed.Client.Scan;
using Dialed.Domain.Model;
using Dialed.UI.Components;
using Dialed.UI.Pages;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace Dialed.UI.Tests;

/// <summary>Catalog suggestions in the bean form, the roaster picker and the label scan (#105).</summary>
public sealed class CatalogUiTests : UiTestContext
{
    private static CancellationToken Ct => Xunit.TestContext.Current.CancellationToken;

    private static CatalogBean Guji(int people = 7) => new(
        "b-1", "Ethiopia Guji Natural", "r-1", "Friedhats", "Ethiopia", "Guji", "Natural", ["Heirloom"], RoastLevel.Light, ["Jasmine", "Blueberry"], people);

    private IRepository<Vendor> Vendors => Services.GetRequiredService<IRepository<Vendor>>();

    private void WaitForSuggestions(IRenderedComponent<BeanForm> form) =>
        form.WaitForElement("#bean-catalog", TimeSpan.FromSeconds(5));

    // ---- The bean form ------------------------------------------------------------------------

    [Fact]
    public void Typing_a_name_suggests_beans_from_the_catalog_with_who_shares_them()
    {
        Catalog.Beans.Add(Guji());
        var form = Render<BeanForm>();

        form.Find("#bean-name").Input("guji");

        WaitForSuggestions(form);
        var row = form.Find(".catalog-row");
        row.TextContent.ShouldContain("Ethiopia Guji Natural");
        row.TextContent.ShouldContain("Friedhats");
        row.TextContent.ShouldContain("shared by 7");
        form.Find(".catalog-use").GetAttribute("aria-label").ShouldBe("Use Ethiopia Guji Natural from the catalog");
    }

    [Fact]
    public void One_letter_asks_nothing()
    {
        Catalog.Beans.Add(Guji());
        var form = Render<BeanForm>();

        form.Find("#bean-name").Input("g");
        Thread.Sleep(600);

        form.FindAll("#bean-catalog").ShouldBeEmpty();
        Catalog.Asked.ShouldBeEmpty();
    }

    [Fact]
    public void Using_an_entry_copies_it_into_the_form_and_everything_stays_editable()
    {
        Catalog.Beans.Add(Guji());
        Bean? saved = null;
        var form = Render<BeanForm>(p => p.Add(x => x.OnSubmit, EventCallback.Factory.Create<Bean>(this, b => saved = b)));
        form.Find("#bean-name").Input("guji");
        WaitForSuggestions(form);

        form.Find(".catalog-use").Click();

        form.WaitForAssertion(() => form.Find("#bean-name").GetAttribute("value").ShouldBe("Ethiopia Guji Natural"));
        form.Find("#bean-country").GetAttribute("value").ShouldBe("Ethiopia");
        form.Find("#bean-region").GetAttribute("value").ShouldBe("Guji");
        form.Find("#bean-process").GetAttribute("value").ShouldBe("Natural");
        form.Find("#bean-country").Change("Kenya"); // still editable
        form.Find("form").Submit();

        saved.ShouldNotBeNull();
        saved.Name.ShouldBe("Ethiopia Guji Natural");
        saved.Origin.Country.ShouldBe("Kenya");
        saved.Origin.Region.ShouldBe("Guji");
        saved.Varieties.ShouldBe(["Heirloom"]);
        saved.RoastLevel.ShouldBe(RoastLevel.Light);
        saved.TastingNotes.ShouldBe(["Jasmine", "Blueberry"]);
    }

    [Fact]
    public async Task Using_an_entry_makes_the_roaster_a_vendor_of_my_own_and_does_not_link_to_the_catalog()
    {
        Catalog.Beans.Add(Guji());
        Bean? saved = null;
        var form = Render<BeanForm>(p => p.Add(x => x.OnSubmit, EventCallback.Factory.Create<Bean>(this, b => saved = b)));
        form.Find("#bean-name").Input("guji");
        WaitForSuggestions(form);

        form.Find(".catalog-use").Click();
        form.WaitForAssertion(() => form.Find(".dialed-picker-selected").TextContent.ShouldContain("Friedhats"));
        form.Find("form").Submit();

        var roaster = (await Vendors.ListAsync(Ct)).ShouldHaveSingleItem();
        roaster.Name.ShouldBe("Friedhats");
        saved!.RoasterId.ShouldBe(roaster.Id);
        saved.Id.ShouldNotBe("b-1"); // a copy, never a link
        saved.Notes.ShouldBeNull();
    }

    [Fact]
    public async Task A_roaster_I_already_have_is_reused_not_duplicated()
    {
        var mine = await Vendors.SaveAsync(new Vendor { Name = "Friedhats Coffee Roasters" }, Ct);
        Catalog.Beans.Add(Guji());
        var form = Render<BeanForm>();
        form.Find("#bean-name").Input("guji");
        WaitForSuggestions(form);

        form.Find(".catalog-use").Click();

        form.WaitForAssertion(() => form.Find(".dialed-picker-selected").TextContent.ShouldContain("Friedhats Coffee Roasters"));
        (await Vendors.ListAsync(Ct)).ShouldHaveSingleItem().Id.ShouldBe(mine.Id);
    }

    [Fact]
    public void Offline_or_with_nothing_found_the_form_works_as_ever()
    {
        Catalog.Down = true;
        var form = Render<BeanForm>();

        form.Find("#bean-name").Input("guji");
        Thread.Sleep(600);

        form.FindAll("#bean-catalog").ShouldBeEmpty();
        form.Find("#bean-name").GetAttribute("value").ShouldBe("guji");
    }

    [Fact]
    public void Editing_an_existing_bean_does_not_offer_the_catalog()
    {
        Catalog.Beans.Add(Guji());
        var form = Render<BeanForm>(p => p.Add(x => x.Bean, new Bean { Name = "Guji" }));

        form.Find("#bean-name").Input("guji natural");
        Thread.Sleep(600);

        form.FindAll("#bean-catalog").ShouldBeEmpty();
        Catalog.Asked.ShouldBeEmpty();
    }

    // ---- The roaster picker -------------------------------------------------------------------

    [Fact]
    public async Task Typing_a_roaster_suggests_catalog_roasters_I_do_not_have_and_choosing_one_copies_it()
    {
        Catalog.Roasters.Add(new CatalogRoaster("r-9", "Manhattan Coffee Roasters", "Netherlands", "Rotterdam", "https://manhattan.example", 5));
        var picker = Render<VendorPicker>(p => p.Add(x => x.UseCatalog, true));

        picker.Find("input").Input("manh");
        picker.WaitForElement(".dialed-picker-catalog", TimeSpan.FromSeconds(5));
        picker.Find(".dialed-picker-catalog").GetAttribute("aria-label").ShouldBe("Use Manhattan Coffee Roasters from the catalog");
        picker.Find(".dialed-picker-catalog").Click();

        var created = (await Vendors.ListAsync(Ct)).ShouldHaveSingleItem();
        (created.Name, created.City, created.Country, created.Website).ShouldBe(("Manhattan Coffee Roasters", "Rotterdam", "Netherlands", "https://manhattan.example"));
        picker.Find(".dialed-picker-selected").TextContent.ShouldContain("Manhattan");
    }

    [Fact]
    public async Task A_catalog_roaster_I_already_have_is_not_suggested_again()
    {
        await Vendors.SaveAsync(new Vendor { Name = "Manhattan Coffee Roasters" }, Ct);
        Catalog.Roasters.Add(new CatalogRoaster("r-9", "Manhattan Coffee Roasters", null, null, null, 5));
        var picker = Render<VendorPicker>(p => p.Add(x => x.UseCatalog, true));

        picker.Find("input").Input("manh");
        Thread.Sleep(600);

        picker.FindAll(".dialed-picker-catalog").ShouldBeEmpty();
    }

    [Fact]
    public void The_roaster_picker_asks_the_catalog_only_where_it_is_wanted()
    {
        Catalog.Roasters.Add(new CatalogRoaster("r-9", "Manhattan", null, null, null, 5));
        var picker = Render<VendorPicker>();

        picker.Find("input").Input("manh");
        Thread.Sleep(600);

        Catalog.Asked.ShouldBeEmpty();
    }

    // ---- The label scan -----------------------------------------------------------------------

    private static ScannedField F(string? value) => new(value, 0.9);

    private static ScannedLabel Label(string name) => new(F(name), F("Friedhats"), F(null), F(null), F(null), F(null), F(null), F(null), F(null), F(null), F("250"), "label-v1", "fake");

    private IRenderedComponent<Scan> Scanned(string name)
    {
        Scanner.Result = Label(name);
        Camera.Next = new CapturedPhoto([1, 2, 3], "image/jpeg", "label.jpg");
        var page = Render<Scan>();
        page.Find("#scan-camera").Click();
        page.WaitForAssertion(() => page.Find("#scan-review"));
        return page;
    }

    private async Task SignIn() =>
        await Services.GetRequiredService<Dialed.Client.Account.AuthSession>().SetAsync(new Dialed.Client.Account.SessionTokens("a", DateTimeOffset.MaxValue, "r", "u1", "Thomas", "h-u1"), Ct);

    [Fact]
    public async Task When_nothing_of_mine_matches_the_scan_offers_catalog_candidates()
    {
        await SignIn();
        Catalog.Beans.Add(Guji());

        var page = Scanned("Guji Natural");

        page.WaitForAssertion(() => page.Find("#scan-catalog .catalog-row").TextContent.ShouldContain("Ethiopia Guji Natural"));
    }

    [Fact]
    public async Task Using_a_candidate_fills_the_origin_fields_but_keeps_the_label_name_and_saves_a_copy()
    {
        await SignIn();
        Catalog.Beans.Add(Guji());
        var page = Scanned("Guji Natural");
        page.WaitForElement("#scan-catalog .catalog-use");

        page.Find("#scan-catalog .catalog-use").Click();

        page.Find("#scan-name").GetAttribute("value").ShouldBe("Guji Natural");
        page.Find("#scan-country").GetAttribute("value").ShouldBe("Ethiopia");
        page.Find("#scan-region").GetAttribute("value").ShouldBe("Guji");
        page.Find("#scan-process").GetAttribute("value").ShouldBe("Natural");
        page.Find("#scan-varieties").GetAttribute("value").ShouldBe("Heirloom");
        page.Find("#scan-notes").GetAttribute("value").ShouldBe("Jasmine, Blueberry");
        page.FindAll("#scan-catalog").ShouldBeEmpty();
        page.Find("#scan-review").Submit();

        var bean = (await Services.GetRequiredService<IRepository<Bean>>().ListAsync(Ct)).ShouldHaveSingleItem();
        (bean.Name, bean.Origin.Country, bean.Origin.Region, bean.Process, bean.RoastLevel).ShouldBe(("Guji Natural", "Ethiopia", "Guji", "Natural", RoastLevel.Light));
    }

    [Fact]
    public async Task A_bean_of_my_own_that_matches_means_no_catalog_candidates()
    {
        await SignIn();
        Catalog.Beans.Add(Guji());
        await Services.GetRequiredService<IRepository<Bean>>().SaveAsync(new Bean { Name = "Guji Natural" }, Ct);

        var page = Scanned("Guji Natural");

        page.FindAll("#scan-catalog").ShouldBeEmpty();
    }

    [Fact]
    public async Task Without_a_network_the_scan_has_no_catalog_section_and_works_as_ever()
    {
        await SignIn();
        Catalog.Beans.Add(Guji());
        Catalog.Down = true;

        var page = Scanned("Guji Natural");

        page.FindAll("#scan-catalog").ShouldBeEmpty();
        page.Find("#scan-save").ShouldNotBeNull();
    }
}
