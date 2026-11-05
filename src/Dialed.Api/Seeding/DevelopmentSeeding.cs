using Microsoft.Extensions.Options;

namespace Dialed.Api.Seeding;

/// <summary>
/// Adds demo data for local development. Each feature story registers its own seeder
/// (<c>services.AddScoped&lt;IDevelopmentSeeder, BeanSeeder&gt;()</c>); seeders must be idempotent.
/// </summary>
public interface IDevelopmentSeeder
{
    /// <summary>Lower runs first (e.g. users and households before beans).</summary>
    int Order { get; }

    Task SeedAsync(CancellationToken cancellationToken);
}

public sealed class SeedOptions
{
    public const string SectionName = "Seed";

    /// <summary>Set by the Aspire AppHost (<c>Seed__Enabled=true</c>). Seeding never runs outside Development.</summary>
    public bool Enabled { get; set; }
}

internal sealed class DevelopmentSeedRunner(
    IServiceScopeFactory scopeFactory,
    IHostEnvironment environment,
    IOptions<SeedOptions> options,
    ILogger<DevelopmentSeedRunner> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!environment.IsDevelopment() || !options.Value.Enabled)
        {
            return;
        }

        await using var scope = scopeFactory.CreateAsyncScope();
        var seeders = scope.ServiceProvider.GetServices<IDevelopmentSeeder>().OrderBy(seeder => seeder.Order).ToList();

        foreach (var seeder in seeders)
        {
            logger.LogInformation("Seeding development data with {Seeder}", seeder.GetType().Name);
            await seeder.SeedAsync(cancellationToken);
        }

        logger.LogInformation("Development seeding finished ({Count} seeders)", seeders.Count);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

public static class DevelopmentSeedingServiceCollectionExtensions
{
    public static IServiceCollection AddDevelopmentSeeding(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<SeedOptions>(configuration.GetSection(SeedOptions.SectionName));
        services.AddHostedService<DevelopmentSeedRunner>();
        return services;
    }
}
