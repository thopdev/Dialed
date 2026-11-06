using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using CommunityToolkit.Datasync.Client;
using DatasyncSpike.Models;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.DependencyInjection;

namespace DatasyncSpike.Tests;

public sealed class SpikeApp : WebApplicationFactory<Program>
{
    public static readonly string? ConnectionString = Environment.GetEnvironmentVariable("COSMOS_CONNECTION_STRING");

    protected override void ConfigureWebHost(IWebHostBuilder builder) =>
        builder.UseSetting("ConnectionStrings:cosmos", ConnectionString ?? "AccountEndpoint=http://localhost:8081/;AccountKey=none");
}

/// <summary>#37 part 2: Datasync table controllers on Cosmos DB, one container partitioned by /householdId.</summary>
public sealed class DatasyncCosmosTests(SpikeApp app) : IClassFixture<SpikeApp>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly string home = "home-" + Guid.NewGuid().ToString("N")[..8];
    private readonly string office = "office-" + Guid.NewGuid().ToString("N")[..8];

    [Fact]
    public async Task Creates_with_a_packed_id_and_reads_it_back_with_a_point_read()
    {
        SkipWithoutCosmos();
        using var alice = Client("alice", home);

        var created = await Create(alice, new { householdId = home, doseG = 18, yieldG = 36, timeS = 28, rating = 4 });

        created["id"]!.GetValue<string>().ShouldStartWith(home + ":");
        created["createdBy"]!.GetValue<string>().ShouldBe("alice");

        using var read = await alice.GetAsync($"tables/brews/{created["id"]}", Ct);
        read.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Members_of_another_household_cannot_read_or_query_it()
    {
        SkipWithoutCosmos();
        using var alice = Client("alice", home);
        using var bob = Client("bob", office);
        var created = await Create(alice, new { householdId = home, rating = 3 });

        using var read = await bob.GetAsync($"tables/brews/{created["id"]}", Ct);
        read.StatusCode.ShouldBe(HttpStatusCode.NotFound);

        var bobsItems = await Items(bob, "tables/brews");
        bobsItems.ShouldNotContain(item => item!["householdId"]!.GetValue<string>() == home);
    }

    [Fact]
    public async Task Cannot_create_in_a_household_you_are_not_in()
    {
        SkipWithoutCosmos();
        using var bob = Client("bob", office);

        using var response = await bob.PostAsJsonAsync("tables/brews", new { householdId = home, rating = 1 }, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Id_must_carry_the_entity_household()
    {
        SkipWithoutCosmos();
        using var carol = Client("carol", home, office);

        using var response = await carol.PostAsJsonAsync("tables/brews",
            new { id = HouseholdIds.New(office), householdId = home, rating = 2 }, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Cannot_move_an_item_to_another_household_on_update()
    {
        SkipWithoutCosmos();
        using var carol = Client("carol", home, office);
        var created = await Create(carol, new { householdId = home, rating = 2 });

        created["householdId"] = office;
        using var response = await Put(carol, created);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Update_with_a_stale_version_is_a_conflict()
    {
        SkipWithoutCosmos();
        using var alice = Client("alice", home);
        var created = await Create(alice, new { householdId = home, rating = 2 });
        var staleVersion = created["version"]!.GetValue<string>();

        created["rating"] = 5;
        using (var first = await Put(alice, created, staleVersion))
        {
            first.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        created["rating"] = 1;
        using var second = await Put(alice, created, staleVersion);
        second.StatusCode.ShouldBe(HttpStatusCode.PreconditionFailed);
    }

    [Fact]
    public async Task Delta_pull_returns_changes_and_soft_deletes_since_the_watermark()
    {
        SkipWithoutCosmos();
        using var alice = Client("alice", home);
        var before = await Create(alice, new { householdId = home, rating = 1 });
        var watermark = before["updatedAt"]!.GetValue<DateTimeOffset>();

        var changed = await Create(alice, new { householdId = home, rating = 4 });
        var removed = await Create(alice, new { householdId = home, rating = 5 });
        using (var delete = await alice.DeleteAsync($"tables/brews/{removed["id"]}", Ct))
        {
            delete.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        }

        var filter = Uri.EscapeDataString($"updatedAt gt {watermark.UtcDateTime:yyyy-MM-ddTHH:mm:ss.fffZ}");
        var delta = await Items(alice, $"tables/brews?$filter={filter}&$orderby=updatedAt&__includedeleted=true");

        var ids = delta.Select(item => item!["id"]!.GetValue<string>()).ToList();
        ids.ShouldContain(changed["id"]!.GetValue<string>());
        ids.ShouldContain(removed["id"]!.GetValue<string>());
        ids.ShouldNotContain(before["id"]!.GetValue<string>());
        delta.Single(item => item!["id"]!.GetValue<string>() == removed["id"]!.GetValue<string>())!["deleted"]!
            .GetValue<bool>().ShouldBeTrue();
    }

    [Fact]
    public async Task Datasync_client_can_add_get_and_query()
    {
        SkipWithoutCosmos();
        using var http = Client("alice", home);
        var client = new DatasyncServiceClient<ClientBrew>(new Uri(http.BaseAddress!, "tables/brews/"), http);

        var added = await client.AddAsync(new ClientBrew { Id = HouseholdIds.New(home), HouseholdId = home, Rating = 4 }, new DatasyncServiceOptions(), Ct);
        added.IsSuccessful.ShouldBeTrue();

        var fetched = await client.GetAsync(added.Value!.Id!, new DatasyncServiceOptions(), Ct);
        fetched.Value!.Rating.ShouldBe(4);

        var found = new List<ClientBrew>();
        await foreach (var brew in client.Query(client.AsQueryable().Where(b => b.HouseholdId == home)))
        {
            found.Add(brew);
        }

        found.ShouldContain(brew => brew.Id == added.Value.Id);
    }

    [Fact]
    public async Task Measures_request_units_of_a_1000_item_delta_pull()
    {
        SkipWithoutCosmos();
        var cosmos = app.Services.GetRequiredService<CosmosClient>();
        var container = cosmos.GetContainer(SpikeCosmos.DatabaseId, SpikeCosmos.ContainerId);
        var household = "ru-" + Guid.NewGuid().ToString("N")[..8];
        var start = DateTimeOffset.UtcNow;

        var seed = Stopwatch.StartNew();
        double writeCharge = 0;
        foreach (var chunk in Enumerable.Range(0, 1000).Chunk(50))
        {
            var responses = await Task.WhenAll(chunk.Select(i => container.CreateItemAsync(
                new Brew
                {
                    Id = HouseholdIds.New(household),
                    HouseholdId = household,
                    DoseG = 18,
                    YieldG = 36,
                    TimeS = 28,
                    Rating = 1 + i % 5,
                    UpdatedAt = start.AddMilliseconds(i),
                },
                new PartitionKey(household),
                cancellationToken: Ct)));
            writeCharge += responses.Sum(response => response.RequestCharge);
        }

        seed.Stop();

        // The query the Datasync repository runs for a delta pull of one household (paged by 100 like the server).
        var query = new QueryDefinition(
                "SELECT * FROM c WHERE c.entity = 'Brew' AND ARRAY_CONTAINS(@households, c.householdId) AND c.updatedAt > @since ORDER BY c.updatedAt, c.id")
            .WithParameter("@households", new[] { household })
            .WithParameter("@since", start.AddSeconds(-1));

        double pullCharge = 0;
        var items = 0;
        var pages = 0;
        var pull = Stopwatch.StartNew();
        using (var iterator = container.GetItemQueryIterator<JsonElement>(query, requestOptions: new QueryRequestOptions { MaxItemCount = 100 }))
        {
            while (iterator.HasMoreResults)
            {
                var page = await iterator.ReadNextAsync(Ct);
                pullCharge += page.RequestCharge;
                items += page.Count;
                pages++;
            }
        }

        pull.Stop();

        var report = $"""
            ### Cosmos DB request units (emulator)
            - Seed 1,000 brews: {writeCharge:0} RU total ({writeCharge / 1000:0.0} RU per write), {seed.ElapsedMilliseconds} ms
            - Delta pull of 1,000 brews (one household, {pages} pages of 100): {pullCharge:0.0} RU, {pull.ElapsedMilliseconds} ms
            """;
        TestContext.Current.TestOutputHelper?.WriteLine(report);
        var summary = Environment.GetEnvironmentVariable("GITHUB_STEP_SUMMARY");
        if (!string.IsNullOrEmpty(summary))
        {
            await File.AppendAllTextAsync(summary, report + Environment.NewLine, Ct);
        }

        items.ShouldBe(1000);
    }

    private static void SkipWithoutCosmos() =>
        Assert.SkipWhen(string.IsNullOrEmpty(SpikeApp.ConnectionString), "COSMOS_CONNECTION_STRING not set (runs in the Spikes workflow).");

    private HttpClient Client(string user, params string[] households)
    {
        var client = app.CreateClient();
        client.DefaultRequestHeaders.Add("X-Spike-User", user);
        client.DefaultRequestHeaders.Add("X-Spike-Households", string.Join(',', households));
        return client;
    }

    private static async Task<JsonObject> Create(HttpClient client, object body)
    {
        using var response = await client.PostAsJsonAsync("tables/brews", body, Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<JsonObject>(Ct))!;
    }

    private static Task<HttpResponseMessage> Put(HttpClient client, JsonObject entity, string? ifMatch = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Put, $"tables/brews/{entity["id"]}") { Content = JsonContent.Create(entity) };
        if (ifMatch is not null)
        {
            request.Headers.TryAddWithoutValidation("If-Match", $"\"{ifMatch}\"");
        }

        return client.SendAsync(request, Ct);
    }

    private static async Task<List<JsonNode?>> Items(HttpClient client, string url)
    {
        using var response = await client.GetAsync(url, Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        var page = await response.Content.ReadFromJsonAsync<JsonObject>(Ct);
        return [.. page!["items"]!.AsArray()];
    }
}

/// <summary>Client-side shape of a brew, as Dialed.Client would use it.</summary>
public sealed class ClientBrew
{
    public string? Id { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }
    public string? Version { get; set; }
    public bool Deleted { get; set; }
    public string HouseholdId { get; set; } = string.Empty;
    public int Rating { get; set; }
}
