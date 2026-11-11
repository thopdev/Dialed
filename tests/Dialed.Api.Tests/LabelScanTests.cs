using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Dialed.AI.Labels;
using Dialed.Api.Ai;
using Dialed.Api.Auth;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;

namespace Dialed.Api.Tests;

public sealed class LabelScanTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<HttpClient> SignedIn(WebApplicationFactory<Program> f, string user)
    {
        var client = f.CreateClient();
        using var login = await client.PostAsJsonAsync("/auth/dev", new DevLoginRequest(user), Ct);
        var tokens = (await login.Content.ReadFromJsonAsync<TokenResponse>(Ct))!;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);
        return client;
    }

    private static ByteArrayContent Photo(int bytes = 100, string type = "image/jpeg")
    {
        var content = new ByteArrayContent(new byte[bytes]);
        content.Headers.ContentType = new MediaTypeHeaderValue(type);
        return content;
    }

    [Fact]
    public async Task A_label_scan_needs_a_signed_in_user()
    {
        using var response = await factory.CreateClient().PostAsync("/ai/label-scans", Photo(), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task A_photo_returns_a_draft_with_prompt_version_and_model()
    {
        var client = await SignedIn(factory, "alice");

        using var response = await client.PostAsync("/ai/label-scans", Photo(), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var result = (await response.Content.ReadFromJsonAsync<LabelScanResponse>(Ct))!;
        result.PromptVersion.ShouldBe(LabelPrompt.Version);
        result.ModelId.ShouldBe(FakeLabelExtractor.ModelId);
        result.Draft.Name.Value.ShouldNotBeNullOrWhiteSpace();
        result.Draft.Name.Confidence.ShouldBeInRange(0, 1);
    }

    [Fact]
    public async Task Other_content_types_empty_and_oversized_photos_are_refused()
    {
        var client = await SignedIn(factory, "bob");

        (await client.PostAsync("/ai/label-scans", Photo(type: "image/png"), Ct)).StatusCode.ShouldBe(HttpStatusCode.UnsupportedMediaType);
        (await client.PostAsync("/ai/label-scans", Photo(0), Ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await client.PostAsync("/ai/label-scans", Photo(AiEndpoints.MaxPhotoBytes + 1), Ct)).StatusCode.ShouldBeOneOf(HttpStatusCode.RequestEntityTooLarge, HttpStatusCode.BadRequest);
    }

    // ---- By photo id (#83) --------------------------------------------------------------------

    private static async Task<string> PushPhoto(HttpClient client, string? contentType = "image/jpeg")
    {
        var id = Guid.CreateVersion7().ToString();
        using var response = await client.PutAsJsonAsync($"/sync/photos/{id}", new System.Text.Json.Nodes.JsonObject { ["id"] = id, ["contentType"] = contentType, ["deleted"] = false }, Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return id;
    }

    private static string HouseholdOf(HttpClient client)
    {
        var token = new JsonWebTokenHandler().ReadJsonWebToken(client.DefaultRequestHeaders.Authorization!.Parameter);
        return token.Claims.First(c => c.Type == TokenIssuer.HouseholdClaim).Value;
    }

    [Fact]
    public async Task A_stored_photo_is_read_from_photo_storage_and_scanned_by_id()
    {
        var client = await SignedIn(factory, "alice");
        var id = await PushPhoto(client);
        var blobs = factory.Services.GetRequiredService<Dialed.Images.IPhotoBlobStore>();
        await blobs.WriteAsync(Dialed.Images.PhotoBlobPaths.Original(HouseholdOf(client), id), [0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3], Ct);

        using var response = await client.PostAsync($"/ai/label-scans/{id}", null, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var result = (await response.Content.ReadFromJsonAsync<LabelScanResponse>(Ct))!;
        result.PromptVersion.ShouldBe(LabelPrompt.Version);
        result.Draft.Name.Value.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task A_photo_that_has_not_synced_or_not_uploaded_is_a_404_that_does_not_use_the_quota()
    {
        using var limited = factory.WithWebHostBuilder(b => b.UseSetting("Ai:LabelScansPerDay", "1"));
        var client = await SignedIn(limited, "bob");
        var unknown = $"/ai/label-scans/{Guid.CreateVersion7()}";
        var id = await PushPhoto(client);

        using var notSynced = await client.PostAsync(unknown, null, Ct);
        using var notUploaded = await client.PostAsync($"/ai/label-scans/{id}", null, Ct);
        using var again = await client.PostAsync($"/ai/label-scans/{id}", null, Ct);

        notSynced.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await notSynced.Content.ReadAsStringAsync(Ct)).ShouldContain("photo-unknown");
        notUploaded.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await notUploaded.Content.ReadAsStringAsync(Ct)).ShouldContain("photo-not-uploaded");
        again.StatusCode.ShouldBe(HttpStatusCode.NotFound); // still 404 and not 429: nothing was counted
    }

    [Fact]
    public async Task Another_household_cannot_scan_a_photo_it_cannot_see()
    {
        var alice = await SignedIn(factory, "alice");
        var carol = await SignedIn(factory, "carol");
        var id = await PushPhoto(alice);
        var blobs = factory.Services.GetRequiredService<Dialed.Images.IPhotoBlobStore>();
        await blobs.WriteAsync(Dialed.Images.PhotoBlobPaths.Original(HouseholdOf(alice), id), [0xFF, 0xD8, 0xFF, 0xE0, 1], Ct);

        using var response = await carol.PostAsync($"/ai/label-scans/{id}", null, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Scanning_by_id_needs_a_signed_in_user_and_shares_the_kill_switch_and_quota()
    {
        (await factory.CreateClient().PostAsync($"/ai/label-scans/{Guid.CreateVersion7()}", null, Ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        using var limited = factory.WithWebHostBuilder(b => b.UseSetting("Ai:LabelScansPerDay", "1"));
        var client = await SignedIn(limited, "alice");
        var blobs = limited.Services.GetRequiredService<Dialed.Images.IPhotoBlobStore>();
        var first = await PushPhoto(client);
        var second = await PushPhoto(client);
        foreach (var id in new[] { first, second })
        {
            await blobs.WriteAsync(Dialed.Images.PhotoBlobPaths.Original(HouseholdOf(client), id), [0xFF, 0xD8, 0xFF, 0xE0, 1], Ct);
        }

        (await client.PostAsync($"/ai/label-scans/{first}", null, Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        using var over = await client.PostAsync($"/ai/label-scans/{second}", null, Ct);
        over.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);

        using var off = factory.WithWebHostBuilder(b => b.UseSetting("Ai:Enabled", "false"));
        var other = await SignedIn(off, "bob");
        var offId = await PushPhoto(other);
        await off.Services.GetRequiredService<Dialed.Images.IPhotoBlobStore>().WriteAsync(Dialed.Images.PhotoBlobPaths.Original(HouseholdOf(other), offId), [0xFF, 0xD8, 0xFF, 0xE0, 1], Ct);
        using var disabled = await other.PostAsync($"/ai/label-scans/{offId}", null, Ct);
        disabled.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        (await disabled.Content.ReadAsStringAsync(Ct)).ShouldContain("ai-disabled");
    }

    private sealed class BrokenExtractor : ILabelExtractor
    {
        public Task<LabelExtraction> ExtractAsync(byte[] jpeg, CancellationToken cancellationToken = default) => throw new LabelExtractionException("down");
    }

    [Fact]
    public async Task When_the_model_fails_the_app_gets_a_friendly_503()
    {
        using var broken = factory.WithWebHostBuilder(b => b.ConfigureServices(s => s.AddSingleton<ILabelExtractor, BrokenExtractor>()));
        var client = await SignedIn(broken, "carol");

        using var response = await client.PostAsync("/ai/label-scans", Photo(), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        (await response.Content.ReadAsStringAsync(Ct)).ShouldContain("by hand");
    }
}

public sealed class ClaudeLabelExtractorTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string GoodAnswer = """
        {
          "model": "claude-test",
          "content": [
            { "type": "text", "text": "ok" },
            { "type": "tool_use", "name": "record_bean_label", "input": {
                "name": { "value": " Guji ", "confidence": 0.9 },
                "roaster": { "value": "Friedhats", "confidence": 7 },
                "country": { "value": "", "confidence": 0.5 },
                "unknownField": { "value": "x", "confidence": 1 }
            } }
          ],
          "usage": { "input_tokens": 1500, "output_tokens": 120 }
        }
        """;

    private sealed class Handler(params HttpStatusCode[] statuses) : HttpMessageHandler
    {
        private int calls;

        public int Calls => calls;

        public HttpRequestMessage? Last { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Last = request;
            var status = statuses[Math.Min(calls++, statuses.Length - 1)];
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(status == HttpStatusCode.OK ? GoodAnswer : "{}") });
        }
    }

    private static ClaudeOptions Options => new() { Endpoint = "https://example.test/", ApiKey = "key", ExtractionModel = "model-x" };

    [Fact]
    public void The_request_forces_the_tool_and_carries_one_jpeg_image()
    {
        var request = ClaudeLabelExtractor.BuildRequest([1, 2, 3], "model-x", 512);

        request["model"]!.GetValue<string>().ShouldBe("model-x");
        request["tool_choice"]!["name"]!.GetValue<string>().ShouldBe(LabelPrompt.ToolName);
        var image = request["messages"]![0]!["content"]![0]!;
        image["source"]!["media_type"]!.GetValue<string>().ShouldBe("image/jpeg");
        image["source"]!["data"]!.GetValue<string>().ShouldBe(Convert.ToBase64String([1, 2, 3]));
        request["tools"]![0]!["input_schema"]!["properties"]!.AsObject().Select(p => p.Key).ShouldBe(BeanDraft.FieldNames);
    }

    [Fact]
    public void The_answer_is_validated_trimmed_and_clamped()
    {
        var result = ClaudeLabelExtractor.Parse(GoodAnswer);

        result.Draft.Name.ShouldBe(new DraftField("Guji", 0.9));
        result.Draft.Roaster.Confidence.ShouldBe(1);
        result.Draft.Country.HasValue.ShouldBeFalse();
        result.Draft.Process.HasValue.ShouldBeFalse();
        (result.ModelId, result.InputTokens, result.OutputTokens, result.PromptVersion).ShouldBe(("claude-test", 1500, 120, LabelPrompt.Version));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("""{ "content": [ { "type": "text", "text": "I cannot" } ] }""")]
    public void An_answer_without_the_tool_call_is_an_error(string json) =>
        Should.Throw<LabelExtractionException>(() => ClaudeLabelExtractor.Parse(json));

    [Fact]
    public async Task The_call_sends_the_key_and_version_and_returns_the_draft()
    {
        var handler = new Handler(HttpStatusCode.OK);
        var extractor = new ClaudeLabelExtractor(new HttpClient(handler), Options);

        var result = await extractor.ExtractAsync([1], Ct);

        result.Draft.Name.Value.ShouldBe("Guji");
        handler.Last!.RequestUri!.ToString().ShouldBe("https://example.test/v1/messages");
        handler.Last.Headers.GetValues("x-api-key").ShouldBe(["key"]);
        handler.Last.Headers.GetValues("anthropic-version").ShouldBe([ClaudeLabelExtractor.ApiVersion]);
    }

    [Fact]
    public async Task A_busy_model_is_retried_once()
    {
        var handler = new Handler(HttpStatusCode.TooManyRequests, HttpStatusCode.OK);

        var result = await new ClaudeLabelExtractor(new HttpClient(handler), Options).ExtractAsync([1], Ct);

        result.Draft.Name.HasValue.ShouldBeTrue();
        handler.Calls.ShouldBe(2);
    }

    [Fact]
    public async Task A_model_that_keeps_failing_gives_a_friendly_error_after_two_tries()
    {
        var handler = new Handler(HttpStatusCode.InternalServerError);

        await Should.ThrowAsync<LabelExtractionException>(() => new ClaudeLabelExtractor(new HttpClient(handler), Options).ExtractAsync([1], Ct));

        handler.Calls.ShouldBe(2);
    }

    [Fact]
    public void Options_are_configured_only_when_all_three_are_set()
    {
        Options.IsConfigured.ShouldBeTrue();
        new ClaudeOptions { Endpoint = "x", ApiKey = "y" }.IsConfigured.ShouldBeFalse();
    }
}
