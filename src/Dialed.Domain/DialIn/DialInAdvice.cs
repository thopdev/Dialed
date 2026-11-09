namespace Dialed.Domain.DialIn;

/// <summary>What to change for the next brew.</summary>
public enum SuggestionKind
{
    GrindFiner,
    GrindCoarser,
    IncreaseDose,
    DecreaseDose,

    /// <summary>More liquid for the same coffee: a longer ratio (espresso: more yield, filter: more water).</summary>
    LongerRatio,

    /// <summary>Less liquid for the same coffee: a shorter ratio.</summary>
    ShorterRatio,
    HotterWater,
    CoolerWater,
    Keep,
}

/// <summary>Why a change is suggested; the app turns the pair of kind and reason into a sentence in the user's language.</summary>
public enum SuggestionReason
{
    Sour,
    Bitter,
    Weak,
    Strong,
    Thin,
    Heavy,
    Flat,
    ShotTooFast,
    ShotTooSlow,
    Balanced,
}

/// <summary>
/// One change. <see cref="From"/> and <see cref="To"/> are the current and suggested value of what changes (grind setting in the
/// grinder's own units, grams, millilitres or degrees Celsius); <c>null</c> when it cannot be stated (no grinder scale, nothing logged).
/// </summary>
public sealed record DialInSuggestion(SuggestionKind Kind, SuggestionReason Reason, decimal? From = null, decimal? To = null);

/// <summary>One main suggestion and, when there is a second thing worth trying, a backup. Both <c>null</c> never happens: a balanced cup gets <see cref="SuggestionKind.Keep"/>.</summary>
public sealed record DialInAdvice(DialInSuggestion Primary, DialInSuggestion? Secondary);
