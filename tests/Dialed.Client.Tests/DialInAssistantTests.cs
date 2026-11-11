using System.Net;
using Dialed.Client.DialIn;
using Dialed.Client.Platform;
using Dialed.Client.Recipes;
using Dialed.Client.Sync;
using Dialed.Domain.Model;

namespace Dialed.Client.Tests;

public sealed class DialInAssistantTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class Handler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public HttpRequestMessage? Last { get; private set; }

        public string? Sent { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Last = request;
            Sent = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(status) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };
        }
    }

    private sealed class Tokens(string? token) : IAccessTokenProvider
    {
        public Task<string?> GetAccessTokenAsync(CancellationToken cancellationToken = default) => Task.FromResult(token);
    }

    private sealed class Online(bool online) : IConnectivity
    {
        public bool IsOnline => online;

#pragma warning disable CS0067
        public event Action<bool>? Changed;
#pragma warning restore CS0067
    }

    private const string Ok = """
        { "changes": [ { "kind": "GrindFiner", "from": 12, "to": 11 }, { "kind": "HotterWater", "from": 93, "to": 94 } ],
          "why": "It tasted sour, which usually means too little extraction.", "promptVersion": "rules-1+recent", "modelId": "rules",
          "inputTokens": 0, "outputTokens": 0, "reasonCode": "Sour" }
        """;

    private static DialInAssistant Assistant(HttpMessageHandler handler, string? token = "t", bool online = true) =>
        new(new HttpClient(handler) { BaseAddress = new Uri("https://api.test/") }, new Tokens(token), new Online(online));

    private static readonly DialInAssistantRequest Request = new(
        "espresso",
        new DialInBrewInfo(18m, null, 36m, 12m, 93m, 28, 2, new Taste(Acidity: -2)),
        [new DialInBrewInfo(18m, null, 36m, 13m, 93m, 26, 2, new Taste(Acidity: -2))],
        BeanOrigin: "Ethiopia",
        GrindMin: 0m,
        GrindMax: 40m,
        GrindStep: 1m);

    [Fact]
    public async Task The_brew_is_posted_with_the_token_and_the_changes_come_back()
    {
        var handler = new Handler(HttpStatusCode.OK, Ok);

        var advice = await Assistant(handler).AdviseAsync(Request, Ct);

        handler.Last!.RequestUri!.AbsolutePath.ShouldBe("/ai/dial-in");
        handler.Last.Headers.Authorization!.Parameter.ShouldBe("t");
        handler.Sent.ShouldNotBeNull().ShouldContain("\"methodCode\":\"espresso\"");
        handler.Sent.ShouldContain("\"acidity\":-2");
        handler.Sent.ShouldContain("\"recent\":[");
        advice.Changes.ShouldBe([new DialInAssistantChange("GrindFiner", 12m, 11m), new DialInAssistantChange("HotterWater", 93m, 94m)]);
        advice.ReasonCode.ShouldBe("Sour");
        advice.PromptVersion.ShouldBe("rules-1+recent");
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests, "{}", AssistantProblem.QuotaUsed)]
    [InlineData(HttpStatusCode.ServiceUnavailable, """{"code":"ai-disabled"}""", AssistantProblem.Disabled)]
    [InlineData(HttpStatusCode.ServiceUnavailable, "{}", AssistantProblem.Unavailable)]
    [InlineData(HttpStatusCode.Unauthorized, "{}", AssistantProblem.SignedOut)]
    public async Task Failures_become_a_problem_the_card_can_word(HttpStatusCode status, string body, AssistantProblem expected)
    {
        var exception = await Should.ThrowAsync<DialInAssistantException>(() => Assistant(new Handler(status, body)).AdviseAsync(Request, Ct));

        exception.Problem.ShouldBe(expected);
    }

    [Fact]
    public async Task Signed_out_or_offline_does_not_call_the_server()
    {
        var handler = new Handler(HttpStatusCode.OK, Ok);

        (await Should.ThrowAsync<DialInAssistantException>(() => Assistant(handler, token: null).AdviseAsync(Request, Ct))).Problem.ShouldBe(AssistantProblem.SignedOut);
        (await Should.ThrowAsync<DialInAssistantException>(() => Assistant(handler, online: false).AdviseAsync(Request, Ct))).Problem.ShouldBe(AssistantProblem.Offline);
        handler.Last.ShouldBeNull();
    }
}
