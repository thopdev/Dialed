namespace Dialed.ArchitectureTests;

/// <summary>JavaScript error reporting (#91): the catcher runs before anything else on the page and never reads an error's message.</summary>
public sealed class ScriptErrorReportingTests
{
    private static readonly string Web = Path.Combine(FindRoot(), "src", "Dialed.Web", "wwwroot");

    private static string FindRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "src", "Dialed.Web")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }

    private static string Read(params string[] path) => File.ReadAllText(Path.Combine([Web, .. path]));

    [Fact]
    public void The_catcher_loads_before_Blazor_starts()
    {
        var index = Read("index.html");
        var catcher = index.IndexOf("<script src=\"js/errors.js\"></script>", StringComparison.Ordinal);

        catcher.ShouldBeGreaterThan(0);
        catcher.ShouldBeLessThan(index.IndexOf("blazor.webassembly", StringComparison.Ordinal));
    }

    [Fact]
    public void The_catcher_listens_for_errors_rejections_and_the_service_worker()
    {
        var catcher = Read("js", "errors.js");

        catcher.ShouldContain("addEventListener('error'");
        catcher.ShouldContain("addEventListener('unhandledrejection'");
        catcher.ShouldContain("'dialed-sw-error'");
        Read("service-worker.published.js").ShouldContain("type: 'dialed-sw-error'");
        Read("js", "platform.js").ShouldContain("'OnScriptError'");
    }

    [Fact]
    public void No_script_reads_the_message_of_an_error_it_reports()
    {
        // The message can hold what the person typed; only the name and stack frames are passed on (the .NET side drops the message line).
        Read("js", "errors.js").ShouldNotContain(".message");
        Read("service-worker.published.js").ShouldNotContain(".message");
    }
}
