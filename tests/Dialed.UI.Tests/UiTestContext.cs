using Bunit;
using Dialed.Client;
using Dialed.Client.Account;
using Dialed.Client.Friends;
using Dialed.Client.Households;
using Dialed.Client.Data;
using Dialed.Client.Platform;
using Dialed.Client.Preferences;
using Dialed.Client.Storage;
using Dialed.Client.Sync;
using Dialed.Domain.Model;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.FluentUI.AspNetCore.Components;

namespace Dialed.UI.Tests;

/// <summary>bUnit context with the same UI services as Dialed.Web; Fluent's JS calls are answered loosely.</summary>
public abstract class UiTestContext : BunitContext
{
    protected TestEnvironment Environment { get; } = new();

    protected TestPwaState Pwa { get; } = new();

    /// <summary>Who is using the app; change <see cref="TestUser.UserId"/> to act as another household member.</summary>
    protected TestUser Context { get; } = new();

    /// <summary>Stands in for the camera and photo library: set <see cref="TestPhotoCapture.Next"/> to what the user will "take".</summary>
    protected TestScanner Scanner { get; } = new();

    protected TestPhotoCapture Camera { get; } = new();

    /// <summary>Sync as the app sees it: set <see cref="TestTokens.Value"/> to sign in, <see cref="TestSyncTransport.Offline"/> to cut the server off.</summary>
    protected TestTokens Tokens { get; } = new();

    protected TestSyncTransport Transport { get; } = new();

    protected TestNetwork Network { get; } = new();

    /// <summary>The passkey prompt: set <see cref="TestPasskeys.Next"/> to what the user does with it.</summary>
    protected TestPasskeys Passkeys { get; } = new();

    /// <summary>The API's account endpoints: set <see cref="TestAccountServer.Respond"/> to change what it answers.</summary>
    protected TestAccountServer Accounts { get; } = new();

    /// <summary>The export and delete endpoints, and the file the person receives.</summary>
    protected TestAccountDataServer DataServer { get; } = new();

    protected TestFiles ExportedFiles { get; } = new();

    /// <summary>Collects the error reports the app would send.</summary>
    protected TestErrorSink ErrorSink { get; } = new();

    /// <summary>The friend endpoints; set <see cref="TestFriendsServer.Respond"/> to script answers per "METHOD path".</summary>
    protected TestFriendsServer FriendsServer { get; } = new();

    /// <summary>The API's household endpoints: a household of one by default.</summary>
    protected TestHouseholdServer HouseholdServer { get; } = new();

    /// <summary>The system share sheet: what was shared, and whether the platform has one.</summary>
    protected TestShare Shares { get; } = new();

    protected UiTestContext()
    {
        Services.AddSingleton<IAppEnvironment>(Environment);
        Services.AddSingleton<IPwaState>(Pwa);
        Services.AddSingleton<Dialed.Client.Platform.IDeviceInfo, TestDeviceInfo>();
        Services.AddSingleton(TimeProvider.System);
        Services.AddSingleton(Context);
        Services.AddSingleton<ICurrentContext>(Context);
        Services.AddSingleton(typeof(IRepository<>), typeof(InMemoryRepository<>));
        Services.AddSingleton<IUserPreferences, InMemoryUserPreferences>();
        Services.AddSingleton<StorageStatus>();
        Services.AddSingleton<UnitFormatter>();
        Services.AddSingleton<BrewTimerHandoff>();
        Services.AddSingleton<ILocalFileStore, InMemoryLocalFileStore>();
        Services.AddSingleton<PhotoService>();
        Services.AddSingleton<IPhotoCapture>(Camera);
        Services.AddSingleton<Dialed.Client.Scan.ILabelScanner>(Scanner);
        Services.AddSingleton<IAccessTokenProvider>(Tokens);
        Services.AddSingleton<ISyncTransport>(Transport);
        Services.AddSingleton<IConnectivity>(Network);
        Services.AddSingleton<IAppLifecycle, TestLifecycle>();
        Services.AddSingleton<IErrorSink>(ErrorSink);
        Services.AddSingleton<IShareService>(Shares);
        Services.AddDialedSync();
        Services.AddSingleton<IPasskeyClient>(Passkeys);
        Services.AddSingleton<ISessionStore, InMemorySessionStore>();
        Services.AddSingleton<AuthSession>();
        Services.AddSingleton(new AccountApi(new HttpClient(Accounts) { BaseAddress = new Uri("https://api.test/") }));
        Services.AddSingleton<AccountService>();
        Services.AddSingleton<IFileExport>(ExportedFiles);
        Services.AddSingleton(new AccountDataApi(new HttpClient(DataServer) { BaseAddress = new Uri("https://api.test/") }, Tokens));
        Services.AddSingleton<AccountDataService>();
        Services.AddSingleton<PasskeyManager>();
        Services.AddSingleton(new FriendsApi(new HttpClient(FriendsServer) { BaseAddress = new Uri("https://api.test/") }, Tokens));
        Services.AddSingleton<FriendCopyService>();
        Services.AddSingleton(new HouseholdApi(new HttpClient(HouseholdServer) { BaseAddress = new Uri("https://api.test/") }, Tokens));
        Services.AddSingleton<HouseholdManager>();
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddFluentUIComponents();
        Services.AddLocalization();
        Services.AddSingleton(new HttpClient(new FailingHandler()) { BaseAddress = new Uri("https://api.test/") });
    }

