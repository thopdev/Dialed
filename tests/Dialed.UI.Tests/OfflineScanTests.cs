using Bunit;
using Dialed.Client;
using Dialed.Client.Account;
using Dialed.Client.Data;
using Dialed.Client.Platform;
using Dialed.Client.Scan;
using Dialed.Domain.Model;
using Dialed.UI.Components;
using Dialed.UI.Pages;
using Microsoft.Extensions.DependencyInjection;

namespace Dialed.UI.Tests;

/// <summary>A label scanned without a connection is kept, read later and reviewed from a list (#83).</summary>
public sealed class OfflineScanTests : UiTestContext
{
    private static CancellationToken Ct => Xunit.TestContext.Current.CancellationToken;

    private static ScannedField F(string? value, double confidence = 0.9) => new(value, confidence);

    private static ScannedLabel Label() => new(
        F("Guji Hambela"), F("Friedhats"), F("Ethiopia"), F(null, 0), F(null, 0), F("Natural", 0.4), F(null, 0), F("light"), F(null, 0),
        F("2026-10-20"), F("250"), "label-v1", "fake");

    private async Task SignIn() =>
        await Services.GetRequiredService<AuthSession>().SetAsync(new SessionTokens("a", DateTimeOffset.MaxValue, "r", "u1", "Thomas", "h-u1"), Ct);

    private IRepository<Photo> Photos => Services.GetRequiredService<IRepository<Photo>>();

    private async Task<Photo> SavedPhoto() =>
        await Photos.SaveAsync(new Photo { ContentType = "image/jpeg", SizeBytes = 3, UploadState = UploadState.Uploaded }, Ct);

    private async Task<IRenderedComponent<Scan>> OfflinePage()
    {
        await SignIn();
        Scanner.Problem = ScanProblem.Offline;
        Camera.Next = new CapturedPhoto([1, 2, 3], "image/jpeg", "label.jpg");
        var page = Render<Scan>();
        page.Find("#scan-camera").Click();
        page.WaitForAssertion(() => page.Find("#scan-problem"));
        return page;
    }

    // ---- Saving a scan for later --------------------------------------------------------------

    [Fact]
    public async Task Offline_the_photo_can_be_kept_and_read_later()
    {
        var page = await OfflinePage();

        page.Find("#scan-problem").TextContent.ShouldContain("offline");
        page.Find("#scan-later").TextContent.ShouldBe("Save the photo and read it when I am online");
        page.Find("#scan-later").Click();

        var photo = (await Photos.ListAsync(Ct)).ShouldHaveSingleItem();
        ScanQueue.Items.ShouldHaveSingleItem().PhotoId.ShouldBe(photo.Id);
        page.FindAll("#scan-problem, #scan-later").ShouldBeEmpty();
        page.Find("#scan-queued").TextContent.ShouldContain("as soon as you are online");
        page.Find(".scan-pending-row .scan-pending-status").TextContent.ShouldBe("Waiting for a connection");
    }

    [Theory]
    [InlineData(ScanProblem.QuotaUsed)]
    [InlineData(ScanProblem.Disabled)]
    [InlineData(ScanProblem.Unavailable)]
    public async Task Other_problems_do_not_offer_to_wait_because_waiting_would_not_help(ScanProblem problem)
    {
        await SignIn();
        Scanner.Problem = problem;
        Camera.Next = new CapturedPhoto([1, 2, 3], "image/jpeg", "label.jpg");
        var page = Render<Scan>();

        page.Find("#scan-camera").Click();

        page.WaitForAssertion(() => page.Find("#scan-problem"));
        page.FindAll("#scan-later").ShouldBeEmpty();
    }

    // ---- The list -----------------------------------------------------------------------------

    [Fact]
    public async Task The_list_shows_every_state_with_the_right_actions()
    {
        await SignIn();
        var ready = await SavedPhoto();
        var waiting = await SavedPhoto();
        var failed = await SavedPhoto();
        ScanQueue.Set(ready.Id, PendingScanStatus.Ready, Label());
        ScanQueue.Set(waiting.Id, PendingScanStatus.Waiting);
        ScanQueue.Set(failed.Id, PendingScanStatus.Failed, problem: ScanProblem.QuotaUsed);

        var page = Render<Scan>();

        string Row(string id, string css) => page.Find($"[data-photo='{id}'] {css}").TextContent;
        Row(ready.Id, ".dialed-row-title").ShouldBe("Guji Hambela");
        Row(ready.Id, ".scan-pending-status").ShouldBe("Ready to review");
        page.Find($"[data-photo='{ready.Id}'] .scan-pending-review").GetAttribute("aria-label").ShouldBe("Review Guji Hambela");
        page.FindAll($"[data-photo='{waiting.Id}'] .scan-pending-review").ShouldBeEmpty();
        Row(waiting.Id, ".scan-pending-status").ShouldBe("Waiting for a connection");
        Row(failed.Id, ".scan-pending-status").ShouldContain("used all label scans");
        page.Find($"[data-photo='{failed.Id}'] .scan-pending-retry").ShouldNotBeNull();
        page.FindAll(".scan-pending-discard").Count.ShouldBe(3);
        page.Find("#scan-pending").GetAttribute("aria-labelledby").ShouldBe(page.Find("#scan-pending-title").Id);
    }

