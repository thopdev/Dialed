namespace PasskeySpike;

/// <summary>Configuration section <c>Passkeys</c>.</summary>
public sealed class PasskeyOptions
{
    /// <summary>
    /// Origins allowed to run a ceremony, e.g. <c>https://localhost:7200</c>. A leading <c>*.</c> in the host
    /// allows any subdomain, e.g. <c>https://*.euw.devtunnels.ms</c>.
    /// </summary>
    public List<string> Origins { get; set; } = [];

    /// <summary>
    /// Fixed relying party ID (e.g. the production domain). When empty, the RP ID is the host of the calling origin,
    /// which is what localhost, dev tunnels and *.azurestaticapps.net (public suffix) need.
    /// </summary>
    public string? RpId { get; set; }

    /// <summary>How long the client has to finish a ceremony.</summary>
    public TimeSpan CeremonyLifetime { get; set; } = TimeSpan.FromMinutes(5);
}

/// <summary>Configuration section <c>Jwt</c>.</summary>
public sealed class JwtOptions
{
    public string Issuer { get; set; } = "dialed-spike";
    public string Audience { get; set; } = "dialed-spike";

    /// <summary>Symmetric signing key (at least 32 bytes). Key Vault in Azure.</summary>
    public string SigningKey { get; set; } = "";

    public TimeSpan AccessTokenLifetime { get; set; } = TimeSpan.FromMinutes(15);
}
