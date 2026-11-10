using Dialed.Api.Passkeys;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Dialed.Api.Tests;

/// <summary>What every email recovery store does: the send cooldown, the lockout window and burnt codes (#112, #197).</summary>
public abstract class EmailRecoveryStoreContract
{
    protected static CancellationToken Ct => TestContext.Current.CancellationToken;

    protected static readonly DateTimeOffset Start = new(2026, 11, 10, 9, 0, 0, TimeSpan.Zero);

    protected ManualTime Time { get; } = new(Start);

    protected abstract IEmailRecoveryStore NewStore();

    protected static string NewAccount() => $"acc-{Guid.NewGuid():N}";

    [Fact]
    public async Task A_second_email_inside_the_cooldown_is_refused_and_allowed_after_it()
    {
        var store = NewStore();
        var account = NewAccount();
        var cooldown = RecoveryService.EmailCooldown;

        (await store.TryBeginSendAsync(account, Start, cooldown, Ct)).ShouldBeTrue();
        (await store.TryBeginSendAsync(account, Start + cooldown - TimeSpan.FromSeconds(1), cooldown, Ct)).ShouldBeFalse();
        (await store.TryBeginSendAsync(NewAccount(), Start, cooldown, Ct)).ShouldBeTrue(); // per account
        (await store.TryBeginSendAsync(account, Start + cooldown, cooldown, Ct)).ShouldBeTrue();
    }

    [Fact]
    public async Task Too_many_failures_lock_the_account_until_the_window_ends()
    {
        var store = NewStore();
        var account = NewAccount();

        for (var i = 0; i < EmailRecoveryLimits.MaxFailures - 1; i++)
        {
            await store.RecordFailureAsync(account, Start.AddSeconds(i), Ct);
        }

        (await store.IsLockedAsync(account, Start.AddMinutes(1), Ct)).ShouldBeFalse();
        await store.RecordFailureAsync(account, Start.AddMinutes(1), Ct);
        (await store.IsLockedAsync(account, Start.AddMinutes(1), Ct)).ShouldBeTrue();
        (await store.IsLockedAsync(NewAccount(), Start.AddMinutes(1), Ct)).ShouldBeFalse();

        // The window counts from the first failure.
        (await store.IsLockedAsync(account, Start + EmailRecoveryLimits.FailureWindow - TimeSpan.FromSeconds(1), Ct)).ShouldBeTrue();
        (await store.IsLockedAsync(account, Start + EmailRecoveryLimits.FailureWindow, Ct)).ShouldBeFalse();
    }

    [Fact]
    public async Task Failures_after_the_window_start_counting_again()
    {
        var store = NewStore();
        var account = NewAccount();
        for (var i = 0; i < EmailRecoveryLimits.MaxFailures - 1; i++)
        {
            await store.RecordFailureAsync(account, Start, Ct);
        }

        var later = Start + EmailRecoveryLimits.FailureWindow;
        await store.RecordFailureAsync(account, later, Ct);

        (await store.IsLockedAsync(account, later, Ct)).ShouldBeFalse();
    }

    [Fact]
    public async Task An_email_code_works_once_and_not_after_it_expires()
    {
        var store = NewStore();
        var account = NewAccount();
        var expires = Start.AddMinutes(15);

        await store.PutAsync(account, "HASH", expires, Ct);
        (await store.CheckAsync(account, "HASH", Start, consume: false, Ct)).ShouldBeTrue();
        (await store.CheckAsync(account, "HASH", expires, consume: false, Ct)).ShouldBeFalse();
        (await store.CheckAsync(account, "HASH", Start, consume: true, Ct)).ShouldBeTrue();
        (await store.CheckAsync(account, "HASH", Start, consume: true, Ct)).ShouldBeFalse();
    }

    [Fact]
    public async Task Too_many_wrong_guesses_burn_the_email_code()
    {
        var store = NewStore();
        var account = NewAccount();
        await store.PutAsync(account, "HASH", Start.AddMinutes(15), Ct);

        for (var i = 0; i < EmailRecoveryLimits.MaxAttempts; i++)
        {
            (await store.CheckAsync(account, "WRONG", Start, consume: false, Ct)).ShouldBeFalse();
        }

        (await store.CheckAsync(account, "HASH", Start, consume: false, Ct)).ShouldBeFalse();

        // A new code starts over.
        await store.PutAsync(account, "NEW", Start.AddMinutes(15), Ct);
        (await store.CheckAsync(account, "NEW", Start, consume: false, Ct)).ShouldBeTrue();
    }

    protected sealed class ManualTime(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }
}

public sealed class InMemoryEmailRecoveryStoreTests : EmailRecoveryStoreContract
{
    protected override IEmailRecoveryStore NewStore() => new InMemoryEmailRecoveryStore();
}

/// <summary>
/// The Cosmos DB store against a real Cosmos DB (the emulator in CI's <c>cosmos</c> job, or locally). Skips without
/// <c>COSMOS_CONNECTION_STRING</c>. Each test class run uses its own container, removed afterwards.
/// </summary>
public sealed class CosmosEmailRecoveryStoreTests : EmailRecoveryStoreContract, IAsyncLifetime
{
    public static readonly string? ConnectionString = Environment.GetEnvironmentVariable("COSMOS_CONNECTION_STRING");

