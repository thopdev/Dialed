using SkiaSharp;

namespace Dialed.Images;

/// <summary>
/// <see cref="IPhotoImageProcessor"/> on SkiaSharp. Decoding and encoding again drops every metadata segment (Skia's JPEG encoder
/// writes none). A photo that is already clean and small enough is kept byte for byte, so it loses no quality and is not written back.
/// </summary>
public sealed class SkiaPhotoImageProcessor : IPhotoImageProcessor
{
    /// <summary>The app scales photos to this before upload (ADR-0008); anything larger is scaled down here too.</summary>
    public const int MaxOriginalDimension = 2048;

    /// <summary>Refuse images that would take too much memory to decode (a "decompression bomb").</summary>
    public const long MaxPixels = 40_000_000;

    public const int JpegQuality = 85;

    public ProcessedPhoto Process(byte[] jpeg, IReadOnlyList<int> thumbnailSizes)
    {
        ArgumentNullException.ThrowIfNull(jpeg);
        ArgumentNullException.ThrowIfNull(thumbnailSizes);

        var hasMetadata = JpegMetadata.HasMetadata(jpeg); // also rejects anything that is not a JPEG

        using var data = SKData.CreateCopy(jpeg);
        using var codec = SKCodec.Create(data) ?? throw new InvalidPhotoException("The photo cannot be decoded.");
        if ((long)codec.Info.Width * codec.Info.Height > MaxPixels)
        {
            throw new InvalidPhotoException("The photo is too large.");
        }

        using var decoded = SKBitmap.Decode(codec) ?? throw new InvalidPhotoException("The photo cannot be decoded.");
        using var upright = Orient(decoded, codec.EncodedOrigin);

        var tooLarge = Math.Max(upright.Width, upright.Height) > MaxOriginalDimension;
        var original = jpeg;
        if (hasMetadata || tooLarge || codec.EncodedOrigin != SKEncodedOrigin.TopLeft)
        {
            original = Encode(upright, MaxOriginalDimension);
        }

        var thumbnails = thumbnailSizes.Distinct().ToDictionary(size => size, size => Encode(upright, size));
        return new ProcessedPhoto(original, !ReferenceEquals(original, jpeg), thumbnails);
    }

    /// <summary>Scales so the longest side is at most <paramref name="maxDimension"/> (never up) and encodes as JPEG.</summary>
    private static byte[] Encode(SKBitmap source, int maxDimension)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxDimension, 1);

        var scale = Math.Min(1.0, (double)maxDimension / Math.Max(source.Width, source.Height));
        var width = Math.Max(1, (int)Math.Round(source.Width * scale));
        var height = Math.Max(1, (int)Math.Round(source.Height * scale));

        using var resized = width == source.Width && height == source.Height
            ? null
            : source.Resize(new SKImageInfo(width, height, source.ColorType, source.AlphaType, source.ColorSpace), new SKSamplingOptions(SKCubicResampler.Mitchell))
                ?? throw new InvalidPhotoException("The photo cannot be resized.");
        using var image = SKImage.FromBitmap(resized ?? source);
        using var encoded = image.Encode(SKEncodedImageFormat.Jpeg, JpegQuality) ?? throw new InvalidPhotoException("The photo cannot be encoded.");
        return encoded.ToArray();
    }

    /// <summary>Applies the EXIF orientation to the pixels: once the EXIF block is gone, a viewer can no longer do it.</summary>
    private static SKBitmap Orient(SKBitmap source, SKEncodedOrigin origin)
    {
        float w = source.Width, h = source.Height;

        // x' = ScaleX*x + SkewX*y + TransX, y' = SkewY*x + ScaleY*y + TransY
        SKMatrix? matrix = origin switch
        {
            SKEncodedOrigin.TopRight => new SKMatrix(-1, 0, w, 0, 1, 0, 0, 0, 1),     // mirrored
            SKEncodedOrigin.BottomRight => new SKMatrix(-1, 0, w, 0, -1, h, 0, 0, 1), // rotated 180°
            SKEncodedOrigin.BottomLeft => new SKMatrix(1, 0, 0, 0, -1, h, 0, 0, 1),   // flipped
            SKEncodedOrigin.LeftTop => new SKMatrix(0, 1, 0, 1, 0, 0, 0, 0, 1),       // transposed
            SKEncodedOrigin.RightTop => new SKMatrix(0, -1, h, 1, 0, 0, 0, 0, 1),     // rotated 90° clockwise
            SKEncodedOrigin.RightBottom => new SKMatrix(0, -1, h, -1, 0, w, 0, 0, 1), // transversed
            SKEncodedOrigin.LeftBottom => new SKMatrix(0, 1, 0, -1, 0, w, 0, 0, 1),   // rotated 90° counter-clockwise
            _ => null,
        };

        if (matrix is null)
        {
            return source.Copy() ?? throw new InvalidPhotoException("The photo cannot be decoded.");
        }

        var swaps = origin is SKEncodedOrigin.LeftTop or SKEncodedOrigin.RightTop or SKEncodedOrigin.RightBottom or SKEncodedOrigin.LeftBottom;
        var info = new SKImageInfo(swaps ? source.Height : source.Width, swaps ? source.Width : source.Height, source.ColorType, source.AlphaType, source.ColorSpace);
        var oriented = new SKBitmap(info);
        using var canvas = new SKCanvas(oriented);
        canvas.SetMatrix(matrix.Value);
        using var image = SKImage.FromBitmap(source);
        canvas.DrawImage(image, 0, 0, SKSamplingOptions.Default);
        return oriented;
    }
}
