using Bunit;
using Dialed.Client.Data;
using Dialed.Client.Platform;
using Dialed.Domain.Model;
using Dialed.UI.Pages;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace Dialed.UI.Tests;

/// <summary>Scanning the barcode on a bag (#104).</summary>
public sealed class BarcodeScanTests : UiTestContext
{
    private static CancellationToken Ct => Xunit.TestContext.Current.CancellationToken;

    private const string Ean = "4006381333931";

    private IRepository<Bean> Beans => Services.GetRequiredService<IRepository<Bean>>();

    private string Uri => Services.GetRequiredService<NavigationManager>().Uri;

    private async Task<Bean> BeanWith(string name, params string[] codes) => await Beans.SaveAsync(new Bean { Name = name, Barcodes = codes }, Ct);

    private IRenderedComponent<BarcodeScan> Page(BarcodeResult? scanned = null)
    {
        if (scanned is not null)
        {
            Barcodes.Results.Enqueue(scanned);
        }

        return Render<BarcodeScan>();
    }

    // ---- Finding a bean -----------------------------------------------------------------------

    [Fact]
    public async Task A_scanned_code_opens_the_bean_that_has_it()
    {
        var bean = await BeanWith("Guji", Ean);

        Page(BarcodeResult.Read(Ean)).WaitForAssertion(() => Uri.ShouldEndWith($"beans/{bean.Id}"));

        Barcodes.PreviewIds.ShouldBe(["barcode-video"]);
    }

    [Fact]
    public async Task A_typed_UPC_A_with_spaces_finds_a_bean_stored_with_the_EAN_13()
    {
        var bean = await BeanWith("Kiambu", "0036000291452");
        var page = Page();

        page.Find("#barcode-manual").Change("0 36000 29145 2");
        page.Find("#barcode-manual-form").Submit();

        Uri.ShouldEndWith($"beans/{bean.Id}");
    }

    [Fact]
    public async Task A_code_on_several_beans_lists_them_to_choose_from()
    {
        var first = await BeanWith("Zeta", Ean);
        var second = await BeanWith("Alpha", Ean);

        var page = Page(BarcodeResult.Read(Ean));

        page.WaitForAssertion(() => page.FindAll("#barcode-matches .barcode-match").Count.ShouldBe(2));
        page.FindAll(".barcode-match").Select(a => a.GetAttribute("href")).ShouldBe([$"beans/{second.Id}", $"beans/{first.Id}"]);
    }

    [Fact]
    public async Task A_number_with_a_wrong_check_digit_is_refused_and_nothing_else_happens()
    {
        await BeanWith("Guji", Ean);
        var page = Page();

        page.Find("#barcode-manual").Change("4006381333932");
        page.Find("#barcode-manual-form").Submit();

        page.Find("#barcode-invalid").TextContent.ShouldContain("not a valid barcode");
        page.FindAll("#barcode-unknown").ShouldBeEmpty();
    }

    // ---- An unknown code ----------------------------------------------------------------------

    private IRenderedComponent<BarcodeScan> Unknown()
    {
        var page = Page(BarcodeResult.Read(Ean));
        page.WaitForAssertion(() => page.Find("#barcode-unknown"));
        return page;
    }

    [Fact]
    public void An_unknown_code_offers_three_ways_forward()
    {
        var page = Unknown();

        page.Find("#barcode-unknown-title").TextContent.ShouldBe("New barcode");
        page.Find("#barcode-unknown p").TextContent.ShouldContain(Ean);
        page.FindAll("#barcode-new, #barcode-attach, #barcode-label").Count.ShouldBe(3);
    }

    [Fact]
    public void Adding_it_as_a_new_bean_opens_the_new_bean_form_with_the_code()
    {
        var page = Unknown();

        page.Find("#barcode-new").Click();

        Uri.ShouldEndWith($"beans/new?barcode={Ean}");
    }

    [Fact]
    public void Scanning_the_label_instead_keeps_the_code_for_the_scan()
    {
        var page = Unknown();

        page.Find("#barcode-label").Click();

        Uri.ShouldEndWith($"scan?barcode={Ean}");
    }

