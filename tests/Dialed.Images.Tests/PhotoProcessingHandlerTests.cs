using Dialed.Domain.Model;

namespace Dialed.Images.Tests;

public sealed class PhotoProcessingHandlerTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly InMemoryPhotoBlobStore blobs = new();
    private readonly FakeRecords records = new();
    private readonly PhotoProcessingHandler handler;

    public PhotoProcessingHandlerTests()
    {
        records.Known.Add(("h1", "p1"));
        handler = new PhotoProcessingHandler(blobs, new SkiaPhotoImageProcessor(), records);
    }

    private sealed class FakeRecords : IPhotoRecords
    {
        public HashSet<(string, string)> Known { get; } = [];

        public Dictionary<(string, string), string> ThumbPaths { get; } = [];

        public Task<bool> SetThumbPathAsync(string householdId, string photoId, string thumbPath, CancellationToken cancellationToken = default)
        {
            if (!Known.Contains((householdId, photoId)))
            {
                return Task.FromResult(false);
            }

            ThumbPaths[(householdId, photoId)] = thumbPath;
            return Task.FromResult(true);
        }
    }

    [Fact]
    public async Task An_uploaded_photo_is_stripped_gets_both_thumbnails_and_its_record_points_to_them()
    {
        var path = PhotoBlobPaths.Original("h1", "p1");
        await blobs.WriteAsync(path, TestPhotos.WithGps(1600, 1200), Ct);

        (await handler.HandleAsync(path, Ct)).ShouldBe(PhotoProcessingOutcome.Processed);

        JpegMetadata.HasMetadata((await blobs.ReadAsync(path, Ct))!).ShouldBeFalse();
        records.ThumbPaths[("h1", "p1")].ShouldBe("thumbnails/h1/p1");
        var photo = new Photo { ThumbPath = records.ThumbPaths[("h1", "p1")] };
        TestPhotos.Size((await blobs.ReadAsync(photo.ThumbnailPath(256)!, Ct))!).ShouldBe((256, 192));
        TestPhotos.Size((await blobs.ReadAsync(photo.ThumbnailPath(768)!, Ct))!).ShouldBe((768, 576));
        blobs.Paths.ShouldBe(["photos/h1/p1.jpg", "thumbnails/h1/p1-256.jpg", "thumbnails/h1/p1-768.jpg"], ignoreOrder: true);
    }

    [Fact]
    public async Task Running_again_for_the_same_blob_changes_nothing()
    {
        var path = PhotoBlobPaths.Original("h1", "p1");
        await blobs.WriteAsync(path, TestPhotos.WithGps(800, 600), Ct);
        await handler.HandleAsync(path, Ct);
        var written = new List<string>();
        blobs.BlobWritten += written.Add;

        // The rewrite of the stripped original raises a second event: that run, and any repeat, finds nothing to do.
        (await handler.HandleAsync(path, Ct)).ShouldBe(PhotoProcessingOutcome.AlreadyProcessed);
        (await handler.HandleAsync(path, Ct)).ShouldBe(PhotoProcessingOutcome.AlreadyProcessed);

        written.ShouldBeEmpty();
    }

    [Fact]
    public async Task Thumbnails_and_other_paths_are_ignored_so_processing_cannot_loop()
    {
        (await handler.HandleAsync("thumbnails/h1/p1-256.jpg", Ct)).ShouldBe(PhotoProcessingOutcome.Ignored);
        (await handler.HandleAsync("photos/h1/../p1.jpg", Ct)).ShouldBe(PhotoProcessingOutcome.Ignored);
        (await handler.HandleAsync("photos/h1/p1.png", Ct)).ShouldBe(PhotoProcessingOutcome.Ignored);
    }

    [Fact]
    public async Task A_blob_deleted_before_processing_is_reported_missing()
    {
        (await handler.HandleAsync(PhotoBlobPaths.Original("h1", "p1"), Ct)).ShouldBe(PhotoProcessingOutcome.Missing);
    }

    [Fact]
    public async Task Without_a_record_on_the_server_it_asks_for_a_retry_but_the_metadata_is_already_gone()
    {
        var path = PhotoBlobPaths.Original("h1", "unsynced");
        await blobs.WriteAsync(path, TestPhotos.WithGps(800, 600), Ct);

        await Should.ThrowAsync<PhotoRecordMissingException>(() => handler.HandleAsync(path, Ct));

        JpegMetadata.HasMetadata((await blobs.ReadAsync(path, Ct))!).ShouldBeFalse();
        records.Known.Add(("h1", "unsynced"));
        (await handler.HandleAsync(path, Ct)).ShouldBe(PhotoProcessingOutcome.AlreadyProcessed);
        records.ThumbPaths[("h1", "unsynced")].ShouldBe("thumbnails/h1/unsynced");
    }

    [Fact]
    public async Task A_file_that_is_not_a_photo_is_invalid()
    {
        var path = PhotoBlobPaths.Original("h1", "p1");
        await blobs.WriteAsync(path, "<html>"u8.ToArray(), Ct);

        await Should.ThrowAsync<InvalidPhotoException>(() => handler.HandleAsync(path, Ct));
    }
}
