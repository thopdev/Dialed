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

    [Fact]
    public void Wildcard_patterns_allow_preview_environments_only()
    {
        var configuration = Configuration(new()
        {
            ["App:Origins:0"] = "https://gentle-sea-0123.5.azurestaticapps.net",
            ["App:Origins:1"] = "https://gentle-sea-0123-*.*.5.azurestaticapps.net",
        });
        var exact = AllowedOrigins.From(configuration);
        var patterns = AllowedOrigins.Patterns(configuration);

        AllowedOrigins.IsAllowed("https://gentle-sea-0123.5.azurestaticapps.net", exact, patterns).ShouldBeTrue();
        AllowedOrigins.IsAllowed("https://gentle-sea-0123-42.westeurope.5.azurestaticapps.net", exact, patterns).ShouldBeTrue();
        AllowedOrigins.IsAllowed("https://other-sea-0123-42.westeurope.5.azurestaticapps.net", exact, patterns).ShouldBeFalse();
        AllowedOrigins.IsAllowed("https://gentle-sea-0123-42.westeurope.5.azurestaticapps.net.evil.com", exact, patterns).ShouldBeFalse();
        AllowedOrigins.IsAllowed("http://gentle-sea-0123-42.westeurope.5.azurestaticapps.net", exact, patterns).ShouldBeFalse();
    }

    [Theory]
    [InlineData("https://*")]
    [InlineData("https://*.com")]
    [InlineData("https://*.example.com")]
    [InlineData("https://*.5.azurestaticapps.net")]
    [InlineData("https://a-*.5.azurestaticapps.net")]
    [InlineData("http://app-*.example.com")]
    public void Patterns_that_could_allow_arbitrary_sites_are_ignored(string pattern)
    {
        var configuration = Configuration(new() { ["App:Origins:0"] = pattern });

        AllowedOrigins.Patterns(configuration).ShouldBeEmpty();
    }

    private static IConfiguration Configuration(Dictionary<string, string?> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build();
}
