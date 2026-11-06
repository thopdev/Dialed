using System.Text.RegularExpressions;

namespace Dialed.Api.Configuration;

/// <summary>
/// Origins the PWA is served from. Combines <c>App:Origins</c> (appsettings) with the "web" endpoints that Aspire
/// injects through service discovery (<c>services:web:https:0</c>, ...), which include the dev-tunnel URL
/// when the AppHost runs with a tunnel.
/// </summary>
internal static partial class AllowedOrigins
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

    /// <summary>
    /// Origin patterns from <c>App:Origins</c>: https entries whose host contains <c>*</c>, standing for one DNS label's characters
    /// (for example <c>https://swa-name-*.*.5.azurestaticapps.net</c> for the PR preview environments). The host must start with at least three literal
    /// characters in its first label and the part after the last <c>*</c> must still name a domain, so a pattern can never allow "any site".
    /// </summary>
    public static Regex[] Patterns(IConfiguration configuration)
    {
        var configured = configuration.GetSection(ConfiguredOriginsKey).Get<string[]>() ?? [];
        return [.. configured.Select(ToPattern).OfType<Regex>()];
    }

    /// <summary>True when the origin is one of the exact origins or matches a pattern.</summary>
    public static bool IsAllowed(string origin, IReadOnlyCollection<string> exact, IReadOnlyCollection<Regex> patterns) =>
        exact.Contains(origin, StringComparer.OrdinalIgnoreCase) || patterns.Any(pattern => pattern.IsMatch(origin));

    private static Regex? ToPattern(string? value)
    {
        if (value is null || !value.StartsWith("https://", StringComparison.OrdinalIgnoreCase) || !value.Contains('*'))
        {
            return null;
        }

        var host = value["https://".Length..].TrimEnd('/');
        var firstWildcard = host.IndexOf('*');
        if (!HostPattern().IsMatch(host)
            || firstWildcard < 3                                  // the app's own name must come first: "name-*"
            || host[..firstWildcard].Contains('.')                // ... within the first label
            || host[(host.LastIndexOf('*') + 1)..].Count(c => c == '.') < 2)
        {
            return null;
        }

        var body = string.Join("[a-z0-9-]+", host.Split('*').Select(Regex.Escape));
        return new Regex($"^https://{body}$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    }

    [GeneratedRegex("^[a-z0-9.*-]+$", RegexOptions.IgnoreCase)]
    private static partial Regex HostPattern();

    /// <summary>CORS origins are scheme + host + port, without path or trailing slash.</summary>
    private static string? ToOrigin(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp)
            ? uri.GetLeftPart(UriPartial.Authority)
            : null;
}
