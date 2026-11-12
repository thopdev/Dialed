using System.Globalization;
using System.Security;
using System.Text;
using Dialed.Domain.Model;

namespace Dialed.UI.Cards;

/// <summary>What goes on a brew card: already in the person's language and units.</summary>
public sealed record BrewCardContent(string Title, string? Subtitle, int? Rating, string Method, IReadOnlyList<(string Label, string Value)> Parameters, string? Notes, string Footer, RoastLevel? Roast = null);

/// <summary>
/// The picture you can share of a brew: bean, method, the numbers and the rating on a warm background. A plain SVG with fixed colours (it leaves the app as an image,
/// so it cannot use the theme) and system fonts, drawn at 1080 by 1350 pixels.
/// </summary>
public static class BrewCard
{
    public const int Width = 1080;

    public const int Height = 1350;

    private const string Cream = "#fbf6ef";
    private const string Brown = "#7d5c46";
    private const string Ink = "#2a1f19";
    private const string Muted = "#7a6a5f";
    private const string Gold = "#eaa300";
    private const string Line = "#e4d8cb";

    public static string Build(BrewCardContent content)
    {
        ArgumentNullException.ThrowIfNull(content);

        var svg = new StringBuilder();
        svg.Append(CultureInfo.InvariantCulture, $"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"{Width}\" height=\"{Height}\" viewBox=\"0 0 {Width} {Height}\" font-family=\"-apple-system, 'Segoe UI', Roboto, Helvetica, Arial, sans-serif\">");
        svg.Append(CultureInfo.InvariantCulture, $"<rect width=\"{Width}\" height=\"{Height}\" fill=\"{Cream}\"/>");
        svg.Append(CultureInfo.InvariantCulture, $"<rect x=\"0\" y=\"0\" width=\"{Width}\" height=\"24\" fill=\"{Brown}\"/>");
        Text(svg, 80, 130, "Dialed", 44, Brown, bold: true);

        Cup(svg, content.Rating, content.Roast);

        var y = 745;
        var titleLines = Wrap(content.Title, 24).Take(2).ToList();
        foreach (var line in titleLines)
        {
            Text(svg, 80, y, line, 72, Ink, bold: true);
            y += 84;
        }

        if (!string.IsNullOrWhiteSpace(content.Subtitle))
        {
            Text(svg, 80, y - 10, Truncate(content.Subtitle, 50), 36, Muted);
            y += 30;
        }

        y += 20;
        svg.Append(CultureInfo.InvariantCulture, $"<rect x=\"80\" y=\"{y - 30}\" width=\"{Width - 160}\" height=\"4\" fill=\"{Line}\"/>");
        Text(svg, 80, y + 36, content.Method, 44, Brown, bold: true);
        if (content.Rating is { } rating and >= 1 and <= 5)
        {
            Text(svg, Width - 80, y + 36, string.Create(CultureInfo.InvariantCulture, $"{rating} / 5"), 44, Brown, bold: true, anchorEnd: true);
        }

        y += 90;
        foreach (var (label, value) in content.Parameters.Take(titleLines.Count > 1 ? 3 : 4))
        {
            Text(svg, 80, y, label, 32, Muted);
            Text(svg, Width - 80, y, value, 38, Ink, anchorEnd: true);
            y += 50;
        }

        if (!string.IsNullOrWhiteSpace(content.Notes) && y < 1215)
        {
            Text(svg, 80, y + 10, Truncate(content.Notes, 46), 32, Muted, italic: true);
        }

        Text(svg, 80, Height - 60, content.Footer, 34, Muted);
        svg.Append("</svg>");
        return svg.ToString();
    }

    /// <summary>The coffee colour of the cup for a roast level (light to dark); the brand brown when it is not known.</summary>
    internal static string CoffeeColour(RoastLevel? roast) => roast switch
    {
        RoastLevel.Light => "#a8742f",
        RoastLevel.MediumLight => "#94622a",
        RoastLevel.Medium => "#7a4a22",
        RoastLevel.MediumDark => "#5e3519",
        RoastLevel.Dark => "#3d2112",
        _ => "#6b4126",
    };

    /// <summary>Which latte art the rating earns: nothing for 1, a dot for 2, a heart for 3, a tulip for 4 and a rosetta for 5.</summary>
    internal static string ArtName(int? rating) => rating switch
    {
        >= 5 => "rosetta",
        4 => "tulip",
        3 => "heart",
        2 => "dot",
        _ => "crema",
    };

