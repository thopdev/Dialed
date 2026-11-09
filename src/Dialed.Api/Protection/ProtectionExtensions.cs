using System.IdentityModel.Tokens.Jwt;
using System.Globalization;
using System.Security.Claims;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Dialed.Api.Protection;

public static class ProtectionExtensions
{
    /// <summary>ProblemDetails for errors, the options and the rate limiter (per user; stricter on <c>/auth</c> and <c>/ai</c>).</summary>
    public static IServiceCollection AddDialedProtection(this IServiceCollection services, IConfiguration configuration)
    {
        var options = configuration.GetSection(ProtectionOptions.Section).Get<ProtectionOptions>() ?? new ProtectionOptions();
        services.AddSingleton(options);
        services.AddProblemDetails();
        services.AddRateLimiter(limiter =>
        {
            limiter.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            limiter.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(http => Partition(http, options));
            limiter.OnRejected = async (context, cancellationToken) =>
            {
                if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
                {
                    context.HttpContext.Response.Headers.RetryAfter = ((int)Math.Ceiling(retryAfter.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
                }

                context.HttpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests;
                await context.HttpContext.Response.WriteAsJsonAsync(
                    new ProblemDetails { Status = StatusCodes.Status429TooManyRequests, Title = "Too many requests", Detail = "Slow down and try again in a moment." },
                    cancellationToken);
            };
        });
        return services;
    }

    /// <summary>Rejects oversized bodies before they are read. Place before authentication so the limit also covers sign-in.</summary>
    public static IApplicationBuilder UseRequestSizeLimit(this IApplicationBuilder app) =>
        app.Use(async (context, next) =>
        {
            var limit = context.RequestServices.GetRequiredService<ProtectionOptions>().MaxRequestBodyBytes;
            if (context.Request.ContentLength > limit)
            {
                context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
                await context.Response.WriteAsJsonAsync(new ProblemDetails
                {
                    Status = StatusCodes.Status413PayloadTooLarge,
                    Title = "Request too large",
                    Detail = $"The request body may be at most {limit} bytes.",
                });
                return;
            }

            // Also stops bodies without a length (chunked) once they pass the limit, where the server supports it.
            var feature = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
            if (feature is { IsReadOnly: false })
            {
                feature.MaxRequestBodySize = limit;
            }

            await next();
        });

    private static RateLimitPartition<string> Partition(HttpContext http, ProtectionOptions options)
    {
        var path = http.Request.Path;
        if (path == "/" || path.StartsWithSegments("/health") || path.StartsWithSegments("/alive"))
        {
            return RateLimitPartition.GetNoLimiter("open");
        }

        var address = http.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        var user = http.User.FindFirstValue(JwtRegisteredClaimNames.Sub);
        if (path.StartsWithSegments("/auth"))
        {
            return Window($"auth:{address}", options.AuthRequestsPerMinute);
        }

        if (path.StartsWithSegments("/friends"))
        {
            return Window($"friends:{user ?? address}", options.FriendsRequestsPerMinute);
        }

        if (path.StartsWithSegments("/ai"))
        {
            return Window($"ai:{user ?? address}", options.AiRequestsPerMinute);
        }

        return Window(user is null ? $"ip:{address}" : $"user:{user}", options.RequestsPerMinute);
    }

    private static RateLimitPartition<string> Window(string key, int permits) =>
        RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = permits,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
        });
}
