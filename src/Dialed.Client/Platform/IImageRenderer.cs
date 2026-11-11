namespace Dialed.Client.Platform;

/// <summary>Turns an SVG picture into a PNG so it can be shared like a photo (the brew card). Web: an image drawn on a canvas.</summary>
public interface IImageRenderer
{
    /// <summary>The PNG bytes of the SVG at the given pixel size, or <c>null</c> when the platform cannot render it.</summary>
    Task<byte[]?> SvgToPngAsync(string svg, int width, int height);
}
