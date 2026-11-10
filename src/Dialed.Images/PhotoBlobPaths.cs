using System.Diagnostics.CodeAnalysis;
using Dialed.Domain.Model;

namespace Dialed.Images;

/// <summary>
/// Blob names (ADR-0008). Originals live under <c>photos/</c>, thumbnails under <c>thumbnails/</c>: writing a thumbnail never
/// matches the blob trigger on <c>photos/</c>, so processing cannot loop.
/// </summary>
public static class PhotoBlobPaths
{
    public const string OriginalsFolder = "photos";

    public const string ThumbnailsFolder = "thumbnails";

    public static string Original(string householdId, string photoId) => $"{OriginalsFolder}/{Check(householdId)}/{Check(photoId)}.jpg";

    /// <summary>The value for <see cref="Photo.ThumbPath"/>: thumbnails are this plus <c>-{size}.jpg</c>.</summary>
    public static string ThumbBase(string householdId, string photoId) => $"{ThumbnailsFolder}/{Check(householdId)}/{Check(photoId)}";

    public static string Thumbnail(string householdId, string photoId, int size) => Photo.ThumbnailPathFor(ThumbBase(householdId, photoId), size);

    /// <summary>Reads household and photo id from an original's path, <c>photos/{householdId}/{photoId}.jpg</c>.</summary>
    public static bool TryParseOriginal(string path, [NotNullWhen(true)] out string? householdId, [NotNullWhen(true)] out string? photoId)
    {
        householdId = photoId = null;
        var parts = path.Split('/');
        if (parts.Length != 3 || parts[0] != OriginalsFolder || !parts[2].EndsWith(".jpg", StringComparison.Ordinal))
        {
            return false;
        }

        var id = parts[2][..^".jpg".Length];
        if (!IsSafe(parts[1]) || !IsSafe(id))
        {
            return false;
        }

        (householdId, photoId) = (parts[1], id);
        return true;
    }

    private static bool IsSafe(string segment) =>
        segment.Length is > 0 and <= 100 && segment is not "." and not ".." && segment.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    private static string Check(string segment) =>
        IsSafe(segment) ? segment : throw new ArgumentException("Ids in blob paths are letters, digits, '-' and '_' only.", nameof(segment));
}
