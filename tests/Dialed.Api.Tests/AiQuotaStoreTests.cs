using Dialed.Api.Ai;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Dialed.Api.Tests;

/// <summary>What every AI quota store does: count per user, operation and day, refuse at the limit, refunds (#88).</summary>
public abstract class AiQuotaStoreContract
{
    protected static CancellationToken Ct => TestContext.Current.CancellationToken;

    protected static readonly DateOnly Today = new(2026, 11, 18);

    protected abstract IAiQuotaStore NewStore();

    protected static string NewUser() => $"user-{Guid.NewGuid():N}";

    [Fact]
    public async Task Calls_are_counted_up_to_the_limit_and_a_refused_call_is_not_counted()
    {
        var store = NewStore();
        var user = NewUser();

        (await store.TryTakeAsync(user, AiOperations.Label, Today, 2, Ct)).ShouldBe(1);
        (await store.TryTakeAsync(user, AiOperations.Label, Today, 2, Ct)).ShouldBe(2);
        (await store.TryTakeAsync(user, AiOperations.Label, Today, 2, Ct)).ShouldBeNull();

        (await store.UsedAsync(user, AiOperations.Label, Today, Ct)).ShouldBe(2);
    }

    [Fact]
    public async Task The_count_is_per_user_operation_and_day()
    {
        var store = NewStore();
        var user = NewUser();
        await store.TryTakeAsync(user, AiOperations.Label, Today, 1, Ct);

        (await store.TryTakeAsync(NewUser(), AiOperations.Label, Today, 1, Ct)).ShouldBe(1);
        (await store.TryTakeAsync(user, AiOperations.Suggestion, Today, 1, Ct)).ShouldBe(1);
        (await store.TryTakeAsync(user, AiOperations.Label, Today.AddDays(1), 1, Ct)).ShouldBe(1);
        (await store.UsedAsync(user, AiOperations.Label, Today.AddDays(2), Ct)).ShouldBe(0);
    }

    [Fact]
    public async Task A_refund_gives_one_call_back_and_never_goes_below_zero()
    {
        var store = NewStore();
        var user = NewUser();
        await store.TryTakeAsync(user, AiOperations.Label, Today, 1, Ct);

        await store.RefundAsync(user, AiOperations.Label, Today, Ct);
        await store.RefundAsync(user, AiOperations.Label, Today, Ct);

        (await store.UsedAsync(user, AiOperations.Label, Today, Ct)).ShouldBe(0);
        (await store.TryTakeAsync(user, AiOperations.Label, Today, 1, Ct)).ShouldBe(1);
    }

    [Fact]
    public async Task A_limit_of_zero_refuses_everything() =>
        (await NewStore().TryTakeAsync(NewUser(), "nonsense", Today, 0, Ct)).ShouldBeNull();
}

public sealed class InMemoryAiQuotaStoreTests : AiQuotaStoreContract
{
    protected override IAiQuotaStore NewStore() => new InMemoryAiQuotaStore();
}

/// <summary>The Cosmos DB quota against a real Cosmos DB; skips without <c>COSMOS_CONNECTION_STRING</c>.</summary>
public sealed class CosmosAiQuotaStoreTests : AiQuotaStoreContract, IAsyncDisposable
{
    private readonly CosmosTestDatabase database = new();

    protected override IAiQuotaStore NewStore() => new CosmosAiQuotaStore(database.New());

    public ValueTask DisposeAsync() => database.DisposeAsync();

    [Fact]
    public async Task The_quota_holds_for_a_new_instance()
    {
        var user = NewUser();
        await NewStore().TryTakeAsync(user, AiOperations.Label, Today, 2, Ct);
        await NewStore().TryTakeAsync(user, AiOperations.Label, Today, 2, Ct);

        (await NewStore().TryTakeAsync(user, AiOperations.Label, Today, 2, Ct)).ShouldBeNull();
    }

    [Fact]
    public async Task Instances_racing_never_hand_out_more_than_the_limit()
    {
        var user = NewUser();
        var stores = Enumerable.Range(0, 4).Select(_ => NewStore()).ToList();

        var results = await Task.WhenAll(stores.SelectMany(s => new[] { s.TryTakeAsync(user, AiOperations.Label, Today, 3, Ct), s.TryTakeAsync(user, AiOperations.Label, Today, 3, Ct) }));

        results.Count(r => r is not null).ShouldBe(3);
        (await NewStore().UsedAsync(user, AiOperations.Label, Today, Ct)).ShouldBe(3);
    }
}

/// <summary>Which quota store the API picks.</summary>
public sealed class AiQuotaStoreRegistrationTests
{
    private static ServiceProvider Build(Dictionary<string, string?> settings)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDialedAi(configuration);
        return services.BuildServiceProvider();
    }

    [Fact]
    public void Without_a_connection_string_the_quota_is_in_memory()
    {
        using var provider = Build([]);
        provider.GetRequiredService<IAiQuotaStore>().ShouldBeOfType<InMemoryAiQuotaStore>();
    }

    [Fact]
    public void With_cosmos_configured_the_quota_is_in_cosmos()
    {
        using var provider = Build(new() { ["ConnectionStrings:cosmos"] = CosmosTestConnection.Emulator });
        provider.GetRequiredService<IAiQuotaStore>().ShouldBeOfType<CosmosAiQuotaStore>();
    }
}

/// <summary>A connection string that selects the Cosmos DB stores without connecting (the client connects on first use).</summary>
public static class CosmosTestConnection
{
    public const string Emulator = "AccountEndpoint=http://localhost:8081/;AccountKey=C2y6yDjf5/R+ob0N8A7Cgv30VRDJIWEHLM+4QDU5DE2nQ9nDuVTqobD4b8mGGyPMbIZnqyMsEcaGQy67XIw/Jw==;";
}
