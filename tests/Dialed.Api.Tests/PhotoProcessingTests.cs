using System.Diagnostics;
using System.Text.Json.Nodes;
using Dialed.Api.Photos;
using Dialed.Api.Sync;
using Dialed.Images;
using Dialed.Images.Tests;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Dialed.Api.Tests;

/// <summary>#69: the in-process stand-in for the blob-triggered Function, and the record update it makes.</summary>
public sealed class PhotoProcessingTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Task<StoreResult> PushPhoto(ISyncStore store, string household, string id, string author = "alice") =>
        store.PushAsync(household, author, "photos", id, new JsonObject { ["id"] = id, ["contentType"] = "image/jpeg" }, null, Ct);

    [Fact]
    public async Task A_photo_written_to_storage_is_stripped_and_thumbnailed_in_the_background_and_the_app_learns_the_thumb_path()
    {
        var store = factory.Services.GetRequiredService<ISyncStore>();
        var blobs = factory.Services.GetRequiredService<IPhotoBlobStore>();
        var trigger = factory.Services.GetRequiredService<InMemoryPhotoTrigger>();
        _ = factory.Server; // starts the host and its background services
        await PushPhoto(store, "photo-household", "uploaded");

        await blobs.WriteAsync(PhotoBlobPaths.Original("photo-household", "uploaded"), TestPhotos.WithGps(1200, 900), Ct);
        await WaitUntilIdle(trigger);

        JpegMetadata.HasMetadata((await blobs.ReadAsync("photos/photo-household/uploaded.jpg", Ct))!).ShouldBeFalse();
        TestPhotos.Size((await blobs.ReadAsync("thumbnails/photo-household/uploaded-256.jpg", Ct))!).ShouldBe((256, 192));
        TestPhotos.Size((await blobs.ReadAsync("thumbnails/photo-household/uploaded-768.jpg", Ct))!).ShouldBe((768, 576));
        var row = await store.GetAsync("photo-household", "photos", "uploaded", Ct);
        row!.Entity["thumbPath"]!.GetValue<string>().ShouldBe("thumbnails/photo-household/uploaded");
        row.Entity["updatedBy"]!.GetValue<string>().ShouldBe("alice");
        trigger.Poisoned.ShouldNotContain(p => p.BlobPath.Contains("uploaded", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Setting_the_thumb_path_twice_bumps_the_version_once_and_an_unknown_photo_reports_missing()
    {
        var store = new InMemorySyncStore(TimeProvider.System);
        var records = new SyncStorePhotoRecords(store);
        await PushPhoto(store, "h", "p", author: "bob");

        (await records.SetThumbPathAsync("h", "p", "thumbnails/h/p", Ct)).ShouldBeTrue();
        (await records.SetThumbPathAsync("h", "p", "thumbnails/h/p", Ct)).ShouldBeTrue();
        (await records.SetThumbPathAsync("h", "nope", "thumbnails/h/nope", Ct)).ShouldBeFalse();

        var row = await store.GetAsync("h", "photos", "p", Ct);
        row!.Version.ShouldBe(2);
        row.Entity["createdBy"]!.GetValue<string>().ShouldBe("bob");
    }

    [Fact]
    public async Task A_photo_whose_record_never_arrives_is_retried_five_times_then_poisoned()
    {
        var (trigger, blobs, _) = Standalone();
        await blobs.WriteAsync("photos/h/orphan.jpg", TestPhotos.Plain(300, 200), Ct);

        (await trigger.RunAsync("photos/h/orphan.jpg", Ct)).ShouldBeNull();

        var poisoned = trigger.Poisoned.ShouldHaveSingleItem();
        poisoned.BlobPath.ShouldBe("photos/h/orphan.jpg");
        poisoned.Attempts.ShouldBe(InMemoryPhotoTrigger.MaxAttempts);
    }

    [Fact]
    public async Task A_file_that_is_not_a_photo_is_poisoned_at_once()
    {
        var (trigger, blobs, store) = Standalone();
        await PushPhoto(store, "h", "fake");
        await blobs.WriteAsync("photos/h/fake.jpg", "GIF89a"u8.ToArray(), Ct);

        (await trigger.RunAsync("photos/h/fake.jpg", Ct)).ShouldBeNull();

        trigger.Poisoned.ShouldHaveSingleItem().Attempts.ShouldBe(1);
    }

    [Fact]
    public async Task A_record_that_arrives_late_is_picked_up_by_a_retry()
    {
        var (trigger, blobs, store) = Standalone(sp => new LateRecords(new SyncStorePhotoRecords(sp.GetRequiredService<ISyncStore>())));
        await PushPhoto(store, "h", "late");
        await blobs.WriteAsync("photos/h/late.jpg", TestPhotos.WithGps(300, 200), Ct);

        (await trigger.RunAsync("photos/h/late.jpg", Ct)).ShouldBe(PhotoProcessingOutcome.AlreadyProcessed); // second attempt: stripped and thumbnailed by the first

        trigger.Poisoned.ShouldBeEmpty();
        (await store.GetAsync("h", "photos", "late", Ct))!.Entity["thumbPath"]!.GetValue<string>().ShouldBe("thumbnails/h/late");
    }

    /// <summary>The record is not on the server for the first attempt: the app syncs metadata and uploads files independently.</summary>
    private sealed class LateRecords(IPhotoRecords inner) : IPhotoRecords
    {
        private bool first = true;

        public Task<bool> SetThumbPathAsync(string householdId, string photoId, string thumbPath, CancellationToken cancellationToken = default)
        {
            if (first)
            {
                first = false;
                return Task.FromResult(false);
            }

            return inner.SetThumbPathAsync(householdId, photoId, thumbPath, cancellationToken);
        }
    }

    private static (InMemoryPhotoTrigger Trigger, InMemoryPhotoBlobStore Blobs, ISyncStore Store) Standalone(Func<IServiceProvider, IPhotoRecords>? records = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<ISyncStore>(new InMemorySyncStore(TimeProvider.System));
        services.AddDialedPhotoProcessing();
        if (records is not null)
        {
            services.AddSingleton(records);
        }

        var provider = services.BuildServiceProvider();
        var trigger = new InMemoryPhotoTrigger(
            provider.GetRequiredService<InMemoryPhotoBlobStore>(), provider.GetRequiredService<IServiceScopeFactory>(), TimeProvider.System,
            NullLogger<InMemoryPhotoTrigger>.Instance)
        { RetryDelay = TimeSpan.Zero };
        return (trigger, provider.GetRequiredService<InMemoryPhotoBlobStore>(), provider.GetRequiredService<ISyncStore>());
    }

    private static async Task WaitUntilIdle(InMemoryPhotoTrigger trigger)
    {
        var clock = Stopwatch.StartNew();
        while (trigger.Pending > 0)
        {
            clock.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(20), "photo processing did not finish");
            await Task.Delay(20, Ct);
        }
    }
}
