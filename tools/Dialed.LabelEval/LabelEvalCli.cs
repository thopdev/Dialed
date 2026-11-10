using System.Globalization;
using Dialed.AI.Labels;
using Dialed.AI.Labels.Evaluation;

namespace Dialed.LabelEval;

/// <summary>
/// Command line for the label golden set (#84):
/// <c>dotnet run --project tools/Dialed.LabelEval -- [--extractor auto|fake|claude] [--golden DIR] [--case ID]... [--markdown FILE] [--json FILE] [--min-accuracy 0.8]</c>.
/// Claude is configured with the same keys as the API, as environment variables: <c>Ai__Endpoint</c>, <c>Ai__ApiKey</c>, <c>Ai__ExtractionModel</c>.
/// Exit code 0: done (and at or above the target when one is given); 1: below the target; 2: wrong arguments or a broken golden set.
/// </summary>
public static class LabelEvalCli
{
    public const string DefaultGoldenPath = "tests/Dialed.AI.Tests/golden";

    public const int Ok = 0;
    public const int BelowTarget = 1;
    public const int UsageError = 2;

    public const string Usage = """
        Usage: dotnet run --project tools/Dialed.LabelEval -- [options]
          --extractor auto|fake|claude  Which extractor to run. auto (default): claude when Ai__Endpoint, Ai__ApiKey
                                        and Ai__ExtractionModel are set, otherwise fake.
          --golden DIR                  Golden set folder (default: tests/Dialed.AI.Tests/golden, found from the current folder up).
          --case ID                     Only this case; repeat for more.
          --markdown FILE               Write the Markdown summary to FILE (for the job summary or a PR comment).
          --json FILE                   Write every field result as JSON to FILE.
          --min-accuracy N              Exit with 1 when the field accuracy is below N (0 to 1), for example 0.8.
        """;

    /// <param name="environment">Reads an environment variable; tests pass their own.</param>
    /// <param name="claude">Builds the Claude extractor; tests pass a double so nothing leaves the machine.</param>
    public static async Task<int> RunAsync(
        string[] args,
        TextWriter output,
        TextWriter error,
        Func<string, string?>? environment = null,
        Func<ClaudeOptions, ILabelExtractor>? claude = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);
        environment ??= Environment.GetEnvironmentVariable;
        claude ??= options => new ClaudeLabelExtractor(new HttpClient(), options);

        Options parsed;
        try
        {
            parsed = Options.Parse(args);
        }
        catch (ArgumentException exception)
        {
            await error.WriteLineAsync(exception.Message);
            await error.WriteLineAsync(Usage);
            return UsageError;
        }

        if (parsed.Help)
        {
            await output.WriteLineAsync(Usage);
            return Ok;
        }

        var golden = parsed.Golden ?? FindGoldenFolder(Directory.GetCurrentDirectory());
        if (golden is null)
        {
            await error.WriteLineAsync($"Could not find '{DefaultGoldenPath}' from the current folder up; pass --golden.");
            return UsageError;
        }

        GoldenSet set;
        try
        {
            set = GoldenSet.Load(golden);
        }
        catch (GoldenSetException exception)
        {
            await error.WriteLineAsync(exception.Message);
            return UsageError;
        }

        if (parsed.Cases.Count > 0)
        {
            var unknown = parsed.Cases.Where(id => set.Cases.All(c => c.Id != id)).ToList();
            if (unknown.Count > 0)
            {
                await error.WriteLineAsync($"Unknown case: {string.Join(", ", unknown)}.");
                return UsageError;
            }

            set = new GoldenSet(set.Directory, set.Cases.Where(c => parsed.Cases.Contains(c.Id)).ToList());
        }

        var options = new ClaudeOptions
        {
            Endpoint = environment("Ai__Endpoint") ?? "",
            ApiKey = environment("Ai__ApiKey") ?? "",
            ExtractionModel = environment("Ai__ExtractionModel") ?? "",
        };
        if (int.TryParse(environment("Ai__TimeoutSeconds"), CultureInfo.InvariantCulture, out var timeout) && timeout > 0)
        {
            options.TimeoutSeconds = timeout;
        }

        var useClaude = parsed.Extractor switch
        {
            "claude" => true,
            "fake" => false,
            _ => options.IsConfigured,
        };
        if (useClaude && !options.IsConfigured)
        {
            await error.WriteLineAsync("--extractor claude needs Ai__Endpoint, Ai__ApiKey and Ai__ExtractionModel.");
            return UsageError;
        }

        var extractor = useClaude ? claude(options) : new FakeLabelExtractor();
        await output.WriteLineAsync(useClaude
            ? $"Running {set.Cases.Count} labels against Claude ({options.ExtractionModel}) with prompt {LabelPrompt.Version}."
            : $"Running {set.Cases.Count} labels against the fake extractor (checks the harness, not the model).");

        var progress = new Progress(output);
        var report = await new LabelEvaluator(extractor).RunAsync(set, progress, cancellationToken);

        var markdown = report.ToMarkdown(parsed.MinAccuracy);
        await output.WriteLineAsync();
        await output.WriteLineAsync(markdown);
        if (parsed.Markdown is not null)
        {
            await File.WriteAllTextAsync(parsed.Markdown, markdown, cancellationToken);
        }

        if (parsed.Json is not null)
        {
            await File.WriteAllTextAsync(parsed.Json, report.ToJson(), cancellationToken);
        }

        return parsed.MinAccuracy is { } target && report.Accuracy < target ? BelowTarget : Ok;
    }

    /// <summary>Looks for the default golden set folder in <paramref name="start"/> and its parents.</summary>
    public static string? FindGoldenFolder(string start)
    {
        for (var folder = new DirectoryInfo(start); folder is not null; folder = folder.Parent)
        {
            var candidate = Path.Combine(folder.FullName, DefaultGoldenPath);
            if (Directory.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private sealed class Progress(TextWriter output) : IProgress<CaseResult>
    {
        public void Report(CaseResult value) => output.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"  {value.Id,-32} {value.Accuracy * 100,5:0}%{(value.Error is null ? "" : "  error: " + value.Error)}"));
    }

    private sealed record Options(string Extractor, string? Golden, IReadOnlyList<string> Cases, string? Markdown, string? Json, double? MinAccuracy, bool Help)
    {
        public static Options Parse(string[] args)
        {
            string extractor = "auto";
            string? golden = null, markdown = null, json = null;
            double? minAccuracy = null;
            var cases = new List<string>();
            var help = false;
            for (var i = 0; i < args.Length; i++)
            {
                string Value() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{args[i]} needs a value.");
                switch (args[i])
                {
                    case "--extractor":
                        extractor = Value();
                        if (extractor is not ("auto" or "fake" or "claude"))
                        {
                            throw new ArgumentException($"Unknown extractor '{extractor}'.");
                        }

                        break;
                    case "--golden": golden = Value(); break;
                    case "--case": cases.Add(Value()); break;
                    case "--markdown": markdown = Value(); break;
                    case "--json": json = Value(); break;
                    case "--min-accuracy":
                        minAccuracy = double.TryParse(Value(), NumberStyles.Float, CultureInfo.InvariantCulture, out var n) && n is >= 0 and <= 1
                            ? n
                            : throw new ArgumentException("--min-accuracy is a number from 0 to 1, for example 0.8.");
                        break;
                    case "-h" or "--help": help = true; break;
                    default: throw new ArgumentException($"Unknown option '{args[i]}'.");
                }
            }

            return new Options(extractor, golden, cases, markdown, json, minAccuracy, help);
        }
    }
}
