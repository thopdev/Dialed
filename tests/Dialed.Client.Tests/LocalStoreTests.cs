using Dialed.Client;
using Dialed.Client.Data;
using Dialed.Client.Storage;
using Dialed.Client.Sync;
using Dialed.Domain.Model;
using Microsoft.Extensions.DependencyInjection;

namespace Dialed.Client.Tests;

public sealed class LocalStoreTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class Context : ICurrentContext
    {
        public string UserId => "u1";

        public string HouseholdId => "h1";
    }

    private static ServiceProvider Host(ILocalStore store) =>
        new ServiceCollection()
            .AddSingleton(store)
            .AddDialedData()
            .AddSingleton<ICurrentContext, Context>()
            .AddSingleton<ISyncState>(sp => new InMemorySyncState(sp.GetRequiredService<ILocalStore>()))
            .BuildServiceProvider();

    [Fact]
    public async Task Saved_records_come_back_after_a_restart()
    {
        var store = new InMemoryLocalStore();
        await using (var first = Host(store))
        {
            var beans = first.GetRequiredService<IRepository<Bean>>();
            var saved = await beans.SaveAsync(new Bean { Name = "Gesha", TastingNotes = ["jasmine", "peach"] }, Ct);
            await first.GetRequiredService<IRepository<Brew>>().SaveAsync(new Brew { BagId = "bag1", MethodCode = "v60", DoseG = 18m }, Ct);
            await beans.SaveAsync(new Bean { Name = "Gone" }, Ct);
            await beans.DeleteAsync((await beans.ListAsync(Ct)).Single(b => b.Name == "Gone").Id, Ct);
        }

        await using var second = Host(store);
        await second.LoadLocalDataAsync(Ct);

        var loaded = await second.GetRequiredService<IRepository<Bean>>().ListAsync(Ct);
        loaded.Select(b => b.Name).ShouldBe(["Gesha"]);
        loaded[0].TastingNotes.ShouldBe(["jasmine", "peach"]);
        loaded[0].Version.ShouldBe(1);
        (await second.GetRequiredService<IRepository<Brew>>().ListAsync(Ct)).Single().DoseG.ShouldBe(18m);
    }

    [Fact]
    public async Task A_deleted_record_stays_deleted_after_a_restart()
    {
        var store = new InMemoryLocalStore();
        string id;
        await using (var first = Host(store))
        {
            var repo = first.GetRequiredService<IRepository<Vendor>>();
            id = (await repo.SaveAsync(new Vendor { Name = "Local roaster" }, Ct)).Id;
            await repo.DeleteAsync(id, Ct);
        }

        await using var second = Host(store);
        await second.LoadLocalDataAsync(Ct);

        var repository = (ISyncRepository<Vendor>)second.GetRequiredService<IRepository<Vendor>>();
        (await repository.ListAsync(Ct)).ShouldBeEmpty();
        (await repository.GetIncludingDeletedAsync(id, Ct))!.Deleted.ShouldBeTrue(); // still there to sync the deletion
    }

    [Fact]
    public async Task Sync_state_survives_a_restart()
    {
        var store = new InMemoryLocalStore();
        await using (var first = Host(store))
        {
            var state = first.GetRequiredService<ISyncState>();
            await state.EnqueueAsync("beans", "b1", Ct);
            await state.EnqueueAsync("brews", "r1", Ct);
            await state.EnqueueAsync("vendors", "v1", Ct);
            await state.RemoveAsync((await state.PendingAsync(Ct))[2].Sequence, Ct);
            await state.RecordFailureAsync((await state.PendingAsync(Ct))[0].Sequence, "offline", Ct);
            await state.SetServerVersionAsync("beans", "b1", "\"v7\"", Ct);
            await state.SetWatermarkAsync("beans", new SyncWatermark(new DateTimeOffset(2026, 11, 9, 8, 0, 0, TimeSpan.Zero), "b1"), Ct);
            await state.AddConflictAsync(new ConflictLogEntry("beans", "b1", new DateTimeOffset(2026, 11, 9, 8, 1, 0, TimeSpan.Zero), "{}"), Ct);
        }

        await using var second = Host(store);
        await second.LoadLocalDataAsync(Ct);

        var restored = second.GetRequiredService<ISyncState>();
        var pending = await restored.PendingAsync(Ct);
        pending.Select(e => (e.Table, e.EntityId)).ShouldBe([("beans", "b1"), ("brews", "r1")]);
        pending[0].Attempts.ShouldBe(1);
        pending[0].LastError.ShouldBe("offline");
        (await restored.GetServerVersionAsync("beans", "b1", Ct)).ShouldBe("\"v7\"");
        (await restored.GetWatermarkAsync("beans", Ct))!.Id.ShouldBe("b1");
        (await restored.ConflictsAsync(Ct)).Single().EntityId.ShouldBe("b1");

        // New entries continue after the stored ones instead of reusing a sequence number.
        await restored.EnqueueAsync("bags", "g1", Ct);
        var all = await restored.PendingAsync(Ct);
        all.Select(e => e.Sequence).Distinct().Count().ShouldBe(3);
    }

    [Fact]
    public async Task Resetting_server_state_is_stored_too_but_keeps_the_outbox()
    {
        var store = new InMemoryLocalStore();
        await using (var first = Host(store))
        {
            var state = first.GetRequiredService<ISyncState>();
            await state.EnqueueAsync("beans", "b1", Ct);
            await state.SetServerVersionAsync("beans", "b1", "v1", Ct);
            await state.SetWatermarkAsync("beans", new SyncWatermark(DateTimeOffset.UnixEpoch, "x"), Ct);
            await state.ResetServerStateAsync(Ct);
        }

        await using var second = Host(store);
        await second.LoadLocalDataAsync(Ct);

        var restored = second.GetRequiredService<ISyncState>();
        (await restored.GetWatermarkAsync("beans", Ct)).ShouldBeNull();
        (await restored.GetServerVersionAsync("beans", "b1", Ct)).ShouldBeNull();
        (await restored.PendingAsync(Ct)).Count.ShouldBe(1);
    }

    [Fact]
    public async Task Records_pulled_from_the_server_are_stored_without_entering_the_outbox()
    {
        var store = new InMemoryLocalStore();
        await using (var first = Host(store))
        {
            var repo = (ISyncRepository<Bean>)first.GetRequiredService<IRepository<Bean>>();
            await repo.ApplyRemoteAsync(new Bean { Name = "From server", HouseholdId = "h1" }, Ct);
        }

        await using var second = Host(store);
        await second.LoadLocalDataAsync(Ct);

        (await second.GetRequiredService<IRepository<Bean>>().ListAsync(Ct)).Single().Name.ShouldBe("From server");
    }

    [Fact]
    public async Task Clearing_the_store_leaves_nothing_to_load()
    {
        var store = new InMemoryLocalStore();
        await using (var first = Host(store))
        {
            await first.GetRequiredService<IRepository<Bean>>().SaveAsync(new Bean { Name = "Gesha" }, Ct);
        }

        await store.ClearAsync(Ct);

        await using var second = Host(store);
        await second.LoadLocalDataAsync(Ct);
        (await second.GetRequiredService<IRepository<Bean>>().ListAsync(Ct)).ShouldBeEmpty();
    }

    [Fact]
    public async Task Without_a_durable_store_loading_changes_nothing()
    {
        await using var provider = new ServiceCollection().AddDialedData().BuildServiceProvider();
        var beans = provider.GetRequiredService<IRepository<Bean>>();
        await beans.SaveAsync(new Bean { Name = "Gesha" }, Ct);

        await provider.LoadLocalDataAsync(Ct);

        (await beans.ListAsync(Ct)).Count.ShouldBe(1);
    }
}
