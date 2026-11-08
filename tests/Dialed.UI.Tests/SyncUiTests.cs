using Bunit;
using Dialed.Client.Data;
using Dialed.Client.Sync;
using Dialed.Domain.Model;
using Dialed.UI.Components;
using Microsoft.Extensions.DependencyInjection;

namespace Dialed.UI.Tests;

public sealed class SyncUiTests : UiTestContext
{
    // Resolving the coordinator builds the engine, which starts watching the repositories for local changes.
    public SyncUiTests() => _ = Sync;

    private SyncCoordinator Sync => Services.GetRequiredService<SyncCoordinator>();

    private IRepository<Vendor> Vendors => Services.GetRequiredService<IRepository<Vendor>>();

    private async Task AddVendor() => await Vendors.SaveAsync(new Vendor { Name = "Roaster" }, Xunit.TestContext.Current.CancellationToken);

    [Fact]
    public async Task Diagnostics_say_when_nobody_is_signed_in()
    {
        await Sync.SyncNowAsync(Xunit.TestContext.Current.CancellationToken);

        var diagnostics = Render<SyncDiagnostics>();

        diagnostics.Find("#sync-status").TextContent.ShouldContain("not signed in");
    }

    [Fact]
    public async Task Diagnostics_show_waiting_changes_and_clear_them_after_Sync_now()
    {
        Tokens.Value = "tkn";
        Transport.Offline = true;
        await AddVendor();
        await Sync.SyncNowAsync(Xunit.TestContext.Current.CancellationToken);
        var diagnostics = Render<SyncDiagnostics>();
        diagnostics.Find("#sync-pending").TextContent.ShouldContain("1");
        diagnostics.Find("#sync-status").TextContent.ShouldContain("offline");

        Transport.Offline = false;
        diagnostics.Find("#sync-now").Click();

        diagnostics.WaitForAssertion(() =>
        {
            diagnostics.Find("#sync-pending").TextContent.ShouldContain("0");
            diagnostics.Find("#sync-status").TextContent.ShouldContain("up to date");
            diagnostics.Find("#sync-last-result").TextContent.ShouldContain("1 sent");
        });
        Transport.Pushes.ShouldBe(1);
    }

    [Fact]
    public async Task The_indicator_stays_quiet_when_not_signed_in()
    {
        await AddVendor();
        await Sync.SyncNowAsync(Xunit.TestContext.Current.CancellationToken);

        Render<SyncIndicator>().FindAll("#sync-indicator").ShouldBeEmpty();
    }

    [Fact]
    public async Task The_indicator_shows_changes_that_are_not_yet_synced()
    {
        Tokens.Value = "tkn";
        Transport.Offline = true;
        await AddVendor();
        await Sync.SyncNowAsync(Xunit.TestContext.Current.CancellationToken);

        Render<SyncIndicator>().Find("#sync-indicator").TextContent.ShouldContain("1 not yet synced");
    }
}
