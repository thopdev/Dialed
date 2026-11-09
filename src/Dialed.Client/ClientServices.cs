using Dialed.Client.Account;
using Dialed.Client.Data;
using Dialed.Client.Sync;
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
        services.TryAddSingleton(typeof(IRepository<>), typeof(InMemoryRepository<>));
        return services;
    }

    /// <summary>
    /// Sync for the record types the server knows (see the API's table list). The host registers <see cref="ISyncTransport"/>,
    /// <see cref="Platform.IConnectivity"/> and <see cref="Platform.IAppLifecycle"/>, and starts <see cref="SyncCoordinator"/> once.
    /// Without a sign-in there is no access token, so nothing is sent and everything stays on the device.
    /// </summary>
    public static IServiceCollection AddDialedSync(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<IAccessTokenProvider, NotSignedIn>();
        services.TryAddSingleton<InMemorySyncState>();
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
            ],
            sp.GetRequiredService<ISyncState>()));
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

    /// <summary>
    /// Accounts: the sign-in session, the API calls, the passkey flow and the access token for sync. Call after
    /// <see cref="AddDialedData"/> and <see cref="AddDialedSync"/>; the host registers <see cref="AccountApi"/>'s HttpClient through
    /// <paramref name="api"/> and an <see cref="Platform.IPasskeyClient"/> (and, to keep the sign-in, an <see cref="ISessionStore"/>).
    /// </summary>
    public static IServiceCollection AddDialedAccount(this IServiceCollection services, Func<IServiceProvider, AccountApi> api)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(api);

        services.TryAddSingleton<ISessionStore, InMemorySessionStore>();
        services.TryAddSingleton<AuthSession>();
        services.TryAddSingleton(api);
        services.TryAddSingleton<AccountService>();

        // The signed-in person (and their household) stamp new records, and their token authorises sync.
        services.RemoveAll<ICurrentContext>();
        services.AddSingleton<ICurrentContext, SessionContext>();
        services.RemoveAll<IAccessTokenProvider>();
        services.AddSingleton<IAccessTokenProvider, SessionTokenProvider>();
        return services;
    }
}
