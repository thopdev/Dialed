using Dialed.Client.Platform;
using Microsoft.JSInterop;

namespace Dialed.Web.Platform;

internal sealed class BrowserPhotoCapture(BrowserModule browser) : IPhotoCapture
{
    public async Task<CapturedPhoto?> CaptureAsync(PhotoSource source, int maxDimension = 2048, CancellationToken cancellationToken = default)
    {
        var module = await browser.GetAsync();
        var result = await module.InvokeAsync<PhotoResult?>(
            "capturePhoto", cancellationToken, [source == PhotoSource.Camera, maxDimension]);
        return result is null
            ? null
            : new CapturedPhoto(Convert.FromBase64String(result.Base64), result.ContentType, result.FileName);
    }

    private sealed record PhotoResult(string Base64, string ContentType, string FileName);
}
