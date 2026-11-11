using Dialed.Client.Account;
using Dialed.Client.Data;
using Dialed.Client.Friends;
using Dialed.Client.Households;
using Dialed.Client.Platform;
using Dialed.Client.Storage;
using Dialed.Client.Sync;
using Dialed.Client.Telemetry;
using Dialed.Domain.DialIn;
using Dialed.Domain.Model;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Dialed.Client;

public static class ClientServices
{
    /// <summary>The data layer every host needs: clock, current user and household, and a repository per record type. Hosts call this once.</summary>
    public static IServiceCollection AddDialedData(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<ICurrentContext, LocalContext>();
        services.TryAddSingleton<ILocalStore, InMemoryLocalStore>();
        services.TryAddSingleton<StorageStatus>();
        services.TryAddSingleton(typeof(IRepository<>), typeof(InMemoryRepository<>));
        return services;
    }

    /// <summary>
    /// Reads everything the <see cref="ILocalStore"/> holds back into the repositories and the sync state. Hosts call this once
    /// after building the service provider and before the first screen renders.
    /// </summary>
    public static async Task LoadLocalDataAsync(this IServiceProvider services, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(services);

        List<ILoadable> loadables =
        [
            Loadable<Bean>(services),
            Loadable<Bag>(services),
            Loadable<Brew>(services),
            Loadable<Recipe>(services),
            Loadable<EquipmentItem>(services),
            Loadable<Vendor>(services),
            Loadable<Photo>(services),
            Loadable<MethodDefault>(services),
            Loadable<DialInFeedback>(services),
        ];
        if (services.GetService<ISyncState>() is ILoadable state)
        {
            loadables.Add(state);
        }

        if (services.GetService<Uploads.IUploadQueue>() is ILoadable uploads)
        {
            loadables.Add(uploads);
        }

        if (services.GetService<Scan.IScanQueue>() is ILoadable scans)
        {
            loadables.Add(scans);
        }

        foreach (var loadable in loadables)
        {
            await loadable.LoadAsync(cancellationToken);
        }
    }

    private static ILoadable Loadable<T>(IServiceProvider services)
        where T : SyncEntity =>
        (ILoadable)services.GetRequiredService<IRepository<T>>();

    /// <summary>
    /// Sync for the record types the server knows (see the API's table list). The host registers <see cref="ISyncTransport"/>,
    /// <see cref="Platform.IConnectivity"/> and <see cref="Platform.IAppLifecycle"/>, and starts <see cref="SyncCoordinator"/> once.
    /// Without a sign-in there is no access token, so nothing is sent and everything stays on the device.
    /// </summary>
    public static IServiceCollection AddDialedSync(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<IAccessTokenProvider, NotSignedIn>();
        services.TryAddSingleton<IErrorSink, NullErrorSink>();
        services.TryAddSingleton<IErrorReportingSettings, InMemoryErrorReportingSettings>();
        services.TryAddSingleton(sp => new ErrorReporter(
            sp.GetRequiredService<IErrorSink>(),
            sp.GetRequiredService<IErrorReportingSettings>(),
            sp.GetService<TimeProvider>() ?? TimeProvider.System,
            sp.GetService<SyncCoordinator>()));
        services.TryAddSingleton(sp => new InMemorySyncState(sp.GetService<ILocalStore>()));
        services.TryAddSingleton<ISyncState>(sp => sp.GetRequiredService<InMemorySyncState>());
        services.TryAddSingleton(sp => new SyncEngine(
            [
                Table<Bean>(sp, "beans"),
                Table<Bag>(sp, "bags"),
                Table<Brew>(sp, "brews"),
                Table<Recipe>(sp, "recipes"),
                Table<EquipmentItem>(sp, "equipment"),
                Table<Vendor>(sp, "vendors"),
                Table<Photo>(sp, "photos"),
                Table<MethodDefault>(sp, "methoddefaults"),
                Table<DialInFeedback>(sp, "dialinfeedback"),
            ],
            sp.GetRequiredService<ISyncState>()));
        services.TryAddSingleton<ICrossTabLock>(SingleWindowLock.Instance);
        services.TryAddSingleton<IBarcodeScanner>(UnsupportedBarcodeScanner.Instance);
        services.TryAddSingleton<SyncCoordinator>();
        return services;
    }

    private static SyncTable<T> Table<T>(IServiceProvider sp, string name)
        where T : SyncEntity =>
        new SyncTable<T>(
            name,
            (ISyncRepository<T>)sp.GetRequiredService<IRepository<T>>(),
            sp.GetRequiredService<ISyncState>(),
            sp.GetRequiredService<ISyncTransport>(),
            sp.GetRequiredService<TimeProvider>()).Attach();

    private sealed class NotSignedIn : IAccessTokenProvider
    {
        public Task<string?> GetAccessTokenAsync(CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);
    }

    /// <summary>Registers label scanning (#83). <paramref name="scanner"/> creates the API client, so the host decides the base address.</summary>
    public static IServiceCollection AddDialedScan(this IServiceCollection services, Func<IServiceProvider, Scan.ILabelScanner> scanner)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(scanner);

        services.TryAddSingleton(scanner);

