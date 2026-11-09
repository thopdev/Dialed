using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Dialed.AI.Labels;

public sealed class ClaudeOptions
{
    /// <summary>Base address of the Messages API, for example the Foundry resource endpoint. Empty: use the fake.</summary>
    public string Endpoint { get; set; } = "";

    public string ApiKey { get; set; } = "";

    /// <summary>Model (deployment) used for extraction. Configuration, not code (ADR-0006).</summary>
    public string ExtractionModel { get; set; } = "";

    public int MaxTokens { get; set; } = 1024;

    public int TimeoutSeconds { get; set; } = 30;

    public bool IsConfigured => !string.IsNullOrWhiteSpace(Endpoint) && !string.IsNullOrWhiteSpace(ApiKey) && !string.IsNullOrWhiteSpace(ExtractionModel);
}

/// <summary>Reads labels with Claude through the Messages API using forced tool use, so the answer is JSON that follows the schema.</summary>
public sealed class ClaudeLabelExtractor(HttpClient http, ClaudeOptions options) : ILabelExtractor
{
    public const string ApiVersion = "2023-06-01";

    public async Task<LabelExtraction> ExtractAsync(byte[] jpeg, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(jpeg);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(options.TimeoutSeconds));

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(options.Endpoint.TrimEnd('/') + "/v1/messages"))
                {
                    Content = JsonContent.Create(BuildRequest(jpeg, options.ExtractionModel, options.MaxTokens)),
                };
                request.Headers.Add("x-api-key", options.ApiKey);
                request.Headers.Add("anthropic-version", ApiVersion);

                using var response = await http.SendAsync(request, timeout.Token);
                if (response.StatusCode is HttpStatusCode.TooManyRequests or >= HttpStatusCode.InternalServerError && attempt < 2)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(500), timeout.Token);
                    continue;
                }

                if (!response.IsSuccessStatusCode)
                {
                    throw new LabelExtractionException($"The model answered {(int)response.StatusCode}.");
                }

                return Parse(await response.Content.ReadAsStringAsync(timeout.Token));
            }
            catch (HttpRequestException exception) when (attempt < 2)
            {
                _ = exception;
                await Task.Delay(TimeSpan.FromMilliseconds(500), timeout.Token);
            }
            catch (HttpRequestException exception)
            {
                throw new LabelExtractionException("The model could not be reached.", exception);
            }
            catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
            {
                throw new LabelExtractionException("The model took too long.", exception);
            }
        }
    }

    /// <summary>The Messages API request: one image, forced use of the recording tool.</summary>
    public static JsonObject BuildRequest(byte[] jpeg, string model, int maxTokens) => new()
    {
        ["model"] = model,
        ["max_tokens"] = maxTokens,
        ["system"] = LabelPrompt.System,
        ["tools"] = new JsonArray(new JsonObject
        {
            ["name"] = LabelPrompt.ToolName,
            ["description"] = "Record what the coffee label says.",
            ["input_schema"] = LabelPrompt.Schema(),
        }),
        ["tool_choice"] = new JsonObject { ["type"] = "tool", ["name"] = LabelPrompt.ToolName },
        ["messages"] = new JsonArray(new JsonObject
        {
            ["role"] = "user",
            ["content"] = new JsonArray(
                new JsonObject
                {
                    ["type"] = "image",
                    ["source"] = new JsonObject { ["type"] = "base64", ["media_type"] = "image/jpeg", ["data"] = Convert.ToBase64String(jpeg) },
                },
                new JsonObject { ["type"] = "text", ["text"] = "Read this coffee bag label." }),
        }),
    };

    /// <summary>Reads the model's answer and validates it: the tool call must be there; confidences are clamped to 0..1; unknown fields are ignored.</summary>
    public static LabelExtraction Parse(string json)
    {
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(json);
        }
        catch (JsonException exception)
        {
            throw new LabelExtractionException("The model answer is not JSON.", exception);
        }

        var input = (root?["content"] as JsonArray)?
            .OfType<JsonObject>()
            .FirstOrDefault(block => block["type"]?.GetValue<string>() == "tool_use" && block["name"]?.GetValue<string>() == LabelPrompt.ToolName)?["input"] as JsonObject
            ?? throw new LabelExtractionException("The model did not return the label tool call.");

        var fields = new Dictionary<string, DraftField>();
        foreach (var name in BeanDraft.FieldNames)
        {
            if (input[name] is not JsonObject field)
            {
                continue;
            }

            var value = field["value"] is JsonValue v && v.TryGetValue<string>(out var text) ? text.Trim() : null;
            var confidence = field["confidence"] is JsonValue c && c.TryGetValue<double>(out var number) ? Math.Clamp(number, 0, 1) : 0;
            fields[name] = string.IsNullOrEmpty(value) ? DraftField.None : new DraftField(value, confidence);
        }

        var usage = root?["usage"];
        return new LabelExtraction(
            BeanDraft.FromFields(fields),
            LabelPrompt.Version,
            root?["model"]?.GetValue<string>() ?? "unknown",
            usage?["input_tokens"]?.GetValue<int>() ?? 0,
            usage?["output_tokens"]?.GetValue<int>() ?? 0);
    }
}
