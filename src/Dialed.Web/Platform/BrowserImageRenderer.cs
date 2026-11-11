using Dialed.Client.Platform;
using Microsoft.JSInterop;

namespace Dialed.Web.Platform;

internal sealed class BrowserImageRenderer(BrowserModule browser) : IImageRenderer
{
    public async Task<byte[]?> SvgToPngAsync(string svg, int width, int height)
    {
        try
        {
            return await (await browser.GetAsync()).InvokeAsync<byte[]?>("svgToPng", svg, width, height);
        }
        catch (JSException)
        {
            return null;
        }
    }
}
