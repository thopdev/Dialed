using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Dialed.AI.Labels;
using Dialed.Api.Ai;
using Dialed.Api.Auth;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Dialed.Api.Tests;

public sealed class AiGateTests
{
    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class Monitor(AiLimits value) : IOptionsMonitor<AiLimits>
    {
        public AiLimits CurrentValue { get; set; } = value;

        public AiLimits Get(string? name) => CurrentValue;

        public IDisposable? OnChange(Action<AiLimits, string?> listener) => null;
    }

    private static (AiGate Gate, Clock Clock, Monitor Monitor) Make(AiLimits? limits = null)
    {
        var clock = new Clock(DateTimeOffset.Parse("2026-11-09T22:00:00Z", System.Globalization.CultureInfo.InvariantCulture));
        var monitor = new Monitor(limits ?? new AiLimits { LabelScansPerDay = 2 });
        return (new AiGate(monitor, clock), clock, monitor);
    }

    [Fact]
    public void Calls_are_allowed_until_the_daily_limit_and_then_refused_with_the_time_until_tomorrow()
    {
        var (gate, _, _) = Make();

        gate.TryEnter("u1", AiOperations.Label).Status.ShouldBe(AiGateStatus.Allowed);
        gate.TryEnter("u1", AiOperations.Label).Status.ShouldBe(AiGateStatus.Allowed);
        var refused = gate.TryEnter("u1", AiOperations.Label);

        refused.Status.ShouldBe(AiGateStatus.QuotaExceeded);
        refused.Limit.ShouldBe(2);
        refused.RetryAfter.ShouldBe(TimeSpan.FromHours(2));
        gate.Used("u1", AiOperations.Label).ShouldBe(2);
    }

    [Fact]
    public void The_quota_is_per_user_and_per_day()
    {
        var (gate, clock, _) = Make();
        gate.TryEnter("u1", AiOperations.Label);
        gate.TryEnter("u1", AiOperations.Label);

        gate.TryEnter("u2", AiOperations.Label).Status.ShouldBe(AiGateStatus.Allowed);
        clock.Now = clock.Now.AddHours(3);
        gate.TryEnter("u1", AiOperations.Label).Status.ShouldBe(AiGateStatus.Allowed);
    }

    [Fact]
    public void A_refund_gives_the_call_back()
    {
        var (gate, _, _) = Make();
        gate.TryEnter("u1", AiOperations.Label);
        gate.TryEnter("u1", AiOperations.Label);

        gate.Refund("u1", AiOperations.Label);

        gate.TryEnter("u1", AiOperations.Label).Status.ShouldBe(AiGateStatus.Allowed);
    }

    [Fact]
    public void The_kill_switch_stops_everything_at_once_and_can_be_turned_back_on()
    {
        var (gate, _, monitor) = Make();
        monitor.CurrentValue = new AiLimits { Enabled = false };

        gate.TryEnter("u1", AiOperations.Label).Status.ShouldBe(AiGateStatus.Disabled);

        monitor.CurrentValue = new AiLimits();
        gate.TryEnter("u1", AiOperations.Label).Status.ShouldBe(AiGateStatus.Allowed);
    }

    [Fact]
    public void Unknown_operations_have_no_quota() =>
        Make().Gate.TryEnter("u1", "nonsense").Status.ShouldBe(AiGateStatus.QuotaExceeded);

    [Fact]
    public void Cost_is_estimated_from_the_configured_prices()
    {
        var (gate, _, _) = Make(new AiLimits { InputUsdPerMillionTokens = 3m, OutputUsdPerMillionTokens = 15m });

        gate.EstimateCostUsd(2000, 100).ShouldBe(0.0075m);
    }
}

public sealed class AiGateEndpointTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>
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

    private static ByteArrayContent Photo()
    {
        var content = new ByteArrayContent(new byte[100]);
        content.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
        return content;
    }

    [Fact]
    public async Task The_second_scan_over_the_limit_gets_429_with_a_friendly_message_and_retry_after()
    {
        using var limited = factory.WithWebHostBuilder(b => b.UseSetting("Ai:LabelScansPerDay", "1"));
        var client = await SignedIn(limited, "alice");

        (await client.PostAsync("/ai/label-scans", Photo(), Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        using var refused = await client.PostAsync("/ai/label-scans", Photo(), Ct);

        refused.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
        refused.Headers.RetryAfter.ShouldNotBeNull();
        var body = await refused.Content.ReadAsStringAsync(Ct);
        body.ShouldContain("ai-quota");
        body.ShouldContain("by hand");
    }

    [Fact]
    public async Task The_kill_switch_answers_503_with_the_ai_disabled_code()
    {
        using var off = factory.WithWebHostBuilder(b => b.UseSetting("Ai:Enabled", "false"));
        var client = await SignedIn(off, "bob");

        using var response = await client.PostAsync("/ai/label-scans", Photo(), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        (await response.Content.ReadAsStringAsync(Ct)).ShouldContain("ai-disabled");
    }

    private sealed class Broken : ILabelExtractor
    {
        public Task<LabelExtraction> ExtractAsync(byte[] jpeg, CancellationToken cancellationToken = default) => throw new LabelExtractionException("down");
    }

    [Fact]
    public async Task A_failed_scan_and_a_rejected_photo_do_not_use_up_the_quota()
    {
        using var broken = factory.WithWebHostBuilder(b => b
            .UseSetting("Ai:LabelScansPerDay", "1")
            .ConfigureServices(s => s.AddSingleton<ILabelExtractor, Broken>()));
        var client = await SignedIn(broken, "carol");

        // limit is 1: if failures were counted, the second call would be 429
        (await client.PostAsync("/ai/label-scans", Photo(), Ct)).StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        (await client.PostAsync("/ai/label-scans", Photo(), Ct)).StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        using var png = new ByteArrayContent(new byte[10]);
        png.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        (await client.PostAsync("/ai/label-scans", png, Ct)).StatusCode.ShouldBe(HttpStatusCode.UnsupportedMediaType);
    }
}