    protected sealed class TestTokens : IAccessTokenProvider
    {
        public string? Value { get; set; }

        public Task<string?> GetAccessTokenAsync(CancellationToken cancellationToken = default) => Task.FromResult(Value);
    }

    protected sealed class TestNetwork : IConnectivity
    {
        public bool IsOnline { get; set; } = true;

        public event Action<bool>? Changed;

        public void Set(bool online)
        {
            IsOnline = online;
            Changed?.Invoke(online);
        }
    }

    private sealed class TestLifecycle : IAppLifecycle
    {
        public bool IsForeground => true;

        public event Action? Resumed
        {
            add { }
            remove { }
        }

        public event Action? Paused
        {
            add { }
            remove { }
        }
    }

    /// <summary>A server that accepts everything and has nothing new, or is unreachable.</summary>
    protected sealed class TestSyncTransport : ISyncTransport
    {
        public bool Offline { get; set; }

        /// <summary>Refuse every push for good, like a missing permission.</summary>
        public bool Reject { get; set; }

        public int Pushes { get; private set; }

        public Task<PushResult<T>> PushAsync<T>(string table, T entity, string? ifMatchVersion, CancellationToken cancellationToken = default)
            where T : SyncEntity
        {
            if (Offline)
            {
                throw new SyncTransportException("offline");
            }

            if (Reject)
            {
                return Task.FromResult(new PushResult<T>(PushOutcome.Rejected, Message: "403 no access"));
            }

            Pushes++;
            return Task.FromResult(new PushResult<T>(PushOutcome.Accepted, new ServerRecord<T>(entity, "1")));
        }

        public Task<PullPage<T>> PullAsync<T>(string table, SyncWatermark? after, int pageSize, CancellationToken cancellationToken = default)
            where T : SyncEntity
        {
            if (Offline)
            {
                throw new SyncTransportException("offline");
            }

            // Vendors the "server" holds: two on the first page, the rest after the gate opens (to look at the app mid-load).
            if (typeof(T) == typeof(Vendor) && ServerVendors.Count > 0)
            {
                if (after is null)
                {
                    return Task.FromResult(new PullPage<T>([.. ServerVendors.Take(2).Select(v => new ServerRecord<T>((T)(object)v, "1"))], true));
                }

                return SecondPageAsync<T>(cancellationToken);
            }

            return Task.FromResult(new PullPage<T>([], false));
        }

        /// <summary>Vendors the server holds for a first pull.</summary>
        public List<Vendor> ServerVendors { get; } = [];

        /// <summary>When set, the pull after the first page waits until this completes.</summary>
        public TaskCompletionSource? HoldSecondPage { get; set; }

