namespace Dialed.UI.Components;

/// <summary>The window for the zoomed origin map: used by the map on screen and by the bean card, so both frame a country the same way.</summary>
internal static class MapView
{
    /// <summary>
    /// A window 1.5 times the country, in the given shape (width over height; the screen map is 5:3), never narrower than a few hundred kilometres
    /// so a small country keeps its neighbours around it, centred on the country.
    /// </summary>
    public static (double X, double Y, double Width, double Height) Zoom(CountryShapes.Shape shape, double aspect = 5.0 / 3)
    {
        var width = Math.Max(Math.Max((shape.X1 - shape.X0) * 1.5, (shape.Y1 - shape.Y0) * 1.5 * aspect), 45);
        var height = width / aspect;
        return ((shape.X0 + shape.X1) / 2 - width / 2, (shape.Y0 + shape.Y1) / 2 - height / 2, width, height);
    }
}
