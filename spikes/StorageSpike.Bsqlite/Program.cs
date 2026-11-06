using Bit.Bsqlite.Storage.IndexedDb;
using Bit.Bsqlite.Storage.Opfs;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using StorageSpike;
using StorageSpike.Data;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<Routes>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

builder.Services.AddBitBsqliteServices(sqlite => sqlite
    .AddStorageProvider<BsqliteOpfsStorageProvider>()
    .AddStorageProvider<BsqliteIndexedDbStorageProvider>()
    .UseStorage(storage => storage
        .Prefer(BsqliteOpfsStorageProvider.ProviderName)
        .Fallback(BsqliteIndexedDbStorageProvider.ProviderName))
    .AddDatabase("spike.db"));
builder.Services.AddBitBsqliteDbContext<SpikeDbContext>();

builder.Services.AddScoped<BrowserStorage>();
builder.Services.AddSingleton<ISpikeHost, BsqliteHost>();

await builder.Build().RunAsync();

internal sealed class BsqliteHost : ISpikeHost
{
    public string LibraryName => "Bit.Bsqlite 10.7.0";

    // Bsqlite migrates on the first CreateDbContextAsync of a session.
    public Task EnsureReadyAsync() => Task.CompletedTask;
}