    private static void Cup(StringBuilder svg, int? rating, RoastLevel? roast)
    {
        const int cx = 540, cy = 395;
        var coffee = CoffeeColour(roast);
        svg.Append(CultureInfo.InvariantCulture, $"<circle cx=\"{cx}\" cy=\"{cy}\" r=\"290\" fill=\"#efe5d8\"/>");
        svg.Append(CultureInfo.InvariantCulture, $"<circle cx=\"{cx}\" cy=\"{cy}\" r=\"236\" fill=\"#ffffff\"/>");
        svg.Append(CultureInfo.InvariantCulture, $"<circle cx=\"{cx}\" cy=\"{cy}\" r=\"204\" fill=\"{coffee}\"/>");
        svg.Append(CultureInfo.InvariantCulture, $"<circle cx=\"{cx}\" cy=\"{cy}\" r=\"204\" fill=\"none\" stroke=\"#ffffff\" stroke-opacity=\"0.25\" stroke-width=\"10\"/>");
        svg.Append(CultureInfo.InvariantCulture, $"<g id=\"latte-art-{ArtName(rating)}\" fill=\"#fbf3e6\" stroke=\"#fbf3e6\" stroke-linecap=\"round\" stroke-linejoin=\"round\">");
        switch (ArtName(rating))
        {
            case "rosetta":
                // Seven nested leaf arcs getting smaller towards the top, and a stem through them.
                for (var i = 0; i < 7; i++)
                {
                    var yy = cy + 120 - (i * 38);
                    var w = 150 - (i * 17);
                    svg.Append(CultureInfo.InvariantCulture, $"<path d=\"M{cx - w} {yy} Q{cx} {yy - 70} {cx + w} {yy}\" fill=\"none\" stroke-width=\"20\"/>");
                }

                svg.Append(CultureInfo.InvariantCulture, $"<path d=\"M{cx} {cy - 150} L{cx} {cy + 170}\" fill=\"none\" stroke-width=\"12\"/>");
                break;
            case "tulip":
                for (var i = 0; i < 3; i++)
                {
                    Heart(svg, cx, cy + 70 - (i * 80), 80 - (i * 14));
                }

                svg.Append(CultureInfo.InvariantCulture, $"<path d=\"M{cx} {cy - 130} L{cx} {cy + 160}\" fill=\"none\" stroke-width=\"10\"/>");
                break;
            case "heart":
                Heart(svg, cx, cy + 10, 120);
                break;
            case "dot":
                svg.Append(CultureInfo.InvariantCulture, $"<circle cx=\"{cx}\" cy=\"{cy}\" r=\"54\" stroke=\"none\"/><circle cx=\"{cx}\" cy=\"{cy}\" r=\"110\" fill=\"none\" stroke-width=\"14\" stroke-opacity=\"0.7\"/>");
                break;
            default:
                svg.Append(CultureInfo.InvariantCulture, $"<path d=\"M{cx - 120} {cy} A120 120 0 0 1 {cx + 100} {cy - 60}\" fill=\"none\" stroke-width=\"14\" stroke-opacity=\"0.5\"/>");
                break;
        }

        svg.Append("</g>");
    }

    private static void Heart(StringBuilder svg, int cx, int cy, int size) =>
        svg.Append(CultureInfo.InvariantCulture,
            $"<path d=\"M{cx} {cy + (size * 0.9):0} C{cx - (size * 1.5):0} {cy - (size * 0.1):0} {cx - (size * 0.6):0} {cy - (size * 1.0):0} {cx} {cy - (size * 0.35):0} C{cx + (size * 0.6):0} {cy - (size * 1.0):0} {cx + (size * 1.5):0} {cy - (size * 0.1):0} {cx} {cy + (size * 0.9):0}Z\" stroke=\"none\"/>");

    private static void Text(StringBuilder svg, int x, int y, string text, int size, string fill, bool bold = false, bool italic = false, bool anchorEnd = false) =>
        svg.Append(CultureInfo.InvariantCulture,
            $"<text x=\"{x}\" y=\"{y}\" font-size=\"{size}\" fill=\"{fill}\"{(bold ? " font-weight=\"700\"" : "")}{(italic ? " font-style=\"italic\"" : "")}{(anchorEnd ? " text-anchor=\"end\"" : "")}>{SecurityElement.Escape(text)}</text>");

    private static string Truncate(string text, int max) => text.Length <= max ? text : text[..(max - 1)] + "…";

    /// <summary>Greedy word wrap on a character budget; a word longer than the budget gets its own line (cut at the budget).</summary>
    internal static IEnumerable<string> Wrap(string text, int max)
    {
        var line = new StringBuilder();
        foreach (var word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var piece = word.Length > max ? word[..max] : word;
            if (line.Length > 0 && line.Length + 1 + piece.Length > max)
            {
                yield return line.ToString();
                line.Clear();
            }

            if (line.Length > 0)
            {
                line.Append(' ');
            }

            line.Append(piece);
        }

        if (line.Length > 0)
        {
            yield return line.ToString();
        }
    }
}
