using System.Text.Json;

namespace Dialed.ArchitectureTests;

/// <summary>The PWA's Content-Security-Policy is enforced (#196) and stays closed: no eval, no foreign scripts, no plugins, no framing.</summary>
public sealed class ContentSecurityPolicyTests
{
    private static readonly string Config = Path.Combine(FindRoot(), "src", "Dialed.Web", "wwwroot", "staticwebapp.config.json");

    private static string FindRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "src", "Dialed.Web")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }

    private static Dictionary<string, string> Headers()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Config));
        return document.RootElement.GetProperty("globalHeaders").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString()!);
    }

    private static Dictionary<string, string[]> Directives(string policy) =>
        policy.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(part => part.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .ToDictionary(parts => parts[0], parts => parts[1..]);

    [Fact]
    public void The_policy_is_enforced_not_report_only()
    {
        var headers = Headers();

        headers.ShouldContainKey("Content-Security-Policy");
        headers.ShouldNotContainKey("Content-Security-Policy-Report-Only");
    }

    [Fact]
    public void Scripts_come_only_from_the_app_and_never_from_eval()
    {
        var script = Directives(Headers()["Content-Security-Policy"])["script-src"];

        script.ShouldContain("'self'");
        script.ShouldContain("'wasm-unsafe-eval'");
        script.ShouldNotContain("'unsafe-eval'");
        script.ShouldNotContain("*");
        script.ShouldAllBe(source => source.StartsWith('\'') || source == "'self'");
    }

    [Fact]
    public void Plugins_framing_and_foreign_bases_are_closed()
    {
        var policy = Directives(Headers()["Content-Security-Policy"]);

        policy["default-src"].ShouldBe(["'self'"]);
        policy["object-src"].ShouldBe(["'none'"]);
        policy["base-uri"].ShouldBe(["'self'"]);
        policy["frame-ancestors"].ShouldBe(["'none'"]);
        policy["form-action"].ShouldBe(["'self'"]);
    }

    [Fact]
    public void Requests_may_only_go_to_the_app_the_api_and_photo_storage()
    {
        var connect = Directives(Headers()["Content-Security-Policy"])["connect-src"];

        connect.ShouldAllBe(source => source == "'self'" || source.StartsWith("https://", StringComparison.Ordinal));
        connect.ShouldNotContain("*");
        connect.ShouldNotContain("https:");
    }
}
