using Bunit;
using Dialed.Client;
using Dialed.Client.Account;
using Dialed.Client.Households;
using Dialed.Client.Data;
using Dialed.Client.Platform;
using Dialed.Client.Preferences;
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
    protected TestPhotoCapture Camera { get; } = new();

    /// <summary>Sync as the app sees it: set <see cref="TestTokens.Value"/> to sign in, <see cref="TestSyncTransport.Offline"/> to cut the server off.</summary>
    protected TestTokens Tokens { get; } = new();

    protected TestSyncTransport Transport { get; } = new();

    protected TestNetwork Network { get; } = new();

    /// <summary>The passkey prompt: set <see cref="TestPasskeys.Next"/> to what the user does with it.</summary>
    protected TestPasskeys Passkeys { get; } = new();

    /// <summary>The API's account endpoints: set <see cref="TestAccountServer.Respond"/> to change what it answers.</summary>
    protected TestAccountServer Accounts { get; } = new();

    /// <summary>The API's household endpoints: a household of one by default.</summary>
    protected TestHouseholdServer HouseholdServer { get; } = new();

    protected UiTestContext()
    {
        Services.AddSingleton<IAppEnvironment>(Environment);
        Services.AddSingleton<IPwaState>(Pwa);
        Services.AddSingleton(TimeProvider.System);
        Services.AddSingleton(Context);
        Services.AddSingleton<ICurrentContext>(Context);
        Services.AddSingleton(typeof(IRepository<>), typeof(InMemoryRepository<>));
        Services.AddSingleton<IUserPreferences, InMemoryUserPreferences>();
        Services.AddSingleton<UnitFormatter>();
        Services.AddSingleton<BrewTimerHandoff>();
        Services.AddSingleton<ILocalFileStore, InMemoryLocalFileStore>();
        Services.AddSingleton<PhotoService>();
        Services.AddSingleton<IPhotoCapture>(Camera);
        Services.AddSingleton<IAccessTokenProvider>(Tokens);
        Services.AddSingleton<ISyncTransport>(Transport);
        Services.AddSingleton<IConnectivity>(Network);
        Services.AddSingleton<IAppLifecycle, TestLifecycle>();
        Services.AddDialedSync();
        Services.AddSingleton<IPasskeyClient>(Passkeys);
        Services.AddSingleton<ISessionStore, InMemorySessionStore>();
        Services.AddSingleton<AuthSession>();
        Services.AddSingleton(new AccountApi(new HttpClient(Accounts) { BaseAddress = new Uri("https://api.test/") }));
        Services.AddSingleton<AccountService>();
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

        public int Pushes { get; private set; }

        public Task<PushResult<T>> PushAsync<T>(string table, T entity, string? ifMatchVersion, CancellationToken cancellationToken = default)
            where T : SyncEntity
        {
            if (Offline)
            {
                throw new SyncTransportException("offline");
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

            return Task.FromResult(new PullPage<T>([], false));
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

    /// <summary>A stand-in for the API's account endpoints: ceremonies succeed and hand out tokens unless told otherwise.</summary>
    protected sealed class TestAccountServer : HttpMessageHandler
    {
        public const string TokensJson = "{\"accessToken\":\"a\",\"accessTokenExpiresAt\":\"2099-01-01T00:00:00+00:00\",\"refreshToken\":\"r\",\"userId\":\"u1\",\"displayName\":\"Thomas\",\"householdId\":\"h-u1\"}";

        public List<string> Paths { get; } = [];

        /// <summary>Overrides the answer for a path (<c>auth/signup</c>, ...); return <c>null</c> for the default answer.</summary>
        public Func<string, HttpResponseMessage?>? Respond { get; set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath.TrimStart('/');
            Paths.Add(path);
            if (Respond?.Invoke(path) is { } custom)
            {
                return Task.FromResult(custom);
            }

            var json = path switch
            {
                "auth/signup/options" or "auth/signin/options" => "{\"options\":{\"challenge\":\"c\"},\"state\":\"s\"}",
                "auth/signup" or "auth/signin" => TokensJson,
                _ => "{}",
            };
            return Task.FromResult(new HttpResponseMessage(path == "auth/signout" ? System.Net.HttpStatusCode.NoContent : System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json"),
            });
        }
    }

    /// <summary>A stand-in for the household endpoints; set <see cref="Members"/> and <see cref="YouAreOwner"/> to shape the household.</summary>
    protected sealed class TestHouseholdServer : HttpMessageHandler
    {
        public List<(string Name, string Id)> Members { get; set; } = [("You", "u1")];

        public bool YouAreOwner { get; set; } = true;

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

            if (call == "POST households/mine/invites")
            {
                return Task.FromResult(Json("{\"code\":\"ABCD2345\",\"expiresAt\":\"2099-01-01T00:00:00+00:00\"}"));
            }

            if (call.StartsWith("DELETE households/mine/members/", StringComparison.Ordinal))
            {
                Members.RemoveAll(m => m.Id == call["DELETE households/mine/members/".Length..]);
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

    protected sealed class TestUser : ICurrentContext
    {
        public string UserId { get; set; } = "local-user";

        public string HouseholdId => "local-household";
    }

    protected sealed class TestEnvironment : IAppEnvironment
    {
        public bool IsDevelopment { get; set; }
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
