using Azure.Core;
using Dialed.Api.Data;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Dialed.Api.Tests;

/// <summary>How the API picks its Cosmos DB connection: connection string (AppHost, emulator), endpoint with managed identity (Azure), or none (#38, #198).</summary>
public sealed class CosmosConnectionTests
{
    private const string EmulatorConnectionString =
        "AccountEndpoint=http://localhost:8081/;AccountKey=C2y6yDjf5/R+ob0N8A7Cgv30VRDJIWEHLM+4QDU5DE2nQ9nDuVTqobD4b8mGGyPMbIZnqyMsEcaGQy67XIw/Jw==;";

    private const string AzureEndpoint = "https://cosmos-dialed-test.documents.azure.com:443/";

    private static IConfiguration Configuration(Dictionary<string, string?> settings) =>
        new ConfigurationBuilder().AddInMemoryCollection(settings).Build();

    [Fact]
    public void Without_any_setting_there_is_no_connection()
    {
        CosmosConnection.Resolve(Configuration([])).ShouldBeNull();
    }

    [Fact]
    public void A_connection_string_connects_with_its_key_and_creates_containers()
    {
        var connection = CosmosConnection.Resolve(Configuration(new() { ["ConnectionStrings:dialed"] = EmulatorConnectionString + "Database=dialed-local" }));

        connection.ShouldNotBeNull();
        connection.ConnectionString.ShouldBe(EmulatorConnectionString);
        connection.Endpoint.ShouldBeNull();
        connection.Database.ShouldBe("dialed-local");
        connection.CreatesContainers.ShouldBeTrue();
    }

    [Fact]
    public void An_endpoint_connects_with_managed_identity_and_leaves_containers_to_bicep()
    {
        var connection = CosmosConnection.Resolve(Configuration(new()
        {
            ["Cosmos:Endpoint"] = AzureEndpoint,
            ["Cosmos:DatabaseName"] = "dialed-dev",
        }));

        connection.ShouldNotBeNull();
        connection.ConnectionString.ShouldBeNull();
        connection.Endpoint.ShouldBe(new Uri(AzureEndpoint));
        connection.Database.ShouldBe("dialed-dev");
        connection.CreatesContainers.ShouldBeFalse();
    }

    [Fact]
    public void An_endpoint_without_a_database_name_uses_the_default_database()
    {
        CosmosConnection.Resolve(Configuration(new() { ["Cosmos:Endpoint"] = AzureEndpoint }))!.Database.ShouldBe(CosmosConnection.DefaultDatabase);
    }

    [Fact]
    public void A_connection_string_wins_over_an_endpoint()
    {
        var connection = CosmosConnection.Resolve(Configuration(new()
        {
            ["ConnectionStrings:cosmos"] = EmulatorConnectionString,
            ["Cosmos:Endpoint"] = AzureEndpoint,
            ["Cosmos:DatabaseName"] = "dialed-dev",
        }));

        connection.ShouldNotBeNull();
        connection.ConnectionString.ShouldBe(EmulatorConnectionString);
        connection.Database.ShouldBe(CosmosConnection.DefaultDatabase);
    }

    [Fact]
    public void An_endpoint_that_is_not_a_uri_fails_at_startup()
    {
        Should.Throw<InvalidOperationException>(() => CosmosConnection.Resolve(Configuration(new() { ["Cosmos:Endpoint"] = "not a uri" })));
    }

    [Fact]
    public void With_an_endpoint_the_registered_cosmos_does_not_create_containers()
    {
        var services = new ServiceCollection();
        services.TryAddDialedCosmos(Configuration(new()
        {
            ["Cosmos:Endpoint"] = AzureEndpoint,
            ["Cosmos:DatabaseName"] = "dialed-dev",
        })).ShouldBeTrue();
        using var provider = services.BuildServiceProvider();

        var cosmos = provider.GetRequiredService<DialedCosmos>();

        cosmos.DatabaseId.ShouldBe("dialed-dev");
        cosmos.CreatesContainers.ShouldBeFalse();
        cosmos.Client.Endpoint.ShouldBe(new Uri(AzureEndpoint));
    }

    [Fact]
    public void Without_a_setting_nothing_is_registered()
    {
        var services = new ServiceCollection();

        services.TryAddDialedCosmos(Configuration([])).ShouldBeFalse();
        services.ShouldBeEmpty();
    }

    [Fact]
    public async Task Without_creating_containers_a_container_is_handed_out_without_calling_cosmos()
    {
        // The credential would throw on any request: the container must come back without one.
        using var client = CosmosConnection.CreateClient(new Uri(AzureEndpoint), new ThrowingCredential());
        var cosmos = new DialedCosmos(client, "dialed-dev", createContainers: false);

        var container = await cosmos.ContainerAsync(new CosmosContainerSpec("recovery", "/accountId"), TestContext.Current.CancellationToken);

        container.Id.ShouldBe("recovery");
        container.Database.Id.ShouldBe("dialed-dev");
    }

    private sealed class ThrowingCredential : TokenCredential
    {
        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("No token in tests.");

        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("No token in tests.");
    }
}
