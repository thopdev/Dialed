using Microsoft.Extensions.Options;

namespace Dialed.Api.Ai;

/// <summary>Limits and switches for AI use (#88). All configurable under "Ai"; <see cref="Enabled"/> is the global kill switch.</summary>
public sealed class AiLimits
{
    /// <summary>Global switch: when false every AI call is refused at once and the app falls back to rules and manual entry. Reloadable without a deploy.</summary>
    public bool Enabled { get; set; } = true;

    public int LabelScansPerDay { get; set; } = 20;

    public int SuggestionsPerDay { get; set; } = 50;

    /// <summary>Prices in USD per million tokens, to estimate cost per call for the dashboards and the daily spend alert.</summary>
    public decimal InputUsdPerMillionTokens { get; set; } = 1m;

    public decimal OutputUsdPerMillionTokens { get; set; } = 5m;

    public int LimitFor(string operation) => operation switch
    {
        AiOperations.Label => LabelScansPerDay,
        AiOperations.Suggestion => SuggestionsPerDay,
        _ => 0,
    };
}

public static class AiOperations
{
    public const string Label = "label";

    public const string Suggestion = "suggestion";
}

public enum AiGateStatus
{
    Allowed,
    Disabled,
    QuotaExceeded,
}

public sealed record AiGateResult(AiGateStatus Status, int Used = 0, int Limit = 0, TimeSpan RetryAfter = default);

/// <summary>
/// Checks the kill switch and counts AI calls per user, operation and UTC day in an <see cref="IAiQuotaStore"/> (Cosmos DB in Azure, so the
/// quota holds across restarts and instances). The switch is checked first and never touches the store.
/// </summary>
public sealed class AiGate(IOptionsMonitor<AiLimits> limits, IAiQuotaStore quota, TimeProvider time)
{
    /// <summary>Checks the switch and the quota and, when allowed, counts the call.</summary>
    public async Task<AiGateResult> TryEnterAsync(string userId, string operation, CancellationToken cancellationToken = default)
    {
        var current = limits.CurrentValue;
        if (!current.Enabled)
        {
            return new AiGateResult(AiGateStatus.Disabled);
        }

        var now = time.GetUtcNow();
        var limit = current.LimitFor(operation);
        if (await quota.TryTakeAsync(userId, operation, Day(now), limit, cancellationToken) is not { } used)
        {
            var tomorrow = new DateTimeOffset(now.UtcDateTime.Date.AddDays(1), TimeSpan.Zero);
            return new AiGateResult(AiGateStatus.QuotaExceeded, limit, limit, tomorrow - now);
        }

        return new AiGateResult(AiGateStatus.Allowed, used, limit);
    }

    /// <summary>Gives the call back, for example when the model failed: people are not charged quota for our errors.</summary>
    public Task RefundAsync(string userId, string operation, CancellationToken cancellationToken = default) =>
        quota.RefundAsync(userId, operation, Day(time.GetUtcNow()), cancellationToken);

    public Task<int> UsedAsync(string userId, string operation, CancellationToken cancellationToken = default) =>
        quota.UsedAsync(userId, operation, Day(time.GetUtcNow()), cancellationToken);

    public decimal EstimateCostUsd(int inputTokens, int outputTokens)
    {
        var current = limits.CurrentValue;
        return (inputTokens * current.InputUsdPerMillionTokens + outputTokens * current.OutputUsdPerMillionTokens) / 1_000_000m;
    }

    private static DateOnly Day(DateTimeOffset now) => DateOnly.FromDateTime(now.UtcDateTime);
}
