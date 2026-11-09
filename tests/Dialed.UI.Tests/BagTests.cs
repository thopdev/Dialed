using Bunit;
using Dialed.Client.Data;
using Dialed.Domain.Inventory;
using Dialed.Domain.Model;
using Dialed.UI.Components;
using Dialed.UI.Pages;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace Dialed.UI.Tests;

public sealed class BagTests : UiTestContext
{
    private static CancellationToken Ct => Xunit.TestContext.Current.CancellationToken;

    private IRepository<Bean> Beans => Services.GetRequiredService<IRepository<Bean>>();

    private IRepository<Bag> Bags => Services.GetRequiredService<IRepository<Bag>>();

    private IRepository<Vendor> Vendors => Services.GetRequiredService<IRepository<Vendor>>();

    private static DateOnly Today => DateOnly.FromDateTime(DateTime.Now);

    private async Task<Bean> SomeBean() => await Beans.SaveAsync(new Bean { Name = "Gesha" }, Ct);

    // ---- Form ---------------------------------------------------------------------------------

    [Fact]
    public void Form_requires_a_bean_and_a_weight()
    {
        Bag? submitted = null;
        var form = Render<BagForm>(p => p.Add(x => x.OnSubmit, EventCallback.Factory.Create<Bag>(this, b => submitted = b)));

        form.Find("form").Submit();

        form.Markup.ShouldContain("Choose a bean.");
        form.Markup.ShouldContain("Enter the weight of the bag.");
        submitted.ShouldBeNull();
    }

    [Fact]
    public void Form_submits_a_bag_with_price_in_cents()
    {
        Bag? submitted = null;
        var beans = new[] { new Bean { Id = "bean1", Name = "Gesha" } };
        var form = Render<BagForm>(p => p
            .Add(x => x.Beans, beans)
            .Add(x => x.OnSubmit, EventCallback.Factory.Create<Bag>(this, b => submitted = b)));
        form.Find("#bag-bean").Change("bean1");
        form.Find("#bag-price").Change("14.50");
        form.Find("#bag-weight").Change("250");
        form.Find("#bag-roast-date").Change("2026-11-01");
        form.Find("#bag-purchased").Change("2026-11-05");
        form.Find("#bag-storage").Change("Fridge");

        form.Find("form").Submit();

        submitted.ShouldNotBeNull();
        submitted.BeanId.ShouldBe("bean1");
        submitted.PriceCents.ShouldBe(1450);
        submitted.Currency.ShouldBe("EUR");
        submitted.WeightG.ShouldBe(250m);
        submitted.RoastDate.ShouldBe(new DateOnly(2026, 11, 1));
        submitted.PurchasedOn.ShouldBe(new DateOnly(2026, 11, 5));
        submitted.Storage.ShouldBe(BagStorage.Fridge);
        submitted.Status.ShouldBe(BagStatus.Sealed);
    }

    [Fact]
    public void Form_preselects_the_given_bean()
    {
        var beans = new[] { new Bean { Id = "bean1", Name = "Gesha" } };

        var form = Render<BagForm>(p => p.Add(x => x.Beans, beans).Add(x => x.BeanId, "bean1"));

        form.Find("#bag-bean").GetAttribute("value").ShouldBe("bean1");
    }

    // ---- Page ---------------------------------------------------------------------------------

    [Fact]
    public void Empty_list_invites_the_first_bag()
    {
        var page = Render<Bags>();

        page.Markup.ShouldContain("No bags yet");
        page.Find("a[href='bags/new']").ShouldNotBeNull();
    }

    [Fact]
    public async Task Creating_a_bag_saves_it_sealed_and_opens_it()
    {
        var bean = await SomeBean();
        var page = Render<Bags>(p => p.Add(x => x.Id, "new"));
        page.Find("#bag-bean").Change(bean.Id);
        page.Find("#bag-weight").Change("250");

        page.Find("form").Submit();

        var bags = await Bags.ListAsync(Ct);
        bags.Count.ShouldBe(1);
        bags[0].Status.ShouldBe(BagStatus.Sealed);
        Services.GetRequiredService<NavigationManager>().Uri.ShouldEndWith($"bags/{bags[0].Id}");
    }

    [Fact]
    public async Task List_hides_finished_bags_until_asked()
    {
        var bean = await SomeBean();
        await Bags.SaveAsync(new Bag { BeanId = bean.Id, WeightG = 250m }, Ct);
        await Bags.SaveAsync(new Bag { BeanId = bean.Id, WeightG = 500m, Status = BagStatus.Finished }, Ct);
        var page = Render<Bags>();

        page.FindAll(".dialed-row-title").Count.ShouldBe(1);

        page.FindAll(".dialed-chip").First(c => c.TextContent == "All").Click();
        page.FindAll(".dialed-row-title").Count.ShouldBe(2);
    }

