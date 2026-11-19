using Dialed.Api.Catalog;
using Dialed.Domain.Catalog;
using Dialed.Domain.Model;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Dialed.Api.Tests;

/// <summary>What every catalog store does: keep the latest build, with its time and hidden entries (#105, #268).</summary>
public abstract class CatalogStoreContract
{
    protected static CancellationToken Ct => TestContext.Current.CancellationToken;

    protected static readonly DateTimeOffset Built = new(2026, 11, 18, 6, 0, 0, TimeSpan.Zero);

    protected abstract ICatalogStore NewStore();

    protected static CatalogBuild Build(string name, int beans = 1) => new(
        new BeanCatalog(
            [new CatalogRoaster("r1", "Roaster " + name, "NL", "Utrecht", "https://example.com", 2, ["roaster"])],
            [.. Enumerable.Range(0, beans).Select(i => new CatalogBean($"b{i}", $"{name} {i}", "r1", "Roaster " + name, "Ethiopia", "Guji", "Washed", ["Heirloom"], RoastLevel.Light, ["jasmine", "peach"], 2, [$"key-{i}"], ["8712345678906"]))]),
        Built,
        [new CatalogEntryAdminDto("bean", "hidden", "Hidden bean", 3, ["hidden-key"])]);

    /// <summary>Same content, whatever list types the store gives back.</summary>
    protected static void ShouldMatch(object actual, object expected) =>
        System.Text.Json.JsonSerializer.Serialize(actual).ShouldBe(System.Text.Json.JsonSerializer.Serialize(expected));

    [Fact]
    public async Task Before_the_first_build_the_catalog_is_empty()
    {
        var build = await NewStore().GetAsync(Ct);

        build.Catalog.Beans.ShouldBeEmpty();
        build.Catalog.Roasters.ShouldBeEmpty();
        build.BuiltAt.ShouldBeNull();
        build.Hidden.ShouldBeEmpty();
    }

    [Fact]
    public async Task The_latest_build_replaces_the_previous_one()
    {
        var store = NewStore();
        await store.SaveAsync(Build("first"), Ct);
        var second = Build("second") with { BuiltAt = Built.AddHours(6) };

        await store.SaveAsync(second, Ct);

        ShouldMatch(await store.GetAsync(Ct), second);
    }
}

public sealed class InMemoryCatalogStoreTests : CatalogStoreContract
{
    protected override ICatalogStore NewStore() => new InMemoryCatalogStore();
}

/// <summary>The Cosmos DB catalog against a real Cosmos DB; skips without <c>COSMOS_CONNECTION_STRING</c>.</summary>
public sealed class CosmosCatalogStoreTests : CatalogStoreContract, IAsyncDisposable
{
    private readonly CosmosTestDatabase database = new();

    private ManualTime Time { get; } = new(Built);

    protected override ICatalogStore NewStore() => new CosmosCatalogStore(database.New(), Time);

    public ValueTask DisposeAsync() => database.DisposeAsync();

    [Fact]
    public async Task A_new_instance_reads_the_build()
    {
        var build = Build("kept");
        await NewStore().SaveAsync(build, Ct);

        ShouldMatch(await NewStore().GetAsync(Ct), build);
    }

    [Fact]
    public async Task A_build_larger_than_one_document_is_kept_in_parts()
    {
        // Random names do not compress, so this build is several megabytes: more than one 2 MB document.
        var build = Build("big", 1) with
        {
            Catalog = new BeanCatalog([], [.. Enumerable.Range(0, 6_000).Select(i => Build("x").Catalog.Beans[0] with { Id = $"b{i}", Name = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(200)) })]),
        };
        await NewStore().SaveAsync(build, Ct);

        var read = await NewStore().GetAsync(Ct);

        read.Catalog.Beans.Count.ShouldBe(6_000);
        ShouldMatch(read.Catalog.Beans[^1], build.Catalog.Beans[^1]);
    }

    [Fact]
    public async Task Another_instance_sees_a_new_build_once_its_copy_is_a_minute_old()
    {
        var reader = NewStore();
        await NewStore().SaveAsync(Build("first"), Ct);
        (await reader.GetAsync(Ct)).Catalog.Beans[0].Name.ShouldBe("first 0");

        await NewStore().SaveAsync(Build("second"), Ct);
        (await reader.GetAsync(Ct)).Catalog.Beans[0].Name.ShouldBe("first 0"); // its copy is still fresh

        Time.Now += CosmosCatalogStore.RefreshAfter;
        (await reader.GetAsync(Ct)).Catalog.Beans[0].Name.ShouldBe("second 0");
    }

    private sealed class ManualTime(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }
}

