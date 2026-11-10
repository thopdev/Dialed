using System.Diagnostics.Metrics;
using System.Text.Json.Nodes;
using Dialed.Api.Sync;
using Dialed.Telemetry;

namespace Dialed.Api.Tests;

public sealed class SuggestionQualityTelemetryTests : IDisposable
{
    private readonly MeterListener listener = new();
    private readonly Lock gate = new();
    private readonly List<(string Instrument, double Value, Dictionary<string, object?> Tags)> seen = [];

    public SuggestionQualityTelemetryTests()
    {
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == DialedTelemetry.MeterName && instrument.Name.StartsWith("dialed.ai.suggestion", StringComparison.Ordinal))
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((i, v, tags, _) => Add(i.Name, v, tags));
        listener.SetMeasurementEventCallback<double>((i, v, tags, _) => Add(i.Name, v, tags));
        listener.Start();
    }

    private void Add(string name, double value, ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        lock (gate)
        {
            seen.Add((name, value, tags.ToArray().ToDictionary(t => t.Key, t => t.Value)));
        }
    }

    private List<(string Instrument, double Value, Dictionary<string, object?> Tags)> For(string version)
    {
        lock (gate)
        {
            return [.. seen.Where(s => s.Tags.GetValueOrDefault("prompt_version") as string == version)];
        }
    }

    private static JsonObject Feedback(string version, int outcome, string source = "rules", int? before = null, int? after = null) => new()
    {
        ["id"] = Guid.NewGuid().ToString(),
        ["brewId"] = "b1",
        ["outcome"] = outcome,
        ["source"] = source,
        ["promptVersion"] = version,
        ["ratingBefore"] = before,
        ["ratingAfter"] = after,
    };

    [Fact]
    public void A_new_applied_or_dismissed_record_counts_once_per_prompt_version()
    {
        SuggestionQualityTelemetry.Record(Feedback("t-v1", 0), isUpdate: false);
        SuggestionQualityTelemetry.Record(Feedback("t-v1", 1), isUpdate: false);

        var counts = For("t-v1").Where(s => s.Instrument == "dialed.ai.suggestion.outcomes").Select(s => (string)s.Tags["outcome"]!).Order().ToList();
        counts.ShouldBe(["applied", "dismissed"]);
    }

    [Fact]
    public void Linking_the_next_brew_records_the_rating_change_and_does_not_count_the_outcome_again()
    {
        SuggestionQualityTelemetry.Record(Feedback("t-v2", 0, before: 2, after: 4), isUpdate: true);

        var seenForVersion = For("t-v2");
        seenForVersion.Count.ShouldBe(1);
        seenForVersion[0].Instrument.ShouldBe("dialed.ai.suggestion.rating_change");
        seenForVersion[0].Value.ShouldBe(2);
    }

    [Fact]
    public void A_missing_or_out_of_range_rating_records_no_change()
    {
        SuggestionQualityTelemetry.Record(Feedback("t-v3", 0, before: 2, after: null), isUpdate: true);
        SuggestionQualityTelemetry.Record(Feedback("t-v3", 0, before: 0, after: 9), isUpdate: true);
        SuggestionQualityTelemetry.Record(Feedback("t-v3", 1, before: 2, after: 4), isUpdate: true);

        For("t-v3").ShouldBeEmpty();
    }

    [Fact]
    public void Odd_prompt_versions_and_sources_are_folded_so_tags_cannot_grow_without_limit()
    {
        SuggestionQualityTelemetry.Record(Feedback("a version with spaces and a very long tail that goes on and on and on", 0, source: "whatever"), isUpdate: false);

        var other = For("other").Where(s => s.Tags.GetValueOrDefault("source") as string == "rules").ToList();
        other.ShouldNotBeEmpty();
    }

    [Fact]
    public void An_unknown_outcome_is_ignored()
    {
        SuggestionQualityTelemetry.Record(Feedback("t-v4", 7), isUpdate: false);

        For("t-v4").ShouldBeEmpty();
    }

    public void Dispose() => listener.Dispose();
}

public sealed class SuggestionQualityPushTests(Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> factory) : IClassFixture<Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program>>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Pushing_feedback_through_sync_is_counted_and_pushing_it_again_is_not_a_new_outcome()
    {
        var total = 0L;
        var changes = new List<double>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (i, l) =>
        {
            if (i.Meter.Name == DialedTelemetry.MeterName && i.Name.StartsWith("dialed.ai.suggestion", StringComparison.Ordinal))
            {
                l.EnableMeasurementEvents(i);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, v, tags, _) =>
        {
            if (tags.ToArray().Any(t => t.Key == "prompt_version" && t.Value as string == "push-v1"))
            {
                Interlocked.Add(ref total, v);
            }
        });
        listener.SetMeasurementEventCallback<double>((_, v, tags, _) =>
        {
            if (tags.ToArray().Any(t => t.Key == "prompt_version" && t.Value as string == "push-v1"))
            {
                lock (changes)
                {
                    changes.Add(v);
                }
            }
        });
        listener.Start();
        var client = factory.CreateClient();
        using var login = await System.Net.Http.Json.HttpClientJsonExtensions.PostAsJsonAsync(client, "/auth/dev", new Dialed.Api.Auth.DevLoginRequest("alice"), Ct);
        var tokens = (await System.Net.Http.Json.HttpContentJsonExtensions.ReadFromJsonAsync<Dialed.Api.Auth.TokenResponse>(login.Content, Ct))!;
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", tokens.AccessToken);
        var id = Guid.NewGuid().ToString();
        var entity = new JsonObject { ["id"] = id, ["brewId"] = "b1", ["kind"] = 0, ["outcome"] = 0, ["source"] = "rules", ["promptVersion"] = "push-v1", ["ratingBefore"] = 2, ["deleted"] = false };

        (await System.Net.Http.Json.HttpClientJsonExtensions.PutAsJsonAsync(client, $"/sync/dialinfeedback/{id}", entity, Ct)).EnsureSuccessStatusCode();
        entity["nextBrewId"] = "b2";
        entity["ratingAfter"] = 5;
        using var update = new HttpRequestMessage(HttpMethod.Put, $"/sync/dialinfeedback/{id}") { Content = System.Net.Http.Json.JsonContent.Create(entity) };
        update.Headers.TryAddWithoutValidation("If-Match", "\"1\"");
        (await client.SendAsync(update, Ct)).EnsureSuccessStatusCode();

        Interlocked.Read(ref total).ShouldBe(1);
        changes.ShouldBe([3d]);
    }
}
