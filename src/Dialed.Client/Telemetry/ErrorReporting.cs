using System.Text.RegularExpressions;
using Dialed.Client.Platform;
using Dialed.Client.Sync;

namespace Dialed.Client.Telemetry;

/// <summary>
/// What is sent about an error (#91). Deliberately small: what kind of error, the exception type, where it happened (stack frames are
/// method names only) and which app version. Messages of unexpected exceptions are never sent, since they can hold what the person typed.
/// The platform adds the device type.
/// </summary>
public sealed record ErrorReport(string Kind, string Type, string? Message, string? Stack, string AppVersion, DateTimeOffset At);

/// <summary>Whether error reports may be sent. On by default; the person can turn it off in Settings and the choice is kept.</summary>
public interface IErrorReportingSettings
{
    bool Enabled { get; }

    /// <summary>Loads the stored choice. Safe to call more than once.</summary>
    Task InitializeAsync();

    Task SetEnabledAsync(bool enabled);

    event Action? Changed;
}

public sealed class InMemoryErrorReportingSettings : IErrorReportingSettings
{
    public bool Enabled { get; private set; } = true;

    public event Action? Changed;

    public Task InitializeAsync() => Task.CompletedTask;

    public Task SetEnabledAsync(bool enabled)
    {
        if (enabled != Enabled)
        {
            Enabled = enabled;
            Changed?.Invoke();
        }

        return Task.CompletedTask;
    }
}

/// <summary>Removes what could identify a person from text that is about to leave the device.</summary>
public static partial class ErrorScrubber
{
    public const int MaxStackLength = 2000;

    [GeneratedRegex(@"[A-Za-z0-9._%+\-]+@[A-Za-z0-9\-]+(\.[A-Za-z0-9\-]+)+")]
    private static partial Regex Email();

    [GeneratedRegex(@"eyJ[\w\-]+\.[\w\-]+\.[\w\-]+")]
    private static partial Regex Jwt();

    [GeneratedRegex(@"\b[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}\b")]
    private static partial Regex Guid();

    [GeneratedRegex(@"(https?://[^\s?#]+)[?#]\S*")]
    private static partial Regex UrlQuery();

    [GeneratedRegex(@"[A-Za-z0-9_\-]{32,}")]
    private static partial Regex LongToken();

    public static string Scrub(string? text, int maxLength)
    {
        if (string.IsNullOrEmpty(text))
        {
            return "";
        }

        var clean = UrlQuery().Replace(text, "$1");
        clean = Jwt().Replace(clean, "[token]");
        clean = Email().Replace(clean, "[email]");
        clean = Guid().Replace(clean, "[id]");
        clean = LongToken().Replace(clean, "[token]");
        return clean.Length > maxLength ? clean[..maxLength] : clean;
    }
}

/// <summary>
/// Reports unexpected exceptions and sync failures (#91), without personal data. Respects the opt-out, sends the same error at most once a
/// minute and at most <see cref="MaxPerSession"/> reports per launch, so a loop cannot flood the service.
/// </summary>
public sealed class ErrorReporter : IDisposable
{
    public const int MaxPerSession = 20;
    private static readonly TimeSpan SameErrorWindow = TimeSpan.FromMinutes(1);

    private readonly IErrorSink sink;
    private readonly IErrorReportingSettings settings;
    private readonly TimeProvider time;
    private readonly SyncCoordinator? sync;
    private readonly Lock gate = new();
    private readonly Dictionary<string, DateTimeOffset> recent = new(StringComparer.Ordinal);
    private int sent;

    public ErrorReporter(IErrorSink sink, IErrorReportingSettings settings, TimeProvider time, SyncCoordinator? sync = null)
    {
        this.sink = sink;
        this.settings = settings;
        this.time = time;
        this.sync = sync;
        if (sync is not null)
        {
            sync.SyncFailed += OnSyncFailed;
        }
    }

    public static string AppVersion { get; } =
        typeof(ErrorReporter).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion.Split('+')[0] ?? "dev";

    /// <summary>Reports an unexpected exception. Returns whether a report was handed to the sink.</summary>
    public Task<bool> ReportAsync(Exception exception, string kind = "unhandled")
    {
        ArgumentNullException.ThrowIfNull(exception);
        var stack = ErrorScrubber.Scrub(exception.StackTrace, ErrorScrubber.MaxStackLength);
        var topFrame = stack.Split('\n', 2)[0].Trim();
        return SendAsync(new ErrorReport(kind, exception.GetType().FullName ?? "Exception", null, stack, AppVersion, time.GetUtcNow()), $"{kind}|{exception.GetType().Name}|{topFrame}");
    }

