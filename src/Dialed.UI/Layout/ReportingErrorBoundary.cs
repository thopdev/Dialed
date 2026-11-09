using Dialed.Client.Telemetry;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;

namespace Dialed.UI.Layout;

/// <summary>The error boundary of the shell: besides showing the error panel it reports the exception (scrubbed, and only when the person allows it).</summary>
public sealed class ReportingErrorBoundary : ErrorBoundary
{
    [Inject]
    private ErrorReporter Reporter { get; set; } = default!;

    protected override async Task OnErrorAsync(Exception exception)
    {
        await Reporter.ReportAsync(exception);
        await base.OnErrorAsync(exception);
    }
}
