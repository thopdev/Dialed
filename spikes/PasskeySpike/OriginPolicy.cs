using Microsoft.Extensions.Options;

namespace PasskeySpike;

/// <summary>Decides which origins may use passkeys and which relying party ID they get.</summary>
public sealed class OriginPolicy(IOptions<PasskeyOptions> options)
{
    private readonly PasskeyOptions options = options.Value;

    public bool IsAllowed(string? origin) => TryResolve(origin, out _);

    /// <summary>Resolves the RP ID for an allowed origin; false when the origin is not allowed.</summary>
    public bool TryResolve(string? origin, out string rpId)
    {
        rpId = "";
        if (string.IsNullOrEmpty(origin) || !Uri.TryCreate(origin, UriKind.Absolute, out var uri) || !IsListed(uri))
        {
            return false;
        }

        if (string.IsNullOrEmpty(options.RpId))
        {
            rpId = uri.Host;
            return true;
        }

        // A fixed RP ID must be the origin's host or a parent domain of it (WebAuthn "registrable domain suffix").
        if (uri.Host.Equals(options.RpId, StringComparison.OrdinalIgnoreCase)
            || uri.Host.EndsWith("." + options.RpId, StringComparison.OrdinalIgnoreCase))
        {
            rpId = options.RpId;
            return true;
        }

        return false;
    }

    private bool IsListed(Uri origin)
    {
        // WebAuthn requires a secure context: https, or http only for localhost.
        if (origin.Scheme != Uri.UriSchemeHttps && !origin.IsLoopback)
        {
            return false;
        }

        foreach (var entry in options.Origins)
        {
            if (!Uri.TryCreate(entry.Replace("*.", "wildcard.", StringComparison.Ordinal), UriKind.Absolute, out var allowed))
            {
                continue;
            }

            var sameSchemeAndPort = allowed.Scheme == origin.Scheme && allowed.Port == origin.Port;
            if (!sameSchemeAndPort)
            {
                continue;
            }

            if (entry.Contains("://*.", StringComparison.Ordinal))
            {
                var suffix = allowed.Host["wildcard".Length..]; // ".euw.devtunnels.ms"
                if (origin.Host.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            else if (allowed.Host.Equals(origin.Host, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
