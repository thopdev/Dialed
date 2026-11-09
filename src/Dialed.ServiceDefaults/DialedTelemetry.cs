using System.Diagnostics.Metrics;

namespace Dialed.Telemetry;

/// <summary>
/// The app's own metrics (#90), on top of the standard ASP.NET Core, HTTP and runtime ones. One meter named <see cref="MeterName"/>;
/// tags never hold personal data (no user ids, emails or content), only the table and the outcome.
/// </summary>
public static class DialedTelemetry
{
    public const string MeterName = "Dialed";

    private static readonly Meter Meter = new(MeterName);

    /// <summary>Pull requests answered, by table.</summary>
    public static readonly Counter<long> SyncPulls = Meter.CreateCounter<long>("dialed.sync.pulls", description: "Sync pull requests, by table.");

    /// <summary>Pushed records, by table and outcome (stored, conflict, forbidden, invalid).</summary>
    public static readonly Counter<long> SyncPushes = Meter.CreateCounter<long>("dialed.sync.pushes", description: "Sync pushes, by table and outcome.");

    /// <summary>Pushes that lost to a newer version on the server.</summary>
    public static readonly Counter<long> SyncConflicts = Meter.CreateCounter<long>("dialed.sync.conflicts", description: "Sync pushes refused because the server had a newer version.");

    /// <summary>Tokens used by AI requests, by direction (input or output).</summary>
    public static readonly Counter<long> AiTokens = Meter.CreateCounter<long>("dialed.ai.tokens", unit: "{token}", description: "AI tokens used, by direction.");

    /// <summary>How long AI requests take.</summary>
    public static readonly Histogram<double> AiLatency = Meter.CreateHistogram<double>("dialed.ai.latency", unit: "ms", description: "AI request latency.");
}
