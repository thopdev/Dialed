using Dialed.Client.Telemetry;

namespace Dialed.Client.Platform;

/// <summary>Where error reports go. Web: the Application Insights JavaScript SDK when it is configured, otherwise nowhere (ADR-0012).</summary>
public interface IErrorSink
{
    /// <summary>Hands over an already scrubbed report. Must not throw.</summary>
    Task SendAsync(ErrorReport report);
}
