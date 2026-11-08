using Dialed.Client;
using Dialed.Client.Data;
using Dialed.Domain.Model;
using Microsoft.Extensions.DependencyInjection;

namespace Dialed.Client.Tests;

public sealed class ClientServicesTests
{
    [Fact]
    public async Task The_data_layer_works_as_registered_for_a_host()
    {
        await using var provider = new ServiceCollection().AddDialedData().BuildServiceProvider();

        var beans = provider.GetRequiredService<IRepository<Bean>>();
        provider.GetRequiredService<IRepository<Bean>>().ShouldBeSameAs(beans);
        provider.GetRequiredService<TimeProvider>().ShouldNotBeNull();
        provider.GetRequiredService<ICurrentContext>().HouseholdId.ShouldNotBeNullOrEmpty();
        provider.GetRequiredService<IRepository<Vendor>>().ShouldNotBeSameAs((object)beans);
    }
}
