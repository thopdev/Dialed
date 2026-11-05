namespace Dialed.Client.Tests;

public sealed class ApiEndpointTests
{
    private static readonly Uri LocalApp = new("https://localhost:7200/");

    [Fact]
    public void Uses_the_configured_address_when_the_app_runs_on_localhost()
    {
        ApiEndpoint.Resolve("https://localhost:7100/", LocalApp)
            .ShouldBe(new Uri("https://localhost:7100/"));
    }

    [Fact]
    public void Follows_the_app_through_a_dev_tunnel_using_the_API_port()
    {
        ApiEndpoint.Resolve("https://localhost:7100/", new Uri("https://x7k2p9q-7200.euw.devtunnels.ms/"))
            .ShouldBe(new Uri("https://x7k2p9q-7100.euw.devtunnels.ms/"));
    }

    [Fact]
    public void Keeps_hyphens_in_tunnel_ids()
    {
        ApiEndpoint.Resolve("https://localhost:7100/", new Uri("https://dialed-thomas-7200.eun1.devtunnels.ms/"))
            .ShouldBe(new Uri("https://dialed-thomas-7100.eun1.devtunnels.ms/"));
    }

    [Fact]
    public void Keeps_a_non_local_API_address_even_inside_a_tunnel()
    {
        ApiEndpoint.Resolve("https://api.example.com/", new Uri("https://x7k2p9q-7200.euw.devtunnels.ms/"))
            .ShouldBe(new Uri("https://api.example.com/"));
    }

    [Fact]
    public void Falls_back_to_the_app_address_without_configuration()
    {
        ApiEndpoint.Resolve(null, LocalApp).ShouldBe(LocalApp);
        ApiEndpoint.Resolve("  ", LocalApp).ShouldBe(LocalApp);
    }
}
