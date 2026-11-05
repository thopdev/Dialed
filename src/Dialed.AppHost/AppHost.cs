using Aspire.Hosting.DevTunnels;
using Microsoft.Extensions.Configuration;

var builder = DistributedApplication.CreateBuilder(args);

// Data: Cosmos DB (Linux emulator) and Blob Storage (Azurite). Containers and data survive restarts,
// because the Cosmos DB emulator takes a while to start.
var cosmos = builder.AddAzureCosmosDB("cosmos")
    .RunAsEmulator(emulator => emulator
#pragma warning disable ASPIRECOSMOSDB001 // Data Explorer for the Linux emulator is marked experimental.
        .WithDataExplorer()
#pragma warning restore ASPIRECOSMOSDB001
        .WithDataVolume()
        .WithLifetime(ContainerLifetime.Persistent));
var database = cosmos.AddCosmosDatabase("dialed");

var storage = builder.AddAzureStorage("storage")
    .RunAsEmulator(azurite => azurite
        .WithDataVolume()
        .WithLifetime(ContainerLifetime.Persistent));
var blobs = storage.AddBlobs("blobs");

// Apps
var web = builder.AddProject<Projects.Dialed_Web>("web");

var api = builder.AddProject<Projects.Dialed_Api>("api")
    .WithReference(database)
    .WithReference(blobs)
    .WithEnvironment("Seed__Enabled", "true");

builder.AddAzureFunctionsProject<Projects.Dialed_Functions>("functions")
    .WithHostStorage(storage)
    .WithReference(database)
    .WithReference(blobs)
    .WaitFor(storage);

// Optional: expose app and API through a Microsoft dev tunnel to test on a real iPhone/iPad
// (launch profile "tunnel", or set DevTunnel__Enabled=true). Needs the devtunnel CLI and `devtunnel user login`.
if (builder.Configuration.GetValue<bool>("DevTunnel:Enabled"))
{
    var tunnel = builder.AddDevTunnel("tunnel", options: new DevTunnelOptions { Region = DevTunnelRegion.WestEurope })
        .WithReference(web)
        .WithReference(api)
        .WithAnonymousAccess();

    // The API learns the app's public tunnel URL (allowed CORS origin, later the passkey origin).
    api.WithReference(web, tunnel);
}
else
{
    api.WithReference(web);
}

builder.Build().Run();