        // Scans taken offline wait here until their photo has uploaded (#83). Call PendingScanQueue.StartAsync once after loading the local data.
        services.TryAddSingleton(sp => new Scan.PendingScanQueue(
            sp.GetRequiredService<ILocalStore>(),
            sp.GetRequiredService<IRepository<Photo>>(),
            sp.GetRequiredService<Scan.ILabelScanner>(),
            sp.GetRequiredService<IConnectivity>(),
            sp.GetRequiredService<IAppLifecycle>(),
            sp.GetService<TimeProvider>() ?? TimeProvider.System));
        services.TryAddSingleton<Scan.IScanQueue>(sp => sp.GetRequiredService<Scan.PendingScanQueue>());
        return services;
    }

    /// <summary>
    /// Registers the photo upload queue (#68). <paramref name="api"/> creates the API client, so the host decides the base address. The host
    /// registers <see cref="Platform.IConnectivity"/>, <see cref="Platform.IAppLifecycle"/> and <see cref="ILocalFileStore"/>, and calls
    /// <see cref="Uploads.PhotoUploadQueue.StartAsync"/> once after loading the local data.
    /// </summary>
    public static IServiceCollection AddDialedUploads(this IServiceCollection services, Func<IServiceProvider, Uploads.IPhotoUploadApi> api)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(api);

        services.TryAddSingleton(api);
        services.TryAddSingleton(sp => new Uploads.PhotoUploadQueue(
            sp.GetRequiredService<ILocalStore>(),
            sp.GetRequiredService<ILocalFileStore>(),
            sp.GetRequiredService<IRepository<Photo>>(),
            sp.GetRequiredService<Uploads.IPhotoUploadApi>(),
            sp.GetRequiredService<IConnectivity>(),
            sp.GetRequiredService<IAppLifecycle>(),
            sp.GetService<TimeProvider>() ?? TimeProvider.System,
            sp.GetService<SyncCoordinator>()));
        services.TryAddSingleton<Uploads.IUploadQueue>(sp => sp.GetRequiredService<Uploads.PhotoUploadQueue>());
        return services;
    }

    /// <summary>Registers the recipe assistant (#87). <paramref name="assistant"/> creates the API client, so the host decides the base address.</summary>
    public static IServiceCollection AddDialedRecipeAssistant(this IServiceCollection services, Func<IServiceProvider, Recipes.IRecipeAssistant> assistant)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(assistant);

        services.TryAddSingleton(assistant);
        return services;
    }

    /// <summary>Registers the dial-in assistant (#86). <paramref name="assistant"/> creates the API client, so the host decides the base address.</summary>
    public static IServiceCollection AddDialedDialInAssistant(this IServiceCollection services, Func<IServiceProvider, DialIn.IDialInAssistant> assistant)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(assistant);

        services.TryAddSingleton(assistant);
        return services;
    }

    /// <summary>
    /// Accounts: the sign-in session, the API calls, the passkey flow and the access token for sync. Call after
    /// <see cref="AddDialedData"/> and <see cref="AddDialedSync"/>; the host registers <see cref="AccountApi"/>'s HttpClient through
    /// <paramref name="api"/> and an <see cref="Platform.IPasskeyClient"/> (and, to keep the sign-in, an <see cref="ISessionStore"/>).
    /// </summary>
    public static IServiceCollection AddDialedAccount(this IServiceCollection services, Func<IServiceProvider, AccountApi> api, Func<IServiceProvider, HouseholdApi> households, Func<IServiceProvider, AccountDataApi>? accountData = null, Func<IServiceProvider, FriendsApi>? friends = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(api);
        ArgumentNullException.ThrowIfNull(households);

        services.TryAddSingleton<ISessionStore, InMemorySessionStore>();
        services.TryAddSingleton<AuthSession>();
        services.TryAddSingleton(api);
        services.TryAddSingleton(households);
        if (accountData is not null)
        {
            services.TryAddSingleton(accountData);
            services.TryAddSingleton(sp => new AccountDataService(
                sp.GetRequiredService<AccountDataApi>(),
                sp.GetRequiredService<IFileExport>(),
                sp.GetRequiredService<IPasskeyClient>(),
                sp.GetRequiredService<AuthSession>(),
                sp.GetService<TimeProvider>() ?? TimeProvider.System,
                sp.GetService<ILocalStore>()));
        }

        if (accountData is not null)
        {
            services.TryAddSingleton(sp => new PasskeyManager(
                sp.GetRequiredService<AccountDataApi>(),
                sp.GetRequiredService<IPasskeyClient>(),
                sp.GetRequiredService<AccountService>()));
        }

        if (friends is not null)
        {
            services.TryAddSingleton(friends);
            services.TryAddSingleton<Friends.FriendCopyService>();
        }

        services.TryAddSingleton<AccountService>();
        services.TryAddSingleton<HouseholdManager>();

        // The signed-in person (and their household) stamp new records, and their token authorises sync.
        services.RemoveAll<ICurrentContext>();
        services.AddSingleton<ICurrentContext, SessionContext>();
        services.RemoveAll<IAccessTokenProvider>();
        services.AddSingleton<IAccessTokenProvider, SessionTokenProvider>();
        return services;
    }
}
