using System.Diagnostics;
using Dialed.AI.Labels;
using Dialed.Telemetry;

namespace Dialed.Api.Ai;

public static class AiEndpoints
{
    /// <summary>The largest photo accepted. The app resizes labels to about 2 megapixels first, which stays well below this.</summary>
    public const int MaxPhotoBytes = 1_000_000;

    public static IServiceCollection AddDialedAi(this IServiceCollection services, IConfiguration configuration)
    {
        var options = configuration.GetSection("Ai").Get<ClaudeOptions>() ?? new ClaudeOptions();
        services.AddSingleton(options);

        if (options.IsConfigured)
        {
            services.AddHttpClient<ILabelExtractor, ClaudeLabelExtractor>((http, _) => new ClaudeLabelExtractor(http, options));
        }
        else
        {
            services.AddSingleton<ILabelExtractor, FakeLabelExtractor>();
        }

        return services;
    }

    /// <summary>
    /// POST /ai/label-scans: a JPEG photo of a bean label in the request body, a <see cref="BeanDraft"/> back. Taking the photo bytes directly is
    /// the interim step: reading a stored photo by id comes with the photo upload queue (#68). Rate limited under the "ai" limit.
    /// </summary>
    public static IEndpointRouteBuilder MapDialedAi(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/ai").RequireAuthorization();

        group.MapPost("/label-scans", async (HttpRequest request, ILabelExtractor extractor, ILoggerFactory logs, CancellationToken ct) =>
        {
            if (!string.Equals(request.ContentType, "image/jpeg", StringComparison.OrdinalIgnoreCase))
            {
                return Results.Problem("Send the photo as image/jpeg.", statusCode: StatusCodes.Status415UnsupportedMediaType);
            }

            using var buffer = new MemoryStream();
            await request.Body.CopyToAsync(buffer, ct);
            if (buffer.Length is 0 or > MaxPhotoBytes)
            {
                return Results.Problem(buffer.Length == 0 ? "The photo is empty." : "The photo is too large.", statusCode: buffer.Length == 0 ? StatusCodes.Status400BadRequest : StatusCodes.Status413PayloadTooLarge);
            }

            var started = Stopwatch.GetTimestamp();
            try
            {
                var extraction = await extractor.ExtractAsync(buffer.ToArray(), ct);
                var elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                DialedTelemetry.AiTokens.Add(extraction.InputTokens, new KeyValuePair<string, object?>("operation", "label"), new KeyValuePair<string, object?>("direction", "input"));
                DialedTelemetry.AiTokens.Add(extraction.OutputTokens, new KeyValuePair<string, object?>("operation", "label"), new KeyValuePair<string, object?>("direction", "output"));
                DialedTelemetry.AiLatency.Record(elapsed, new KeyValuePair<string, object?>("operation", "label"));
                return Results.Ok(new LabelScanResponse(extraction.Draft, extraction.PromptVersion, extraction.ModelId));
            }
            catch (LabelExtractionException exception)
            {
                logs.CreateLogger("Dialed.Ai").LogWarning(exception, "Label extraction failed");
                return Results.Problem("The label could not be read right now. Enter the bean by hand, or try again later.", statusCode: StatusCodes.Status503ServiceUnavailable);
            }
        });

        return endpoints;
    }
}

public sealed record LabelScanResponse(BeanDraft Draft, string PromptVersion, string ModelId);
