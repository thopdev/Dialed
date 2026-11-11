using System.Buffers.Binary;
using System.Text;
using SkiaSharp;

namespace Dialed.Images.Tests;

/// <summary>
/// Sample JPEGs for the tests, made on the fly: a plain one from Skia, and the same with the metadata a phone writes added by hand
/// (EXIF with camera make, orientation and a GPS position in Amsterdam, an XMP block and a comment).
/// </summary>
public static class TestPhotos
{
    /// <summary>52° 22' 12.34" N: the latitude rationals as they appear in the EXIF block (big-endian).</summary>
    public static readonly byte[] LatitudeBytes = Rationals((52, 1), (22, 1), (1234, 100));

    public const string CameraMake = "Cam";

    public const string Comment = "Taken at home by Alice";

    public const string XmpNamespace = "http://ns.adobe.com/xap/1.0/";

    /// <summary>Red left half, blue right half, so tests can see which way the photo was turned.</summary>
    public static byte[] Plain(int width, int height)
    {
        using var bitmap = new SKBitmap(width, height, SKColorType.Rgba8888, SKAlphaType.Opaque);
        using (var canvas = new SKCanvas(bitmap))
        {
            using var red = new SKPaint { Color = SKColors.Red };
            using var blue = new SKPaint { Color = SKColors.Blue };
            canvas.DrawRect(0, 0, width / 2f, height, red);
            canvas.DrawRect(width / 2f, 0, width - (width / 2f), height, blue);
        }

        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Jpeg, 90);
        return data.ToArray();
    }

    /// <summary>A phone photo: <see cref="Plain"/> plus EXIF (make, orientation, GPS), XMP and a comment.</summary>
    public static byte[] WithGps(int width, int height, ushort orientation = 1)
    {
        var plain = Plain(width, height);
        var segments = JpegMetadata.ReadSegments(plain);
        var insertAt = segments.FirstOrDefault(s => s.Marker == 0xE0) is { } app0 ? app0.Offset + app0.Length : 2;

        byte[] extra =
        [
            .. Segment(0xE1, [.. "Exif\0\0"u8, .. Tiff(orientation)]),
            .. Segment(0xE1, [.. Encoding.ASCII.GetBytes(XmpNamespace + "\0"), .. "<x:xmpmeta><exif:GPSLatitude>52,22.2N</exif:GPSLatitude></x:xmpmeta>"u8]),
            .. Segment(0xFE, Encoding.ASCII.GetBytes(Comment)),
        ];

        return [.. plain.AsSpan(0, insertAt), .. extra, .. plain.AsSpan(insertAt)];
    }

    public static bool Contains(byte[] haystack, ReadOnlySpan<byte> needle) => haystack.AsSpan().IndexOf(needle) >= 0;

    public static bool Contains(byte[] haystack, string ascii) => Contains(haystack, Encoding.ASCII.GetBytes(ascii));

    public static (int Width, int Height) Size(byte[] jpeg)
    {
        using var codec = SKCodec.Create(new MemoryStream(jpeg)) ?? throw new InvalidOperationException("Not an image.");
        return (codec.Info.Width, codec.Info.Height);
    }

    public static SKColor PixelAt(byte[] jpeg, int x, int y)
    {
        using var bitmap = SKBitmap.Decode(jpeg);
        return bitmap.GetPixel(x, y);
    }

    private static byte[] Segment(byte marker, byte[] payload)
    {
        var length = payload.Length + 2;
        return [0xFF, marker, (byte)(length >> 8), (byte)length, .. payload];
    }

    // TIFF, big-endian. IFD0 at 8 (3 entries, ends at 50), GPS IFD at 50 (4 entries, ends at 104), latitude at 104, longitude at 128.
    private static byte[] Tiff(ushort orientation)
    {
        var tiff = new byte[152];
        var span = tiff.AsSpan();
        "MM"u8.CopyTo(span);
        BinaryPrimitives.WriteUInt16BigEndian(span[2..], 42);
        BinaryPrimitives.WriteUInt32BigEndian(span[4..], 8);

        BinaryPrimitives.WriteUInt16BigEndian(span[8..], 3);
        Entry(span[10..], 0x010F, 2, 4, Encoding.ASCII.GetBytes(CameraMake + "\0")); // Make
        Entry(span[22..], 0x0112, 3, 1, [(byte)(orientation >> 8), (byte)orientation, 0, 0]); // Orientation
        Entry(span[34..], 0x8825, 4, 1, BigEndian(50)); // GPS IFD pointer
        BinaryPrimitives.WriteUInt32BigEndian(span[46..], 0);

        BinaryPrimitives.WriteUInt16BigEndian(span[50..], 4);
        Entry(span[52..], 0x0001, 2, 2, "N\0\0\0"u8.ToArray()); // GPSLatitudeRef
        Entry(span[64..], 0x0002, 5, 3, BigEndian(104)); // GPSLatitude
        Entry(span[76..], 0x0003, 2, 2, "E\0\0\0"u8.ToArray()); // GPSLongitudeRef
        Entry(span[88..], 0x0004, 5, 3, BigEndian(128)); // GPSLongitude
        BinaryPrimitives.WriteUInt32BigEndian(span[100..], 0);

        LatitudeBytes.CopyTo(span[104..]);
        Rationals((4, 1), (53, 1), (0, 1)).CopyTo(span[128..]);
        return tiff;
    }

    private static void Entry(Span<byte> at, ushort tag, ushort type, uint count, byte[] value)
    {
        BinaryPrimitives.WriteUInt16BigEndian(at, tag);
        BinaryPrimitives.WriteUInt16BigEndian(at[2..], type);
        BinaryPrimitives.WriteUInt32BigEndian(at[4..], count);
        value.CopyTo(at[8..]);
    }

    private static byte[] BigEndian(uint value)
    {
        var bytes = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, value);
        return bytes;
    }

    private static byte[] Rationals(params (uint Numerator, uint Denominator)[] values) =>
        [.. values.SelectMany(v => BigEndian(v.Numerator).Concat(BigEndian(v.Denominator)))];
}
