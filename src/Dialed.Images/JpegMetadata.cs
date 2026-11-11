namespace Dialed.Images;

/// <summary>One marker segment of a JPEG file header, for example APP1 (0xE1) with EXIF, XMP or GPS data.</summary>
public sealed record JpegSegment(byte Marker, int Offset, int Length)
{
    public bool IsApp => Marker is >= 0xE0 and <= 0xEF;

    /// <summary>Segments that can carry who, where or with what a photo was taken: APP1 (EXIF, GPS, XMP), APP3 to APP13 (IPTC, maker data), APP15 and comments.</summary>
    public bool IsMetadata => Marker is 0xFE || (IsApp && Marker is not (0xE0 or 0xE2 or 0xEE));
}

/// <summary>
/// Reads the header segments of a JPEG without decoding it. Allowed after stripping: APP0 (JFIF), APP2 (ICC colour profile) and APP14
/// (Adobe colour transform); none of them describe the person or the place.
/// </summary>
public static class JpegMetadata
{
    /// <summary>The segments before the image data (start of scan). Throws <see cref="InvalidPhotoException"/> for anything that is not a JPEG.</summary>
    public static IReadOnlyList<JpegSegment> ReadSegments(ReadOnlySpan<byte> jpeg)
    {
        if (jpeg.Length < 4 || jpeg[0] != 0xFF || jpeg[1] != 0xD8)
        {
            throw new InvalidPhotoException("Not a JPEG file.");
        }

        var segments = new List<JpegSegment>();
        var position = 2;
        while (position + 4 <= jpeg.Length)
        {
            if (jpeg[position] != 0xFF)
            {
                throw new InvalidPhotoException("Broken JPEG header.");
            }

            var marker = jpeg[position + 1];
            if (marker == 0xFF)
            {
                position++; // fill byte
                continue;
            }

            if (marker is 0xD9 or 0xDA)
            {
                break; // end of image or start of scan: no more header segments
            }

            var length = (jpeg[position + 2] << 8) | jpeg[position + 3];
            if (length < 2 || position + 2 + length > jpeg.Length)
            {
                throw new InvalidPhotoException("Broken JPEG header.");
            }

            segments.Add(new JpegSegment(marker, position, length + 2));
            position += 2 + length;
        }

        return segments;
    }

    /// <summary>Whether the file still has EXIF, GPS, XMP, IPTC or comment segments.</summary>
    public static bool HasMetadata(ReadOnlySpan<byte> jpeg) => ReadSegments(jpeg).Any(segment => segment.IsMetadata);
}
