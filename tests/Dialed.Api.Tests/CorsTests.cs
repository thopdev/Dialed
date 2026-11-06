using Microsoft.AspNetCore.Mvc.Testing;

namespace Dialed.Api.Tests;

public sealed class CorsTests(WebApplicationFactory<Program> factory)
    : IClassFixture<WebApplicationFactory<Program>>
{
    [Fact]
    public async Task Allows_the_local_app_origin()
    {
        using var response = await GetHealthFrom("https://localhost:7200");

        response.Headers.GetValues("Access-Control-Allow-Origin").ShouldBe(["https://localhost:7200"]);
    }

    [Fact]
    public async Task Does_not_allow_unknown_origins()
    {
        using var response = await GetHealthFrom("https://evil.example");

        response.Headers.Contains("Access-Control-Allow-Origin").ShouldBeFalse();
    }

    private async Task<HttpResponseMessage> GetHealthFrom(string origin)
    {
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/health");
        request.Headers.Add("Origin", origin);
        return await client.SendAsync(request, TestContext.Current.CancellationToken);
    }
}
