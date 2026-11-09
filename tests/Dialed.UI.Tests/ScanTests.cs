using Bunit;
using Dialed.Client.Account;
using Dialed.Client.Data;
using Dialed.Client.Platform;
using Dialed.Client.Scan;
using Dialed.Domain.Model;
using Dialed.UI.Pages;

using Microsoft.Extensions.DependencyInjection;

namespace Dialed.UI.Tests;

public sealed class ScanTests : UiTestContext
{
    private static CancellationToken Ct => Xunit.TestContext.Current.CancellationToken;

    private static ScannedField F(string? value, double confidence = 0.9) => new(value, confidence);

    private static ScannedLabel Label(ScannedField? name = null, ScannedField? roaster = null) => new(
        name ?? F("Guji Hambela"), roaster ?? F("Friedhats"), F("Ethiopia"), F(null, 0), F(null, 0), F("Natural", 0.4), F(null, 0), F("light"), F(null, 0),
        F("2026-10-20"), F("250"), "label-v1", "fake");

    private async Task SignIn() =>
        await Services.GetRequiredService<AuthSession>().SetAsync(new SessionTokens("a", DateTimeOffset.MaxValue, "r", "u1", "Thomas", "h-u1"), Ct);

    private IRenderedComponent<Scan> Scanned(ScannedLabel? label = null)
    {
        Scanner.Result = label ?? Label();
        Camera.Next = new CapturedPhoto([1, 2, 3], "image/jpeg", "label.jpg");
        var page = Render<Scan>();
        page.Find("#scan-camera").Click();
        page.WaitForAssertion(() => page.Find("#scan-review"));
        return page;
    }

    [Fact]
    public void Signed_out_people_are_pointed_to_manual_entry()
    {
        var page = Render<Scan>();

        page.FindAll("#scan-camera").ShouldBeEmpty();
        page.Markup.ShouldContain("needs an account");
    }

    [Fact]
    public async Task A_scan_prefills_the_form_and_marks_uncertain_fields()
    {
        await SignIn();

        var page = Scanned();

        page.Find("#scan-name").GetAttribute("value").ShouldBe("Guji Hambela");
        page.Find("#scan-roaster").GetAttribute("value").ShouldBe("Friedhats");
        page.Find("#scan-weight").GetAttribute("value").ShouldBe("250");
        page.Find("#scan-roastdate").GetAttribute("value").ShouldBe("2026-10-20");
        page.FindAll(".dialed-uncertain").Count.ShouldBe(1);
        page.Find(".dialed-uncertain").TextContent.ShouldContain("Not sure");
    }

    [Fact]
    public async Task Saving_creates_roaster_bean_and_bag_and_everything_was_editable()
    {
        await SignIn();
        var page = Scanned();

        page.Find("#scan-name").Change("Guji Natural");
        page.Find("#scan-weight").Change("340");
        page.Find("#scan-review").Submit();

        var bean = (await Services.GetRequiredService<IRepository<Bean>>().ListAsync(Ct)).ShouldHaveSingleItem();
        bean.Name.ShouldBe("Guji Natural");
        bean.Origin.Country.ShouldBe("Ethiopia");
        bean.RoastLevel.ShouldBe(RoastLevel.Light);
        bean.PhotoIds.Count.ShouldBe(1);
        (await Services.GetRequiredService<IRepository<Vendor>>().ListAsync(Ct)).ShouldHaveSingleItem().Name.ShouldBe("Friedhats");
        var bag = (await Services.GetRequiredService<IRepository<Bag>>().ListAsync(Ct)).ShouldHaveSingleItem();
        (bag.BeanId, bag.WeightG, bag.RoastDate).ShouldBe((bean.Id, 340m, new DateOnly(2026, 10, 20)));
    }

    [Fact]
    public async Task An_existing_roaster_and_bean_are_offered_and_linking_only_adds_the_bag()
    {
        await SignIn();
        var roaster = await Services.GetRequiredService<IRepository<Vendor>>().SaveAsync(new Vendor { Name = "Friedhats Coffee Roasters" }, Ct);
        var bean = await Services.GetRequiredService<IRepository<Bean>>().SaveAsync(new Bean { Name = "Guji Hambela", RoasterId = roaster.Id, Origin = new BeanOrigin(Country: "Ethiopia") }, Ct);

        var page = Scanned();

        page.Find("#scan-roaster-match").TextContent.ShouldContain("Friedhats Coffee Roasters");
        page.Find("#scan-bean-match").TextContent.ShouldContain("Guji Hambela");
        page.Find("#scan-review").Submit();

        (await Services.GetRequiredService<IRepository<Bean>>().ListAsync(Ct)).ShouldHaveSingleItem().Id.ShouldBe(bean.Id);
        (await Services.GetRequiredService<IRepository<Vendor>>().ListAsync(Ct)).ShouldHaveSingleItem();
        (await Services.GetRequiredService<IRepository<Bag>>().ListAsync(Ct)).ShouldHaveSingleItem().BeanId.ShouldBe(bean.Id);
    }

    [Fact]
    public async Task Choosing_create_new_makes_a_second_bean()
    {
        await SignIn();
        await Services.GetRequiredService<IRepository<Bean>>().SaveAsync(new Bean { Name = "Guji Hambela" }, Ct);

        var page = Scanned();
        page.FindAll("#scan-bean-match input[type=radio]")[1].Change(true);
        page.Find("#scan-review").Submit();

        (await Services.GetRequiredService<IRepository<Bean>>().ListAsync(Ct)).Count.ShouldBe(2);
    }

    [Theory]
    [InlineData(ScanProblem.QuotaUsed, "all label scans for today")]
    [InlineData(ScanProblem.Disabled, "switched off")]
    [InlineData(ScanProblem.Offline, "offline")]
    [InlineData(ScanProblem.Unavailable, "could not be read")]
    public async Task Problems_are_explained_with_manual_entry_as_the_way_out(ScanProblem problem, string text)
    {
        await SignIn();
        Scanner.Problem = problem;
        Camera.Next = new CapturedPhoto([1], "image/jpeg", "label.jpg");
        var page = Render<Scan>();

        page.Find("#scan-camera").Click();

        page.WaitForAssertion(() => page.Find("#scan-problem").TextContent.ShouldContain(text));
        page.Find("a[href='beans/new']").ShouldNotBeNull();
    }

    [Fact]
    public async Task Cancelling_the_camera_does_nothing()
    {
        await SignIn();
        Camera.Next = null;
        var page = Render<Scan>();

        page.Find("#scan-camera").Click();

        Scanner.Scans.ShouldBe(0);
        page.FindAll("#scan-review").ShouldBeEmpty();
    }
}
