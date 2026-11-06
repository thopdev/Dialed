namespace Dialed.Client;

/// <summary>
/// What the UI needs to know about the host it runs in. Implemented by the host (Dialed.Web today,
/// a MAUI Blazor Hybrid host later) so that UI code stays free of host-specific APIs.
/// </summary>
public interface IAppEnvironment
{
    /// <summary>True for development builds (component gallery, diagnostics); false in production.</summary>
    bool IsDevelopment { get; }
}
