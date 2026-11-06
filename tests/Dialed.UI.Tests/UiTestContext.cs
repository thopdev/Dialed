using Bunit;
using Dialed.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.FluentUI.AspNetCore.Components;

namespace Dialed.UI.Tests;

/// <summary>bUnit context with the same UI services as Dialed.Web; Fluent's JS calls are answered loosely.</summary>
public abstract class UiTestContext : BunitContext
{
    protected TestEnvironment Environment { get; } = new();

    protected UiTestContext()
    {
        Services.AddSingleton<IAppEnvironment>(Environment);
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddFluentUIComponents();
        Services.AddLocalization();
        Services.AddSingleton(new HttpClient(new FailingHandler()) { BaseAddress = new Uri("https://api.test/") });
    }

    protected sealed class TestEnvironment : IAppEnvironment
    {
        public bool IsDevelopment { get; set; }
    }

    private sealed class FailingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new HttpRequestException("offline in tests");
    }
}
