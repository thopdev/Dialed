using System.Net;
using System.Text;
using Dialed.Client.Sync;
using Dialed.Domain.Model;

namespace Dialed.Client.Tests;

public sealed class HttpSyncTransportTests
{
    private sealed class Token(string? value) : IAccessTokenProvider
    {
        public Task<string?> GetAccessTokenAsync(CancellationToken cancellationToken = default) => Task.FromResult(value);
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        public string? LastBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add(request);
            return respond(request);
        }
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string json) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static (HttpSyncTransport Transport, StubHandler Handler) Create(Func<HttpRequestMessage, HttpResponseMessage> respond, string? token = "tkn")
    {
        var handler = new StubHandler(respond);
        return (new HttpSyncTransport(new HttpClient(handler) { BaseAddress = new Uri("https://api.test/") }, new Token(token)), handler);
    }

    private const string VendorJson = """{"id":"v1","householdId":"h","name":"Roaster","updatedAt":"2026-01-01T10:00:00+00:00","version":2}""";

    [Fact]
    public async Task Push_sends_if_match_and_token_and_reads_the_stored_record()
    {
        var (transport, handler) = Create(_ => Json(HttpStatusCode.OK, $$"""{"entity":{{VendorJson}},"version":"2"}"""));

        var result = await transport.PushAsync("vendors", new Vendor { Id = "v1", Name = "Roaster" }, "1", TestContext.Current.CancellationToken);

        result.Outcome.ShouldBe(PushOutcome.Accepted);
        result.Record!.Version.ShouldBe("2");
        result.Record.Entity.Name.ShouldBe("Roaster");
        var request = handler.Requests.Single();
        request.Method.ShouldBe(HttpMethod.Put);
        request.RequestUri!.AbsolutePath.ShouldBe("/sync/vendors/v1");
        request.Headers.GetValues("If-Match").Single().ShouldBe("\"1\"");
        request.Headers.Authorization!.Parameter.ShouldBe("tkn");
        handler.LastBody.ShouldContain("\"name\":\"Roaster\"");
    }

    [Fact]
    public async Task Push_of_a_new_record_sends_no_if_match()
    {
        var (transport, handler) = Create(_ => Json(HttpStatusCode.OK, $$"""{"entity":{{VendorJson}},"version":"1"}"""));

        await transport.PushAsync("vendors", new Vendor { Id = "v1", Name = "Roaster" }, null, TestContext.Current.CancellationToken);

        handler.Requests.Single().Headers.Contains("If-Match").ShouldBeFalse();
    }

    [Fact]
    public async Task Push_conflict_returns_the_server_copy()
    {
        var (transport, _) = Create(_ => Json(HttpStatusCode.Conflict, $$"""{"entity":{{VendorJson}},"version":"2"}"""));

        var result = await transport.PushAsync("vendors", new Vendor { Id = "v1", Name = "Mine" }, "1", TestContext.Current.CancellationToken);

        result.Outcome.ShouldBe(PushOutcome.Conflict);
        result.Record!.Entity.Name.ShouldBe("Roaster");
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.NotFound)]
    public async Task Push_refusals_are_rejections(HttpStatusCode status)
    {
        var (transport, _) = Create(_ => Json(status, """{"error":"nope"}"""));

        var result = await transport.PushAsync("vendors", new Vendor { Id = "v1" }, null, TestContext.Current.CancellationToken);

        result.Outcome.ShouldBe(PushOutcome.Rejected);
        result.Message.ShouldContain("nope");
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.Unauthorized)]
    public async Task Server_trouble_and_expired_sign_in_stop_the_sync_instead_of_dropping_changes(HttpStatusCode status)
    {
        var (transport, _) = Create(_ => new HttpResponseMessage(status));

        await Should.ThrowAsync<SyncTransportException>(() => transport.PushAsync("vendors", new Vendor { Id = "v1" }, null, TestContext.Current.CancellationToken));
        await Should.ThrowAsync<SyncTransportException>(() => transport.PullAsync<Vendor>("vendors", null, 10, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Network_failures_are_transport_exceptions()
    {
        var (transport, _) = Create(_ => throw new HttpRequestException("offline"));
        await Should.ThrowAsync<SyncTransportException>(() => transport.PullAsync<Vendor>("vendors", null, 10, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Not_signed_in_is_a_transport_exception_and_sends_nothing()
    {
        var (transport, handler) = Create(_ => Json(HttpStatusCode.OK, "{}"), token: null);

        await Should.ThrowAsync<SyncTransportException>(() => transport.PullAsync<Vendor>("vendors", null, 10, TestContext.Current.CancellationToken));
        handler.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task Pull_sends_the_watermark_and_reads_the_page()
    {
        var (transport, handler) = Create(_ => Json(HttpStatusCode.OK, $$"""{"records":[{"entity":{{VendorJson}},"version":"2"}],"hasMore":true}"""));
        var after = new SyncWatermark(new DateTimeOffset(2026, 1, 1, 9, 0, 0, TimeSpan.Zero), "a b");

        var page = await transport.PullAsync<Vendor>("vendors", after, 25, TestContext.Current.CancellationToken);

        page.HasMore.ShouldBeTrue();
        page.Records.Single().Version.ShouldBe("2");
        page.Records[0].Entity.UpdatedAt.ShouldBe(new DateTimeOffset(2026, 1, 1, 10, 0, 0, TimeSpan.Zero));
        var url = handler.Requests.Single().RequestUri!.PathAndQuery;
        url.ShouldStartWith("/sync/vendors?pageSize=25");
        url.ShouldContain("afterId=a%20b");
        url.ShouldContain("afterUpdatedAt=2026-01-01T09%3A00%3A00.0000000%2B00%3A00");
    }

    [Fact]
    public async Task Garbage_answers_are_transport_exceptions()
    {
        var (transport, _) = Create(_ => Json(HttpStatusCode.OK, "not json"));
        await Should.ThrowAsync<SyncTransportException>(() => transport.PullAsync<Vendor>("vendors", null, 10, TestContext.Current.CancellationToken));
    }
}
