using System.Text.Json;
using System.Text.RegularExpressions;

namespace Dialed.ArchitectureTests;

/// <summary>Install metadata (#97): every icon and splash screen the page and manifest point to exists, and light and dark theme colours are declared.</summary>
public sealed partial class InstallMetadataTests
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

    private static string Index => File.ReadAllText(Path.Combine(Web, "index.html"));

    [GeneratedRegex("rel=\"apple-touch-startup-image\" href=\"([^\"]+)\" media=\"([^\"]+)\"")]
    private static partial Regex Splash();

    [Fact]
    public void Manifest_names_the_app_and_has_any_and_maskable_icons()
    {
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(Web, "manifest.webmanifest")));
        var root = manifest.RootElement;

        root.GetProperty("name").GetString().ShouldBe("Dialed");
        var icons = root.GetProperty("icons").EnumerateArray().ToList();
        icons.Select(i => i.GetProperty("purpose").GetString()).ShouldContain("any");
        icons.Select(i => i.GetProperty("purpose").GetString()).ShouldContain("maskable");
        icons.Select(i => i.GetProperty("sizes").GetString()).ShouldContain("192x192");
        icons.Select(i => i.GetProperty("sizes").GetString()).ShouldContain("512x512");

        foreach (var icon in icons)
        {
            File.Exists(Path.Combine(Web, icon.GetProperty("src").GetString()!)).ShouldBeTrue(icon.GetProperty("src").GetString());
        }
    }

    [Fact]
    public void The_page_has_an_apple_touch_icon_and_light_and_dark_theme_colours()
    {
        File.Exists(Path.Combine(Web, "icons", "apple-touch-icon.png")).ShouldBeTrue();
        Index.ShouldContain("rel=\"apple-touch-icon\"");
        Index.ShouldContain("name=\"theme-color\" content=\"#6F4E37\" media=\"(prefers-color-scheme: light)\"");
        Index.ShouldContain("name=\"theme-color\" content=\"#1E1713\" media=\"(prefers-color-scheme: dark)\"");
        Index.ShouldContain("apple-mobile-web-app-title\" content=\"Dialed\"");
    }

    [Fact]
    public void Every_splash_screen_exists_and_comes_in_light_and_dark()
    {
        var splashes = Splash().Matches(Index);

        splashes.Count.ShouldBeGreaterThanOrEqualTo(2 * 15);
        splashes.Count(m => m.Groups[2].Value.Contains("prefers-color-scheme: light")).ShouldBe(splashes.Count(m => m.Groups[2].Value.Contains("prefers-color-scheme: dark")));

        foreach (Match splash in splashes)
        {
            File.Exists(Path.Combine(Web, splash.Groups[1].Value)).ShouldBeTrue(splash.Groups[1].Value);
        }
    }

    [Fact]
    public void Splash_screens_stay_out_of_the_offline_cache()
    {
        File.ReadAllText(Path.Combine(Web, "service-worker.published.js")).ShouldContain("icons\\/splash");
    }
}
