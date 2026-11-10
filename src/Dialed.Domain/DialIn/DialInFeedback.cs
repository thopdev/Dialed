using Dialed.Domain.Model;

namespace Dialed.Domain.DialIn;

/// <summary>What the person did with a suggestion (#86).</summary>
public enum DialInOutcome
{
    /// <summary>The suggestion was copied into the next brew.</summary>
    Applied,

    /// <summary>The person said it was not useful.</summary>
    Dismissed,
}

/// <summary>
/// Whether a dial-in suggestion was used, kept with the prompt version so advice can be compared over time (#86). The rules have
/// their own version (<see cref="DialInAdvisor.RulesVersion"/>); an AI refinement records the prompt version it came from.
/// </summary>
public sealed record DialInFeedback : SyncEntity
{
    public required string BrewId { get; init; }

    public required SuggestionKind Kind { get; init; }

    public required DialInOutcome Outcome { get; init; }

    /// <summary><c>rules</c> or <c>ai</c>.</summary>
    public string Source { get; init; } = "rules";

    public required string PromptVersion { get; init; }

    /// <summary>The rating of the brew the advice was about, when the suggestion was applied (#89).</summary>
    public int? RatingBefore { get; init; }

    /// <summary>The brew made with the applied suggestion, once it is saved (#89).</summary>
    public string? NextBrewId { get; init; }

    /// <summary>That brew's rating, when it has one. The change from <see cref="RatingBefore"/> says whether the suggestion helped.</summary>
    public int? RatingAfter { get; init; }
}
