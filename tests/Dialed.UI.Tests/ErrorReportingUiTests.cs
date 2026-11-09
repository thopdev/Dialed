using Bunit;
using Dialed.Client.Telemetry;
using Dialed.UI.Components;
using Dialed.UI.Layout;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace Dialed.UI.Tests;

public sealed class ErrorReportingUiTests : UiTestContext
{
    private sealed class Thrower : ComponentBase
    {
        protected override void OnInitialized() => throw new InvalidOperationException("the bean the person typed: Ethiopia Guji");
    }

    private IRenderedComponent<ReportingErrorBoundary> BrokenScreen() =>
        Render<ReportingErrorBoundary>(p => p
            .Add(b => b.ChildContent, (RenderFragment)(builder => builder.OpenComponent<Thrower>(0)))
            .Add(b => b.ErrorContent, (RenderFragment<Exception>)(_ => builder => builder.AddContent(0, "broken"))));

    [Fact]
    public void A_broken_screen_is_reported_without_what_the_person_typed()
    {
        var screen = BrokenScreen();

        screen.WaitForAssertion(() => ErrorSink.Reports.Count.ShouldBe(1));
        var report = ErrorSink.Reports.Single();
        report.Kind.ShouldBe("unhandled");
        report.Type.ShouldBe("System.InvalidOperationException");
        report.Message.ShouldBeNull();
        report.Stack.ShouldNotContain("Ethiopia");
        report.AppVersion.ShouldNotBeNullOrWhiteSpace();
        screen.Markup.ShouldContain("broken");
    }

    [Fact]
    public async Task Turning_reporting_off_in_settings_stops_the_reports_and_it_can_be_turned_on_again()
    {
        var setting = Render<ErrorReportingSetting>();
        setting.Find("#error-reporting-toggle").HasAttribute("checked").ShouldBeTrue();

        setting.Find("#error-reporting-toggle").Change(false);
        await Task.Yield();
        Services.GetRequiredService<IErrorReportingSettings>().Enabled.ShouldBeFalse();
        BrokenScreen();
        await Task.Delay(50, Xunit.TestContext.Current.CancellationToken);
        ErrorSink.Reports.ShouldBeEmpty();

        setting.Find("#error-reporting-toggle").Change(true);
        Services.GetRequiredService<IErrorReportingSettings>().Enabled.ShouldBeTrue();
    }
}
