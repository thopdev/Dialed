namespace Dialed.Domain.Geography;

/// <summary>
/// Where a place lands on the origin map: a plain equirectangular projection of longitude -180..180 and latitude 75..-58 onto a 1000 wide canvas
/// (the polar caps are cut off, nobody grows coffee there). The land outline in the UI project is drawn with the same numbers.
/// </summary>
public static class WorldMapProjection
{
    public const double Width = 1000;
    public const double TopLatitude = 75;
    public const double BottomLatitude = -58;
    public static double Height => (TopLatitude - BottomLatitude) / 360 * Width;

    public static (double X, double Y) Project(double latitude, double longitude) =>
        ((longitude + 180) / 360 * Width, (TopLatitude - latitude) / 360 * Width);
}
