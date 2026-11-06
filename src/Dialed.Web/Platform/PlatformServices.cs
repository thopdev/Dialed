using Dialed.Client.Platform;
using Microsoft.Extensions.DependencyInjection;

namespace Dialed.Web.Platform;

internal static class PlatformServices
{
    /// <summary>Registers the browser implementations of every platform interface in Dialed.Client.</summary>
    public static IServiceCollection AddBrowserPlatform(this IServiceCollection services)
    {
        services.AddSingleton<BrowserModule>();
        services.AddSingleton<IPhotoCapture, BrowserPhotoCapture>();
        services.AddSingleton<ILocalFileStore, BrowserLocalFileStore>();
        services.AddSingleton<BrowserConnectivity>();
        services.AddSingleton<IConnectivity>(sp => sp.GetRequiredService<BrowserConnectivity>());
        services.AddSingleton<IShareService, BrowserShareService>();
        services.AddSingleton<IPasskeyClient, BrowserPasskeyClient>();
        services.AddSingleton<IStoragePersistence, BrowserStoragePersistence>();
        services.AddSingleton<IWakeLock, BrowserWakeLock>();
        services.AddSingleton<ISoundCue, BrowserSoundCue>();
        services.AddSingleton<BrowserAppLifecycle>();
        services.AddSingleton<IAppLifecycle>(sp => sp.GetRequiredService<BrowserAppLifecycle>());
        return services;
    }

    /// <summary>Starts the services that listen to browser events (connectivity, visibility).</summary>
    public static async Task StartBrowserPlatformAsync(this IServiceProvider services)
    {
        await services.GetRequiredService<BrowserConnectivity>().InitializeAsync();
        await services.GetRequiredService<BrowserAppLifecycle>().InitializeAsync();
    }
}
