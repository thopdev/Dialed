using Dialed.Client.Platform;
using Dialed.Client.Telemetry;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

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
        services.AddSingleton<IFileExport, BrowserFileExport>();
        services.AddSingleton<IErrorSink, BrowserErrorSink>();
        services.AddSingleton<IErrorReportingSettings, BrowserErrorReportingSettings>();
        services.AddSingleton<IPreferenceStore, BrowserPreferenceStore>();
        services.AddSingleton<ILoggerProvider>(sp => new ErrorReportingLoggerProvider(sp));
        services.AddSingleton<BrowserScriptErrors>();
        services.AddSingleton<IPasskeyClient, BrowserPasskeyClient>();
        services.AddSingleton<IStoragePersistence, BrowserStoragePersistence>();
        services.AddSingleton<IDeviceInfo, BrowserDeviceInfo>();
        services.AddSingleton<IWakeLock, BrowserWakeLock>();
        services.AddSingleton<ICrossTabLock, BrowserCrossTabLock>();
        services.AddSingleton<ISoundCue, BrowserSoundCue>();
        services.AddSingleton<BrowserAppLifecycle>();
        services.AddSingleton<IAppLifecycle>(sp => sp.GetRequiredService<BrowserAppLifecycle>());
        return services;
    }

    /// <summary>Starts the services that listen to browser events (JavaScript errors, connectivity, visibility).</summary>
    public static async Task StartBrowserPlatformAsync(this IServiceProvider services)
    {
        await services.GetRequiredService<BrowserScriptErrors>().InitializeAsync();
        await services.GetRequiredService<BrowserConnectivity>().InitializeAsync();
        await services.GetRequiredService<BrowserAppLifecycle>().InitializeAsync();
    }
}
