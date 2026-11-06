using Dialed.Api.Seeding;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Dialed.Api.Tests;

public sealed class DevelopmentSeedRunnerTests
{
    [Fact]
    public async Task Runs_all_seeders_in_order_when_enabled_in_Development()
    {
        var log = new List<string>();

        await RunSeeding(Environments.Development, enabled: true, log);

        log.ShouldBe(["households", "beans"]);
    }

    [Fact]
    public async Task Does_nothing_when_disabled()
    {
        var log = new List<string>();

        await RunSeeding(Environments.Development, enabled: false, log);

        log.ShouldBeEmpty();
    }

    [Fact]
    public async Task Never_runs_outside_Development()
    {
        var log = new List<string>();

        await RunSeeding(Environments.Production, enabled: true, log);

        log.ShouldBeEmpty();
    }

    private static async Task RunSeeding(string environmentName, bool enabled, List<string> log)
    {
        var services = new ServiceCollection();
        services.AddScoped<IDevelopmentSeeder>(_ => new RecordingSeeder("beans", order: 20, log));
        services.AddScoped<IDevelopmentSeeder>(_ => new RecordingSeeder("households", order: 10, log));
        await using var provider = services.BuildServiceProvider();

        var runner = new DevelopmentSeedRunner(
            provider.GetRequiredService<IServiceScopeFactory>(),
            new FakeHostEnvironment(environmentName),
            Options.Create(new SeedOptions { Enabled = enabled }),
            NullLogger<DevelopmentSeedRunner>.Instance);

        await runner.StartAsync(TestContext.Current.CancellationToken);
    }

    private sealed class RecordingSeeder(string name, int order, List<string> log) : IDevelopmentSeeder
    {
        public int Order => order;

        public Task SeedAsync(CancellationToken cancellationToken)
        {
            log.Add(name);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeHostEnvironment(string environmentName) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = environmentName;
        public string ApplicationName { get; set; } = "Dialed.Api.Tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