    [Fact]
    public async Task Detail_shows_seller_remaining_and_age()
    {
        var bean = await SomeBean();
        var seller = await Vendors.SaveAsync(new Vendor { Name = "Bean Shop", Kinds = VendorKinds.Seller }, Ct);
        var bag = await Bags.SaveAsync(new Bag { BeanId = bean.Id, SellerId = seller.Id, WeightG = 250m, PriceCents = 1450, Currency = "EUR", RoastDate = Today.AddDays(-5) }, Ct);

        var page = Render<Bags>(p => p.Add(x => x.Id, bag.Id));

        page.Find(".dialed-detail-pane h1").TextContent.ShouldBe("Gesha");
        page.Markup.ShouldContain("Bean Shop");
        page.Markup.ShouldContain("14.50 EUR");
        page.Markup.ShouldContain("5 days");
        page.Markup.ShouldContain("250 g of 250 g left");
        page.FindAll("#bag-offer-finish").ShouldBeEmpty();
    }

    private async Task<(IRenderedComponent<Bags> Page, Bag Bag)> OpenDetail(Bag? template = null)
    {
        var bean = await SomeBean();
        var bag = await Bags.SaveAsync((template ?? new Bag { BeanId = bean.Id, WeightG = 250m }) with { BeanId = bean.Id }, Ct);
        return (Render<Bags>(p => p.Add(x => x.Id, bag.Id)), bag);
    }

    private static void Press(IRenderedComponent<Bags> page, string label) =>
        page.FindAll(".dialed-form-actions button").First(b => b.TextContent.Trim() == label).Click();

    [Fact]
    public async Task Open_freeze_thaw_and_finish_follow_the_lifecycle()
    {
        var (page, bag) = await OpenDetail();

        Press(page, "Open");
        (await Bags.GetAsync(bag.Id, Ct))!.Status.ShouldBe(BagStatus.Open);

        Press(page, "Freeze");
        var frozen = (await Bags.GetAsync(bag.Id, Ct))!;
        frozen.Status.ShouldBe(BagStatus.Frozen);
        frozen.FrozenOn.ShouldBe(Today);

        Press(page, "Thaw");
        (await Bags.GetAsync(bag.Id, Ct))!.Status.ShouldBe(BagStatus.Open);

        Press(page, "Finish");
        var finished = (await Bags.GetAsync(bag.Id, Ct))!;
        finished.Status.ShouldBe(BagStatus.Finished);
        finished.FinishedOn.ShouldBe(Today);
        page.WaitForAssertion(() => page.FindAll("#bag-step-date").ShouldBeEmpty());
    }

    [Fact]
    public async Task A_thaw_date_before_the_freeze_is_refused()
    {
        var (page, bag) = await OpenDetail();
        Press(page, "Freeze");

        page.Find("#bag-step-date").Change(Today.AddDays(-3).ToString("yyyy-MM-dd"));
        Press(page, "Thaw");

        page.Markup.ShouldContain("That date does not work");
        (await Bags.GetAsync(bag.Id, Ct))!.Status.ShouldBe(BagStatus.Frozen);
    }

    [Fact]
    public async Task An_empty_bag_offers_to_finish()
    {
        var (page, bag) = await OpenDetail(new Bag { BeanId = "x", WeightG = 0m });

        page.Find("#bag-offer-finish").TextContent.ShouldContain("Mark it finished?");
        page.Find("#bag-offer-finish button").Click();

        (await Bags.GetAsync(bag.Id, Ct))!.Status.ShouldBe(BagStatus.Finished);
    }

    [Fact]
    public async Task Bought_again_creates_a_fresh_sealed_copy_dated_today()
    {
        var (page, bag) = await OpenDetail(new Bag { BeanId = "x", WeightG = 250m, PriceCents = 1450, Currency = "EUR", Status = BagStatus.Finished, RoastDate = Today.AddDays(-40) });

        Press(page, "Bought again");

        var bags = await Bags.ListAsync(Ct);
        bags.Count.ShouldBe(2);
        var copy = bags.Single(b => b.Id != bag.Id);
        copy.BeanId.ShouldBe(bag.BeanId);
        copy.PriceCents.ShouldBe(1450);
        copy.PurchasedOn.ShouldBe(Today);
        copy.Status.ShouldBe(BagStatus.Sealed);
        copy.RoastDate.ShouldBeNull();
    }

    [Fact]
    public async Task Editing_a_bag_saves_the_change()
    {
        var (page, bag) = await OpenDetail();

        page.FindAll(".dialed-page-actions button").First(b => b.GetAttribute("aria-label") == "Edit").Click();
        page.Find("#bag-weight").Change("500");
        page.Find("form").Submit();

        (await Bags.GetAsync(bag.Id, Ct))!.WeightG.ShouldBe(500m);
    }

    [Fact]
    public async Task Deleting_a_bag_asks_first()
    {
        var (page, bag) = await OpenDetail();

        page.FindAll(".dialed-page-actions button").First(b => b.GetAttribute("aria-label") == "Delete").Click();
        (await Bags.GetAsync(bag.Id, Ct)).ShouldNotBeNull();
        page.Find(".dialed-confirm .dialed-button-danger").Click();

        (await Bags.GetAsync(bag.Id, Ct)).ShouldBeNull();
    }

    [Fact]
    public async Task Bean_detail_links_to_adding_a_bag()
    {
        var bean = await SomeBean();

        var page = Render<Beans>(p => p.Add(x => x.Id, bean.Id));

        page.Find($"a[href='bags/new?beanId={bean.Id}']").ShouldNotBeNull();
    }
}
