using System.Net;
using Dialed.Client.Account;
using Dialed.Client.Data;
using Dialed.Client.Platform;
using Dialed.Client.Sync;
using Dialed.Domain.Model;

namespace Dialed.Client.Tests;

public sealed class SyncRobustnessTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(1, 5)]
    [InlineData(2, 10)]
    [InlineData(3, 20)]
    [InlineData(7, 300)]
    [InlineData(40, 300)]
    public void Backoff_doubles_up_to_five_minutes(int failures, int seconds) =>
        SyncCoordinator.BackoffDelay(failures).ShouldBe(TimeSpan.FromSeconds(seconds));

    // ---- token expiry during a sync -----------------------------------------------------------

    private sealed class Tokens : IAccessTokenProvider
    {
        public string Current { get; set; } = "old";

        public int Renewals { get; private set; }

        public Task<string?> GetAccessTokenAsync(CancellationToken cancellationToken = default) => Task.FromResult<string?>(Current);

        public Task<string?> RenewAccessTokenAsync(string? refusedToken, CancellationToken cancellationToken = default)
        {
            Renewals++;
            Current = "new";
            return Task.FromResult<string?>(Current);
        }
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<string?> Bearers { get; } = [];

        public List<string> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Bearers.Add(request.Headers.Authorization?.Parameter);
            Bodies.Add(request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken));
            return respond(request);
        }
    }

    private static HttpResponseMessage Page() =>
        new(HttpStatusCode.OK) { Content = new StringContent("{\"records\":[],\"hasMore\":false}", System.Text.Encoding.UTF8, "application/json") };

    [Fact]
    public async Task An_expired_token_is_renewed_once_and_the_request_sent_again()
    {
        var handler = new Handler(request => request.Headers.Authorization?.Parameter == "old" ? new HttpResponseMessage(HttpStatusCode.Unauthorized) : Page());
        var tokens = new Tokens();
        var transport = new HttpSyncTransport(new HttpClient(handler) { BaseAddress = new Uri("https://api.test/") }, tokens);

        var page = await transport.PullAsync<Vendor>("vendors", null, 100, Ct);

        page.Records.ShouldBeEmpty();
        handler.Bearers.ShouldBe(["old", "new"]);
        tokens.Renewals.ShouldBe(1);
    }

    [Fact]
    public async Task A_token_that_is_still_refused_after_renewing_stops_the_sync()
    {
        var handler = new Handler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized));
        var transport = new HttpSyncTransport(new HttpClient(handler) { BaseAddress = new Uri("https://api.test/") }, new Tokens());

        var failure = await Should.ThrowAsync<SyncTransportException>(() => transport.PullAsync<Vendor>("vendors", null, 100, Ct));

        failure.Message.ShouldContain("expired");
        handler.Bearers.Count.ShouldBe(2); // one retry, not a loop
    }

    [Fact]
    public async Task A_push_is_sent_again_with_the_same_content_after_renewing()
    {
        var handler = new Handler(request => request.Headers.Authorization?.Parameter == "old"
            ? new HttpResponseMessage(HttpStatusCode.Unauthorized)
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"entity\":{\"id\":\"v1\",\"name\":\"Roaster\"},\"version\":\"2\"}", System.Text.Encoding.UTF8, "application/json") });
        var transport = new HttpSyncTransport(new HttpClient(handler) { BaseAddress = new Uri("https://api.test/") }, new Tokens());

        var result = await transport.PushAsync("vendors", new Vendor { Id = "v1", Name = "Roaster" }, "1", Ct);

        result.Outcome.ShouldBe(PushOutcome.Accepted);
        handler.Bodies.Count.ShouldBe(2);
        handler.Bodies[1].ShouldBe(handler.Bodies[0]);
    }

    // ---- refused changes ----------------------------------------------------------------------

    private sealed class Context : ICurrentContext
    {
        public string UserId => "u1";

        public string HouseholdId => "h1";
    }

    [Fact]
    public async Task Refused_changes_are_listed_and_can_be_retried_or_discarded()
    {
        var server = new FakeSyncServer(TimeProvider.System);
        var vendors = new InMemoryRepository<Vendor>(new Context(), TimeProvider.System);
        var state = new InMemorySyncState();
        var engine = new SyncEngine([new SyncTable<Vendor>("vendors", vendors, state, server, TimeProvider.System).Attach()], state);
        var kept = await vendors.SaveAsync(new Vendor { Name = "Kept" }, Ct);
        var dropped = await vendors.SaveAsync(new Vendor { Name = "Dropped" }, Ct);
        server.RejectIds.Add(kept.Id);
        server.RejectIds.Add(dropped.Id);

        var result = await engine.SyncAsync(Ct);

        result.Failed.ShouldBe(2);
        var failed = await engine.FailedAsync(Ct);
        failed.Select(f => f.EntityId).ShouldBe([kept.Id, dropped.Id], ignoreOrder: true);
        failed.ShouldAllBe(f => f.LastError == "no access" && f.Attempts == 1);

        server.RejectIds.Remove(kept.Id);
        await engine.RetryAsync(failed.Single(f => f.EntityId == kept.Id).Sequence, Ct);
        await engine.DiscardAsync(failed.Single(f => f.EntityId == dropped.Id).Sequence, Ct);
        (await engine.FailedAsync(Ct)).ShouldBeEmpty();

        var retried = await engine.SyncAsync(Ct);

        retried.Pushed.ShouldBe(1);
        (await engine.PendingCountAsync(Ct)).ShouldBe(0);
        server.All("vendors").Cast<Vendor>().Select(v => v.Name).ShouldBe(["Kept"]);
        (await vendors.GetAsync(dropped.Id, Ct))!.Name.ShouldBe("Dropped"); // discarded from the queue, still on this device
    }
}
