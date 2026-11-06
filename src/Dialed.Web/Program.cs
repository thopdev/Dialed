using Dialed.Client;
using Dialed.UI;
using Dialed.Web;
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
builder.Services.AddSingleton<IAppEnvironment, WebAppEnvironment>();
builder.Services.AddSingleton<PwaState>();
builder.Services.AddSingleton<IPwaState>(sp => sp.GetRequiredService<PwaState>());
builder.Services.AddFluentUIComponents();
builder.Services.AddLocalization();

var host = builder.Build();

// Offline support and install state (#115); a failure here must never stop the app from starting.
try
{
    await host.Services.GetRequiredService<PwaState>().InitializeAsync();
}
catch (JSException)
{
}

await host.RunAsync();
