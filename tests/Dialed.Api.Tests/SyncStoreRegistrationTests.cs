using Dialed.Api.Friends;
using Dialed.Api.Households;
using Dialed.Api.PublicProfiles;
using Dialed.Api.Sync;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.Internal;

namespace Dialed.Api.Tests;

/// <summary>Which sync, household, friend and public profile stores the API picks (#38).</summary>
public sealed class SyncStoreRegistrationTests
{
    private const string Emulator = "AccountEndpoint=http://localhost:8081/;AccountKey=C2y6yDjf5/R+ob0N8A7Cgv30VRDJIWEHLM+4QDU5DE2nQ9nDuVTqobD4b8mGGyPMbIZnqyMsEcaGQy67XIw/Jw==;Database=dialed";

    private static ServiceProvider Build(Dictionary<string, string?> settings, string environment = "Production")
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(TimeProvider.System);
        services.AddDialedSync(configuration);
        services.AddDialedHouseholds(new HostingEnvironment { EnvironmentName = environment }, configuration);
        services.AddDialedFriends(configuration);
        services.AddDialedPublicProfiles(configuration);
        return services.BuildServiceProvider();
    }

    [Fact]
    public void Without_a_connection_string_the_stores_are_in_memory()
    {
        using var provider = Build([]);

        provider.GetRequiredService<ISyncStore>().ShouldBeOfType<InMemorySyncStore>();
        provider.GetRequiredService<IHouseholdStore>().ShouldBeOfType<InMemoryHouseholdStore>();
        provider.GetRequiredService<IFriendStore>().ShouldBeOfType<InMemoryFriendStore>();
        provider.GetRequiredService<IPublicProfileStore>().ShouldBeOfType<InMemoryPublicProfileStore>();
    }

    [Fact]
    public void With_cosmos_configured_the_stores_are_cosmos()
    {
        using var provider = Build(new() { ["ConnectionStrings:dialed"] = Emulator });

        provider.GetRequiredService<ISyncStore>().ShouldBeOfType<CosmosSyncStore>();
        provider.GetRequiredService<IHouseholdStore>().ShouldBeOfType<CosmosHouseholdStore>();
        provider.GetRequiredService<IFriendStore>().ShouldBeOfType<CosmosFriendStore>();
        provider.GetRequiredService<IPublicProfileStore>().ShouldBeOfType<CosmosPublicProfileStore>();
    }

    [Fact]
    public async Task Development_starts_with_the_shared_dev_households()
    {
        using var provider = Build([], Environments.Development);

        var store = provider.GetRequiredService<IHouseholdStore>();

        (await store.FindAsync("dev-household", TestContext.Current.CancellationToken))!.OwnerId.ShouldBe("dev-alice");
        (await store.FindAsync("dev-household-carol", TestContext.Current.CancellationToken))!.OwnerId.ShouldBe("dev-carol");
    }
}
