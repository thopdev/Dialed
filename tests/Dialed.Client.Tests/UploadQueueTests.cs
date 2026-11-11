using System.Net;
using Dialed.Client.Data;
using Dialed.Client.Platform;
using Dialed.Client.Storage;
using Dialed.Client.Sync;
using Dialed.Client.Uploads;
using Dialed.Domain.Model;

namespace Dialed.Client.Tests;

public sealed class UploadQueueTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly byte[] Jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3];

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 11, 10, 12, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => Now;

        public void Advance(TimeSpan by) => Now += by;
    }

    private sealed class Network : IConnectivity
    {
        public bool IsOnline { get; set; } = true;

        public event Action<bool>? Changed;

        public void Set(bool online)
        {
            IsOnline = online;
            Changed?.Invoke(online);
        }
    }

    private sealed class Lifecycle : IAppLifecycle
    {
        public bool IsForeground => true;

        public event Action? Resumed;

        public event Action? Paused;

        public void Resume() => Resumed?.Invoke();

        public void Pause() => Paused?.Invoke();
    }

    /// <summary>Storage and API in one: answers with whatever <see cref="Next"/> says, then stores the file.</summary>
    private sealed class FakeApi : IPhotoUploadApi
    {
        public Queue<UploadProblem> Problems { get; } = new();

        public Dictionary<string, byte[]> Stored { get; } = [];

        public int UrlRequests { get; private set; }

        public int ReadUrlRequests { get; private set; }

        public Task<PhotoUrl> GetUploadUrlAsync(string photoId, CancellationToken cancellationToken = default)
        {
            lock (Stored)
            {
                UrlRequests++;
                if (Problems.TryDequeue(out var problem))
                {
                    throw new PhotoUploadException(problem);
                }
            }

            return Task.FromResult(new PhotoUrl(new Uri($"https://storage.test/{photoId}.jpg?sig=x"), "PUT", new Dictionary<string, string>(), DateTimeOffset.MaxValue));
        }

        public Task UploadAsync(PhotoUrl target, byte[] content, string contentType, CancellationToken cancellationToken = default)
        {
            lock (Stored)
            {
                Stored[target.Url.AbsolutePath.Trim('/')[..^4]] = content;
            }

            return Task.CompletedTask;
        }

        public Task<PhotoUrl?> GetReadUrlAsync(string photoId, CancellationToken cancellationToken = default)
        {
            ReadUrlRequests++;
            return Task.FromResult<PhotoUrl?>(new PhotoUrl(new Uri($"https://storage.test/{photoId}.jpg?sp=r"), "GET", new Dictionary<string, string>(), new DateTimeOffset(2026, 11, 10, 12, 30, 0, TimeSpan.Zero)));
        }
    }

    private readonly Clock time = new();
    private readonly Network network = new();
    private readonly Lifecycle lifecycle = new();
    private readonly InMemoryLocalStore store = new();
    private readonly InMemoryLocalFileStore files = new();
    private readonly FakeApi api = new();
    private readonly InMemoryRepository<Photo> photos;
    private readonly List<PhotoUploadQueue> queues = [];

    public UploadQueueTests()
    {
        photos = new InMemoryRepository<Photo>(new LocalContext(), time, store);
    }

    public void Dispose()
    {
        foreach (var queue in queues)
        {
            queue.Dispose();
        }
    }

    private PhotoUploadQueue NewQueue(SyncCoordinator? sync = null)
    {
        var queue = new PhotoUploadQueue(store, files, photos, api, network, lifecycle, time, sync);
        queues.Add(queue);
        return queue;
    }

    private async Task<Photo> TakePhoto()
    {
        var photo = new Photo { SizeBytes = Jpeg.Length };
        await files.SaveAsync(photo.LocalPath, Jpeg, Ct);
        return await photos.SaveAsync(photo, Ct);
    }

    private static async Task Eventually(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++)
        {
            await Task.Delay(10, Ct);
        }

        condition().ShouldBeTrue();
    }

    private async Task<UploadState?> StateOf(string photoId) => (await photos.GetAsync(photoId, Ct))?.UploadState;

    [Fact]
    public async Task A_queued_photo_is_uploaded_and_marked_uploaded()
    {
        var queue = NewQueue();
        var photo = await TakePhoto();

        await queue.EnqueueAsync(photo.Id, Ct);
        await queue.ProcessAsync(Ct);

        await Eventually(() => queue.PendingCount == 0);
        api.Stored[photo.Id].ShouldBe(Jpeg);
        (await StateOf(photo.Id)).ShouldBe(UploadState.Uploaded);
        queue.StatusOf(photo.Id).ShouldBeNull();
        (await store.ReadAllAsync(PhotoUploadQueue.Collection, Ct)).ShouldBeEmpty();
    }

    [Fact]
    public async Task Offline_photos_wait_and_go_when_the_network_comes_back()
    {
        network.IsOnline = false;
        var queue = NewQueue();
        await queue.StartAsync(Ct);
        var photo = await TakePhoto();

        await queue.EnqueueAsync(photo.Id, Ct);
        await queue.ProcessAsync(Ct);

        queue.PendingCount.ShouldBe(1);
        queue.StatusOf(photo.Id).ShouldBe(UploadStatus.Waiting);
        api.UrlRequests.ShouldBe(0);

        network.Set(true);

        await Eventually(() => queue.PendingCount == 0);
        api.Stored.ShouldContainKey(photo.Id);
    }

    [Fact]
    public async Task The_queue_survives_a_restart()
    {
        network.IsOnline = false;
        var before = NewQueue();
        var photo = await TakePhoto();
        await before.EnqueueAsync(photo.Id, Ct);
        before.Dispose();

        network.IsOnline = true;
        var after = NewQueue();
        await after.LoadAsync(Ct);
        after.Items.ShouldHaveSingleItem().PhotoId.ShouldBe(photo.Id);

        await after.ProcessAsync(Ct);

        await Eventually(() => after.PendingCount == 0);
        api.Stored.ShouldContainKey(photo.Id);
    }

    [Fact]
    public async Task Server_trouble_is_retried_with_a_growing_wait()
    {
        var queue = NewQueue();
        var photo = await TakePhoto();
        api.Problems.Enqueue(UploadProblem.Transient);
        api.Problems.Enqueue(UploadProblem.Transient);

        await queue.EnqueueAsync(photo.Id, Ct);
        await Eventually(() => queue.Items.Single().Attempts == 1);
        var first = queue.Items.Single();
        first.Status.ShouldBe(UploadStatus.Waiting);
        first.NextAttemptAt.ShouldBe(time.Now + TimeSpan.FromSeconds(10));

        // Not due yet: nothing happens.
        await queue.ProcessAsync(Ct);
        api.UrlRequests.ShouldBe(1);

        time.Advance(TimeSpan.FromSeconds(10));
        await queue.ProcessAsync(Ct);
        queue.Items.Single().NextAttemptAt.ShouldBe(time.Now + TimeSpan.FromSeconds(20));

        time.Advance(TimeSpan.FromSeconds(20));
        await queue.ProcessAsync(Ct);
        await Eventually(() => queue.PendingCount == 0);
        api.UrlRequests.ShouldBe(3);
        (await StateOf(photo.Id)).ShouldBe(UploadState.Uploaded);
    }

    [Fact]
    public void Backoff_doubles_up_to_half_an_hour()
    {
        PhotoUploadQueue.BackoffDelay(1).ShouldBe(TimeSpan.FromSeconds(10));
        PhotoUploadQueue.BackoffDelay(2).ShouldBe(TimeSpan.FromSeconds(20));
        PhotoUploadQueue.BackoffDelay(4).ShouldBe(TimeSpan.FromSeconds(80));
        PhotoUploadQueue.BackoffDelay(20).ShouldBe(TimeSpan.FromMinutes(30));
    }

    [Fact]
    public async Task After_repeated_failures_the_photo_is_failed_until_retried()
    {
        var queue = NewQueue();
        var photo = await TakePhoto();
        for (var i = 0; i < PhotoUploadQueue.MaxFailures; i++)
        {
            api.Problems.Enqueue(UploadProblem.Transient);
        }

        await queue.EnqueueAsync(photo.Id, Ct);
        for (var i = 0; i < PhotoUploadQueue.MaxFailures; i++)
        {
            await queue.ProcessAsync(Ct);
            time.Advance(TimeSpan.FromHours(1));
        }

        await Eventually(() => queue.FailedCount == 1);
        queue.PendingCount.ShouldBe(0);
        queue.StatusOf(photo.Id).ShouldBe(UploadStatus.Failed);
        queue.Items.Single().LastError.ShouldNotBeNull();
        (await StateOf(photo.Id)).ShouldBe(UploadState.Failed);

        // No automatic retry once failed.
        time.Advance(TimeSpan.FromDays(1));
        await queue.ProcessAsync(Ct);
        api.UrlRequests.ShouldBe(PhotoUploadQueue.MaxFailures);

        await queue.RetryAsync(cancellationToken: Ct);

        await Eventually(() => queue.FailedCount == 0 && queue.PendingCount == 0);
        (await StateOf(photo.Id)).ShouldBe(UploadState.Uploaded);
    }

    [Fact]
    public async Task A_refused_photo_fails_at_once()
    {
        var queue = NewQueue();
        var photo = await TakePhoto();
        api.Problems.Enqueue(UploadProblem.Rejected);

        await queue.EnqueueAsync(photo.Id, Ct);

        await Eventually(() => queue.FailedCount == 1);
        api.UrlRequests.ShouldBe(1);
        (await StateOf(photo.Id)).ShouldBe(UploadState.Failed);
    }

    [Fact]
    public async Task Being_offline_or_not_synced_yet_never_makes_an_upload_fail()
    {
        var queue = NewQueue();
        var photo = await TakePhoto();
        for (var i = 0; i < PhotoUploadQueue.MaxFailures * 2; i++)
        {
            api.Problems.Enqueue(i % 2 == 0 ? UploadProblem.Offline : UploadProblem.NotSynced);
        }

        await queue.EnqueueAsync(photo.Id, Ct);
        for (var i = 0; i < PhotoUploadQueue.MaxFailures * 2; i++)
        {
            await queue.ProcessAsync(Ct);
            time.Advance(TimeSpan.FromHours(1));
        }

        await queue.ProcessAsync(Ct);
        await Eventually(() => queue.PendingCount == 0);
        queue.FailedCount.ShouldBe(0);
        (await StateOf(photo.Id)).ShouldBe(UploadState.Uploaded);
    }

    [Fact]
    public async Task A_finished_sync_wakes_uploads_that_waited_for_the_server_but_not_ones_that_met_an_error()
    {
        var state = new InMemorySyncState();
        var engine = new SyncEngine([new SyncTable<Photo>("photos", photos, state, new FakeSyncServer(time), time).Attach()], state);
        using var sync = new SyncCoordinator(engine, state, new Token("tkn"), network, lifecycle, time, debounce: TimeSpan.Zero);
        var queue = NewQueue(sync);
        await queue.StartAsync(Ct);
        var unreachable = await TakePhoto();
        var serverError = await TakePhoto();
        api.Problems.Enqueue(UploadProblem.Offline);
        api.Problems.Enqueue(UploadProblem.Transient);

        await queue.EnqueueAsync(unreachable.Id, Ct);
        await Eventually(() => queue.Items.Single().Attempts == 1);
        await queue.EnqueueAsync(serverError.Id, Ct);
        await Eventually(() => queue.Items.All(i => i.Attempts == 1));
        api.UrlRequests.ShouldBe(2);

        await sync.SyncNowAsync(Ct);

        await Eventually(() => api.Stored.ContainsKey(unreachable.Id));
        queue.StatusOf(serverError.Id).ShouldBe(UploadStatus.Waiting);
        api.Stored.ShouldNotContainKey(serverError.Id);
    }

    [Fact]
    public async Task Signed_out_uploads_wait_without_counting_attempts()
    {
        var queue = NewQueue();
        var photo = await TakePhoto();
        api.Problems.Enqueue(UploadProblem.SignedOut);

        await queue.EnqueueAsync(photo.Id, Ct);
        await Eventually(() => api.UrlRequests == 1);
        await queue.ProcessAsync(Ct); // a later trigger (sign-in, resume) tries again
        await Eventually(() => queue.PendingCount == 0);
        api.Stored.ShouldContainKey(photo.Id);
    }

    [Fact]
    public async Task Coming_back_to_the_app_retries_waiting_uploads()
    {
        var queue = NewQueue();
        await queue.StartAsync(Ct);
        var photo = await TakePhoto();
        api.Problems.Enqueue(UploadProblem.SignedOut);
        await queue.EnqueueAsync(photo.Id, Ct);
        await Eventually(() => api.UrlRequests == 1);

        lifecycle.Resume();

        await Eventually(() => queue.PendingCount == 0);
    }

    [Fact]
    public async Task A_photo_cleared_from_the_device_before_upload_is_marked_failed()
    {
        network.IsOnline = false;
        var queue = NewQueue();
        var photo = await TakePhoto();
        await queue.EnqueueAsync(photo.Id, Ct);
        await files.DeleteAsync(photo.LocalPath, Ct);

        network.IsOnline = true;
        await queue.ProcessAsync(Ct);

        queue.Items.ShouldBeEmpty();
        api.UrlRequests.ShouldBe(0);
        (await StateOf(photo.Id)).ShouldBe(UploadState.Failed);
    }

    [Fact]
    public async Task A_deleted_photo_leaves_the_queue()
    {
        network.IsOnline = false;
        var queue = NewQueue();
        var photo = await TakePhoto();
        await queue.EnqueueAsync(photo.Id, Ct);
        await photos.DeleteAsync(photo.Id, Ct);

        network.IsOnline = true;
        await queue.ProcessAsync(Ct);

        queue.Items.ShouldBeEmpty();
        api.UrlRequests.ShouldBe(0);
    }

    [Fact]
    public async Task Photos_taken_before_the_queue_existed_are_picked_up_on_start()
    {
        var waiting = await TakePhoto();
        var elsewhere = await photos.SaveAsync(new Photo(), Ct); // taken on another device: no file here
        var uploaded = await photos.SaveAsync(new Photo { UploadState = UploadState.Uploaded }, Ct);
        await files.SaveAsync(uploaded.LocalPath, Jpeg, Ct);
        var queue = NewQueue();

        await queue.StartAsync(Ct);

        await Eventually(() => api.Stored.ContainsKey(waiting.Id) && queue.PendingCount == 0);
        api.Stored.Keys.ShouldBe([waiting.Id]);
        (await StateOf(elsewhere.Id)).ShouldBe(UploadState.Pending);
    }

    [Fact]
    public async Task The_photo_service_queues_new_photos_and_unqueues_removed_ones()
    {
        network.IsOnline = false;
        var queue = NewQueue();
        var service = new PhotoService(photos, files, queue, api, time);

        var kept = await service.AddAsync(new CapturedPhoto(Jpeg, "image/jpeg", "a.jpg"), Ct);
        var removed = await service.AddAsync(new CapturedPhoto(Jpeg, "image/jpeg", "b.jpg"), Ct);
        service.UploadStatusOf(kept.Id).ShouldBe(UploadStatus.Waiting);

        await service.RemoveAsync(removed.Id, Ct);

        queue.Items.Select(i => i.PhotoId).ShouldBe([kept.Id]);
    }

    [Fact]
    public async Task Photos_not_on_this_device_are_shown_from_a_read_url_once_uploaded()
    {
        var service = new PhotoService(photos, files, NewQueue(), api, time);
        var pending = await photos.SaveAsync(new Photo(), Ct);
        var uploaded = await photos.SaveAsync(new Photo { UploadState = UploadState.Uploaded }, Ct);
        var local = await TakePhoto();

        (await service.GetImageSourceAsync(local.Id, Ct)).ShouldStartWith("data:image/jpeg;base64,");
        (await service.GetImageSourceAsync(pending.Id, Ct)).ShouldBeNull();
        (await service.GetImageSourceAsync(uploaded.Id, Ct)).ShouldBe($"https://storage.test/{uploaded.Id}.jpg?sp=r");

        // Reused while valid, fetched again close to expiry.
        await service.GetImageSourceAsync(uploaded.Id, Ct);
        api.ReadUrlRequests.ShouldBe(1);
        time.Advance(TimeSpan.FromMinutes(29.5));
        await service.GetImageSourceAsync(uploaded.Id, Ct);
        api.ReadUrlRequests.ShouldBe(2);
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(respond(request));
        }
    }

    private sealed class Token(string? value) : IAccessTokenProvider
    {
        public Task<string?> GetAccessTokenAsync(CancellationToken cancellationToken = default) => Task.FromResult(value);
    }

    private static HttpResponseMessage Json(string json) =>
        new(HttpStatusCode.OK) { Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json") };

    [Fact]
    public async Task The_api_client_asks_for_a_url_and_sends_the_file_there_without_the_token()
    {
        using var apiHandler = new Handler(_ => Json("""{"url":"https://storage.test/p1.jpg?sig=abc","method":"PUT","headers":{"x-ms-blob-type":"BlockBlob"},"expiresAt":"2026-11-10T12:10:00Z"}"""));
        using var storageHandler = new Handler(_ => new HttpResponseMessage(HttpStatusCode.Created));
        using var apiHttp = new HttpClient(apiHandler) { BaseAddress = new Uri("https://api.test/") };
        using var storageHttp = new HttpClient(storageHandler);
        var client = new PhotoUploadApi(apiHttp, storageHttp, new Token("tkn"));

        var target = await client.GetUploadUrlAsync("p1", Ct);
        await client.UploadAsync(target, Jpeg, "image/jpeg", Ct);

        var urlRequest = apiHandler.Requests.Single();
        urlRequest.Method.ShouldBe(HttpMethod.Post);
        urlRequest.RequestUri!.ToString().ShouldBe("https://api.test/photos/p1/upload-url");
        urlRequest.Headers.Authorization!.Parameter.ShouldBe("tkn");

        var upload = storageHandler.Requests.Single();
        upload.Method.ShouldBe(HttpMethod.Put);
        upload.RequestUri!.ToString().ShouldBe("https://storage.test/p1.jpg?sig=abc");
        upload.Headers.Authorization.ShouldBeNull();
        upload.Headers.GetValues("x-ms-blob-type").ShouldBe(["BlockBlob"]);
        upload.Content!.Headers.ContentType!.MediaType.ShouldBe("image/jpeg");
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound, UploadProblem.NotSynced)]
    [InlineData(HttpStatusCode.Unauthorized, UploadProblem.SignedOut)]
    [InlineData(HttpStatusCode.InternalServerError, UploadProblem.Transient)]
    public async Task The_api_client_tells_why_there_is_no_upload_url(HttpStatusCode status, UploadProblem expected)
    {
        using var handler = new Handler(_ => new HttpResponseMessage(status));
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://api.test/") };
        var client = new PhotoUploadApi(http, http, new Token("tkn"));

        (await Should.ThrowAsync<PhotoUploadException>(() => client.GetUploadUrlAsync("p1", Ct))).Problem.ShouldBe(expected);
    }

    [Theory]
    [InlineData(HttpStatusCode.RequestEntityTooLarge, UploadProblem.Rejected)]
    [InlineData(HttpStatusCode.UnsupportedMediaType, UploadProblem.Rejected)]
    [InlineData(HttpStatusCode.Forbidden, UploadProblem.Transient)]
    [InlineData(HttpStatusCode.ServiceUnavailable, UploadProblem.Transient)]
    public async Task The_api_client_tells_why_storage_refused_the_file(HttpStatusCode status, UploadProblem expected)
    {
        using var handler = new Handler(_ => new HttpResponseMessage(status));
        using var http = new HttpClient(handler);
        var client = new PhotoUploadApi(http, http, new Token("tkn"));
        var target = new PhotoUrl(new Uri("https://storage.test/p1.jpg"), "PUT", new Dictionary<string, string>(), DateTimeOffset.MaxValue);

        (await Should.ThrowAsync<PhotoUploadException>(() => client.UploadAsync(target, Jpeg, "image/jpeg", Ct))).Problem.ShouldBe(expected);
    }

    [Fact]
    public async Task The_api_client_reports_no_network_and_no_sign_in()
    {
        using var handler = new Handler(_ => throw new HttpRequestException("no route"));
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://api.test/") };

        (await Should.ThrowAsync<PhotoUploadException>(() => new PhotoUploadApi(http, http, new Token("tkn")).GetUploadUrlAsync("p1", Ct))).Problem.ShouldBe(UploadProblem.Offline);
        (await Should.ThrowAsync<PhotoUploadException>(() => new PhotoUploadApi(http, http, new Token(null)).GetUploadUrlAsync("p1", Ct))).Problem.ShouldBe(UploadProblem.SignedOut);
        (await new PhotoUploadApi(http, http, new Token("tkn")).GetReadUrlAsync("p1", Ct)).ShouldBeNull();
    }
}
