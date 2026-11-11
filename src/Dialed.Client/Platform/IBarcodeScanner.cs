namespace Dialed.Client.Platform;

/// <summary>Why a scan ended without a code.</summary>
public enum BarcodeProblem
{
    /// <summary>The person left the screen or cancelled.</summary>
    Cancelled,

    /// <summary>The camera permission was refused.</summary>
    PermissionDenied,

    /// <summary>No camera, or it could not be started.</summary>
    NoCamera,

    /// <summary>This platform cannot read barcodes (no camera API, or the reader could not load).</summary>
    Unsupported,
}

/// <summary>A scan's outcome: the code that was read, or why none was.</summary>
public sealed record BarcodeResult(string? Code, BarcodeProblem? Problem = null)
{
    public static BarcodeResult Read(string code) => new(code);

    public static BarcodeResult Failed(BarcodeProblem problem) => new(null, problem);
}

/// <summary>
/// Reads one barcode or QR code with the camera (#104). Web: the live camera in the element with id <paramref name="previewElementId"/>, decoded by a
/// self-hosted WebAssembly reader; MAUI: a native scanner that ignores the id and shows its own screen. Stops the camera when it has a code, when the
/// token is cancelled, or on failure.
/// </summary>
public interface IBarcodeScanner
{
    /// <summary>Starts the camera and waits for the first valid code. Call it from a screen that has the preview element.</summary>
    Task<BarcodeResult> ScanAsync(string previewElementId, CancellationToken cancellationToken = default);
}

/// <summary>The default for platforms without a scanner: every scan is "unsupported", so the screen offers typing the number instead.</summary>
public sealed class UnsupportedBarcodeScanner : IBarcodeScanner
{
    public static readonly UnsupportedBarcodeScanner Instance = new();

    public Task<BarcodeResult> ScanAsync(string previewElementId, CancellationToken cancellationToken = default) =>
        Task.FromResult(BarcodeResult.Failed(BarcodeProblem.Unsupported));
}