/// <summary>What every catalog rules store does: hide and merge by key, and take either back (#105).</summary>
public abstract class CatalogRulesStoreContract
{
    protected static CancellationToken Ct => TestContext.Current.CancellationToken;

    protected abstract ICatalogRulesStore NewStore();

    [Fact]
    public async Task Without_rules_nothing_is_hidden_or_merged()
    {
        var rules = await NewStore().GetAsync(Ct);

        rules.HiddenKeys.ShouldBeEmpty();
        rules.Merges.ShouldBeEmpty();
    }

    [Fact]
    public async Task Hidden_keys_can_be_brought_back()
    {
        var store = NewStore();
        await store.HideAsync(["a", "b"], Ct);
        await store.HideAsync(["b", "c"], Ct);

        (await store.GetAsync(Ct)).HiddenKeys.Order(StringComparer.Ordinal).ShouldBe(["a", "b", "c"]);
        (await store.UnhideAsync(["b", "z"], Ct)).ShouldBeTrue();
        (await store.UnhideAsync(["z"], Ct)).ShouldBeFalse();
        (await store.GetAsync(Ct)).HiddenKeys.Order(StringComparer.Ordinal).ShouldBe(["a", "c"]);
    }

    [Fact]
    public async Task A_merge_is_kept_once_and_can_be_taken_back()
    {
        var store = NewStore();
        await store.MergeAsync("a", "b", Ct);
        await store.MergeAsync("a", "b", Ct);
        await store.MergeAsync("c", "d", Ct);

        (await store.GetAsync(Ct)).Merges.ShouldBe([("a", "b"), ("c", "d")]);
        (await store.UnmergeAsync("a", "b", Ct)).ShouldBeTrue();
        (await store.UnmergeAsync("a", "b", Ct)).ShouldBeFalse();
        (await store.GetAsync(Ct)).Merges.ShouldBe([("c", "d")]);
    }
}

public sealed class InMemoryCatalogRulesStoreTests : CatalogRulesStoreContract
{
    protected override ICatalogRulesStore NewStore() => new InMemoryCatalogRulesStore();
}

/// <summary>The Cosmos DB rules against a real Cosmos DB. Every store here shares one database, as instances of one API would.</summary>
public sealed class CosmosCatalogRulesStoreTests : CatalogRulesStoreContract, IAsyncDisposable
{
    private readonly CosmosTestDatabase database = new();

    protected override ICatalogRulesStore NewStore() => new CosmosCatalogRulesStore(database.New());

    public ValueTask DisposeAsync() => database.DisposeAsync();

    [Fact]
    public async Task Rules_hold_for_a_new_instance()
    {
        await NewStore().HideAsync(["a"], Ct);
        await NewStore().MergeAsync("x", "y", Ct);

        var rules = await NewStore().GetAsync(Ct);
        rules.HiddenKeys.ShouldBe(["a"]);
        rules.Merges.ShouldBe([("x", "y")]);
    }

    [Fact]
    public async Task Instances_changing_the_rules_at_once_lose_nothing()
    {
        var stores = Enumerable.Range(0, 5).Select(_ => NewStore()).ToList();

        await Task.WhenAll(stores.Select((s, i) => s.HideAsync([$"key-{i}"], Ct)));

        (await NewStore().GetAsync(Ct)).HiddenKeys.Count.ShouldBe(5);
    }
}

/// <summary>Which catalog stores the API picks.</summary>
public sealed class CatalogStoreRegistrationTests
{
    private static ServiceProvider Build(Dictionary<string, string?> settings)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(TimeProvider.System);
        services.AddDialedCatalog(configuration);
        return services.BuildServiceProvider();
    }

    [Fact]
    public void Without_a_connection_string_the_stores_are_in_memory()
    {
        using var provider = Build([]);
        provider.GetRequiredService<ICatalogStore>().ShouldBeOfType<InMemoryCatalogStore>();
        provider.GetRequiredService<ICatalogRulesStore>().ShouldBeOfType<InMemoryCatalogRulesStore>();
    }

    [Fact]
    public void With_cosmos_configured_the_stores_are_in_cosmos()
    {
        using var provider = Build(new() { ["ConnectionStrings:cosmos"] = CosmosTestConnection.Emulator });
        provider.GetRequiredService<ICatalogStore>().ShouldBeOfType<CosmosCatalogStore>();
        provider.GetRequiredService<ICatalogRulesStore>().ShouldBeOfType<CosmosCatalogRulesStore>();
    }
}
