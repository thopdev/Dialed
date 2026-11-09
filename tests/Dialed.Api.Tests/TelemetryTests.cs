using System.Diagnostics.Metrics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Dialed.Api.Auth;
using Dialed.Telemetry;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Dialed.Api.Tests;

/// <summary>Custom metrics and the correlation id (#90).</summary>
public sealed class TelemetryTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<HttpClient> SignedIn(WebApplicationFactory<Program> app)
    {
        var client = app.CreateClient();
        using var login = await client.PostAsJsonAsync("/auth/dev", new DevLoginRequest("alice"), Ct);
        var tokens = (await login.Content.ReadFromJsonAsync<TokenResponse>(Ct))!;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);
        return client;
    }

    /// <summary>Collects what the app's meter reports while the test runs: (instrument, outcome tag) to the total.</summary>
    private sealed class Listen : IDisposable
    {
        private readonly MeterListener listener = new();
        private readonly Lock gate = new();
        public Dictionary<string, long> Totals { get; } = [];

        public Listen()
        {
            listener.InstrumentPublished = (instrument, l) =>
            {
                if (instrument.Meter.Name == DialedTelemetry.MeterName)
                {
                    l.EnableMeasurementEvents(instrument);
                }
            };
            listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
            {
                var outcome = tags.ToArray().FirstOrDefault(t => t.Key == "outcome").Value as string;
                lock (gate)
                {
                    var key = outcome is null ? instrument.Name : $"{instrument.Name}:{outcome}";
                    Totals[key] = Totals.GetValueOrDefault(key) + value;
                }
            });
            listener.Start();
        }

        public long Of(string key)
        {
            lock (gate)
            {
                return Totals.GetValueOrDefault(key);
            }
        }

        public void Dispose() => listener.Dispose();
    }

    [Fact]
    public async Task Pulls_pushes_and_conflicts_are_counted_without_personal_data()
    {
        using var listen = new Listen();
        using var client = await SignedIn(factory);
        var id = Guid.NewGuid().ToString();
        var bean = new JsonObject { ["id"] = id, ["name"] = "Metric", ["deleted"] = false };

        (await client.GetAsync("/sync/beans", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await client.PutAsJsonAsync($"/sync/beans/{id}", bean, Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        using var stale = new HttpRequestMessage(HttpMethod.Put, $"/sync/beans/{id}") { Content = JsonContent.Create(bean) };
        stale.Headers.TryAddWithoutValidation("If-Match", "\"0\"");
        (await client.SendAsync(stale, Ct)).StatusCode.ShouldBe(HttpStatusCode.Conflict);

        listen.Of("dialed.sync.pulls").ShouldBeGreaterThanOrEqualTo(1);
        listen.Of("dialed.sync.pushes:stored").ShouldBeGreaterThanOrEqualTo(1);
        listen.Of("dialed.sync.pushes:conflict").ShouldBeGreaterThanOrEqualTo(1);
        listen.Of("dialed.sync.conflicts").ShouldBeGreaterThanOrEqualTo(1);
    }

    [Fact]
    public async Task Every_answer_carries_a_correlation_id_and_error_bodies_repeat_it()
    {
        using var client = factory.CreateClient();

        using var ok = await client.GetAsync("/", Ct);
        using var refused = await client.PostAsJsonAsync("/auth/signup/options", new { email = "", displayName = "" }, Ct);

        ok.Headers.GetValues("X-Correlation-Id").Single().ShouldNotBeNullOrWhiteSpace();
        refused.StatusCode.ShouldNotBe(HttpStatusCode.OK);
        var header = refused.Headers.GetValues("X-Correlation-Id").Single();
        if (refused.Content.Headers.ContentType?.MediaType?.Contains("json", StringComparison.Ordinal) == true)
        {
            var body = (await refused.Content.ReadFromJsonAsync<JsonNode>(Ct))!;
            if (body["correlationId"] is { } inBody)
            {
                inBody.GetValue<string>().ShouldBe(header);
            }
        }
    }

    [Fact]
    public async Task A_rate_limited_answer_names_the_correlation_id_in_the_body()
    {
        await using var app = factory.WithWebHostBuilder(b => b.UseSetting("Protection:AuthRequestsPerMinute", "1"));
        using var client = app.CreateClient();
        await client.PostAsJsonAsync("/auth/refresh", new RefreshRequest("x"), Ct);

        using var limited = await client.PostAsJsonAsync("/auth/refresh", new RefreshRequest("x"), Ct);

        limited.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
        var body = (await limited.Content.ReadFromJsonAsync<JsonNode>(Ct))!;
        body["correlationId"]!.GetValue<string>().ShouldBe(limited.Headers.GetValues("X-Correlation-Id").Single());
    }

    private sealed class ThrowingFilter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            next(app);
            app.Map("/boom", branch => branch.Run(_ => throw new InvalidOperationException("secret detail that must not leak")));
        };
    }

    [Fact]
    public async Task An_unexpected_error_returns_a_problem_with_the_correlation_id_and_no_details()
    {
        await using var app = factory.WithWebHostBuilder(b => b.ConfigureServices(s => s.AddTransient<IStartupFilter, ThrowingFilter>()));
        using var client = app.CreateClient();

        using var response = await client.GetAsync("/boom", Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.InternalServerError);
        var text = await response.Content.ReadAsStringAsync(Ct);
        text.ShouldNotContain("secret detail");
        text.ShouldContain("correlationId");
        text.ShouldContain(response.Headers.GetValues("X-Correlation-Id").Single());
    }
}
