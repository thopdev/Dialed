namespace Dialed.Api.Passkeys;

/// <summary>Configuration section <c>Passkeys</c>. The origins that may run a ceremony are the app origins (<c>App:Origins</c>, see AllowedOrigins).</summary>
public sealed class PasskeyOptions
{
    public const string SectionName = "Passkeys";

    /// <summary>
    /// Fixed relying party ID (e.g. the production domain). When empty, the RP ID is the host of the calling origin,
    /// which is what localhost, dev tunnels and *.azurestaticapps.net (public suffix) need.
    /// </summary>
    public string? RpId { get; set; }

    /// <summary>How long the client has to finish a ceremony.</summary>
    public TimeSpan CeremonyLifetime { get; set; } = TimeSpan.FromMinutes(5);
}
