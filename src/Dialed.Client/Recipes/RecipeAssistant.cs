using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
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
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<SuggestedRecipe> SuggestAsync(RecipeAssistantRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var token = await tokens.GetAccessTokenAsync(cancellationToken) ?? throw new RecipeAssistantException(AssistantProblem.SignedOut);
        if (!connectivity.IsOnline)
        {
            throw new RecipeAssistantException(AssistantProblem.Offline);
        }

        using var message = new HttpRequestMessage(HttpMethod.Post, "ai/recipe-suggestions") { Content = JsonContent.Create(request, options: Json) };
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(message, cancellationToken);
        }
        catch (HttpRequestException)
        {
            throw new RecipeAssistantException(AssistantProblem.Offline);
        }

        using (response)
        {
            if (response.IsSuccessStatusCode)
            {
                try
                {
                    return await response.Content.ReadFromJsonAsync<SuggestedRecipe>(Json, cancellationToken) ?? throw new RecipeAssistantException(AssistantProblem.Unavailable);
                }
                catch (JsonException)
                {
                    throw new RecipeAssistantException(AssistantProblem.Unavailable);
                }
            }

            var text = await response.Content.ReadAsStringAsync(cancellationToken);
            throw response.StatusCode switch
            {
                HttpStatusCode.Unauthorized => new RecipeAssistantException(AssistantProblem.SignedOut),
                HttpStatusCode.TooManyRequests => new RecipeAssistantException(AssistantProblem.QuotaUsed),
                HttpStatusCode.ServiceUnavailable when text.Contains("ai-disabled", StringComparison.Ordinal) => new RecipeAssistantException(AssistantProblem.Disabled),
                _ => new RecipeAssistantException(AssistantProblem.Unavailable),
            };
        }
    }
}
