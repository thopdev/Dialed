using Dialed.Client.Platform;
using Microsoft.JSInterop;

namespace Dialed.Web.Platform;

internal sealed class BrowserFileExport(BrowserModule browser) : IFileExport
{
    public async Task<bool> SaveAsync(string fileName, string contentType, byte[] content) =>
        await (await browser.GetAsync()).InvokeAsync<bool>("saveFile", fileName, contentType, content);
}
