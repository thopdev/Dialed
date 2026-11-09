using System.Net;
using Dialed.Client.Platform;
using Dialed.Client.Scan;
using Dialed.Client.Sync;

namespace Dialed.Client.Tests;

public sealed class LabelScannerTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class Handler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public HttpRequestMessage? Last { get; private set; }

        public byte[]? Sent { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Last = request;
            Sent = request.Content is null ? null : await request.Content.ReadAsByteArrayAsync(cancellationToken);
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

    private static readonly CapturedPhoto Photo = new([9, 8, 7], "image/jpeg", "l.jpg");

    private const string Ok = """
        { "draft": { "name": { "value": "Guji", "confidence": 0.9 }, "roaster": { "value": null, "confidence": 0 }, "country": { "value": "Ethiopia", "confidence": 0.4 },
          "region": { "value": null, "confidence": 0 }, "farm": { "value": null, "confidence": 0 }, "process": { "value": null, "confidence": 0 },
          "varieties": { "value": null, "confidence": 0 }, "roastLevel": { "value": null, "confidence": 0 }, "tastingNotes": { "value": null, "confidence": 0 },
          "roastDate": { "value": null, "confidence": 0 }, "weightG": { "value": "250", "confidence": 0.9 } },
          "promptVersion": "label-v1", "modelId": "m" }
        """;

    private static LabelScanner Scanner(Handler handler, string? token = "t", bool online = true) =>
        new(new HttpClient(handler) { BaseAddress = new Uri("https://api.test/") }, new Tokens(token), new Online(online));

    [Fact]
    public async Task The_photo_is_posted_as_jpeg_with_the_token_and_the_draft_is_returned()
    {
        var handler = new Handler(HttpStatusCode.OK, Ok);

        var label = await Scanner(handler).ScanAsync(Photo, Ct);

        handler.Last!.RequestUri!.AbsolutePath.ShouldBe("/ai/label-scans");
        handler.Last.Content!.Headers.ContentType!.MediaType.ShouldBe("image/jpeg");
        handler.Last.Headers.Authorization!.Parameter.ShouldBe("t");
        handler.Sent.ShouldBe([9, 8, 7]);
        label.Name.Value.ShouldBe("Guji");
        label.Country.IsUncertain.ShouldBeTrue();
        label.Name.IsUncertain.ShouldBeFalse();
        label.Roaster.HasValue.ShouldBeFalse();
        label.WeightG.Value.ShouldBe("250");
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests, "{}", ScanProblem.QuotaUsed)]
    [InlineData(HttpStatusCode.ServiceUnavailable, "{\"code\":\"ai-disabled\"}", ScanProblem.Disabled)]
    [InlineData(HttpStatusCode.ServiceUnavailable, "{}", ScanProblem.Unavailable)]
    [InlineData(HttpStatusCode.Unauthorized, "{}", ScanProblem.SignedOut)]
    [InlineData(HttpStatusCode.InternalServerError, "oops", ScanProblem.Unavailable)]
    public async Task Server_answers_become_problems_the_screen_can_explain(HttpStatusCode status, string body, ScanProblem expected)
    {
        var exception = await Should.ThrowAsync<LabelScanException>(() => Scanner(new Handler(status, body)).ScanAsync(Photo, Ct));

        exception.Problem.ShouldBe(expected);
    }

    [Fact]
    public async Task Signed_out_and_offline_never_reach_the_server()
    {
        var handler = new Handler(HttpStatusCode.OK, Ok);

        (await Should.ThrowAsync<LabelScanException>(() => Scanner(handler, token: null).ScanAsync(Photo, Ct))).Problem.ShouldBe(ScanProblem.SignedOut);
        (await Should.ThrowAsync<LabelScanException>(() => Scanner(handler, online: false).ScanAsync(Photo, Ct))).Problem.ShouldBe(ScanProblem.Offline);

        handler.Last.ShouldBeNull();
    }

    [Fact]
    public async Task A_garbled_success_answer_is_unavailable()
    {
        var exception = await Should.ThrowAsync<LabelScanException>(() => Scanner(new Handler(HttpStatusCode.OK, "not json")).ScanAsync(Photo, Ct));

        exception.Problem.ShouldBe(ScanProblem.Unavailable);
    }
}
