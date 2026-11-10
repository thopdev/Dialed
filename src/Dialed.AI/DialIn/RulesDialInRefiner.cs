using Dialed.Domain.BrewMethods;
using Dialed.Domain.DialIn;
using Dialed.Domain.Equipment;
using Dialed.Domain.Model;

namespace Dialed.AI.DialIn;

/// <summary>
/// Answers with the offline rules of <see cref="DialInAdvisor"/> until a Claude endpoint is configured and its answers have been verified
/// against real brews. The shape is the one the model call will return, so the app and the endpoint do not change when it is switched on.
/// </summary>
public sealed class RulesDialInRefiner : IDialInRefiner
{
    public const string ModelId = "rules";

    /// <summary>The rules in <see cref="DialInAdvisor"/> have their own version; change it when they change.</summary>
    public const string PromptVersion = "rules-1";

    public Task<DialInRefinement> RefineAsync(DialInContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        var method = BrewMethodCatalog.Default.Find(context.MethodCode) ?? throw new DialInRefinementException("Unknown brew method.");
        var b = context.Brew;
        var brew = new Brew { BagId = "ai", MethodCode = context.MethodCode, DoseG = b.DoseG, WaterG = b.WaterG, YieldG = b.YieldG, GrindSetting = b.GrindSetting, TempC = b.TempC, TimeS = b.TimeS, Rating = b.Rating, Taste = b.Taste };
        GrindScale? scale = context.GrindMin is { } min && context.GrindMax is { } max && max > min ? GrindScale.Number(min, max, context.GrindStep ?? 0m) : null;

        var advice = DialInAdvisor.Advise(method, brew, scale);
        List<DialInChange> changes = [Change(advice.Primary)];
        if (advice.Secondary is { } secondary)
        {
            changes.Add(Change(secondary));
        }

        return Task.FromResult(new DialInRefinement(changes, advice.Primary.Reason.ToString(), PromptVersion, ModelId));
    }

    private static DialInChange Change(DialInSuggestion s) => new(s.Kind.ToString(), s.From, s.To);
}
