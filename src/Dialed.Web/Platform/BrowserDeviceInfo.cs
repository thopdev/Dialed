using Dialed.Client.Platform;
using Microsoft.JSInterop;

namespace Dialed.Web.Platform;

internal sealed class BrowserDeviceInfo(BrowserModule browser) : IDeviceInfo
{
    public async Task<string> DescribeAsync()
    {
        try
        {
            return await (await browser.GetAsync()).InvokeAsync<string>("describeDevice");
        }
        catch (Exception)
        {
            return "unknown";
        }
    }
}
