using System.Globalization;
using System.Security;
using System.Text;
using Dialed.UI.Components;

namespace Dialed.UI.Cards;

/// <summary>What goes on a bean card: already in the person's language and units. <see cref="CountryIso2"/> picks the country drawn on the map, if known.</summary>
public sealed record BeanCardContent(string Title, string? Roaster, string? Origin, int? Rating, string? CountryIso2, IReadOnlyList<(string Label, string Value)> Facts, string? Notes, string Footer);

/// <summary>
/// The picture you can share of a bean: its name, roaster and origin with the country drawn on a small map, your rating, the main facts and the tasting
/// notes. A plain SVG with fixed colours (it leaves the app as an image) and system fonts, 1080 by 1350 pixels like the brew card.
/// </summary>
public static class BeanCard
{
    public const int Width = BrewCard.Width;

    public const int Height = BrewCard.Height;

    private const string Cream = "#fbf6ef";
    private const string Brown = "#7d5c46";
    private const string Ink = "#2a1f19";
    private const string Muted = "#7a6a5f";
    private const string Gold = "#eaa300";
    private const string Line = "#e4d8cb";
    private const string Sea = "#f1e9df";
    private const string Land = "#e3d6c7";

    private const int MapX = 80;
    private const int MapWidth = Width - 160;
    private const int MapHeight = 340;

    public static string Build(BeanCardContent content)
    {
        ArgumentNullException.ThrowIfNull(content);

        var svg = new StringBuilder();
        svg.Append(CultureInfo.InvariantCulture, $"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"{Width}\" height=\"{Height}\" viewBox=\"0 0 {Width} {Height}\" font-family=\"-apple-system, 'Segoe UI', Roboto, Helvetica, Arial, sans-serif\">");
        svg.Append(CultureInfo.InvariantCulture, $"<rect width=\"{Width}\" height=\"{Height}\" fill=\"{Cream}\"/><rect width=\"{Width}\" height=\"24\" fill=\"{Brown}\"/>");
        Text(svg, MapX, 130, "Dialed", 44, Brown, bold: true);

        var y = 250;
        foreach (var line in BrewCard.Wrap(content.Title, 20).Take(2))
        {
            Text(svg, MapX, y, line, 78, Ink, bold: true);
            y += 90;
        }

        var sub = string.Join(" · ", new[] { content.Roaster, content.Origin }.Where(p => !string.IsNullOrWhiteSpace(p)));
        if (sub.Length > 0)
        {
            Text(svg, MapX, y, sub.Length <= 46 ? sub : sub[..45] + "…", 38, Muted);
            y += 30;
        }

        if (content.Rating is { } rating and >= 1 and <= 5)
        {
            y += 70;
            // Five dim stars with the lit ones drawn exactly over them, so the spacing is the font's own.
            Text(svg, MapX, y, "★★★★★", 64, Line);
            Text(svg, MapX, y, new string('★', rating), 64, Gold);
        }

        y += 40;
        AppendMap(svg, content.CountryIso2, y);
        y += MapHeight + 60;

        foreach (var (label, value) in content.Facts.Take(4))
        {
            Text(svg, MapX, y, label, 36, Muted);
            Text(svg, Width - MapX, y, value, 42, Ink, anchorEnd: true);
            y += 56;
        }

        if (!string.IsNullOrWhiteSpace(content.Notes))
        {
            y += 12;
            foreach (var line in BrewCard.Wrap(content.Notes, 36).Take(2))
            {
                Text(svg, MapX, y, line, 38, Muted, italic: true);
                y += 50;
            }
        }

        Text(svg, MapX, Height - 56, content.Footer, 34, Muted);
        svg.Append("</svg>");
        return svg.ToString();
    }

    /// <summary>The country, zoomed, with its neighbours; a plain panel when the country is not known.</summary>
    private static void AppendMap(StringBuilder svg, string? iso2, int y)
    {
        svg.Append(CultureInfo.InvariantCulture, $"<clipPath id=\"map-clip\"><rect x=\"{MapX}\" y=\"{y}\" width=\"{MapWidth}\" height=\"{MapHeight}\" rx=\"28\"/></clipPath>");
        svg.Append(CultureInfo.InvariantCulture, $"<rect x=\"{MapX}\" y=\"{y}\" width=\"{MapWidth}\" height=\"{MapHeight}\" rx=\"28\" fill=\"{Sea}\"/>");
        if (iso2 is null || !CountryShapes.ByIso2.TryGetValue(iso2, out var shape))
        {
            return;
        }

        var view = MapView.Zoom(shape, (double)MapWidth / MapHeight);
        // The clip goes on a group around the nested svg: on the svg itself it would be applied in the map's own (zoomed) coordinates and clip everything away.
        svg.Append("<g clip-path=\"url(#map-clip)\">");
        svg.Append(CultureInfo.InvariantCulture,
            $"<svg x=\"{MapX}\" y=\"{y}\" width=\"{MapWidth}\" height=\"{MapHeight}\" viewBox=\"{view.X.ToString("0.##", CultureInfo.InvariantCulture)} {view.Y.ToString("0.##", CultureInfo.InvariantCulture)} {view.Width.ToString("0.##", CultureInfo.InvariantCulture)} {view.Height.ToString("0.##", CultureInfo.InvariantCulture)}\">");
        svg.Append(CultureInfo.InvariantCulture, $"<path d=\"{CountryShapes.Others}\" fill=\"{Land}\" stroke=\"{Sea}\" stroke-width=\"1.5\" vector-effect=\"non-scaling-stroke\"/>");
        svg.Append(CultureInfo.InvariantCulture, $"<path d=\"{shape.Path}\" fill=\"{Brown}\" fill-opacity=\"0.85\" stroke=\"{Sea}\" stroke-width=\"1.5\" vector-effect=\"non-scaling-stroke\"/>");
        svg.Append("</svg></g>");
    }

    private static void Text(StringBuilder svg, int x, int y, string text, int size, string fill, bool bold = false, bool italic = false, bool anchorEnd = false) =>
        svg.Append(CultureInfo.InvariantCulture,
            $"<text x=\"{x}\" y=\"{y}\" font-size=\"{size}\" fill=\"{fill}\"{(bold ? " font-weight=\"700\"" : "")}{(italic ? " font-style=\"italic\"" : "")}{(anchorEnd ? " text-anchor=\"end\"" : "")}>{SecurityElement.Escape(text)}</text>");
}
