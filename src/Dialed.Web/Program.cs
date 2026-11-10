using Microsoft.Extensions.DependencyInjection;
using Bit.Bsqlite.Storage.IndexedDb;
using Bit.Bsqlite.Storage.Opfs;
using Dialed.Client;
using Dialed.Client.Account;
using Dialed.Client.Friends;
using Dialed.Client.Households;
using Dialed.Client.Storage;
using Dialed.Client.Sync;
using Dialed.Client.Preferences;
using Dialed.UI;
using Dialed.Web;
using Dialed.Web.Platform;
using Dialed.Web.Storage;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using Microsoft.FluentUI.AspNetCore.Components;
using Microsoft.JSInterop;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<Routes>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

// API address from wwwroot/appsettings*.json; follows the app through a dev tunnel when testing on an iPhone.
var appBaseAddress = new Uri(builder.HostEnvironment.BaseAddress);
var apiBaseAddress = ApiEndpoint.Resolve(builder.Configuration["Api:BaseUrl"], appBaseAddress);
builder.Services.AddScoped(_ => new HttpClient { BaseAddress = apiBaseAddress });

// UI: Fluent UI Blazor components and IStringLocalizer<Strings> (English resources in Dialed.UI, Dutch in Strings.nl.resx; Blazor picks the language from the browser).
// Local database: SQLite in the browser (OPFS, IndexedDB as fallback). Registered before AddDialedData so it replaces the in-memory store.
builder.Services.AddBitBsqliteServices(sqlite => sqlite
    .AddStorageProvider<BsqliteOpfsStorageProvider>()
    .AddStorageProvider<BsqliteIndexedDbStorageProvider>()
    .UseStorage(storage => storage
        .Prefer(BsqliteOpfsStorageProvider.ProviderName)
        .Fallback(BsqliteIndexedDbStorageProvider.ProviderName))
    .AddDatabase("dialed.db"));
builder.Services.AddBitBsqliteDbContext<LocalDbContext>();
// Bit registers IDbContextFactory<LocalDbContext> as scoped, and our singletons (local store, photo service) use it. In the browser the
// whole app is one scope, so that is safe at runtime, but the Development host validates scopes and refuses to start.
builder.ConfigureContainer(new DefaultServiceProviderFactory(new ServiceProviderOptions { ValidateScopes = false, ValidateOnBuild = false }));
builder.Services.AddSingleton<BsqliteLocalStore>();
// A full or blocked disk must not break saving: failed writes are reported and the data stays in memory (#114).
builder.Services.AddSingleton<ILocalStore>(sp => new ResilientLocalStore(sp.GetRequiredService<BsqliteLocalStore>(), sp.GetRequiredService<StorageStatus>()));
builder.Services.AddDialedData();
builder.Services.AddSingleton<ISyncTransport>(sp => new HttpSyncTransport(new HttpClient { BaseAddress = apiBaseAddress }, sp.GetRequiredService<IAccessTokenProvider>()));
builder.Services.AddSingleton<ISessionStore, BrowserSessionStore>();
builder.Services.AddDialedSync();
builder.Services.AddDialedAccount(
    _ => new AccountApi(new HttpClient { BaseAddress = apiBaseAddress }),
    sp => new HouseholdApi(new HttpClient { BaseAddress = apiBaseAddress }, sp.GetRequiredService<IAccessTokenProvider>()),
    sp => new AccountDataApi(new HttpClient { BaseAddress = apiBaseAddress }, sp.GetRequiredService<IAccessTokenProvider>()),
    sp => new FriendsApi(new HttpClient { BaseAddress = apiBaseAddress }, sp.GetRequiredService<IAccessTokenProvider>()));
builder.Services.AddDialedScan(sp => new Dialed.Client.Scan.LabelScanner(new HttpClient { BaseAddress = apiBaseAddress }, sp.GetRequiredService<IAccessTokenProvider>(), sp.GetRequiredService<Dialed.Client.Platform.IConnectivity>()));
builder.Services.AddDialedRecipeAssistant(sp => new Dialed.Client.Recipes.RecipeAssistant(new HttpClient { BaseAddress = apiBaseAddress }, sp.GetRequiredService<IAccessTokenProvider>(), sp.GetRequiredService<Dialed.Client.Platform.IConnectivity>()));
builder.Services.AddSingleton<IAppEnvironment, WebAppEnvironment>();
builder.Services.AddSingleton<PwaState>();
builder.Services.AddSingleton<IPwaState>(sp => sp.GetRequiredService<PwaState>());
builder.Services.AddSingleton<StoredUserPreferences>();
builder.Services.AddSingleton<IUserPreferences>(sp => sp.GetRequiredService<StoredUserPreferences>());
builder.Services.AddSingleton<UnitFormatter>();
builder.Services.AddSingleton<BrewTimerHandoff>();
builder.Services.AddSingleton<PhotoService>();
builder.Services.AddBrowserPlatform();
builder.Services.AddFluentUIComponents();
builder.Services.AddLocalization();

var host = builder.Build();

// Read the saved data back before the first screen; if the database cannot be opened the app starts empty and keeps working in memory.
var storage = host.Services.GetRequiredService<StorageStatus>();
try
{
    await host.Services.LoadLocalDataAsync();
    storage.Durable = true;
}
catch (Exception exception)
{
    Console.Error.WriteLine($"Local data was not loaded: {exception.Message}");
}

// Preferences and whether setup is done (#226); the store swallows its own errors.
await host.Services.GetRequiredService<StoredUserPreferences>().LoadAsync();

// Offline support and install state (#115); a failure here must never stop the app from starting.
try
{
    await host.Services.GetRequiredService<PwaState>().InitializeAsync();
    await using (var platform = await host.Services.GetRequiredService<IJSRuntime>().InvokeAsync<IJSObjectReference>("import", "./js/platform.js"))
    {
        storage.Persisted = await platform.InvokeAsync<bool>("requestPersistentStorage");
    }

    await host.Services.StartBrowserPlatformAsync();
}
catch (JSException)
{
}

// Sync must never stop the app from starting; problems show up in the sync diagnostics instead.
try
{
    await host.Services.GetRequiredService<AuthSession>().InitializeAsync();
    await host.Services.GetRequiredService<SyncCoordinator>().StartAsync();
}
catch (Exception exception)
{
    Console.Error.WriteLine($"Sync did not start: {exception.Message}");
}

await host.RunAsync();
