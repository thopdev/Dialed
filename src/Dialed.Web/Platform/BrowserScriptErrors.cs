using Dialed.Client.Telemetry;
using Microsoft.JSInterop;

namespace Dialed.Web.Platform;

/// <summary>
/// Receives JavaScript errors from js/errors.js (uncaught errors, unhandled promise rejections, service worker errors) and hands them to
/// <see cref="ErrorReporter"/>, so they get the same opt-out, scrubbing and limits as .NET errors (#91).
/// </summary>
internal sealed class BrowserScriptErrors(BrowserModule browser, ErrorReporter reporter)
{
    /// <summary>Starts forwarding, including what was caught before the app was ready. Called once at startup.</summary>
    public async Task InitializeAsync() =>
        await (await browser.GetAsync()).InvokeAsync<bool>("watchScriptErrors", DotNetObjectReference.Create(this));

    [JSInvokable]
    public Task OnScriptError(string? kind, string? name, string? stack) => reporter.ReportScriptErrorAsync(kind ?? "script", name, stack);
}
