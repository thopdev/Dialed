namespace Dialed.AI.Recipes;

/// <summary>The numbers of a recipe the person already has for a similar bean, to start from.</summary>
public sealed record SimilarRecipe(decimal? DoseG, decimal? WaterG, decimal? YieldG, decimal? GrindSetting, decimal? TempC, int? TimeS, double? AverageRating);

/// <summary>
/// What the recipe assistant is told (#87): the method, the bean's attributes, how long ago it was roasted, the grinder's scale and the person's
/// best recipe for a similar bean. Attributes only: no names of people, no notes or photos.
/// </summary>
public sealed record RecipeSuggestionContext(
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
    SimilarRecipe? Similar = null);

/// <summary>
/// Why a parameter has its value. <see cref="Code"/> is one of <see cref="RecipePrompt.ReasonCodes"/> and the app turns it into a sentence in the
/// user's language; <see cref="Text"/> is the model's own sentence when it wrote one.
/// </summary>
public sealed record ParameterReason(string Parameter, string Code, string? Text = null);

/// <summary>A recipe for the method with a reason per parameter, and the prompt version and model that produced it. Parameters the method does not use are <c>null</c>.</summary>
public sealed record RecipeSuggestionResult(
    decimal DoseG,
    decimal? WaterG,
    decimal? YieldG,
    decimal? GrindSetting,
    decimal? TempC,
    int? TimeS,
    IReadOnlyList<ParameterReason> Reasons,
    string PromptVersion,
    string ModelId,
    int InputTokens = 0,
    int OutputTokens = 0);

public interface IRecipeSuggester
{
    Task<RecipeSuggestionResult> SuggestAsync(RecipeSuggestionContext context, CancellationToken cancellationToken = default);
}

/// <summary>The model call failed or its answer did not follow the schema.</summary>
public sealed class RecipeSuggestionException(string message, Exception? inner = null) : Exception(message, inner);
