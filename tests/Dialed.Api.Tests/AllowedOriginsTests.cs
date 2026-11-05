using Dialed.Api.Configuration;
using Microsoft.Extensions.Configuration;

namespace Dialed.Api.Tests;

public sealed class AllowedOriginsTests
{
    [Fact]
    public void Combines_configured_origins_with_service_discovery_endpoints_of_the_web_app()
    {
        var configuration = Configuration(new()
        {
            ["App:Origins:0"] = "https://localhost:7200",
            ["App:Origins:1"] = "http://localhost:5200",
            ["services:web:https:0"] = "https://dialed-7200.euw.devtunnels.ms/",
        });

        AllowedOrigins.From(configuration).ShouldBe(
        [
            "https://localhost:7200",
            "http://localhost:5200",
            "https://dialed-7200.euw.devtunnels.ms",
        ]);
    }

    [Fact]
    public void Strips_paths_trailing_slashes_and_duplicates()
    {
        var configuration = Configuration(new()
        {
            ["App:Origins:0"] = "https://localhost:7200/",
            ["services:web:https:0"] = "https://localhost:7200/some/path",
        });

        AllowedOrigins.From(configuration).ShouldBe(["https://localhost:7200"]);
    }

    [Fact]
    public void Ignores_values_that_are_not_http_origins()
    {
        var configuration = Configuration(new()
        {
            ["App:Origins:0"] = "not a url",
            ["App:Origins:1"] = "ftp://files.example.com",
        });

        AllowedOrigins.From(configuration).ShouldBeEmpty();
    }

    private static IConfiguration Configuration(Dictionary<string, string?> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build();
}
