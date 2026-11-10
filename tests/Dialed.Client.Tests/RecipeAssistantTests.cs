using System.Net;
using Dialed.Client.Platform;
using Dialed.Client.Recipes;
using Dialed.Client.Sync;

namespace Dialed.Client.Tests;

public sealed class RecipeAssistantTests
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

    private sealed class Throwing : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => throw new HttpRequestException("down");
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
        { "doseG": 15, "waterG": 250, "yieldG": null, "grindSetting": 20, "tempC": 95, "timeS": 180,
          "reasons": [ { "parameter": "dose", "code": "Baseline", "text": null }, { "parameter": "temperature", "code": "LightRoast", "text": null } ],
          "promptVersion": "recipe-rules-1", "modelId": "rules" }
        """;

    private static RecipeAssistant Assistant(HttpMessageHandler handler, string? token = "t", bool online = true) =>
        new(new HttpClient(handler) { BaseAddress = new Uri("https://api.test/") }, new Tokens(token), new Online(online));

    private static readonly RecipeAssistantRequest Request = new("v60", BeanOrigin: "Ethiopia", RoastLevel: "light", Similar: new SimilarRecipeInfo(15m, 250m, null, 20m, 94m, 180, 4.5));

    [Fact]
    public async Task The_request_is_posted_as_json_with_the_token_and_the_suggestion_is_returned()
    {
        var handler = new Handler(HttpStatusCode.OK, Ok);

        var suggestion = await Assistant(handler).SuggestAsync(Request, Ct);

        handler.Last!.RequestUri!.AbsolutePath.ShouldBe("/ai/recipe-suggestions");
        handler.Last.Headers.Authorization!.Parameter.ShouldBe("t");
        handler.Sent!.ShouldContain("\"methodCode\":\"v60\"");
        handler.Sent!.ShouldContain("\"similar\":");
        suggestion.DoseG.ShouldBe(15m);
        suggestion.TempC.ShouldBe(95m);
        suggestion.YieldG.ShouldBeNull();
        suggestion.Reasons.Select(r => r.Code).ShouldBe(["Baseline", "LightRoast"]);
        suggestion.PromptVersion.ShouldBe("recipe-rules-1");
    }

    [Fact]
    public async Task Signed_out_and_offline_are_reported_without_calling_the_server()
    {
        var handler = new Handler(HttpStatusCode.OK, Ok);

        (await Should.ThrowAsync<RecipeAssistantException>(() => Assistant(handler, token: null).SuggestAsync(Request, Ct))).Problem.ShouldBe(AssistantProblem.SignedOut);
        (await Should.ThrowAsync<RecipeAssistantException>(() => Assistant(handler, online: false).SuggestAsync(Request, Ct))).Problem.ShouldBe(AssistantProblem.Offline);
        handler.Last.ShouldBeNull();
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "{}", AssistantProblem.SignedOut)]
    [InlineData(HttpStatusCode.TooManyRequests, "{}", AssistantProblem.QuotaUsed)]
    [InlineData(HttpStatusCode.ServiceUnavailable, "{\"code\":\"ai-disabled\"}", AssistantProblem.Disabled)]
    [InlineData(HttpStatusCode.ServiceUnavailable, "{}", AssistantProblem.Unavailable)]
    [InlineData(HttpStatusCode.InternalServerError, "{}", AssistantProblem.Unavailable)]
    public async Task Server_answers_map_to_a_problem(HttpStatusCode status, string body, AssistantProblem expected)
    {
        var exception = await Should.ThrowAsync<RecipeAssistantException>(() => Assistant(new Handler(status, body)).SuggestAsync(Request, Ct));

        exception.Problem.ShouldBe(expected);
    }

    [Fact]
    public async Task A_dropped_connection_is_offline_and_a_broken_body_is_unavailable()
    {
        (await Should.ThrowAsync<RecipeAssistantException>(() => Assistant(new Throwing()).SuggestAsync(Request, Ct))).Problem.ShouldBe(AssistantProblem.Offline);
        (await Should.ThrowAsync<RecipeAssistantException>(() => Assistant(new Handler(HttpStatusCode.OK, "not json")).SuggestAsync(Request, Ct))).Problem.ShouldBe(AssistantProblem.Unavailable);
    }
}
