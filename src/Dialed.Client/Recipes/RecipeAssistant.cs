using Dialed.Client.Sync;
using Dialed.Client.Platform;

namespace Dialed.Client.Recipes;

/// <summary>The recipe of a similar bean the suggestion starts from (#87).</summary>
public sealed record SimilarRecipeInfo(decimal? DoseG, decimal? WaterG, decimal? YieldG, decimal? GrindSetting, decimal? TempC, int? TimeS, double? AverageRating);

/// <summary>What the assistant is told: attributes of the bean, the grinder's scale and the person's best similar recipe. No names, notes or photos.</summary>
public sealed record RecipeAssistantRequest(
    string MethodCode,
    string? BeanOrigin = null,
    string? BeanProcess = null,
    string? RoastLevel = null,
    string? Varieties = null,
    int? DaysSinceRoast = null,
    string? GrinderName = null,
    decimal? GrindMin = null,
    decimal? GrindMax = null,
    decimal? GrindStep = null,
    SimilarRecipeInfo? Similar = null);

/// <summary>Why a parameter has its value: a <see cref="Code"/> the app words in the user's language, or the model's own <see cref="Text"/>.</summary>
public sealed record SuggestionReason(string Parameter, string Code, string? Text);

/// <summary>A suggested recipe with a reason per parameter and the prompt version and model that produced it.</summary>
public sealed record SuggestedRecipe(
    decimal DoseG,
    decimal? WaterG,
    decimal? YieldG,
    decimal? GrindSetting,
    decimal? TempC,
    int? TimeS,
    IReadOnlyList<SuggestionReason> Reasons,
    string PromptVersion,
    string ModelId);

public enum AssistantProblem
{
    /// <summary>Not signed in: suggestions run on the server.</summary>
    SignedOut,

    /// <summary>No connection.</summary>
    Offline,

    /// <summary>Daily quota used up (429).</summary>
    QuotaUsed,

    /// <summary>The kill switch is on (503, code ai-disabled).</summary>
    Disabled,

    /// <summary>The server or the model failed.</summary>
    Unavailable,
}

public sealed class RecipeAssistantException(AssistantProblem problem) : Exception(problem.ToString())
{
    public AssistantProblem Problem { get; } = problem;
}

public interface IRecipeAssistant
{
    /// <summary>Asks the API for a starting recipe. Throws <see cref="RecipeAssistantException"/>.</summary>
    Task<SuggestedRecipe> SuggestAsync(RecipeAssistantRequest request, CancellationToken cancellationToken = default);
}

/// <summary>Calls <c>POST /ai/recipe-suggestions</c>.</summary>
public sealed class RecipeAssistant(HttpClient http, IAccessTokenProvider tokens, IConnectivity connectivity) : IRecipeAssistant
{
    public Task<SuggestedRecipe> SuggestAsync(RecipeAssistantRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return AssistantHttp.PostAsync<RecipeAssistantRequest, SuggestedRecipe>(
            http, tokens, connectivity, "ai/recipe-suggestions", request, problem => new RecipeAssistantException(problem), cancellationToken);
    }
}
