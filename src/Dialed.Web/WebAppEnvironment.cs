using Dialed.Client;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;

namespace Dialed.Web;

/// <summary>Browser host implementation of <see cref="IAppEnvironment"/>.</summary>
internal sealed class WebAppEnvironment(IWebAssemblyHostEnvironment environment) : IAppEnvironment
{
    public bool IsDevelopment => environment.IsDevelopment();
}
