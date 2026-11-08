using Bunit;
using Dialed.Client;
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
