using Bunit;
using Dialed.Client;
using Dialed.Client.Data;
using Dialed.Client.Preferences;
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
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddFluentUIComponents();
        Services.AddLocalization();
        Services.AddSingleton(new HttpClient(new FailingHandler()) { BaseAddress = new Uri("https://api.test/") });
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
