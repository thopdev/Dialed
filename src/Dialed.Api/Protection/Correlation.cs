using System.Diagnostics;

namespace Dialed.Api.Protection;

/// <summary>
/// A correlation id on every answer (<c>X-Correlation-Id</c>, and <c>correlationId</c> in error bodies) so a tester can quote it and
/// the matching trace and logs are found. It is the trace id of the request, so it also appears in Application Insights.
/// </summary>
public static class Correlation
{
    public const string Header = "X-Correlation-Id";

    public static string Of(HttpContext http) =>
        http.Items.TryGetValue(Header, out var id) && id is string known
            ? known
            : (string)(http.Items[Header] = Activity.Current?.TraceId.ToString() ?? http.TraceIdentifier);

    public static IApplicationBuilder UseCorrelationId(this IApplicationBuilder app) =>
        app.Use((context, next) =>
        {
            var id = Of(context);
            context.Response.OnStarting(() =>
            {
                context.Response.Headers[Header] = id;
                return Task.CompletedTask;
            });

            // Everything logged while handling the request carries the id.
            var logger = context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("Dialed.Request");
            using (logger.BeginScope(new Dictionary<string, object> { ["CorrelationId"] = id }))
            {
                return next();
            }
        });
}
