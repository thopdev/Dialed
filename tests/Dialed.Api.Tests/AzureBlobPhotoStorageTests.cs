using System.Runtime.CompilerServices;
using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Dialed.Api.Photos;
using Dialed.Images;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace Dialed.Api.Tests;

/// <summary>Photos in Blob Storage (#68, #69, #198): SAS scope and expiry, delegation key caching, the upload watcher and the wiring.</summary>
public sealed class AzureBlobPhotoStorageTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 18, 12, 0, 0, TimeSpan.Zero);
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly BlobContainerClient Container = new(new Uri("https://stdialedtest.blob.core.windows.net/photos"));

    [Fact]
    public async Task Upload_urls_are_a_write_only_user_delegation_sas_on_one_blob_for_minutes()
    {
        var time = new ManualTime(Start);
        var urls = new AzureBlobPhotoUrls(Container, new FixedKeys(), new PhotoStorageOptions(), time);

        var issued = await urls.IssueUploadAsync(new PhotoBlobRef("h1", "p1"), Ct);
        var query = QueryHelpers.ParseQuery(issued.Url.Query);

        issued.Method.ShouldBe("PUT");
        issued.Headers.ShouldContainKeyAndValue("x-ms-blob-type", "BlockBlob");
        issued.Url.GetLeftPart(UriPartial.Path).ShouldBe("https://stdialedtest.blob.core.windows.net/photos/photos/h1/p1.jpg");
        query["sr"].ToString().ShouldBe("b");           // one blob, not the container
        query["sp"].ToString().ShouldBe("cw");          // create and write only: no read, list or delete
        query["spr"].ToString().ShouldBe("https");
        query["skoid"].ToString().ShouldBe("object-id"); // signed with a user delegation key, not an account key
        query.ContainsKey("sig").ShouldBeTrue();
        DateTimeOffset.Parse(query["se"].ToString(), System.Globalization.CultureInfo.InvariantCulture).ShouldBe(Start.AddMinutes(10));
        DateTimeOffset.Parse(query["st"].ToString(), System.Globalization.CultureInfo.InvariantCulture).ShouldBe(Start.AddMinutes(-5));
        issued.ExpiresAt.ShouldBe(Start.AddMinutes(10));
    }

    [Fact]
    public async Task Read_urls_only_read_and_serve_a_private_jpeg()
    {
        var urls = new AzureBlobPhotoUrls(Container, new FixedKeys(), new PhotoStorageOptions(), new ManualTime(Start));

        var issued = await urls.IssueReadAsync(new PhotoBlobRef("h1", "p1"), Ct);
        var query = QueryHelpers.ParseQuery(issued.Url.Query);

        issued.Method.ShouldBe("GET");
        issued.Headers.ShouldBeEmpty();
        query["sp"].ToString().ShouldBe("r");
        query["sr"].ToString().ShouldBe("b");
        query["rsct"].ToString().ShouldBe("image/jpeg");
        query["rscc"].ToString().ShouldBe("private, max-age=1800");
        issued.ExpiresAt.ShouldBe(Start.AddMinutes(30));
    }

    [Fact]
    public async Task Delegation_keys_are_reused_until_they_would_expire_before_the_url()
    {
        var time = new ManualTime(Start);
        var service = new CountingService();
        var keys = new CachedUserDelegationKeys(service, time);

        var first = await keys.GetAsync(Start.AddMinutes(10), Ct);
        (await keys.GetAsync(Start.AddMinutes(30), Ct)).ShouldBeSameAs(first);
        service.Calls.ShouldBe(1);

        time.Advance(TimeSpan.FromHours(23.9));
        (await keys.GetAsync(time.GetUtcNow().AddMinutes(10), Ct)).ShouldNotBeSameAs(first);
        service.Calls.ShouldBe(2);
        service.LastExpiresOn.ShouldBe(time.GetUtcNow().Add(CachedUserDelegationKeys.KeyLifetime));
    }

    [Fact]
    public async Task The_watcher_raises_new_uploads_once()
    {
        var listing = new FakeListing();
        var watcher = Watcher(listing);
        var raised = new List<string>();
        watcher.BlobWritten += raised.Add;

        listing.Add("photos/h1/p1.jpg", "e1", Start);
        listing.Add("photos/h1/notes.txt", "e2", Start);
        listing.Add("elsewhere/h1/p2.jpg", "e3", Start);

        (await watcher.PollAsync(Ct)).ShouldBe(["photos/h1/p1.jpg"]);
        (await watcher.PollAsync(Ct)).ShouldBeEmpty(); // same version: already handed to the trigger
        raised.ShouldBe(["photos/h1/p1.jpg"]);
    }

    [Fact]
    public async Task The_watcher_skips_processed_photos_and_their_rewrite_but_not_a_new_upload()
    {
        var listing = new FakeListing();
        listing.Add("thumbnails/h1/p1-256.jpg", "t1", Start.AddSeconds(2));
        listing.Add("thumbnails/h1/p1-768.jpg", "t2", Start.AddSeconds(3));
        listing.Add("photos/h1/p1.jpg", "stripped", Start.AddSeconds(4)); // the stripped original, written right after the thumbnails
        listing.Add("thumbnails/h1/p2-256.jpg", "t3", Start);              // only one size: not finished
        listing.Add("photos/h1/p2.jpg", "e2", Start);
        var watcher = Watcher(listing);

        (await watcher.PollAsync(Ct)).ShouldBe(["photos/h1/p2.jpg"]);

        listing.Add("photos/h1/p1.jpg", "again", Start.AddMinutes(10)); // uploaded again later
        (await watcher.PollAsync(Ct)).ShouldBe(["photos/h1/p1.jpg"]);
    }

    [Fact]
    public async Task The_watcher_deletes_originals_over_the_size_limit()
    {
        var listing = new FakeListing();
        listing.Add("photos/h1/big.jpg", "e1", Start, size: 6_000_000);
        var store = new InMemoryPhotoBlobStore();
        await store.WriteAsync("photos/h1/big.jpg", [0xFF, 0xD8, 0xFF], Ct);

        (await Watcher(listing, store).PollAsync(Ct)).ShouldBeEmpty();

        (await store.ExistsAsync("photos/h1/big.jpg", Ct)).ShouldBeFalse();
    }

    [Fact]
    public async Task A_blob_endpoint_in_configuration_selects_blob_storage()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Storage:BlobEndpoint"] = "https://stdialedtest.blob.core.windows.net/",
            ["Storage:PhotosContainer"] = "photos",
            ["Photos:LocalFolder"] = Path.GetTempPath(), // ignored once Blob Storage is configured
        }).Build();
        var services = new ServiceCollection().AddLogging();
        services.AddDialedPhotoProcessing();
        services.AddDialedPhotos(configuration, new FakeEnvironment());
        await using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<IPhotoUrlIssuer>().ShouldBeOfType<AzureBlobPhotoUrls>();
        provider.GetRequiredService<IPhotoBlobStore>().ShouldBeOfType<AzureBlobPhotoStore>();
        provider.GetRequiredService<IPhotoBlobEvents>().ShouldBeOfType<BlobStoragePhotoWatcher>();
        provider.GetRequiredService<BlobContainerClient>().Uri.ShouldBe(new Uri("https://stdialedtest.blob.core.windows.net/photos"));
        provider.GetServices<IHostedService>().ShouldContain(s => s is BlobStoragePhotoWatcher);
    }

    [Fact]
    public async Task Without_a_blob_endpoint_the_local_stand_in_stays()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Photos:LocalFolder"] = Path.Combine(Path.GetTempPath(), "dialed-photo-tests", Guid.NewGuid().ToString()),
        }).Build();
        var services = new ServiceCollection().AddLogging();
        services.AddDialedPhotoProcessing();
        services.AddDialedPhotos(configuration, new FakeEnvironment());
        await using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<IPhotoUrlIssuer>().ShouldBeOfType<LocalSignedPhotoUrls>();
        provider.GetRequiredService<IPhotoBlobStore>().ShouldBeOfType<FileSystemPhotoBlobStore>();
    }

    private static BlobStoragePhotoWatcher Watcher(FakeListing listing, IPhotoBlobStore? store = null) =>
        new(listing, store ?? new InMemoryPhotoBlobStore(), new BlobStorageOptions(), new PhotoStorageOptions(), new ManualTime(Start),
            NullLogger<BlobStoragePhotoWatcher>.Instance);

    private static UserDelegationKey Key(DateTimeOffset startsOn, DateTimeOffset expiresOn) =>
        BlobsModelFactory.UserDelegationKey(
            "object-id", "tenant-id", startsOn, expiresOn, "b", "2025-01-05", Convert.ToBase64String(new byte[32]));

    private sealed class FixedKeys : IUserDelegationKeySource
    {
        public Task<UserDelegationKey> GetAsync(DateTimeOffset validUntil, CancellationToken cancellationToken = default) =>
            Task.FromResult(Key(Start.AddHours(-1), Start.AddDays(1)));
    }

    private sealed class CountingService() : BlobServiceClient(new Uri("https://stdialedtest.blob.core.windows.net/"))
    {
        public int Calls { get; private set; }

        public DateTimeOffset LastExpiresOn { get; private set; }

        public override Task<Response<UserDelegationKey>> GetUserDelegationKeyAsync(
            DateTimeOffset? startsOn, DateTimeOffset expiresOn, CancellationToken cancellationToken = default)
        {
            Calls++;
            LastExpiresOn = expiresOn;
            return Task.FromResult(Response.FromValue(Key(startsOn ?? expiresOn.AddDays(-1), expiresOn), null!));
        }
    }

    private sealed class FakeListing : IPhotoBlobListing
    {
        private readonly Dictionary<string, PhotoBlobInfo> blobs = new(StringComparer.Ordinal);

        public void Add(string path, string etag, DateTimeOffset lastModified, long size = 1000) =>
            blobs[path] = new PhotoBlobInfo(path, etag, size, lastModified);

        public async IAsyncEnumerable<PhotoBlobInfo> ListAsync(string prefix, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            foreach (var blob in blobs.Values.Where(b => b.Path.StartsWith(prefix, StringComparison.Ordinal)).OrderBy(b => b.Path, StringComparer.Ordinal))
            {
                yield return blob;
            }
        }
    }

    private sealed class FakeEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Production";

        public string ApplicationName { get; set; } = "Dialed.Api";

        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;

        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } =
            new Microsoft.Extensions.FileProviders.NullFileProvider();
    }

    private sealed class ManualTime(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset now = start;

        public override DateTimeOffset GetUtcNow() => now;

        public void Advance(TimeSpan by) => now += by;
    }
}
