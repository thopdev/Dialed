using Bunit;
using Dialed.Client.Data;
using Dialed.Domain.Model;
using Dialed.UI.Components;
using Dialed.UI.Pages;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace Dialed.UI.Tests;

public sealed class VendorTests : UiTestContext
{
    private static CancellationToken Ct => Xunit.TestContext.Current.CancellationToken;

    private IRepository<Vendor> Vendors => Services.GetRequiredService<IRepository<Vendor>>();

    private IRepository<Bean> Beans => Services.GetRequiredService<IRepository<Bean>>();

    private IRepository<Bag> Bags => Services.GetRequiredService<IRepository<Bag>>();

    // ---- Form ---------------------------------------------------------------------------------

    [Fact]
    public void Form_requires_a_name()
    {
        Vendor? submitted = null;
        var form = Render<VendorForm>(p => p.Add(x => x.OnSubmit, EventCallback.Factory.Create<Vendor>(this, v => submitted = v)));

        form.Find("form").Submit();

        form.Find("#vendor-name + .dialed-field-error").TextContent.ShouldBe("Enter a name.");
        submitted.ShouldBeNull();
    }

    [Fact]
    public void Form_requires_at_least_one_kind()
    {
        var form = Render<VendorForm>();
        form.Find("#vendor-name").Input("Friedhats");
        form.FindAll("input[type=checkbox]")[0].Change(false);

        form.Find("form").Submit();

        form.Markup.ShouldContain("Choose roaster, seller or both.");
    }

    [Fact]
    public void Form_rejects_an_unusable_website()
    {
        var form = Render<VendorForm>();
        form.Find("#vendor-name").Input("Friedhats");
        form.Find("#vendor-website").Change("not a url");

        form.Find("form").Submit();

        form.Markup.ShouldContain("Enter a web address");
    }

    [Fact]
    public void Form_submits_a_cleaned_up_vendor()
    {
        Vendor? submitted = null;
        var form = Render<VendorForm>(p => p.Add(x => x.OnSubmit, EventCallback.Factory.Create<Vendor>(this, v => submitted = v)));
        form.Find("#vendor-name").Input("  Friedhats ");
        form.FindAll("input[type=checkbox]")[1].Change(true);
        form.Find("#vendor-country").Change("Netherlands");
        form.Find("#vendor-city").Change("Amsterdam");
        form.Find("#vendor-website").Change("friedhats.com");
        form.Find("#vendor-notes").Change("  Great filter roasts ");

        form.Find("form").Submit();

        submitted.ShouldNotBeNull();
        submitted.Name.ShouldBe("Friedhats");
        submitted.Kinds.ShouldBe(VendorKinds.Roaster | VendorKinds.Seller);
        submitted.Country.ShouldBe("Netherlands");
        submitted.City.ShouldBe("Amsterdam");
        submitted.Website.ShouldBe("https://friedhats.com");
        submitted.Notes.ShouldBe("Great filter roasts");
    }

    [Fact]
    public void Form_starts_from_the_vendor_being_edited()
    {
        var vendor = new Vendor { Name = "Onyx", Kinds = VendorKinds.Seller, City = "Rogers" };

        var form = Render<VendorForm>(p => p.Add(x => x.Vendor, vendor));

        form.Find("#vendor-name").GetAttribute("value").ShouldBe("Onyx");
        form.Find("#vendor-city").GetAttribute("value").ShouldBe("Rogers");
        form.FindAll("input[type=checkbox]")[0].HasAttribute("checked").ShouldBeFalse();
        form.FindAll("input[type=checkbox]")[1].HasAttribute("checked").ShouldBeTrue();
    }

    [Fact]
    public void Form_cancel_raises_the_callback()
    {
        var cancelled = false;
        var form = Render<VendorForm>(p => p.Add(x => x.OnCancel, EventCallback.Factory.Create(this, () => cancelled = true)));

        form.FindAll("button[type=button]").Last().Click();

        cancelled.ShouldBeTrue();
    }

