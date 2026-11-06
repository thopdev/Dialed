using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using SqliteWasmBlazor;
using StorageSpike;
using StorageSpike.Data;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<Routes>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

builder.Services.AddDbContextFactory<SpikeDbContext>(options =>
    options.UseSqliteWasm(new SqliteWasmConnection("Data Source=spike.db")));
builder.Services.AddSqliteWasm(options => options.BaseHref = new Uri(builder.HostEnvironment.BaseAddress).AbsolutePath);
builder.Services.AddSqliteWasmDbContext<SpikeDbContext>();

builder.Services.AddScoped<BrowserStorage>();
builder.Services.AddScoped<ISpikeHost, SqliteWasmHost>();

await builder.Build().RunAsync();

internal sealed class SqliteWasmHost(ISqliteWasmInitializer initializer) : ISpikeHost
{
    public string LibraryName => "SqliteWasmBlazor 0.9.4-pre";

    // Starts the worker and applies pending migrations; idempotent.
    public Task EnsureReadyAsync() => initializer.InitializeAsync().AsTask();
}
