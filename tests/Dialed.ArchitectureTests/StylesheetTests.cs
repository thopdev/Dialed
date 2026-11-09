using System.Text.RegularExpressions;

namespace Dialed.ArchitectureTests;

/// <summary>
/// Our CSS must reach the browser intact. A missing "}" does not break the build: with CSS nesting the browser
/// silently files every later rule under the unclosed one, so hundreds of lines stop applying (it happened twice).
/// </summary>
public sealed partial class StylesheetTests
{
    private static readonly string Root = FindRoot();

    private static string FindRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "src", "Dialed.Web")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }

    public static TheoryData<string> Stylesheets()
    {
        var data = new TheoryData<string>();
        foreach (var dir in new[] { Path.Combine("src", "Dialed.UI", "wwwroot", "css"), Path.Combine("src", "Dialed.Web", "wwwroot", "css") })
        {
            foreach (var file in Directory.GetFiles(Path.Combine(Root, dir), "*.css").Order(StringComparer.Ordinal))
            {
                data.Add(Path.GetRelativePath(Root, file).Replace('\\', '/'));
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Stylesheets))]
    public void Rules_are_closed_and_never_nested_by_accident(string file)
    {
        // The first few are enough: one missing "}" reports every rule after it.
        CssStructureProblems(File.ReadAllText(Path.Combine(Root, file))).Take(5).ShouldBeEmpty();
    }

    [Fact]
    public void The_checker_catches_an_unclosed_rule()
    {
        CssStructureProblems(".a {\n color: red;\n\n.b {\n color: blue;\n}\n").ShouldNotBeEmpty();
        CssStructureProblems(".a { color: red; }\n@media (min-width: 1px) { .b { color: blue; } }\n@keyframes k { from { opacity: 0; } to { opacity: 1; } }").ShouldBeEmpty();
    }

    [Fact]
    public void Index_links_our_stylesheets_and_the_scoped_component_styles()
    {
        var index = File.ReadAllText(Path.Combine(Root, "src", "Dialed.Web", "wwwroot", "index.html"));

        // Fluent UI components (sidebar nav, spinner, buttons) ship their styles in the scoped CSS bundle.
        index.ShouldContain("href=\"Dialed.Web#[.{fingerprint}].styles.css\"");
        foreach (var file in Directory.GetFiles(Path.Combine(Root, "src", "Dialed.UI", "wwwroot", "css"), "*.css"))
        {
            index.ShouldContain($"href=\"_content/Dialed.UI/css/{Path.GetFileName(file)}\"");
        }
    }

    /// <summary>Unbalanced braces, and a style rule opened directly inside another style rule (we only nest in at-rules).</summary>
    private static List<string> CssStructureProblems(string css)
    {
        // Blank out comments but keep their line breaks, so reported line numbers match the file.
        var text = Strings().Replace(Comments().Replace(css, m => new string('\n', m.Value.Count(c => c == '\n'))), "\"\"");
        var problems = new List<string>();
        var stack = new Stack<(bool AtRule, string Prelude, int Line)>();
        var preludeStart = 0;
        var line = 1;
        for (var i = 0; i < text.Length; i++)
        {
            switch (text[i])
            {
                case '\n':
                    line++;
                    break;
                case ';':
                    preludeStart = i + 1;
                    break;
                case '{':
                    var prelude = text[preludeStart..i].Trim();
                    var atRule = prelude.StartsWith('@');
                    if (!atRule && stack.TryPeek(out var parent) && !parent.AtRule)
                    {
                        problems.Add($"line {line}: '{prelude}' is inside '{parent.Prelude}' (line {parent.Line}); is a '}}' missing?");
                    }

                    stack.Push((atRule, prelude, line));
                    preludeStart = i + 1;
                    break;
                case '}':
                    if (!stack.TryPop(out _))
                    {
                        problems.Add($"line {line}: '}}' without a matching '{{'");
                    }

                    preludeStart = i + 1;
                    break;
            }
        }

        problems.AddRange(stack.Select(open => $"line {open.Line}: '{open.Prelude}' is never closed"));
        return problems;
    }

    [GeneratedRegex(@"/\*.*?\*/", RegexOptions.Singleline)]
    private static partial Regex Comments();

    [GeneratedRegex("\"[^\"\\n]*\"|'[^'\\n]*'")]
    private static partial Regex Strings();
}
