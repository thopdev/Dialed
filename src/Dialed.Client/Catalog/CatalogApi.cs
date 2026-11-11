using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Dialed.Client.Platform;
using Dialed.Client.Sync;
using Dialed.Domain.Model;

namespace Dialed.Client.Catalog;

/// <summary>A roaster from the shared catalog (#105): what people chose to share, with a count of how many.</summary>
public sealed record CatalogRoaster(string Id, string Name, string? Country, string? City, string? Website, int People);

public sealed record CatalogBean(
    string Id,
    string Name,
    string? RoasterId,
    string? RoasterName,
    string? Country,
    string? Region,
    string? Process,
    IReadOnlyList<string> Varieties,
    RoastLevel? RoastLevel,
    IReadOnlyList<string> TastingNotes,
    int People);

/// <summary>
/// Suggestions from the shared catalog. Online only and always optional: without a network, a sign-in or an answer the lists are
/// simply empty, so nothing that uses them ever breaks. Using an entry copies it; it is never linked.
/// </summary>
public interface ICatalogApi
{
    Task<IReadOnlyList<CatalogRoaster>> SearchRoastersAsync(string query, CancellationToken cancellationToken = default);

    /// <summary>Beans by name, optionally narrowed to a roaster (a roaster alone lists its beans).</summary>
    Task<IReadOnlyList<CatalogBean>> SearchBeansAsync(string? query, string? roaster, CancellationToken cancellationToken = default);
}

/// <summary>Nothing is suggested: the default until a host registers the real client.</summary>
public sealed class NoCatalog : ICatalogApi
{
    public static readonly NoCatalog Instance = new();

    public Task<IReadOnlyList<CatalogRoaster>> SearchRoastersAsync(string query, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<CatalogRoaster>>([]);

    public Task<IReadOnlyList<CatalogBean>> SearchBeansAsync(string? query, string? roaster, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<CatalogBean>>([]);
}

/// <summary>Calls <c>GET /catalog/roasters</c> and <c>GET /catalog/beans</c>.</summary>
public sealed class CatalogApi(HttpClient http, IAccessTokenProvider tokens, IConnectivity connectivity) : ICatalogApi
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public Task<IReadOnlyList<CatalogRoaster>> SearchRoastersAsync(string query, CancellationToken cancellationToken = default) =>
        GetAsync<CatalogRoaster>($"catalog/roasters?q={Uri.EscapeDataString(query)}", cancellationToken);

    public Task<IReadOnlyList<CatalogBean>> SearchBeansAsync(string? query, string? roaster, CancellationToken cancellationToken = default) =>
        GetAsync<CatalogBean>($"catalog/beans?q={Uri.EscapeDataString(query ?? "")}&roaster={Uri.EscapeDataString(roaster ?? "")}", cancellationToken);

    private async Task<IReadOnlyList<T>> GetAsync<T>(string path, CancellationToken cancellationToken)
    {
        if (!connectivity.IsOnline)
        {
            return [];
        }

        try
        {
            if (await tokens.GetAccessTokenAsync(cancellationToken) is not { } token)
            {
                return [];
            }

            using var request = new HttpRequestMessage(HttpMethod.Get, path);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var response = await http.SendAsync(request, cancellationToken);
            return response.IsSuccessStatusCode
                ? await response.Content.ReadFromJsonAsync<List<T>>(Json, cancellationToken) ?? []
                : [];
        }
        catch (Exception e) when (e is HttpRequestException or JsonException or SyncTransportException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            // A suggestion that cannot be fetched is no suggestion.
            return [];
        }
    }
}