    private const string Database = "dialed-tests";
    private readonly string containerId = $"recovery-{Guid.NewGuid():N}";
    private readonly List<CosmosClient> clients = [];

    /// <summary>A new store over a new client, as a restarted or second API instance would have.</summary>
    protected override IEmailRecoveryStore NewStore()
    {
        Assert.SkipWhen(string.IsNullOrEmpty(ConnectionString), "COSMOS_CONNECTION_STRING not set (runs in CI's cosmos job).");
        var client = CosmosConnection.CreateClient(ConnectionString!);
        clients.Add(client);
        return new CosmosEmailRecoveryStore(new CosmosRecoveryContainer(client, Database, containerId), Time);
    }

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        if (clients.Count > 0)
        {
            try
            {
                await clients[0].GetContainer(Database, containerId).DeleteContainerAsync();
            }
            catch (CosmosException)
            {
                // never created
            }
        }

        foreach (var client in clients)
        {
            client.Dispose();
        }
    }

    [Fact]
    public async Task The_cooldown_holds_for_a_new_instance()
    {
        var account = NewAccount();
        (await NewStore().TryBeginSendAsync(account, Start, RecoveryService.EmailCooldown, Ct)).ShouldBeTrue();

        (await NewStore().TryBeginSendAsync(account, Start.AddSeconds(10), RecoveryService.EmailCooldown, Ct)).ShouldBeFalse();
    }

    [Fact]
    public async Task The_lockout_holds_for_a_new_instance()
    {
        var account = NewAccount();
        var first = NewStore();
        for (var i = 0; i < EmailRecoveryLimits.MaxFailures - 1; i++)
        {
            await first.RecordFailureAsync(account, Start, Ct);
        }

        var second = NewStore();
        (await second.IsLockedAsync(account, Start, Ct)).ShouldBeFalse();
        await second.RecordFailureAsync(account, Start, Ct); // counts on from the first instance's failures

        (await NewStore().IsLockedAsync(account, Start.AddMinutes(1), Ct)).ShouldBeTrue();
    }

    [Fact]
    public async Task An_email_code_and_its_wrong_guesses_hold_for_a_new_instance()
    {
        var account = NewAccount();
        await NewStore().PutAsync(account, "HASH", Start.AddMinutes(15), Ct);
        for (var i = 0; i < EmailRecoveryLimits.MaxAttempts - 1; i++)
        {
            (await NewStore().CheckAsync(account, "WRONG", Start, consume: false, Ct)).ShouldBeFalse();
        }

        (await NewStore().CheckAsync(account, "HASH", Start, consume: true, Ct)).ShouldBeTrue();
        (await NewStore().CheckAsync(account, "HASH", Start, consume: true, Ct)).ShouldBeFalse(); // used up everywhere
    }

    [Fact]
    public async Task Two_instances_racing_for_one_code_let_only_one_use_it()
    {
        var account = NewAccount();
        await NewStore().PutAsync(account, "HASH", Start.AddMinutes(15), Ct);
        var a = NewStore();
        var b = NewStore();

        var results = await Task.WhenAll(
            a.CheckAsync(account, "HASH", Start, consume: true, Ct),
            b.CheckAsync(account, "HASH", Start, consume: true, Ct));

        results.Count(r => r).ShouldBe(1);
    }
}

/// <summary>Which store the API picks (#197).</summary>
public sealed class EmailRecoveryStoreRegistrationTests
{
    private static IServiceProvider Build(Dictionary<string, string?> settings)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(TimeProvider.System);
        services.AddDialedPasskeys(configuration);
        return services.BuildServiceProvider();
    }

    [Fact]
    public void Without_a_connection_string_the_store_is_in_memory()
    {
        Build([]).GetRequiredService<IEmailRecoveryStore>().ShouldBeOfType<InMemoryEmailRecoveryStore>();
    }

    [Fact]
    public void With_the_aspire_database_reference_the_store_is_cosmos()
    {
        using var provider = (ServiceProvider)Build(new()
        {
            ["ConnectionStrings:dialed"] = "AccountEndpoint=http://localhost:8081/;AccountKey=C2y6yDjf5/R+ob0N8A7Cgv30VRDJIWEHLM+4QDU5DE2nQ9nDuVTqobD4b8mGGyPMbIZnqyMsEcaGQy67XIw/Jw==;Database=dialed",
        });

        provider.GetRequiredService<IEmailRecoveryStore>().ShouldBeOfType<CosmosEmailRecoveryStore>();
    }

    [Theory]
    [InlineData("AccountEndpoint=http://localhost:8081/;AccountKey=a2V5==;Database=dialed-dev", "AccountEndpoint=http://localhost:8081/;AccountKey=a2V5==;", "dialed-dev")]
    [InlineData("AccountEndpoint=http://localhost:8081/;AccountKey=a2V5==", "AccountEndpoint=http://localhost:8081/;AccountKey=a2V5==;", "dialed")]
    public void The_database_name_comes_out_of_the_connection_string(string raw, string connectionString, string database)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:cosmos"] = raw }).Build();

        CosmosConnection.From(configuration).ShouldBe((connectionString, database));
    }
}
