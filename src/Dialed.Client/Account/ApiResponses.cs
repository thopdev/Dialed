using System.Text.Json;

namespace Dialed.Client.Account;

/// <summary>Turns API error answers into exceptions: 4xx carry a reason that is safe to show, everything else is "unavailable".</summary>
internal static class ApiResponses
{
    public static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var status = (int)response.StatusCode;
        if (status is >= 400 and < 500)
        {
            // ProblemDetails from the API carries a reason that is safe to show.
            string? detail = null;
            try
            {
                using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
                if (document.RootElement.TryGetProperty("detail", out var d))
                {
                    detail = d.GetString();
                }
            }
            catch (JsonException)
            {
            }

            throw new AccountRefusedException(status, detail ?? $"The server refused the request ({status}).");
        }

        throw new AccountApiException($"The server answered {status}.");
    }
}
