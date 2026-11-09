using Dialed.Client.Platform;
using Dialed.Client.Telemetry;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;

namespace Dialed.Web.Platform;

/// <summary>Hands scrubbed reports to the page, which passes them to the Application Insights JavaScript SDK when one is configured (ADR-0012).</summary>
internal sealed class BrowserErrorSink(BrowserModule browser) : IErrorSink
{
    public async Task SendAsync(ErrorReport report)
    {
        try
        {
            await (await browser.GetAsync()).InvokeVoidAsync("reportError", report);
        }
        catch (Exception)
        {
            // Never a reason to break anything else.
        }
    }
}

/// <summary>Keeps the opt-out in localStorage so it survives restarts. If storage is blocked the default (on) applies for that launch.</summary>
internal sealed class BrowserErrorReportingSettings(BrowserModule browser) : IErrorReportingSettings
{
    private bool loaded;

    public bool Enabled { get; private set; } = true;

    public event Action? Changed;

    public async Task InitializeAsync()
    {
        if (loaded)
        {
            return;
        }

        loaded = true;
        try
        {
            Enabled = await (await browser.GetAsync()).InvokeAsync<string?>("errorReportingLoad") != "off";
        }
        catch (Exception)
        {
            Enabled = true;
        }

        Changed?.Invoke();
    }

    public async Task SetEnabledAsync(bool enabled)
    {
        loaded = true;
        Enabled = enabled;
        Changed?.Invoke();
        try
        {
            await (await browser.GetAsync()).InvokeVoidAsync("errorReportingSave", enabled ? "on" : "off");
        }
        catch (Exception)
        {
            // Applies for this launch only.
        }
    }
}

/// <summary>Reports exceptions that are logged as errors (unhandled exceptions in Blazor end up here), through the same scrubbing and opt-out.</summary>
internal sealed class ErrorReportingLoggerProvider(IServiceProvider services) : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => new Reporter(services);

    public void Dispose()
    {
    }

    private sealed class Reporter(IServiceProvider services) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Error;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (exception is null || !IsEnabled(logLevel))
            {
                return;
            }

            try
            {
                if (services.GetService(typeof(ErrorReporter)) is ErrorReporter reporter)
                {
                    _ = reporter.ReportAsync(exception);
                }
            }
            catch (Exception)
            {
                // Logging must not fail because reporting did.
            }
        }
    }
}
