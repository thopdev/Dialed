using Dialed.Client.Data;
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
}
