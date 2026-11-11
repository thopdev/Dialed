using Dialed.Client.Platform;
using Dialed.Client.Recipes;
using Dialed.Client.Sync;
using Dialed.Domain.Model;

namespace Dialed.Client.DialIn;

/// <summary>The numbers of one brew. No names, notes or photos.</summary>
public sealed record DialInBrewInfo(decimal DoseG, decimal? WaterG, decimal? YieldG, decimal? GrindSetting, decimal? TempC, int? TimeS, int? Rating, Taste Taste)
{
    public static DialInBrewInfo From(Brew brew)
    {
        ArgumentNullException.ThrowIfNull(brew);
        return new(brew.DoseG, brew.WaterG, brew.YieldG, brew.GrindSetting, brew.TempC, brew.TimeS, brew.Rating, brew.Taste);
    }
}

/// <summary>What the dial-in assistant is told (#86): the brew, the latest earlier brews of the same bag (newest first), the bean and the grinder.</summary>
public sealed record DialInAssistantRequest(
    string MethodCode,
    DialInBrewInfo Brew,
    IReadOnlyList<DialInBrewInfo>? Recent = null,
    string? BeanOrigin = null,
    string? BeanProcess = null,
    string? RoastLevel = null,
    string? GrinderName = null,
    decimal? GrindMin = null,
    decimal? GrindMax = null,
    decimal? GrindStep = null)
{
    /// <summary>The server only reads this many; sending more is wasted bytes.</summary>
    public const int MaxRecent = 5;
}

/// <summary>One change as from → to. <see cref="Kind"/> is a <c>SuggestionKind</c> name.</summary>
public sealed record DialInAssistantChange(string Kind, decimal? From, decimal? To);

/// <summary>The assistant's answer: one or two changes, why (in English, or a <see cref="ReasonCode"/> the app words), and the prompt version.</summary>
public sealed record DialInAssistantAdvice(IReadOnlyList<DialInAssistantChange> Changes, string Why, string PromptVersion, string ModelId, string? ReasonCode = null);

public sealed class DialInAssistantException(AssistantProblem problem) : Exception(problem.ToString())
{
    public AssistantProblem Problem { get; } = problem;
}

public interface IDialInAssistant
{
    /// <summary>Asks the API to refine the dial-in advice for a brew. Throws <see cref="DialInAssistantException"/>.</summary>
    Task<DialInAssistantAdvice> AdviseAsync(DialInAssistantRequest request, CancellationToken cancellationToken = default);
}

/// <summary>Calls <c>POST /ai/dial-in</c>.</summary>
public sealed class DialInAssistant(HttpClient http, IAccessTokenProvider tokens, IConnectivity connectivity) : IDialInAssistant
{
    public Task<DialInAssistantAdvice> AdviseAsync(DialInAssistantRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return AssistantHttp.PostAsync<DialInAssistantRequest, DialInAssistantAdvice>(
            http, tokens, connectivity, "ai/dial-in", request, problem => new DialInAssistantException(problem), cancellationToken);
    }
}