    [Fact]
    public async Task Adding_the_code_to_a_bean_I_have_saves_it_there_and_opens_the_bean()
    {
        var bean = await BeanWith("Guji Hambela", "96385074");
        await BeanWith("Kiambu AA");
        var page = Unknown();

        page.Find("#barcode-attach").Click();
        page.FindAll(".barcode-attach-bean").Select(b => b.TextContent.Trim()).ShouldBe(["Guji Hambela", "Kiambu AA"]);
        page.Find("#barcode-search").Input("guji");
        page.FindAll(".barcode-attach-bean").Select(b => b.TextContent.Trim()).ShouldBe(["Guji Hambela"]);
        page.Find(".barcode-attach-bean").Click();

        var saved = (await Beans.GetAsync(bean.Id, Ct))!;
        saved.Barcodes.ShouldBe(["96385074", Ean]);
        Uri.ShouldEndWith($"beans/{bean.Id}");
    }

    [Fact]
    public void With_no_beans_the_attach_list_says_so_and_goes_back()
    {
        var page = Unknown();

        page.Find("#barcode-attach").Click();

        page.Find("#barcode-attach-empty").TextContent.ShouldBe("No beans found.");
        page.Find("#barcode-attach-back").Click();
        page.Find("#barcode-unknown").ShouldNotBeNull();
    }

    [Fact]
    public void Scan_another_code_goes_back_to_scanning()
    {
        var page = Unknown();
        Barcodes.Results.Enqueue(BarcodeResult.Read("96385074"));

        page.Find("#barcode-retry").Click();

        page.WaitForAssertion(() => Barcodes.PreviewIds.Count.ShouldBe(2));
        page.FindAll("#barcode-unknown").Count.ShouldBe(1); // the second code is unknown too
        page.Find("#barcode-unknown p").TextContent.ShouldContain("96385074");
    }

    // ---- The camera ---------------------------------------------------------------------------

    [Theory]
    [InlineData(BarcodeProblem.PermissionDenied, "camera is not allowed")]
    [InlineData(BarcodeProblem.NoCamera, "No camera could be started")]
    [InlineData(BarcodeProblem.Unsupported, "not available here")]
    public void A_camera_problem_is_explained_and_typing_still_works(BarcodeProblem problem, string text)
    {
        var page = Page(BarcodeResult.Failed(problem));

        page.WaitForAssertion(() => page.Find("#barcode-problem").TextContent.ShouldContain(text));
        page.Find("#barcode-problem").GetAttribute("role").ShouldBe("alert");
        page.Find("#barcode-manual").ShouldNotBeNull();
    }

    [Fact]
    public void While_the_camera_runs_the_hint_and_preview_show_and_leaving_the_screen_stops_it()
    {
        Barcodes.Hold = true;
        var page = Page();

        page.WaitForAssertion(() => page.Find("#barcode-hint").TextContent.ShouldContain("nothing is sent anywhere"));
        page.Find("#barcode-video").HasAttribute("hidden").ShouldBeFalse();
        page.Find("#barcode-video").GetAttribute("aria-label").ShouldBe("Camera view");

        page.Instance.Dispose();

        Barcodes.CancelledByPage.ShouldBeTrue();
    }

    [Fact]
    public void A_cancelled_scan_leaves_the_manual_field_and_a_scan_again_button()
    {
        var page = Page(BarcodeResult.Failed(BarcodeProblem.Cancelled));

        page.WaitForAssertion(() => page.Find("#barcode-again"));
        page.FindAll("#barcode-problem").ShouldBeEmpty();
        page.Find("#barcode-video").HasAttribute("hidden").ShouldBeTrue();
    }

    // ---- Entry points -------------------------------------------------------------------------

    [Fact]
    public void Beans_and_Home_link_to_the_scanner()
    {
        Render<Beans>().Find("#beans-scan-code").GetAttribute("href").ShouldBe("scan-code");
    }
}
