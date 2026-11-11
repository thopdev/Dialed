using Dialed.AI.Labels;
using Dialed.LabelEval;

namespace Dialed.AI.Tests;

/// <summary>The command line that CI and people run; never reaches a real model here.</summary>
public sealed class LabelEvalCliTests : IDisposable
{
    private readonly string _temp = Directory.CreateTempSubdirectory("dialed-label-eval-").FullName;
    private readonly StringWriter _out = new();
    private readonly StringWriter _err = new();

    public void Dispose()
    {
        Directory.Delete(_temp, recursive: true);
        _out.Dispose();
        _err.Dispose();
    }

    private static readonly Dictionary<string, string> Configured = new()
    {
        ["Ai__Endpoint"] = "https://example.invalid",
        ["Ai__ApiKey"] = "test-key",
        ["Ai__ExtractionModel"] = "claude-test",
        ["Ai__TimeoutSeconds"] = "90",
    };

    private Task<int> Run(IReadOnlyDictionary<string, string>? environment = null, Func<ClaudeOptions, ILabelExtractor>? claude = null, params string[] args) =>
        LabelEvalCli.RunAsync(
            ["--golden", GoldenSetTests.GoldenFolder, .. args],
            _out,
            _err,
            name => environment is not null && environment.TryGetValue(name, out var value) ? value : null,
            claude ?? (_ => throw new InvalidOperationException("Claude must not be called.")),
            TestContext.Current.CancellationToken);

    [Fact]
    public async Task Without_configuration_it_runs_the_fake_and_writes_both_reports()
    {
        var markdown = Path.Combine(_temp, "report.md");
        var json = Path.Combine(_temp, "report.json");

        var code = await Run(args: ["--markdown", markdown, "--json", json]);

        code.ShouldBe(LabelEvalCli.Ok, _err.ToString());
        _out.ToString().ShouldContain("against the fake extractor");
        _out.ToString().ShouldContain("synthetic-fake-match");
        File.ReadAllText(markdown).ShouldContain("Field accuracy");
        File.ReadAllText(json).ShouldContain("\"promptVersion\"");
    }

    [Fact]
    public async Task Below_the_target_it_exits_with_1()
    {
        (await Run(args: ["--extractor", "fake", "--min-accuracy", "0.99"])).ShouldBe(LabelEvalCli.BelowTarget);
        _out.ToString().ShouldContain("below target");
    }

    [Fact]
    public async Task One_case_can_be_run_on_its_own()
    {
        (await Run(args: ["--case", "synthetic-fake-match", "--min-accuracy", "1"])).ShouldBe(LabelEvalCli.Ok);
        _out.ToString().ShouldContain("Running 1 labels");
    }

    [Fact]
    public async Task With_configuration_it_runs_claude_with_those_settings()
    {
        ClaudeOptions? used = null;

        var code = await Run(Configured, options => { used = options; return new FakeLabelExtractor(); }, "--case", "synthetic-dutch");

        code.ShouldBe(LabelEvalCli.Ok);
        used.ShouldNotBeNull();
        used.Endpoint.ShouldBe("https://example.invalid");
        used.ExtractionModel.ShouldBe("claude-test");
        used.TimeoutSeconds.ShouldBe(90);
        _out.ToString().ShouldContain("against Claude (claude-test)");
    }

    [Fact]
    public async Task Fake_can_be_forced_even_when_claude_is_configured()
    {
        (await Run(Configured, args: ["--extractor", "fake", "--case", "synthetic-dutch"])).ShouldBe(LabelEvalCli.Ok);
        _out.ToString().ShouldContain("fake extractor");
    }

    [Fact]
    public async Task The_default_claude_extractor_is_built_from_the_settings()
    {
        // Real ClaudeLabelExtractor against an unreachable address: every case is an error, nothing leaves the machine.
        var environment = new Dictionary<string, string>(Configured) { ["Ai__Endpoint"] = "http://127.0.0.1:9", ["Ai__TimeoutSeconds"] = "5" };

        var code = await LabelEvalCli.RunAsync(
            ["--golden", GoldenSetTests.GoldenFolder, "--case", "synthetic-minimal-blend"], _out, _err, name => environment.GetValueOrDefault(name), cancellationToken: TestContext.Current.CancellationToken);

        code.ShouldBe(LabelEvalCli.Ok);
        _out.ToString().ShouldContain("error:");
    }

    [Theory]
    [InlineData("--extractor", "claude")]
    [InlineData("--extractor", "openai")]
    [InlineData("--min-accuracy", "80")]
    [InlineData("--case", "does-not-exist")]
    [InlineData("--bogus")]
    [InlineData("--json")]
    public async Task Wrong_arguments_exit_with_2(params string[] args)
    {
        (await Run(args: args)).ShouldBe(LabelEvalCli.UsageError);
        _err.ToString().ShouldNotBeEmpty();
    }

    [Fact]
    public async Task A_broken_golden_set_exits_with_2()
    {
        File.WriteAllText(Path.Combine(_temp, "x.json"), "{");

        (await LabelEvalCli.RunAsync(["--golden", _temp], _out, _err, _ => null, cancellationToken: TestContext.Current.CancellationToken)).ShouldBe(LabelEvalCli.UsageError);
        _err.ToString().ShouldContain("not valid JSON");
    }

    [Fact]
    public async Task Help_prints_the_usage()
    {
        (await LabelEvalCli.RunAsync(["--help"], _out, _err, _ => null, cancellationToken: TestContext.Current.CancellationToken)).ShouldBe(LabelEvalCli.Ok);
        _out.ToString().ShouldContain("--min-accuracy");
    }

    [Fact]
    public void The_golden_folder_is_found_from_a_subfolder()
    {
        var root = Directory.CreateDirectory(Path.Combine(_temp, "repo"));
        Directory.CreateDirectory(Path.Combine(root.FullName, LabelEvalCli.DefaultGoldenPath));
        var deep = Directory.CreateDirectory(Path.Combine(root.FullName, "src", "deep"));

        LabelEvalCli.FindGoldenFolder(deep.FullName).ShouldBe(Path.Combine(root.FullName, LabelEvalCli.DefaultGoldenPath));
        LabelEvalCli.FindGoldenFolder(Path.GetPathRoot(_temp)!).ShouldBeNull();
    }
}
