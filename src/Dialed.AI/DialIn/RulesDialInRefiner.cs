using Dialed.Domain.BrewMethods;
using Dialed.Domain.DialIn;
using Dialed.Domain.Equipment;
using Dialed.Domain.Model;

namespace Dialed.AI.DialIn;

/// <summary>
/// Answers with the offline rules of <see cref="DialInAdvisor"/> until a Claude endpoint is configured and its answers have been verified
/// against real brews. It adds one thing the rules on the device do not do: when the latest earlier brew of the bag already made the
/// first change without a better rating, it leads with the other one. The answer is deterministic and has the shape the model call will
/// return, so the app and the endpoint do not change when it is switched on.
/// </summary>
public sealed class RulesDialInRefiner : IDialInRefiner
{
    public const string ModelId = "rules";

    /// <summary>The version of these rules, recorded with accepted and dismissed suggestions (#89).</summary>
    public const string PromptVersion = DialInAdvisor.RulesVersion + "+recent";

    /// <summary>The <see cref="DialInRefinement.ReasonCode"/> when the first change was skipped because it was already tried.</summary>
    public const string AlreadyTriedCode = "AlreadyTried";

    public Task<DialInRefinement> RefineAsync(DialInContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        var method = BrewMethodCatalog.Default.Find(context.MethodCode) ?? throw new DialInRefinementException("Unknown brew method.");
        var brew = ToBrew(context.MethodCode, context.Brew);
        GrindScale? scale = context.GrindMin is { } min && context.GrindMax is { } max && max > min ? GrindScale.Number(min, max, context.GrindStep ?? 0m) : null;

        var advice = DialInAdvisor.Advise(method, brew, scale);
        var first = advice.Primary;
        var second = advice.Secondary;
        var tried = false;
        if (second is not null && context.Recent is [var latest, ..] && DialInHistory.AlreadyTried(first.Kind, method, brew, ToBrew(context.MethodCode, latest)))
        {
            (first, second) = (second, null);
            tried = true;
        }

        List<DialInChange> changes = [Change(first)];
        if (second is not null)
        {
            changes.Add(Change(second));
        }

        var why = tried
            ? $"Your last change in this direction did not give a better cup, so try this instead. {Sentence(first.Reason)}"
            : Sentence(first.Reason);
        return Task.FromResult(new DialInRefinement(changes, why, PromptVersion, ModelId, ReasonCode: tried ? AlreadyTriedCode : first.Reason.ToString()));
    }

    private static Brew ToBrew(string methodCode, DialInBrewSummary b) =>
        new() { BagId = "ai", MethodCode = methodCode, DoseG = b.DoseG, WaterG = b.WaterG, YieldG = b.YieldG, GrindSetting = b.GrindSetting, TempC = b.TempC, TimeS = b.TimeS, Rating = b.Rating, Taste = b.Taste ?? new Taste() };

    private static DialInChange Change(DialInSuggestion s) => new(s.Kind.ToString(), s.From, s.To);

    private static string Sentence(SuggestionReason reason) => reason switch
    {
        SuggestionReason.Sour => "It tasted sour, which usually means too little extraction.",
        SuggestionReason.Bitter => "It tasted bitter, which usually means too much extraction.",
        SuggestionReason.Weak => "It was weak, so put more coffee in the cup.",
        SuggestionReason.Strong => "It was strong, so let more water through the same coffee.",
        SuggestionReason.Thin => "The body was thin.",
        SuggestionReason.Heavy => "The body was heavy.",
        SuggestionReason.Flat => "It lacked sweetness, which often means too little extraction.",
        SuggestionReason.ShotTooFast => "The shot ran fast.",
        SuggestionReason.ShotTooSlow => "The shot ran slow.",
        _ => "The cup was balanced. Change one thing at a time if you want to explore.",
    };
}
