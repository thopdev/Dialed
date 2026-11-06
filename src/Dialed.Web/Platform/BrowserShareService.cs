using Dialed.Client.Platform;
using Microsoft.JSInterop;

namespace Dialed.Web.Platform;

internal sealed class BrowserShareService(BrowserModule browser) : IShareService
{
    public async Task<bool> IsSupportedAsync() =>
        await (await browser.GetAsync()).InvokeAsync<bool>("canShare");

    public async Task<bool> ShareAsync(ShareRequest request) =>
        await (await browser.GetAsync()).InvokeAsync<bool>("share", request.Title, request.Text, request.Url);
}
