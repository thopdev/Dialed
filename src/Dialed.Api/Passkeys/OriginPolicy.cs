using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;

namespace Dialed.Api.Passkeys;

/// <summary>Decides which origins may use passkeys and which relying party ID they get.</summary>
public sealed class OriginPolicy(IReadOnlyCollection<string> exact, IReadOnlyCollection<Regex> patterns, IOptions<PasskeyOptions> options)
{
    private readonly PasskeyOptions options = options.Value;

    /// <summary>Resolves the RP ID for an allowed origin; false when the origin is not allowed.</summary>
    public bool TryResolve(string? origin, out string rpId)
    {
        rpId = "";
        if (string.IsNullOrEmpty(origin)
            || !Uri.TryCreate(origin, UriKind.Absolute, out var uri)
            || !IsSecureContext(uri)
            || !Dialed.Api.Configuration.AllowedOrigins.IsAllowed(uri.GetLeftPart(UriPartial.Authority), exact, patterns))
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

    /// <summary>WebAuthn requires a secure context: https, or http only for localhost.</summary>
    private static bool IsSecureContext(Uri origin) => origin.Scheme == Uri.UriSchemeHttps || origin.IsLoopback;
}
