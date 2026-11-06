namespace Dialed.Api.Configuration;

/// <summary>
/// Origins the PWA is served from. Combines <c>App:Origins</c> (appsettings) with the "web" endpoints that Aspire
/// injects through service discovery (<c>services:web:https:0</c>, ...), which include the dev-tunnel URL
/// when the AppHost runs with a tunnel.
/// </summary>
internal static class AllowedOrigins
{
    public const string ConfiguredOriginsKey = "App:Origins";
    public const string WebServiceDiscoveryKey = "services:web";

    public static string[] From(IConfiguration configuration)
    {
        var configured = configuration.GetSection(ConfiguredOriginsKey).Get<string[]>() ?? [];

        var discovered = configuration.GetSection(WebServiceDiscoveryKey)
            .GetChildren()                                  // endpoint names: https, http
            .SelectMany(endpoint => endpoint.GetChildren()) // 0, 1, ...
            .Select(entry => entry.Value);

        return [.. configured
            .Concat(discovered)
            .Select(ToOrigin)
            .OfType<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>CORS origins are scheme + host + port, without path or trailing slash.</summary>
    private static string? ToOrigin(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp)
            ? uri.GetLeftPart(UriPartial.Authority)
            : null;
}
