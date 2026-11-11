using Dialed.Domain.Model;

namespace Dialed.AI.DialIn;

/// <summary>The numbers of one brew, as the dial-in assistant sees them. No names, notes or photos: nothing that identifies a person.</summary>
public sealed record DialInBrewSummary(decimal DoseG, decimal? WaterG, decimal? YieldG, decimal? GrindSetting, decimal? TempC, int? TimeS, int? Rating, Taste Taste);

/// <summary>What the assistant is told: the brew that did not taste right, the latest brews of the same bag, and the coffee and grinder.</summary>
public sealed record DialInContext(
    string MethodCode,
    DialInBrewSummary Brew,
    IReadOnlyList<DialInBrewSummary>? Recent = null,
    string? BeanOrigin = null,
    string? BeanProcess = null,
    string? RoastLevel = null,
    string? GrinderName = null,
    decimal? GrindMin = null,
    decimal? GrindMax = null,
    decimal? GrindStep = null)
{
    /// <summary>Only the latest few brews go to the model: enough to see a trend, cheap in tokens.</summary>
    public const int MaxRecent = 5;
}

/// <summary>One change as from to. <see cref="Kind"/> is a <c>SuggestionKind</c> name (GrindFiner, HotterWater, ...).</summary>
public sealed record DialInChange(string Kind, decimal? From, decimal? To);

/// <summary>
/// One or two changes with a short reason, and which prompt and model produced them (recorded with accept or dismiss, #86). <see cref="Why"/>
/// is the reason in English; <see cref="ReasonCode"/>, when set, is a reason the app words in the user's language instead (the rules set it,
/// a model answer does not).
/// </summary>
public sealed record DialInRefinement(IReadOnlyList<DialInChange> Changes, string Why, string PromptVersion, string ModelId, int InputTokens = 0, int OutputTokens = 0, string? ReasonCode = null);

public interface IDialInRefiner
{
    Task<DialInRefinement> RefineAsync(DialInContext context, CancellationToken cancellationToken = default);
}

/// <summary>The model call failed or its answer did not follow the schema.</summary>
public sealed class DialInRefinementException(string message, Exception? inner = null) : Exception(message, inner);