    [Fact]
    public async Task Try_again_restarts_a_failed_scan_and_discard_removes_the_scan_and_its_photo()
    {
        await SignIn();
        var failed = await SavedPhoto();
        ScanQueue.Set(failed.Id, PendingScanStatus.Failed, problem: ScanProblem.Unavailable);
        var page = Render<Scan>();

        page.Find(".scan-pending-retry").Click();

        ScanQueue.Retried.ShouldBe([failed.Id]);
        page.Find(".scan-pending-status").TextContent.ShouldBe("Waiting for a connection");

        page.Find(".scan-pending-discard").Click();

        page.FindAll("#scan-pending").ShouldBeEmpty();
        (await Photos.ListAsync(Ct)).ShouldAllBe(p => p.Deleted);
    }

    // ---- Reviewing a kept draft ---------------------------------------------------------------

    [Fact]
    public async Task Reviewing_a_kept_scan_fills_the_form_and_saving_uses_the_photo_it_already_has()
    {
        await SignIn();
        var photo = await SavedPhoto();
        ScanQueue.Set(photo.Id, PendingScanStatus.Ready, Label());
        var page = Render<Scan>();

        page.Find(".scan-pending-review").Click();

        page.Find("#scan-name").GetAttribute("value").ShouldBe("Guji Hambela");
        page.Find("#scan-weight").GetAttribute("value").ShouldBe("250");
        page.FindAll(".dialed-uncertain").Count.ShouldBe(1);

        page.Find("#scan-review").Submit();

        var bean = (await Services.GetRequiredService<IRepository<Bean>>().ListAsync(Ct)).ShouldHaveSingleItem();
        bean.PhotoIds.ShouldBe([photo.Id]);
        (await Photos.ListAsync(Ct)).ShouldHaveSingleItem().Deleted.ShouldBeFalse();
        ScanQueue.Items.ShouldBeEmpty();
        (await Services.GetRequiredService<IRepository<Bag>>().ListAsync(Ct)).ShouldHaveSingleItem().WeightG.ShouldBe(250m);
    }

    [Fact]
    public async Task Scan_again_from_a_kept_scan_goes_back_to_the_list_and_keeps_the_scan()
    {
        await SignIn();
        var photo = await SavedPhoto();
        ScanQueue.Set(photo.Id, PendingScanStatus.Ready, Label());
        var page = Render<Scan>();
        page.Find(".scan-pending-review").Click();

        page.Find("#scan-again").Click();

        page.Find("#scan-camera").ShouldNotBeNull();
        ScanQueue.Items.ShouldHaveSingleItem();
    }

    [Fact]
    public async Task Linking_a_kept_scan_to_an_existing_bean_does_not_keep_an_unused_photo()
    {
        await SignIn();
        var existing = await Services.GetRequiredService<IRepository<Bean>>().SaveAsync(new Bean { Name = "Guji Hambela", Origin = new BeanOrigin("Ethiopia") }, Ct);
        var photo = await SavedPhoto();
        ScanQueue.Set(photo.Id, PendingScanStatus.Ready, Label());
        var page = Render<Scan>();
        page.Find(".scan-pending-review").Click();

        page.Find("#scan-review").Submit();

        (await Services.GetRequiredService<IRepository<Bean>>().ListAsync(Ct)).ShouldHaveSingleItem().Id.ShouldBe(existing.Id);
        (await Photos.ListAsync(Ct)).ShouldAllBe(p => p.Deleted);
        ScanQueue.Items.ShouldBeEmpty();
    }

    // ---- The banner ---------------------------------------------------------------------------

    [Fact]
    public async Task The_banner_links_to_scans_to_review_and_otherwise_says_scans_are_waiting()
    {
        var banner = Render<PendingScansNote>();
        banner.FindAll(".dialed-scans-note").ShouldBeEmpty();

        var waiting = await SavedPhoto();
        ScanQueue.Set(waiting.Id, PendingScanStatus.Waiting);
        banner.WaitForAssertion(() => banner.Find("#scans-waiting").TextContent.ShouldBe("Label scans waiting for a connection: 1"));
        banner.Find("#scans-waiting a").GetAttribute("href").ShouldBe("scan");

        var ready = await SavedPhoto();
        ScanQueue.Set(ready.Id, PendingScanStatus.Ready, Label());
        banner.WaitForAssertion(() => banner.Find("#scans-ready").TextContent.ShouldBe("Scans to review: 1"));
        banner.FindAll("#scans-waiting").ShouldBeEmpty();

        await ScanQueue.RemoveAsync(ready.Id, Ct);
        await ScanQueue.RemoveAsync(waiting.Id, Ct);
        banner.WaitForAssertion(() => banner.FindAll(".dialed-scans-note").ShouldBeEmpty());
    }
}
