namespace Dialed.AppHost.Tests;

/// <summary>
/// Checks the local stack's resource model without starting containers (no Docker needed).
/// </summary>
public sealed class AppHostModelTests
{
    [Fact]
    public async Task Defines_the_local_stack()
    {
        await using var appHost = await DistributedApplicationTestingBuilder
            .CreateAsync<Projects.Dialed_AppHost>(TestContext.Current.CancellationToken);

        var names = appHost.Resources.Select(resource => resource.Name).ToList();

        names.ShouldContain("cosmos");
        names.ShouldContain("dialed");
        names.ShouldContain("storage");
        names.ShouldContain("blobs");
        names.ShouldContain("web");
        names.ShouldContain("api");
        names.ShouldContain("functions");
    }

    [Fact]
    public async Task Adds_the_dev_tunnel_only_when_enabled()
    {
        await using var withoutTunnel = await DistributedApplicationTestingBuilder
            .CreateAsync<Projects.Dialed_AppHost>(TestContext.Current.CancellationToken);
        await using var withTunnel = await DistributedApplicationTestingBuilder
            .CreateAsync<Projects.Dialed_AppHost>(["DevTunnel:Enabled=true"], TestContext.Current.CancellationToken);

        withoutTunnel.Resources.ShouldNotContain(resource => resource.Name == "tunnel");
        withTunnel.Resources.ShouldContain(resource => resource.Name == "tunnel");
    }
}
