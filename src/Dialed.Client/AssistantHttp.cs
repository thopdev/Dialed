using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Dialed.Client.Platform;
using Dialed.Client.Recipes;
using Dialed.Client.Sync;

namespace Dialed.Client;

/// <summary>The one way the app calls an <c>/ai/...</c> endpoint: signed in, online, and every failure turned into an <see cref="AssistantProblem"/>.</summary>
internal static class AssistantHttp
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static async Task<TResponse> PostAsync<TRequest, TResponse>(
        HttpClient http, IAccessTokenProvider tokens, IConnectivity connectivity, string path, TRequest request,
        Func<AssistantProblem, Exception> fail, CancellationToken cancellationToken)
        where TResponse : class
    {
        var token = await tokens.GetAccessTokenAsync(cancellationToken) ?? throw fail(AssistantProblem.SignedOut);
        if (!connectivity.IsOnline)
        {
            throw fail(AssistantProblem.Offline);
        }

        using var message = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(request, options: Json) };
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(message, cancellationToken);
        }
        catch (HttpRequestException)
        {
            throw fail(AssistantProblem.Offline);
        }

        using (response)
        {
            if (response.IsSuccessStatusCode)
            {
                try
                {
                    return await response.Content.ReadFromJsonAsync<TResponse>(Json, cancellationToken) ?? throw fail(AssistantProblem.Unavailable);
                }
                catch (JsonException)
                {
                    throw fail(AssistantProblem.Unavailable);
                }
            }

            var text = await response.Content.ReadAsStringAsync(cancellationToken);
            throw fail(response.StatusCode switch
            {
                HttpStatusCode.Unauthorized => AssistantProblem.SignedOut,
                HttpStatusCode.TooManyRequests => AssistantProblem.QuotaUsed,
                HttpStatusCode.ServiceUnavailable when text.Contains("ai-disabled", StringComparison.Ordinal) => AssistantProblem.Disabled,
                _ => AssistantProblem.Unavailable,
            });
        }
    }
}
