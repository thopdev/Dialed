namespace Dialed.Images;

/// <summary>The file cannot be read as a photo. Retrying does not help, so the event goes straight to the poison list.</summary>
public sealed class InvalidPhotoException(string message, Exception? inner = null) : Exception(message, inner);

/// <param name="Original">The photo to keep: the input itself when it was already clean, otherwise a re-encoded copy without metadata.</param>
/// <param name="OriginalChanged">Whether <paramref name="Original"/> differs from the input and has to be written back.</param>
/// <param name="Thumbnails">JPEG per requested size, the longest side at most that size.</param>
public sealed record ProcessedPhoto(byte[] Original, bool OriginalChanged, IReadOnlyDictionary<int, byte[]> Thumbnails);

/// <summary>Pure image work: no storage, no records. Safe to call from several threads.</summary>
public interface IPhotoImageProcessor
{
    /// <summary>Strips metadata (applying the EXIF orientation first, so nothing ends up sideways) and makes thumbnails.</summary>
    /// <exception cref="InvalidPhotoException">The bytes are not a JPEG that can be decoded, or the photo is unreasonably large.</exception>
    ProcessedPhoto Process(byte[] jpeg, IReadOnlyList<int> thumbnailSizes);
}
