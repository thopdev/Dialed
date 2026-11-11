using Dialed.Client.Platform;
using Dialed.Client.Sync;
using Dialed.Client.Telemetry;

namespace Dialed.Client.Tests;

/// <summary>Error reports (#91): no personal data, an opt-out, and limits that stop a loop from flooding the service.</summary>
public sealed class ErrorReportingTests
{
    private sealed class Sink : IErrorSink
    {
        public List<ErrorReport> Reports { get; } = [];

        public Task SendAsync(ErrorReport report)
        {
            Reports.Add(report);
            return Task.CompletedTask;
        }
    }

    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static (ErrorReporter Reporter, Sink Sink, InMemoryErrorReportingSettings Settings, Clock Clock) Make()
    {
        var sink = new Sink();
        var settings = new InMemoryErrorReportingSettings();
        var clock = new Clock(DateTimeOffset.Parse("2026-11-09T12:00:00Z", System.Globalization.CultureInfo.InvariantCulture));
        return (new ErrorReporter(sink, settings, clock), sink, settings, clock);
    }

    private static Exception Thrown(string message)
    {
        try
        {
            throw new InvalidOperationException(message);
        }
        catch (Exception exception)
        {
            return exception;
        }
    }

    [Fact]
    public async Task An_exception_is_reported_by_type_and_stack_but_never_with_its_message()
    {
        var (reporter, sink, _, _) = Make();

        (await reporter.ReportAsync(Thrown("thomas@example.com asked for Ethiopia Guji"))).ShouldBeTrue();

        var report = sink.Reports.Single();
        report.Type.ShouldBe("System.InvalidOperationException");
        report.Message.ShouldBeNull();
        report.Stack.ShouldNotBeNull();
        report.Stack.ShouldNotContain("Ethiopia");
        report.AppVersion.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Nothing_is_sent_when_the_person_opted_out()
    {
        var (reporter, sink, settings, _) = Make();
        await settings.SetEnabledAsync(false);

        (await reporter.ReportAsync(Thrown("x"))).ShouldBeFalse();
        (await reporter.ReportSyncFailureAsync(new SyncResult(SyncOutcome.Completed, Failed: 2))).ShouldBeFalse();

        sink.Reports.ShouldBeEmpty();
    }

    [Fact]
    public async Task The_same_error_is_reported_once_a_minute()
    {
        var (reporter, sink, _, clock) = Make();
        var error = Thrown("loop");

        (await reporter.ReportAsync(error)).ShouldBeTrue();
        (await reporter.ReportAsync(error)).ShouldBeFalse();
        clock.Now = clock.Now.AddMinutes(2);
        (await reporter.ReportAsync(error)).ShouldBeTrue();

        sink.Reports.Count.ShouldBe(2);
    }

    [Fact]
    public async Task A_launch_sends_at_most_twenty_reports()
    {
        var (reporter, sink, _, _) = Make();

        for (var i = 0; i < 50; i++)
        {
            await reporter.ReportSyncFailureAsync(new SyncResult(SyncOutcome.Completed, Failed: 1));
            await reporter.ReportAsync(new ArgumentException("different " + i), "kind" + i);
        }

        sink.Reports.Count.ShouldBe(ErrorReporter.MaxPerSession);
    }

    [Fact]
    public async Task A_sync_failure_report_holds_counts_only()
    {
        var (reporter, sink, _, _) = Make();

        await reporter.ReportSyncFailureAsync(new SyncResult(SyncOutcome.Completed, Pushed: 3, Conflicts: 1, Failed: 2));

        var report = sink.Reports.Single();
        report.Kind.ShouldBe("sync");
        report.Message.ShouldBe("2 change(s) could not be synced (3 sent, 1 conflicts)");
        report.Stack.ShouldBeNull();
    }

    [Theory]
    [InlineData("failed for thomas.hhopp@gmail.com now", "failed for [email] now")]
    [InlineData("GET https://api.example.com/sync/beans?token=abc&user=1 failed", "GET https://api.example.com/sync/beans failed")]
    [InlineData("user 0194e0c5-7b0a-7c2e-8f5a-1b2c3d4e5f60 missing", "user [id] missing")]
    [InlineData("Bearer eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxIn0.abc-def_ghi", "Bearer [token]")]
    [InlineData("key ABCDEFGHIJKLMNOPQRSTUVWXYZ012345 end", "key [token] end")]
    public void The_scrubber_removes_what_could_identify_someone(string input, string expected) =>
        ErrorScrubber.Scrub(input, 500).ShouldBe(expected);

    [Fact]
    public void The_scrubber_cuts_long_text()
    {
        ErrorScrubber.Scrub(string.Concat(Enumerable.Repeat("word ", 20)), 20).Length.ShouldBe(20);
        ErrorScrubber.Scrub(null, 20).ShouldBe("");
    }

    private const string ChromeStack =
        "TypeError: Cannot read properties of undefined (reading 'thomas@example.com typed this')\n" +
        "    at saveNote (https://dialed.example.com/js/platform.js?v=0194e0c5-7b0a-7c2e-8f5a-1b2c3d4e5f60:12:5)\n" +
        "    at async https://dialed.example.com/_framework/blazor.webassembly.js:1:300";

    private const string SafariStack =
        "saveNote@https://dialed.example.com/js/platform.js:12:5\n" +
        "@https://dialed.example.com/_framework/blazor.webassembly.js:1:300";

    [Fact]
    public async Task A_script_error_keeps_its_name_and_frames_but_not_its_message()
    {
        var (reporter, sink, _, _) = Make();

        (await reporter.ReportScriptErrorAsync("script", "TypeError", ChromeStack)).ShouldBeTrue();

        var report = sink.Reports.Single();
        report.Kind.ShouldBe("script");
        report.Type.ShouldBe("TypeError");
        report.Message.ShouldBeNull();
        report.Stack.ShouldNotBeNull();
        report.Stack.ShouldBe("at saveNote (/js/platform.js:12:5)\nat async /_framework/blazor.webassembly.js:1:300");
        report.Stack.ShouldNotContain("typed this");
        report.Stack.ShouldNotContain("@example.com");
        report.Stack.ShouldNotContain("dialed.example.com");
    }

    [Fact]
    public void Firefox_and_Safari_frames_are_kept_too() =>
        ScriptErrors.Frames(SafariStack).ShouldBe("saveNote@/js/platform.js:12:5\n@/_framework/blazor.webassembly.js:1:300");

    [Theory]
    [InlineData("TypeError", "TypeError")]
    [InlineData("AbortError", "AbortError")]
    [InlineData("Bad input from thomas@example.com", "Error")]
    [InlineData("", "Error")]
    [InlineData(null, "Error")]
    public void A_script_error_name_that_could_hold_text_becomes_Error(string? name, string expected) =>
        ScriptErrors.Name(name).ShouldBe(expected);

    [Fact]
    public async Task A_rejection_with_a_plain_value_is_reported_without_a_stack()
    {
        var (reporter, sink, _, _) = Make();

        (await reporter.ReportScriptErrorAsync("rejection", "UnhandledRejection", "")).ShouldBeTrue();
        (await reporter.ReportScriptErrorAsync("anything else", "Error", null)).ShouldBeTrue();

        sink.Reports[0].Kind.ShouldBe("rejection");
        sink.Reports[0].Stack.ShouldBeNull();
        sink.Reports[1].Kind.ShouldBe("script");
    }

    [Fact]
    public async Task Script_errors_follow_the_opt_out_and_the_same_error_limit()
    {
        var (reporter, sink, settings, clock) = Make();

        (await reporter.ReportScriptErrorAsync("service-worker", "TypeError", ChromeStack)).ShouldBeTrue();
        (await reporter.ReportScriptErrorAsync("service-worker", "TypeError", ChromeStack.Replace("typed this", "typed that", StringComparison.Ordinal))).ShouldBeFalse();
        clock.Now = clock.Now.AddMinutes(2);
        await settings.SetEnabledAsync(false);
        (await reporter.ReportScriptErrorAsync("service-worker", "TypeError", ChromeStack)).ShouldBeFalse();

        sink.Reports.Count.ShouldBe(1);
    }
}