    // ---- Picker -------------------------------------------------------------------------------

    [Fact]
    public async Task Picker_suggests_matching_vendors_forgivingly()
    {
        await Vendors.SaveAsync(new Vendor { Name = "Friedhats", City = "Amsterdam" }, Ct);
        await Vendors.SaveAsync(new Vendor { Name = "Tim Wendelboe" }, Ct);
        var picker = Render<VendorPicker>();

        picker.Find("input").Input("freidhat");

        picker.FindAll(".dialed-picker-option").Select(o => o.TextContent.Trim()).ShouldContain(t => t.StartsWith("Friedhats", StringComparison.Ordinal));
        picker.Markup.ShouldNotContain("Wendelboe");
    }

    [Fact]
    public async Task Picker_offers_to_create_a_new_vendor_and_selects_it()
    {
        string? selectedId = null;
        var picker = Render<VendorPicker>(p => p
            .Add(x => x.SelectedIdChanged, EventCallback.Factory.Create<string?>(this, id => selectedId = id)));
        picker.Find("input").Input("Coffee Collective");

        picker.Find(".dialed-picker-create").TextContent.ShouldContain("Create \"Coffee Collective\"");
        picker.Find(".dialed-picker-create").Click();

        var vendors = await Vendors.ListAsync(Ct);
        vendors.Count.ShouldBe(1);
        vendors[0].Name.ShouldBe("Coffee Collective");
        vendors[0].Kinds.ShouldBe(VendorKinds.Roaster);
        selectedId.ShouldBe(vendors[0].Id);
        picker.WaitForAssertion(() => picker.Find(".dialed-picker-selected .dialed-chip").TextContent.ShouldContain("Coffee Collective"));
    }

    [Fact]
    public async Task Picker_does_not_offer_to_create_a_name_that_exists()
    {
        await Vendors.SaveAsync(new Vendor { Name = "Friedhats" }, Ct);
        var picker = Render<VendorPicker>();

        picker.Find("input").Input("friedhats");

        picker.FindAll(".dialed-picker-create").ShouldBeEmpty();
        picker.FindAll(".dialed-picker-option").Count.ShouldBe(1);
    }

    [Fact]
    public async Task Picker_selecting_and_clearing_report_the_choice()
    {
        var vendor = await Vendors.SaveAsync(new Vendor { Name = "Friedhats" }, Ct);
        var changes = new List<string?>();
        var picker = Render<VendorPicker>(p => p
            .Add(x => x.SelectedIdChanged, EventCallback.Factory.Create<string?>(this, id => changes.Add(id))));
        picker.Find("input").Input("fried");

        picker.Find(".dialed-picker-option").Click();
        picker.Find(".dialed-picker-clear").Click();

        changes.ShouldBe([vendor.Id, null]);
        picker.FindAll("input[type=text]").Count.ShouldBe(1);
    }

    [Fact]
    public async Task Picker_only_suggests_vendors_of_the_wanted_kind()
    {
        await Vendors.SaveAsync(new Vendor { Name = "Seller Shop", Kinds = VendorKinds.Seller }, Ct);
        await Vendors.SaveAsync(new Vendor { Name = "Seller Roastery", Kinds = VendorKinds.Roaster }, Ct);
        var picker = Render<VendorPicker>(p => p.Add(x => x.Kind, VendorKinds.Roaster));

        picker.Find("input").Input("seller");

        picker.Markup.ShouldContain("Seller Roastery");
        picker.Markup.ShouldNotContain("Seller Shop");
    }

    [Fact]
    public async Task Picker_shows_a_preselected_vendor()
    {
        var vendor = await Vendors.SaveAsync(new Vendor { Name = "Friedhats" }, Ct);

        var picker = Render<VendorPicker>(p => p.Add(x => x.SelectedId, vendor.Id));

        picker.Find(".dialed-picker-selected .dialed-chip").TextContent.ShouldContain("Friedhats");
    }