    /// <summary>
    /// Reports an error from JavaScript (#91): an uncaught error, an unhandled promise rejection or a service worker error. Only the error
    /// name and the stack frames are kept; the message is dropped, also where a browser puts it on the first line of the stack.
    /// </summary>
    public Task<bool> ReportScriptErrorAsync(string kind, string? name, string? stack)
    {
        var safeKind = ScriptErrors.Kind(kind);
        var type = ScriptErrors.Name(name);
        var frames = ErrorScrubber.Scrub(ScriptErrors.Frames(stack), ErrorScrubber.MaxStackLength);
        var topFrame = frames.Split('\n', 2)[0].Trim();
        return SendAsync(
            new ErrorReport(safeKind, type, null, frames.Length == 0 ? null : frames, AppVersion, time.GetUtcNow()),
            $"{safeKind}|{type}|{topFrame}");
    }

    /// <summary>Reports that changes keep failing to sync (counts only, no content).</summary>
    public Task<bool> ReportSyncFailureAsync(SyncResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        var message = $"{result.Failed} change(s) could not be synced ({result.Pushed} sent, {result.Conflicts} conflicts)";
        return SendAsync(new ErrorReport("sync", "SyncFailure", message, null, AppVersion, time.GetUtcNow()), "sync|failure");
    }

    private void OnSyncFailed(SyncResult result) => _ = ReportSyncFailureAsync(result);

    private async Task<bool> SendAsync(ErrorReport report, string key)
    {
        try
        {
            await settings.InitializeAsync();
            if (!settings.Enabled)
            {
                return false;
            }

            lock (gate)
            {
                var now = time.GetUtcNow();
                if (sent >= MaxPerSession || (recent.TryGetValue(key, out var last) && now - last < SameErrorWindow))
                {
                    return false;
                }

                recent[key] = now;
                sent++;
            }

            await sink.SendAsync(report with { Message = report.Message is null ? null : ErrorScrubber.Scrub(report.Message, 300) });
            return true;
        }
        catch (Exception)
        {
            // Reporting must never be the reason something else breaks.
            return false;
        }
    }

    public void Dispose()
    {
        if (sync is not null)
        {
            sync.SyncFailed -= OnSyncFailed;
        }
    }
}

/// <summary>Turns what a browser says about a JavaScript error into something safe to report.</summary>
public static partial class ScriptErrors
{
    public const int MaxFrames = 20;
    private static readonly string[] Kinds = ["script", "rejection", "service-worker"];

    // Chrome/Edge: "    at fn (https://host/js/a.js:1:2)". Firefox/Safari: "fn@https://host/js/a.js:1:2".
    [GeneratedRegex(@"^\s*at\s+\S.*$|^[^\s@]*@\S+:\d+(:\d+)?$")]
    private static partial Regex Frame();

    [GeneratedRegex(@"https?://[^/\s)]+")]
    private static partial Regex Origin();

    // "a.js?v=123:12:5" keeps its line and column: only the query goes.
    [GeneratedRegex(@"[?#][^\s:)]*")]
    private static partial Regex Query();

    [GeneratedRegex(@"^[A-Za-z][A-Za-z0-9_$.]{0,63}$")]
    private static partial Regex SafeName();

    /// <summary>One of the known kinds; anything else becomes "script".</summary>
    public static string Kind(string? kind) => kind is not null && Kinds.Contains(kind, StringComparer.Ordinal) ? kind : "script";

    /// <summary>The error's name (TypeError, AbortError...) when it looks like one; a custom name with spaces or text becomes "Error".</summary>
    public static string Name(string? name) => name is not null && SafeName().IsMatch(name) ? name : "Error";

    /// <summary>
    /// Keeps only the lines that are stack frames, so the message (which can hold what the person typed) never leaves the device, and
    /// drops the origin from URLs so the same error groups the same way on every host.
    /// </summary>
    public static string Frames(string? stack)
    {
        if (string.IsNullOrWhiteSpace(stack))
        {
            return "";
        }

        var frames = stack.Split('\n')
            .Select(line => line.TrimEnd('\r'))
            .Where(line => Frame().IsMatch(line))
            .Take(MaxFrames)
            .Select(line => Origin().Replace(Query().Replace(line.Trim(), ""), ""));
        return string.Join('\n', frames);
    }
}

/// <summary>The default sink: reports go nowhere until a host brings one.</summary>
public sealed class NullErrorSink : IErrorSink
{
    public Task SendAsync(ErrorReport report) => Task.CompletedTask;
}
