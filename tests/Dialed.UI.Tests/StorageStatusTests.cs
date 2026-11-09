using Bunit;
using Dialed.Client.Storage;
using Dialed.UI.Pages;
using Microsoft.Extensions.DependencyInjection;

namespace Dialed.UI.Tests;

public sealed class StorageStatusTests : UiTestContext
{
    private string Status()
    {
        var page = Render<Settings>();
        return page.Find("#storage-status").TextContent;
    }

    [Fact]
    public void Memory_only_data_says_so()
    {
        Status().ShouldContain("only kept while the app is open");
    }

    [Fact]
    public void Saved_data_the_browser_may_clear_is_flagged()
    {
        Services.GetRequiredService<StorageStatus>().Durable = true;

        Status().ShouldContain("may clear it");
    }

    [Fact]
    public void Persistent_storage_is_reported_as_safe()
    {
        var storage = Services.GetRequiredService<StorageStatus>();
        storage.Durable = true;
        storage.Persisted = true;

        Status().ShouldContain("will not clear it");
    }
}