    // ---- Page ---------------------------------------------------------------------------------

    [Fact]
    public void Empty_list_invites_the_first_vendor()
    {
        var page = Render<Vendors>();

        page.Markup.ShouldContain("No roasters or sellers yet");
        page.Find("a[href='vendors/new']").ShouldNotBeNull();
    }

    [Fact]
    public async Task List_shows_vendors_searches_and_filters_by_kind()
    {
        await Vendors.SaveAsync(new Vendor { Name = "Friedhats", Kinds = VendorKinds.Roaster, City = "Amsterdam" }, Ct);
        await Vendors.SaveAsync(new Vendor { Name = "Bean Shop", Kinds = VendorKinds.Seller }, Ct);
        var page = Render<Vendors>();

        page.FindAll(".dialed-row-title").Select(t => t.TextContent).ShouldBe(["Bean Shop", "Friedhats"]);

        page.FindAll(".dialed-chip").First(c => c.TextContent == "Sellers").Click();
        page.FindAll(".dialed-row-title").Select(t => t.TextContent).ShouldBe(["Bean Shop"]);

        page.FindAll(".dialed-chip").First(c => c.TextContent == "All").Click();
        page.Find("#vendor-search").Input("fried");
        page.FindAll(".dialed-row-title").Select(t => t.TextContent).ShouldBe(["Friedhats"]);

        page.Find("#vendor-search").Input("zzzz");
        page.Markup.ShouldContain("Nothing matches.");
    }

    [Fact]
    public async Task Detail_shows_the_vendor_with_its_beans_and_bags_and_opens_the_website_in_a_new_tab()
    {
        var vendor = await Vendors.SaveAsync(new Vendor { Name = "Friedhats", Website = "https://friedhats.com", City = "Amsterdam", Country = "Netherlands" }, Ct);
        var bean = await Beans.SaveAsync(new Bean { Name = "Gesha Village", RoasterId = vendor.Id }, Ct);
        await Bags.SaveAsync(new Bag { BeanId = bean.Id, SellerId = vendor.Id, WeightG = 250m }, Ct);

        var page = Render<Vendors>(p => p.Add(x => x.Id, vendor.Id));

        page.Find(".dialed-detail-pane h1").TextContent.ShouldBe("Friedhats");
        page.Markup.ShouldContain("Amsterdam, Netherlands");
        var link = page.Find("a[href='https://friedhats.com']");
        link.GetAttribute("target").ShouldBe("_blank");
        link.GetAttribute("rel").ShouldBe("noopener noreferrer");
        page.Markup.ShouldContain("Gesha Village");
        page.Markup.ShouldContain("250 g");
    }

    [Fact]
    public void Unknown_vendor_shows_a_not_found_message()
    {
        Render<Vendors>(p => p.Add(x => x.Id, "does-not-exist")).Markup.ShouldContain("no longer exists");
    }

    [Fact]
    public async Task Creating_a_vendor_saves_it_and_opens_it()
    {
        var page = Render<Vendors>(p => p.Add(x => x.Id, "new"));
        page.Find("#vendor-name").Input("Onyx");

        page.Find("form").Submit();

        var vendors = await Vendors.ListAsync(Ct);
        vendors.Select(v => v.Name).ShouldBe(["Onyx"]);
        Services.GetRequiredService<NavigationManager>().Uri.ShouldEndWith($"vendors/{vendors[0].Id}");
    }

