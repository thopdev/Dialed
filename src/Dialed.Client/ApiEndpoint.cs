using System.Text.RegularExpressions;

namespace Dialed.Client;

/// <summary>Works out which base address the app should use to call the API.</summary>
public static partial class ApiEndpoint
{
    /// <summary>
    /// Returns the configured API address. When it points to localhost and the app itself is opened through a
    /// Microsoft dev tunnel (<c>https://{tunnel}-{port}.{region}.devtunnels.ms</c>, e.g. on an iPhone), the API is
    /// reached through the same tunnel on the API's local port instead. Without configuration the app's own
    /// address is used.
    /// </summary>
    public static Uri Resolve(string? configuredApiBaseAddress, Uri appBaseAddress)
    {
        ArgumentNullException.ThrowIfNull(appBaseAddress);

        if (string.IsNullOrWhiteSpace(configuredApiBaseAddress))
        {
            return appBaseAddress;
        }

        var configured = new Uri(configuredApiBaseAddress, UriKind.Absolute);
        var tunnel = DevTunnelHost().Match(appBaseAddress.Host);

        if (!configured.IsLoopback || !tunnel.Success)
        {
            return configured;
        }

        var host = $"{tunnel.Groups["tunnel"].Value}-{configured.Port}.{tunnel.Groups["domain"].Value}";
        return new UriBuilder(Uri.UriSchemeHttps, host) { Path = configured.AbsolutePath }.Uri;
    }

    [GeneratedRegex(@"^(?<tunnel>.+)-(?<port>\d+)\.(?<domain>[a-z0-9-]+\.devtunnels\.ms)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DevTunnelHost();
}
