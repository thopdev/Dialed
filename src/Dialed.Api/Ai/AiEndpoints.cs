using System.Diagnostics;
using System.Security.Claims;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.IdentityModel.JsonWebTokens;
using Dialed.AI.DialIn;
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
        services.Configure<AiLimits>(configuration.GetSection("Ai"));
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<AiGate>();

        // The dial-in assistant answers with the offline rules until a Claude call for it has been verified against real brews (#86).
        services.TryAddSingleton<IDialInRefiner, RulesDialInRefiner>();

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

        group.MapPost("/label-scans", async (HttpRequest request, ClaimsPrincipal user, AiGate gate, ILabelExtractor extractor, ILoggerFactory logs, CancellationToken ct) =>
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

            var userId = user.FindFirstValue(JwtRegisteredClaimNames.Sub) ?? "";
            var entry = gate.TryEnter(userId, AiOperations.Label);
            if (entry.Status == AiGateStatus.Disabled)
            {
                return Results.Problem("AI is switched off for now. Enter the bean by hand.", statusCode: StatusCodes.Status503ServiceUnavailable, extensions: new Dictionary<string, object?> { ["code"] = "ai-disabled" });
            }

            if (entry.Status == AiGateStatus.QuotaExceeded)
            {
                request.HttpContext.Response.Headers.RetryAfter = ((int)Math.Ceiling(entry.RetryAfter.TotalSeconds)).ToString(System.Globalization.CultureInfo.InvariantCulture);
                return Results.Problem($"You have used all {entry.Limit} label scans for today. Enter the bean by hand, or try again tomorrow.", statusCode: StatusCodes.Status429TooManyRequests, extensions: new Dictionary<string, object?> { ["code"] = "ai-quota" });
            }

            var started = Stopwatch.GetTimestamp();
            try
            {
                var extraction = await extractor.ExtractAsync(buffer.ToArray(), ct);
                var elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                DialedTelemetry.AiTokens.Add(extraction.InputTokens, new KeyValuePair<string, object?>("operation", "label"), new KeyValuePair<string, object?>("direction", "input"));
                DialedTelemetry.AiTokens.Add(extraction.OutputTokens, new KeyValuePair<string, object?>("operation", "label"), new KeyValuePair<string, object?>("direction", "output"));
                DialedTelemetry.AiLatency.Record(elapsed, new KeyValuePair<string, object?>("operation", "label"));
                DialedTelemetry.AiEstimatedCost.Add((double)gate.EstimateCostUsd(extraction.InputTokens, extraction.OutputTokens), new KeyValuePair<string, object?>("operation", "label"));
                return Results.Ok(new LabelScanResponse(extraction.Draft, extraction.PromptVersion, extraction.ModelId));
            }
            catch (LabelExtractionException exception)
            {
                gate.Refund(userId, AiOperations.Label);
                logs.CreateLogger("Dialed.Ai").LogWarning(exception, "Label extraction failed");
                return Results.Problem("The label could not be read right now. Enter the bean by hand, or try again later.", statusCode: StatusCodes.Status503ServiceUnavailable);
            }
        });

        group.MapPost("/dial-in", async (DialInContext context, ClaimsPrincipal user, AiGate gate, IDialInRefiner refiner, ILoggerFactory logs, HttpContext http, CancellationToken ct) =>
        {
            if (Dialed.Domain.BrewMethods.BrewMethodCatalog.Default.Find(context.MethodCode ?? "") is null || context.Brew is null || context.Brew.DoseG <= 0m)
            {
                return Results.Problem("Send a known brew method and the dose of the brew.", statusCode: StatusCodes.Status400BadRequest);
            }

            var userId = user.FindFirstValue(JwtRegisteredClaimNames.Sub) ?? "";
            var entry = gate.TryEnter(userId, AiOperations.Suggestion);
            if (entry.Status == AiGateStatus.Disabled)
            {
                return Results.Problem("AI is switched off for now. The built-in advice still works.", statusCode: StatusCodes.Status503ServiceUnavailable, extensions: new Dictionary<string, object?> { ["code"] = "ai-disabled" });
            }

            if (entry.Status == AiGateStatus.QuotaExceeded)
            {
                http.Response.Headers.RetryAfter = ((int)Math.Ceiling(entry.RetryAfter.TotalSeconds)).ToString(System.Globalization.CultureInfo.InvariantCulture);
                return Results.Problem($"You have used all {entry.Limit} suggestions for today. The built-in advice still works.", statusCode: StatusCodes.Status429TooManyRequests, extensions: new Dictionary<string, object?> { ["code"] = "ai-quota" });
            }

            var started = Stopwatch.GetTimestamp();
            try
            {
                var trimmed = context with { Recent = context.Recent?.Take(DialInContext.MaxRecent).ToList() };
                var refinement = await refiner.RefineAsync(trimmed, ct);
                var operation = new KeyValuePair<string, object?>("operation", "dialin");
                DialedTelemetry.AiTokens.Add(refinement.InputTokens, operation, new KeyValuePair<string, object?>("direction", "input"));
                DialedTelemetry.AiTokens.Add(refinement.OutputTokens, operation, new KeyValuePair<string, object?>("direction", "output"));
                DialedTelemetry.AiLatency.Record(Stopwatch.GetElapsedTime(started).TotalMilliseconds, operation);
                DialedTelemetry.AiEstimatedCost.Add((double)gate.EstimateCostUsd(refinement.InputTokens, refinement.OutputTokens), operation);
                return Results.Ok(refinement);
            }
            catch (DialInRefinementException exception)
            {
                gate.Refund(userId, AiOperations.Suggestion);
                logs.CreateLogger("Dialed.Ai").LogWarning(exception, "Dial-in refinement failed");
                return Results.Problem("No suggestion right now. The built-in advice still works.", statusCode: StatusCodes.Status503ServiceUnavailable);
            }
        });

        return endpoints;
    }
}

public sealed record LabelScanResponse(BeanDraft Draft, string PromptVersion, string ModelId);