        private async Task<PullPage<T>> SecondPageAsync<T>(CancellationToken cancellationToken)
            where T : SyncEntity
        {
            if (HoldSecondPage is { } gate)
            {
                await gate.Task.WaitAsync(cancellationToken);
            }

            return new PullPage<T>([.. ServerVendors.Skip(2).Select(v => new ServerRecord<T>((T)(object)v, "1"))], false);
        }
    }

    protected sealed class TestPasskeys : IPasskeyClient
    {
        public PasskeyOutcome Next { get; set; } = new(PasskeyStatus.Success, "{\"id\":\"cred\"}");

        public bool Supported { get; set; } = true;

        public Task<bool> IsSupportedAsync() => Task.FromResult(Supported);

        public Task<bool> IsAutofillSupportedAsync() => Task.FromResult(false);

        public Task<PasskeyOutcome> CreateAsync(string optionsJson) => Task.FromResult(Next);

        public Task<PasskeyOutcome> GetAsync(string optionsJson, bool autofill = false) => Task.FromResult(Next);

        public Task CancelPendingAsync() => Task.CompletedTask;
    }

    protected sealed class TestErrorSink : IErrorSink
    {
        public List<Dialed.Client.Telemetry.ErrorReport> Reports { get; } = [];

        public Task SendAsync(Dialed.Client.Telemetry.ErrorReport report)
        {
            Reports.Add(report);
            return Task.CompletedTask;
        }
    }

    protected sealed class TestFiles : IFileExport
    {
        public List<(string Name, string ContentType, byte[] Content)> Saved { get; } = [];

        /// <summary>Pretend the person dismissed the share sheet.</summary>
        public bool Dismiss { get; set; }

        public Task<bool> SaveAsync(string fileName, string contentType, byte[] content)
        {
            if (Dismiss)
            {
                return Task.FromResult(false);
            }

            Saved.Add((fileName, contentType, content));
            return Task.FromResult(true);
        }
    }

    protected sealed class TestFriendsServer : HttpMessageHandler
    {
        public List<string> Calls { get; } = [];

        /// <summary>Overrides the answer for "METHOD path?query"; return <c>null</c> for the default (empty lists, code ABCD2345, no content).</summary>
        public Func<string, HttpResponseMessage?>? Respond { get; set; }

        public static HttpResponseMessage Json(string json, System.Net.HttpStatusCode status = System.Net.HttpStatusCode.OK) =>
            new(status) { Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json") };

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var call = $"{request.Method} {request.RequestUri!.PathAndQuery.TrimStart('/')}";
            Calls.Add(call);
            if (Respond?.Invoke(call) is { } custom)
            {
                return Task.FromResult(custom);
            }

            var path = request.RequestUri.AbsolutePath.TrimStart('/');
            return Task.FromResult(path switch
            {
                "friends/code" or "friends/code/rotate" => Json("{\"code\":\"ABCD2345\"}"),
                "friends/requests" or "friends/following" or "friends/following/pending" or "friends/followers" or "friends/blocks" => Json("[]"),
                _ => new HttpResponseMessage(System.Net.HttpStatusCode.NoContent),
            });
        }
    }

    protected sealed class TestAccountDataServer : HttpMessageHandler
    {
        public List<string> Paths { get; } = [];

        /// <summary>The passkeys the account lists.</summary>
        public string PasskeyList { get; set; } = "[{\"id\":\"p1\",\"name\":\"First passkey\",\"createdAt\":\"2026-01-01T00:00:00+00:00\",\"lastUsedAt\":null,\"backedUp\":false}]";

        public string RecoveryJson { get; set; } = "{\"recoveryCodesLeft\":8,\"hasBackedUpPasskey\":false,\"passkeys\":1}";

