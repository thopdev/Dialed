using System.Globalization;
using System.Security;
using System.Text;

namespace Dialed.UI.Cards;

/// <summary>What goes on a brew card: already in the person's language and units.</summary>
public sealed record BrewCardContent(string Title, string? Subtitle, int? Rating, string Method, IReadOnlyList<(string Label, string Value)> Parameters, string? Notes, string Footer);

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

        var y = 280;
        foreach (var line in Wrap(content.Title, 20).Take(3))
        {
            Text(svg, 80, y, line, 84, Ink, bold: true);
            y += 100;
        }

        if (!string.IsNullOrWhiteSpace(content.Subtitle))
        {
            Text(svg, 80, y, Truncate(content.Subtitle, 44), 40, Muted);
            y += 70;
        }

        if (content.Rating is { } rating and >= 1 and <= 5)
        {
            // Five dim stars with the lit ones drawn exactly over them, so the spacing is the font's own.
            Text(svg, 80, y + 20, "★★★★★", 72, Line);
            Text(svg, 80, y + 20, new string('★', rating), 72, Gold);
            y += 110;
        }

        y = Math.Max(y + 20, 600);
        svg.Append(CultureInfo.InvariantCulture, $"<rect x=\"80\" y=\"{y - 30}\" width=\"{Width - 160}\" height=\"4\" fill=\"{Line}\"/>");
        Text(svg, 80, y + 60, content.Method, 52, Brown, bold: true);
        y += 130;
        foreach (var (label, value) in content.Parameters.Take(6))
        {
            Text(svg, 80, y, label, 38, Muted);
            Text(svg, Width - 80, y, value, 44, Ink, anchorEnd: true);
            y += 66;
        }

        if (!string.IsNullOrWhiteSpace(content.Notes))
        {
            y += 10;
            foreach (var line in Wrap(content.Notes, 36).Take(2))
            {
                Text(svg, 80, y, line, 40, Muted, italic: true);
                y += 56;
            }
        }

        Text(svg, 80, Height - 60, content.Footer, 34, Muted);
        svg.Append("</svg>");
        return svg.ToString();
    }

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
