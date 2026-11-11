using Dialed.Client.Platform;
using Microsoft.JSInterop;

namespace Dialed.Web.Platform;

/// <summary>Live camera plus a self-hosted WebAssembly reader (wwwroot/lib/zxing); see platform.js <c>barcodeScan</c>.</summary>
internal sealed class BrowserBarcodeScanner(BrowserModule browser) : IBarcodeScanner
{
    public async Task<BarcodeResult> ScanAsync(string previewElementId, CancellationToken cancellationToken = default)
    {
        var module = await browser.GetAsync();

        // JS keeps running when the .NET side stops waiting, so a cancel has to stop the camera there.
        using var registration = cancellationToken.Register(() => _ = module.InvokeVoidAsync("barcodeStop"));
        try
        {
            var result = await module.InvokeAsync<ScanResult>("barcodeScan", CancellationToken.None, [previewElementId]);
            return result.Code is { Length: > 0 } code
                ? BarcodeResult.Read(code)
                : BarcodeResult.Failed(Enum.TryParse<BarcodeProblem>(result.Problem, out var problem) ? problem : BarcodeProblem.Unsupported);
        }
        catch (JSException)
        {
            return BarcodeResult.Failed(BarcodeProblem.Unsupported);
        }
    }

    private sealed record ScanResult(string? Code, string? Problem);
}
