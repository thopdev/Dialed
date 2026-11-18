using Azure.Storage.Blobs;
using Dialed.Api.Photos;
using Dialed.Images;
using Dialed.Images.Tests;
using Microsoft.Extensions.Logging.Abstractions;

namespace Dialed.Api.Tests;

/// <summary>
/// <see cref="AzureBlobPhotoStore"/> and <see cref="BlobStoragePhotoWatcher"/> against Azurite, the Blob Storage emulator, with
/// <c>AZURITE_CONNECTION_STRING</c> (CI's azurite job). Without it these tests skip. Azurite signs with the account key, so the user
/// delegation SAS itself is covered by the unit tests and the dev deployment, not here. Each test uses its own container.
/// </summary>
public sealed class AzuriteBlobPhotoStoreTests : IAsyncLifetime
{
    public static readonly string? ConnectionString = Environment.GetEnvironmentVariable("AZURITE_CONNECTION_STRING");

    private BlobContainerClient container = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        Assert.SkipWhen(string.IsNullOrEmpty(ConnectionString), "AZURITE_CONNECTION_STRING not set (runs in CI's azurite job).");
        container = new BlobServiceClient(ConnectionString).GetBlobContainerClient("photos-" + Guid.NewGuid().ToString("N")[..12]);
        await container.CreateAsync(cancellationToken: Ct);
    }

    public async ValueTask DisposeAsync()
    {
        if (container is not null)
        {
            await container.DeleteIfExistsAsync();
        }
    }

    [Fact]
    public async Task Writes_reads_lists_and_deletes_photos()
    {
        var store = new AzureBlobPhotoStore(container);
        var photo = TestPhotos.Plain(40, 30);

        (await store.ReadAsync("photos/h1/p1.jpg", Ct)).ShouldBeNull();
        (await store.ExistsAsync("photos/h1/p1.jpg", Ct)).ShouldBeFalse();

        await store.WriteAsync("photos/h1/p1.jpg", photo, Ct);
        await store.WriteAsync("thumbnails/h1/p1-256.jpg", photo, Ct);

        (await store.ReadAsync("photos/h1/p1.jpg", Ct)).ShouldBe(photo);
        (await store.ExistsAsync("photos/h1/p1.jpg", Ct)).ShouldBeTrue();
        (await container.GetBlobClient("photos/h1/p1.jpg").GetPropertiesAsync(cancellationToken: Ct)).Value.ContentType.ShouldBe("image/jpeg");

        var originals = new List<PhotoBlobInfo>();
        await foreach (var blob in store.ListAsync("photos/", Ct))
        {
            originals.Add(blob);
        }

        originals.ShouldHaveSingleItem().Path.ShouldBe("photos/h1/p1.jpg");
        originals[0].Size.ShouldBe(photo.Length);
        originals[0].ETag.ShouldNotBeNullOrEmpty();

        await store.DeleteAsync("photos/h1/p1.jpg", Ct);
        await store.DeleteAsync("photos/h1/p1.jpg", Ct);
        (await store.ExistsAsync("photos/h1/p1.jpg", Ct)).ShouldBeFalse();
        await Should.ThrowAsync<ArgumentException>(() => store.ReadAsync("photos/../x.jpg", Ct));
    }

    [Fact]
    public async Task An_upload_is_found_stripped_and_thumbnailed_once()
    {
        var store = new AzureBlobPhotoStore(container);
        var records = new Records();
        var handler = new PhotoProcessingHandler(store, new SkiaPhotoImageProcessor(), records);
        var watcher = new BlobStoragePhotoWatcher(
            store, store, new BlobStorageOptions(), new PhotoStorageOptions(), TimeProvider.System, NullLogger<BlobStoragePhotoWatcher>.Instance);

        // What the app does with its upload URL: the original, still carrying GPS metadata.
        await store.WriteAsync("photos/h1/p1.jpg", TestPhotos.WithGps(900, 600), Ct);

        var found = await watcher.PollAsync(Ct);
        found.ShouldBe(["photos/h1/p1.jpg"]);
        (await handler.HandleAsync(found[0], Ct)).ShouldBe(PhotoProcessingOutcome.Processed);

        JpegMetadata.HasMetadata((await store.ReadAsync("photos/h1/p1.jpg", Ct))!).ShouldBeFalse();
        (await store.ExistsAsync("thumbnails/h1/p1-256.jpg", Ct)).ShouldBeTrue();
        (await store.ExistsAsync("thumbnails/h1/p1-768.jpg", Ct)).ShouldBeTrue();
        records.ThumbPath.ShouldBe("thumbnails/h1/p1");

        // The stripped original is a new version, written right after the thumbnails: not a new upload.
        (await watcher.PollAsync(Ct)).ShouldBeEmpty();

        // A fresh watcher (an API restart) also leaves it alone.
        var restarted = new BlobStoragePhotoWatcher(
            store, store, new BlobStorageOptions(), new PhotoStorageOptions(), TimeProvider.System, NullLogger<BlobStoragePhotoWatcher>.Instance);
        (await restarted.PollAsync(Ct)).ShouldBeEmpty();
    }

    private sealed class Records : IPhotoRecords
    {
        public string? ThumbPath { get; private set; }

        public Task<bool> SetThumbPathAsync(string householdId, string photoId, string thumbPath, CancellationToken cancellationToken = default)
        {
            ThumbPath = thumbPath;
            return Task.FromResult(true);
        }
    }
}
