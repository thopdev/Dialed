using Dialed.Client.Platform;

namespace Dialed.Web.Platform;

/// <summary>Files in the browser's origin private file system (not visible to the user, kept with the site data).</summary>
internal sealed class BrowserLocalFileStore(BrowserModule browser) : ILocalFileStore
{
    public async Task SaveAsync(string path, byte[] content, CancellationToken cancellationToken = default) =>
        await (await browser.GetAsync()).InvokeVoidAsync("fileSave", cancellationToken, [path, Convert.ToBase64String(content)]);

    public async Task<byte[]?> ReadAsync(string path, CancellationToken cancellationToken = default)
    {
        var base64 = await (await browser.GetAsync()).InvokeAsync<string?>("fileRead", cancellationToken, [path]);
        return base64 is null ? null : Convert.FromBase64String(base64);
    }

    public async Task<bool> ExistsAsync(string path, CancellationToken cancellationToken = default) =>
        await (await browser.GetAsync()).InvokeAsync<bool>("fileExists", cancellationToken, [path]);

    public async Task DeleteAsync(string path, CancellationToken cancellationToken = default) =>
        await (await browser.GetAsync()).InvokeVoidAsync("fileDelete", cancellationToken, [path]);

    public async Task<IReadOnlyList<string>> ListAsync(string folder, CancellationToken cancellationToken = default) =>
        await (await browser.GetAsync()).InvokeAsync<string[]>("fileList", cancellationToken, [folder]);
}
