using Dialed.Client;
using Dialed.UI;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<Routes>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

// API address from wwwroot/appsettings*.json; follows the app through a dev tunnel when testing on an iPhone.
var appBaseAddress = new Uri(builder.HostEnvironment.BaseAddress);
var apiBaseAddress = ApiEndpoint.Resolve(builder.Configuration["Api:BaseUrl"], appBaseAddress);
builder.Services.AddScoped(_ => new HttpClient { BaseAddress = apiBaseAddress });

await builder.Build().RunAsync();
