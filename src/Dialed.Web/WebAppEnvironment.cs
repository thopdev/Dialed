using Dialed.Client;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using Microsoft.Extensions.Configuration;

namespace Dialed.Web;

/// <summary>Browser host implementation of <see cref="IAppEnvironment"/>.</summary>
internal sealed class WebAppEnvironment(IWebAssemblyHostEnvironment environment, IConfiguration configuration) : IAppEnvironment
{
    public bool IsDevelopment => environment.IsDevelopment();

    public string? FeedbackEmail => configuration["Feedback:Email"];
}