    [Fact]
    public async Task Editing_a_vendor_saves_the_change()
    {
        var vendor = await Vendors.SaveAsync(new Vendor { Name = "Onyx" }, Ct);
        var page = Render<Vendors>(p => p.Add(x => x.Id, vendor.Id));

        page.FindAll(".dialed-page-actions button").First(b => b.TextContent == "Edit").Click();
        page.Find("#vendor-name").Input("Onyx Coffee Lab");
        page.Find("form").Submit();

        (await Vendors.GetAsync(vendor.Id, Ct))!.Name.ShouldBe("Onyx Coffee Lab");
        page.WaitForAssertion(() => page.Find(".dialed-detail-pane h1").TextContent.ShouldBe("Onyx Coffee Lab"));
    }

    [Fact]
    public async Task An_edit_from_an_outdated_copy_shows_a_conflict_message()
    {
        var vendor = await Vendors.SaveAsync(new Vendor { Name = "Onyx" }, Ct);
        var page = Render<Vendors>(p => p.Add(x => x.Id, vendor.Id));
        page.FindAll(".dialed-page-actions button").First(b => b.TextContent == "Edit").Click();

        await Vendors.SaveAsync(vendor with { Name = "Changed elsewhere" }, Ct);
        page.Find("#vendor-name").Input("My edit");
        page.Find("form").Submit();

        page.WaitForAssertion(() => page.Markup.ShouldContain("changed elsewhere"));
    }

    [Fact]
    public async Task Deleting_an_unused_vendor_asks_for_confirmation_first()
    {
        var vendor = await Vendors.SaveAsync(new Vendor { Name = "Onyx" }, Ct);
        var page = Render<Vendors>(p => p.Add(x => x.Id, vendor.Id));

        page.FindAll(".dialed-page-actions button").First(b => b.TextContent == "Delete").Click();
        page.Find(".dialed-confirm").TextContent.ShouldContain("Delete Onyx?");
        (await Vendors.GetAsync(vendor.Id, Ct)).ShouldNotBeNull();

        page.Find(".dialed-confirm .dialed-button-danger").Click();

        (await Vendors.GetAsync(vendor.Id, Ct)).ShouldBeNull();
    }

    [Fact]
    public async Task Deleting_a_used_vendor_warns_and_unlinks_beans_and_bags()
    {
        var vendor = await Vendors.SaveAsync(new Vendor { Name = "Friedhats" }, Ct);
        var bean = await Beans.SaveAsync(new Bean { Name = "Gesha", RoasterId = vendor.Id }, Ct);
        var bag = await Bags.SaveAsync(new Bag { BeanId = bean.Id, SellerId = vendor.Id, WeightG = 250m }, Ct);
        var page = Render<Vendors>(p => p.Add(x => x.Id, vendor.Id));

        page.FindAll(".dialed-page-actions button").First(b => b.TextContent == "Delete").Click();
        page.Find(".dialed-confirm").TextContent.ShouldContain("used by 1 bean(s) and 1 bag(s)");
        page.Find(".dialed-confirm .dialed-button-danger").Click();

        (await Vendors.GetAsync(vendor.Id, Ct)).ShouldBeNull();
        (await Beans.GetAsync(bean.Id, Ct))!.RoasterId.ShouldBeNull();
        (await Bags.GetAsync(bag.Id, Ct))!.SellerId.ShouldBeNull();
    }

    [Fact]
    public async Task Cancelling_the_delete_keeps_the_vendor()
    {
        var vendor = await Vendors.SaveAsync(new Vendor { Name = "Onyx" }, Ct);
        var page = Render<Vendors>(p => p.Add(x => x.Id, vendor.Id));
        page.FindAll(".dialed-page-actions button").First(b => b.TextContent == "Delete").Click();

        page.FindAll(".dialed-confirm button").First(b => b.TextContent == "Cancel").Click();

        page.FindAll(".dialed-confirm").ShouldBeEmpty();
        (await Vendors.GetAsync(vendor.Id, Ct)).ShouldNotBeNull();
    }

    [Fact]
    public void More_page_links_to_the_vendors()
    {
        Render<More>().Find("a[href='vendors']").ShouldNotBeNull();
    }
}
