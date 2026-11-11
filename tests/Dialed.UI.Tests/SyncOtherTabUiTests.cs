using Bunit;
using Dialed.Client.Platform;
using Dialed.Client.Sync;
using Dialed.UI.Components;
using Microsoft.Extensions.DependencyInjection;

namespace Dialed.UI.Tests;

/// <summary>Only one tab syncs at a time (#39); the other tabs say a sync is running elsewhere.</summary>
public sealed class SyncOtherTabUiTests : UiTestContext
{
    private sealed class HeldByOtherTab : ICrossTabLock
    {
        public Task<IAsyncDisposable?> TryAcquireAsync(string name, CancellationToken cancellationToken = default) =>
            Task.FromResult<IAsyncDisposable?>(null);

        public async Task<IAsyncDisposable> AcquireAsync(string name, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            throw new InvalidOperationException();
        }
    }

    public SyncOtherTabUiTests() => Services.AddSingleton<ICrossTabLock>(new HeldByOtherTab());

    [Fact]
    public async Task Indicator_and_diagnostics_say_another_tab_is_syncing()
    {
        Tokens.Value = "tkn";
        var sync = Services.GetRequiredService<SyncCoordinator>();
        await sync.SyncNowAsync(Xunit.TestContext.Current.CancellationToken);

        Render<SyncIndicator>().Find("#sync-other-tab").TextContent.ShouldContain("Syncing in another tab");
        var diagnostics = Render<SyncDiagnostics>();
        diagnostics.Find("#sync-status").TextContent.ShouldContain("syncing in another tab");
        diagnostics.Find("#sync-now").HasAttribute("disabled").ShouldBeTrue();
    }
}
