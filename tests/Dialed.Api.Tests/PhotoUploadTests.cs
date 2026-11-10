using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Dialed.Api.Auth;
using Dialed.Api.Photos;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Dialed.Api.Tests;

public sealed class PhotoUploadTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly byte[] Jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3, 4, 0xFF, 0xD9];

    private sealed class RecordingHandler : IPhotoUploadedHandler
    {
        public List<PhotoBlobRef> Uploaded { get; } = [];

        public Task OnUploadedAsync(PhotoBlobRef blob, CancellationToken cancellationToken = default)
        {
            lock (Uploaded)
            {
                Uploaded.Add(blob);
            }

            return Task.CompletedTask;
        }
    }

    private async Task<HttpClient> ClientFor(string user, WebApplicationFactory<Program>? f = null)
    {
        var client = (f ?? factory).CreateClient();
        using var login = await client.PostAsJsonAsync("/auth/dev", new DevLoginRequest(user), Ct);
        var tokens = (await login.Content.ReadFromJsonAsync<TokenResponse>(Ct))!;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);
        return client;
    }

    private static async Task<string> PushPhoto(HttpClient client, int? visibility = null)
    {
        var id = Guid.CreateVersion7().ToString();
        var photo = new JsonObject { ["id"] = id, ["contentType"] = "image/jpeg", ["deleted"] = false };
        if (visibility is not null)
        {
            photo["visibility"] = visibility;
        }

        using var response = await client.PutAsJsonAsync($"/sync/photos/{id}", photo, Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return id;
    }

    private static async Task<PhotoUrlResponse> UploadUrl(HttpClient client, string id)
    {
        using var response = await client.PostAsync($"/photos/{id}/upload-url", null, Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return (await response.Content.ReadFromJsonAsync<PhotoUrlResponse>(Ct))!;
    }

    private static async Task<HttpResponseMessage> Put(HttpClient anonymous, string url, byte[] content, string type = "image/jpeg")
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, url) { Content = new ByteArrayContent(content) };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue(type);
        request.Headers.Add("x-ms-blob-type", "BlockBlob");
        return await anonymous.SendAsync(request, Ct);
    }

    [Fact]
    public async Task Upload_urls_need_a_signed_in_user()
    {
        using var anonymous = factory.CreateClient();

        (await anonymous.PostAsync($"/photos/{Guid.NewGuid()}/upload-url", null, Ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await anonymous.GetAsync($"/photos/{Guid.NewGuid()}/url", Ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task A_photo_whose_record_has_not_synced_has_no_upload_url()
    {
        using var alice = await ClientFor("alice");

        using var response = await alice.PostAsync($"/photos/{Guid.NewGuid()}/upload-url", null, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task A_photo_is_uploaded_with_its_url_and_read_back_with_a_read_url()
    {
        var handler = new RecordingHandler();
        using var f = factory.WithWebHostBuilder(b => b.ConfigureServices(s => s.AddSingleton<IPhotoUploadedHandler>(handler)));
        using var alice = await ClientFor("alice", f);
        using var anonymous = f.CreateClient();
        var id = await PushPhoto(alice);

        var upload = await UploadUrl(alice, id);
        upload.Method.ShouldBe("PUT");
        upload.Headers["x-ms-blob-type"].ShouldBe("BlockBlob");
        upload.ExpiresAt.ShouldBeGreaterThan(DateTimeOffset.UtcNow);
        upload.ExpiresAt.ShouldBeLessThanOrEqualTo(DateTimeOffset.UtcNow.AddMinutes(11));
        upload.Url.ShouldContain($"/{id}.jpg");

        using (var put = await Put(anonymous, upload.Url, Jpeg))
        {
            put.StatusCode.ShouldBe(HttpStatusCode.Created);
        }

        handler.Uploaded.ShouldHaveSingleItem().PhotoId.ShouldBe(id);

        var read = (await alice.GetFromJsonAsync<PhotoUrlResponse>($"/photos/{id}/url", Ct))!;
        read.Method.ShouldBe("GET");
        using var get = await anonymous.GetAsync(read.Url, Ct);
        get.StatusCode.ShouldBe(HttpStatusCode.OK);
        get.Content.Headers.ContentType!.MediaType.ShouldBe("image/jpeg");
        get.Headers.CacheControl!.Private.ShouldBeTrue();
        (await get.Content.ReadAsByteArrayAsync(Ct)).ShouldBe(Jpeg);
    }

    [Fact]
    public async Task Household_members_can_read_a_shared_photo_but_other_households_cannot_see_it()
    {
        using var alice = await ClientFor("alice");
        using var bob = await ClientFor("bob");
        using var carol = await ClientFor("carol");
        var id = await PushPhoto(alice);

        (await bob.GetAsync($"/photos/{id}/url", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await carol.GetAsync($"/photos/{id}/url", Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await carol.PostAsync($"/photos/{id}/upload-url", null, Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task A_private_photo_is_only_for_its_author()
    {
        using var alice = await ClientFor("alice");
        using var bob = await ClientFor("bob");
        var id = await PushPhoto(alice, visibility: 0);

        (await alice.GetAsync($"/photos/{id}/url", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await bob.GetAsync($"/photos/{id}/url", Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task A_deleted_photo_has_no_urls()
    {
        using var alice = await ClientFor("alice");
        var id = await PushPhoto(alice);
        var page = await alice.GetFromJsonAsync<Dialed.Api.Sync.SyncPageDto>("/sync/photos", Ct);
        var version = page!.Records.First(r => r.Entity["id"]!.GetValue<string>() == id).Version;
        using var request = new HttpRequestMessage(HttpMethod.Put, $"/sync/photos/{id}") { Content = JsonContent.Create(new JsonObject { ["id"] = id, ["deleted"] = true }) };
        request.Headers.TryAddWithoutValidation("If-Match", $"\"{version}\"");
        (await alice.SendAsync(request, Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);

        (await alice.PostAsync($"/photos/{id}/upload-url", null, Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Tampered_reused_or_misused_urls_are_refused()
    {
        using var alice = await ClientFor("alice");
        using var anonymous = factory.CreateClient();
        var id = await PushPhoto(alice);
        var other = await PushPhoto(alice);
        var upload = await UploadUrl(alice, id);
        var read = (await alice.GetFromJsonAsync<PhotoUrlResponse>($"/photos/{id}/url", Ct))!;

        // Another photo's path with this signature.
        (await Put(anonymous, upload.Url.Replace(id, other, StringComparison.Ordinal), Jpeg)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        // A read URL cannot write, an upload URL cannot read.
        (await Put(anonymous, read.Url, Jpeg)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await anonymous.GetAsync(upload.Url, Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        // A later expiry with the old signature.
        var later = System.Text.RegularExpressions.Regex.Replace(upload.Url, @"se=\d+", "se=9999999999");
        (await Put(anonymous, later, Jpeg)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        // No signature at all.
        (await Put(anonymous, upload.Url[..upload.Url.IndexOf('?', StringComparison.Ordinal)], Jpeg)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Only_jpeg_photos_within_the_size_limit_are_accepted()
    {
        using var alice = await ClientFor("alice");
        using var anonymous = factory.CreateClient();
        var id = await PushPhoto(alice);
        var upload = await UploadUrl(alice, id);

        (await Put(anonymous, upload.Url, Jpeg, "image/png")).StatusCode.ShouldBe(HttpStatusCode.UnsupportedMediaType);
        (await Put(anonymous, upload.Url, [1, 2, 3, 4])).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await Put(anonymous, upload.Url, [])).StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        var tooLarge = new byte[5_000_001];
        Jpeg.CopyTo(tooLarge, 0);
        (await Put(anonymous, upload.Url, tooLarge)).StatusCode.ShouldBe(HttpStatusCode.RequestEntityTooLarge);

        // Larger than the API's normal body limit but within the photo limit.
        var large = new byte[2_000_000];
        Jpeg.CopyTo(large, 0);
        (await Put(anonymous, upload.Url, large)).StatusCode.ShouldBe(HttpStatusCode.Created);
    }

    [Fact]
    public void Signed_urls_stop_working_when_they_expire()
    {
        var time = new ManualTime(new DateTimeOffset(2026, 11, 10, 12, 0, 0, TimeSpan.Zero));
        var http = new HttpContextAccessor { HttpContext = new DefaultHttpContext() };
        http.HttpContext.Request.Scheme = "https";
        http.HttpContext.Request.Host = new HostString("api.test");
        var urls = new LocalSignedPhotoUrls(http, new PhotoStorageOptions(), time);
        var blob = new PhotoBlobRef("h1", "p1");

        var issued = urls.IssueUpload(blob);
        var query = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(issued.Url.Query);
        issued.Url.AbsolutePath.ShouldBe("/photo-blobs/h1/p1.jpg");
        issued.ExpiresAt.ShouldBe(time.GetUtcNow().AddMinutes(10));

        urls.Verify(blob, "w", query["sp"], query["se"], query["sig"]).ShouldBeTrue();
        time.Advance(TimeSpan.FromMinutes(11));
        urls.Verify(blob, "w", query["sp"], query["se"], query["sig"]).ShouldBeFalse();
    }

    [Fact]
    public void Photo_references_cannot_leave_the_photo_folder()
    {
        PhotoBlobRef.IsSafeId("0193-abc_DEF").ShouldBeTrue();
        PhotoBlobRef.IsSafeId("../etc").ShouldBeFalse();
        PhotoBlobRef.IsSafeId("a/b").ShouldBeFalse();
        PhotoBlobRef.IsSafeId("").ShouldBeFalse();
        Should.Throw<ArgumentException>(() => new FileSystemPhotoBlobStore(Path.GetTempPath()).ReadAsync(new PhotoBlobRef("..", "x"), Ct));
    }

    [Fact]
    public async Task The_file_store_writes_reads_and_deletes_photos()
    {
        var root = Path.Combine(Path.GetTempPath(), "dialed-photo-tests", Guid.NewGuid().ToString());
        var store = new FileSystemPhotoBlobStore(root);
        var blob = new PhotoBlobRef("h1", "p1");

        await store.WriteAsync(blob, Jpeg, Ct);
        (await store.ReadAsync(blob, Ct)).ShouldBe(Jpeg);
        File.Exists(Path.Combine(root, "h1", "p1.jpg")).ShouldBeTrue();

        await store.DeleteAsync(blob, Ct);
        await store.DeleteAsync(blob, Ct);
        (await store.ReadAsync(blob, Ct)).ShouldBeNull();
        Directory.Delete(root, recursive: true);
    }

    private sealed class ManualTime(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset now = start;

        public override DateTimeOffset GetUtcNow() => now;

        public void Advance(TimeSpan by) => now += by;
    }
}
