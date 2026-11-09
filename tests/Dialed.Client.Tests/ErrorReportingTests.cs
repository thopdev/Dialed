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
        ErrorScrubber.Scrub(new string('a', 50) + " " + new string('b', 50), 20).Length.ShouldBe(20);
        ErrorScrubber.Scrub(null, 20).ShouldBe("");
    }
}
