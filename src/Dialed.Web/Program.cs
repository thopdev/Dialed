using Dialed.Client;
using Dialed.Client.Account;
using Dialed.Client.Households;
using Dialed.Client.Sync;
using Dialed.Client.Preferences;
using Dialed.UI;
using Dialed.Web;
using Dialed.Web.Platform;
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

// UI: Fluent UI Blazor components and IStringLocalizer<Strings> (English resources in Dialed.UI).
builder.Services.AddDialedData();
builder.Services.AddSingleton<ISyncTransport>(sp => new HttpSyncTransport(new HttpClient { BaseAddress = apiBaseAddress }, sp.GetRequiredService<IAccessTokenProvider>()));
builder.Services.AddSingleton<ISessionStore, BrowserSessionStore>();
builder.Services.AddDialedSync();
builder.Services.AddDialedAccount(
    _ => new AccountApi(new HttpClient { BaseAddress = apiBaseAddress }),
    sp => new HouseholdApi(new HttpClient { BaseAddress = apiBaseAddress }, sp.GetRequiredService<IAccessTokenProvider>()));
builder.Services.AddSingleton<IAppEnvironment, WebAppEnvironment>();
builder.Services.AddSingleton<PwaState>();
builder.Services.AddSingleton<IPwaState>(sp => sp.GetRequiredService<PwaState>());
builder.Services.AddSingleton<IUserPreferences, InMemoryUserPreferences>();
builder.Services.AddSingleton<UnitFormatter>();
builder.Services.AddSingleton<BrewTimerHandoff>();
builder.Services.AddSingleton<PhotoService>();
builder.Services.AddBrowserPlatform();
builder.Services.AddFluentUIComponents();
builder.Services.AddLocalization();

var host = builder.Build();

// Offline support and install state (#115); a failure here must never stop the app from starting.
try
{
    await host.Services.GetRequiredService<PwaState>().InitializeAsync();
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
