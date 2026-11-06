namespace Dialed.Client.Platform;

public enum PhotoSource
{
    /// <summary>Take a new photo with the camera.</summary>
    Camera,

    /// <summary>Pick an existing photo from the photo library.</summary>
    Library,
}

/// <summary>A photo as JPEG bytes, already scaled down so it is cheap to store and upload.</summary>
public sealed record CapturedPhoto(byte[] Content, string ContentType, string FileName);

/// <summary>Takes or picks a photo (bean label, bag, brew). Web: file input; MAUI: MediaPicker.</summary>
public interface IPhotoCapture
{
    /// <summary>Returns the photo, or <c>null</c> when the user cancels. Call it from a tap handler (browsers require that).</summary>
    /// <param name="source">Camera or photo library.</param>
    /// <param name="maxDimension">Longest side in pixels; larger photos are scaled down.</param>
    Task<CapturedPhoto?> CaptureAsync(PhotoSource source, int maxDimension = 2048, CancellationToken cancellationToken = default);
}