        /// <summary>Overrides the answer for a path; return <c>null</c> for the default answer.</summary>
        public Func<string, HttpResponseMessage?>? Respond { get; set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath.TrimStart('/');
            var key = request.Method == HttpMethod.Put || request.Method == HttpMethod.Delete ? $"{request.Method} {path}" : path;
            Paths.Add(key);
            if (Respond?.Invoke(key) is { } custom)
            {
                return Task.FromResult(custom);
            }

            return Task.FromResult(path switch
            {
                "account/export" => new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent("{\"formatVersion\":1}", System.Text.Encoding.UTF8, "application/json") },
                "account/passkeys" when request.Method == HttpMethod.Get => new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent(PasskeyList, System.Text.Encoding.UTF8, "application/json") },
                "account/recovery" => new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent(RecoveryJson, System.Text.Encoding.UTF8, "application/json") },
                "account/recovery/codes" => new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent("{\"codes\":[\"NEW1-AAAA\",\"NEW2-BBBB\"]}", System.Text.Encoding.UTF8, "application/json") },
                "account/passkeys/options" => new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent("{\"options\":{\"challenge\":\"c\"},\"state\":\"s\"}", System.Text.Encoding.UTF8, "application/json") },
                "account/delete/options" => new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent("{\"options\":{\"challenge\":\"c\"},\"state\":\"s\"}", System.Text.Encoding.UTF8, "application/json") },
                _ => new HttpResponseMessage(System.Net.HttpStatusCode.NoContent),
            });
        }
    }

    /// <summary>A stand-in for the API's account endpoints: ceremonies succeed and hand out tokens unless told otherwise.</summary>
    protected sealed class TestAccountServer : HttpMessageHandler
    {
        public const string TokensJson = "{\"accessToken\":\"a\",\"accessTokenExpiresAt\":\"2099-01-01T00:00:00+00:00\",\"refreshToken\":\"r\",\"userId\":\"u1\",\"displayName\":\"Thomas\",\"householdId\":\"h-u1\"}";

        public List<string> Paths { get; } = [];

        /// <summary>The request bodies, in order, next to <see cref="Paths"/>.</summary>
        public List<string> Bodies { get; } = [];

        /// <summary>Overrides the answer for a path (<c>auth/signup</c>, ...); return <c>null</c> for the default answer.</summary>
        public Func<string, HttpResponseMessage?>? Respond { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath.TrimStart('/');
            Paths.Add(path);
            Bodies.Add(request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken));
            if (Respond?.Invoke(path) is { } custom)
            {
                return custom;
            }

            var json = path switch
            {
                "auth/signup/options" or "auth/signin/options" or "auth/recover/options" => "{\"options\":{\"challenge\":\"c\"},\"state\":\"s\"}",
                "auth/signup" or "auth/signin" or "auth/recover" => TokensJson,
                _ => "{}",
            };
            return new HttpResponseMessage(path is "auth/signout" or "auth/recover/email" ? System.Net.HttpStatusCode.NoContent : System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json"),
            };
        }
    }

    /// <summary>A stand-in for the household endpoints; set <see cref="Members"/> and <see cref="YouAreOwner"/> to shape the household.</summary>
    protected sealed class TestHouseholdServer : HttpMessageHandler
    {
        public List<(string Name, string Id)> Members { get; set; } = [("You", "u1")];

        public bool YouAreOwner { get; set; } = true;

        /// <summary>The households the person belongs to (id, name); the one named by <see cref="ActiveId"/> is active.</summary>
        public List<(string Id, string Name)> Households { get; set; } = [("h1", "Home")];

        public string ActiveId { get; set; } = "h1";

        public List<string> Calls { get; } = [];

        /// <summary>Overrides the answer for "METHOD path"; return <c>null</c> for the default answer.</summary>
        public Func<string, HttpResponseMessage?>? Respond { get; set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var call = $"{request.Method} {request.RequestUri!.AbsolutePath.TrimStart('/')}";
            Calls.Add(call);
            if (Respond?.Invoke(call) is { } custom)
            {
                return Task.FromResult(custom);
            }

            if (call == "GET households")
            {
                return Task.FromResult(Json("[" + string.Join(",", Households.Select(h => $"{{\"id\":\"{h.Id}\",\"name\":\"{h.Name}\",\"isPersonal\":false,\"isActive\":{(h.Id == ActiveId ? "true" : "false")},\"isOwner\":true,\"memberCount\":1}}")) + "]"));
            }

            if (call == "POST households/mine/switch")
            {
                ActiveId = System.Text.Json.JsonDocument.Parse(request.Content!.ReadAsStringAsync(cancellationToken).GetAwaiter().GetResult()).RootElement.GetProperty("householdId").GetString()!;
            }

            if (call == "POST households/mine/invites")
            {
                return Task.FromResult(Json("{\"code\":\"ABCD2345\",\"expiresAt\":\"2099-01-01T00:00:00+00:00\"}"));
            }

            if (call.StartsWith("DELETE households/mine/members/", StringComparison.Ordinal))
            {
                Members.RemoveAll(m => m.Id == call["DELETE households/mine/members/".Length..]);
            }
            else if (call == "POST households/mine/transfer")
            {
                YouAreOwner = false;
            }
            else if (call == "POST households/mine/join")
            {
                Members = [("Owner", "owner"), ("You", "u1")];
                YouAreOwner = false;
            }
            else if (call == "POST households/mine/leave")
            {
                Members = [("You", "u1")];
                YouAreOwner = true;
            }

            var members = string.Join(",", Members.Select((m, i) => $"{{\"userId\":\"{m.Id}\",\"displayName\":\"{m.Name}\",\"isOwner\":{(YouAreOwner ? (m.Id == "u1" ? "true" : "false") : (m.Id == "owner" ? "true" : "false"))}}}"));
            return Task.FromResult(Json($"{{\"id\":\"h1\",\"name\":\"Our household\",\"members\":[{members}],\"youAreOwner\":{(YouAreOwner ? "true" : "false")}}}"));
        }

        private static HttpResponseMessage Json(string json) =>
            new(System.Net.HttpStatusCode.OK) { Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json") };
    }

    protected sealed class TestScanner : Dialed.Client.Scan.ILabelScanner
    {
        public Dialed.Client.Scan.ScannedLabel? Result { get; set; }

        public Dialed.Client.Scan.ScanProblem? Problem { get; set; }

        public int Scans { get; private set; }

        public Task<Dialed.Client.Scan.ScannedLabel> ScanAsync(CapturedPhoto photo, CancellationToken cancellationToken = default)
        {
            Scans++;
            return Problem is { } problem
                ? throw new Dialed.Client.Scan.LabelScanException(problem)
                : Task.FromResult(Result ?? throw new InvalidOperationException("No scan result set."));
        }
    }

    protected sealed class TestPhotoCapture : IPhotoCapture
    {
        public CapturedPhoto? Next { get; set; }

        public PhotoSource? LastSource { get; private set; }

        public Task<CapturedPhoto?> CaptureAsync(PhotoSource source, int maxDimension = 2048, CancellationToken cancellationToken = default)
        {
            LastSource = source;
            return Task.FromResult(Next);
        }
    }

    protected sealed class TestShare : IShareService
    {
        public bool Supported { get; set; } = true;

        public List<ShareRequest> Shared { get; } = [];

        public Task<bool> IsSupportedAsync() => Task.FromResult(Supported);

        public Task<bool> ShareAsync(ShareRequest request)
        {
            Shared.Add(request);
            return Task.FromResult(true);
        }
    }

    protected sealed class TestUser : ICurrentContext
    {
        public string UserId { get; set; } = "local-user";

        public string HouseholdId => "local-household";
    }

    protected sealed class TestEnvironment : IAppEnvironment
    {
        public bool IsDevelopment { get; set; }

        public string? FeedbackEmail { get; set; }
    }

    protected sealed class TestDeviceInfo : Dialed.Client.Platform.IDeviceInfo
    {
        public Task<string> DescribeAsync() => Task.FromResult("phone, iOS 18.1, installed app");
    }

    protected sealed class TestPwaState : IPwaState
    {
        public bool UpdateAvailable { get; private set; }

        public bool IsInstalled { get; set; }

        public bool NeedsInstallGuide { get; set; }

        public int UpdatesApplied { get; private set; }

        public event Action? Changed;

        public Task ApplyUpdateAsync()
        {
            UpdatesApplied++;
            return Task.CompletedTask;
        }

        public void PublishUpdate()
        {
            UpdateAvailable = true;
            Changed?.Invoke();
        }
    }

    private sealed class FailingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new HttpRequestException("offline in tests");
    }
}
