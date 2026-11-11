using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Dialed.Client.Admin;

public enum AdminProblem
{
    /// <summary>The key is wrong (401).</summary>
    WrongKey,

    /// <summary>The server has no admin key set, so the admin endpoints do not exist (404 on a check).</summary>
    NotEnabled,

    /// <summary>The thing asked about is gone (404 after the check).</summary>
    NotFound,

    TooManyTries,

    /// <summary>No network, or an answer the app cannot read.</summary>
    Unreachable,
}

public sealed class AdminException(AdminProblem problem) : Exception(problem.ToString())
{
    public AdminProblem Problem { get; } = problem;
}

public sealed record AdminReport(string Id, string ReporterId, string OwnerId, string OwnerName, string Target, string? ItemId, string Reason, string? Note, DateTimeOffset CreatedAt);

public sealed record AdminSuspended(string UserId, string DisplayName);

/// <summary>A catalog entry as the owner sees it: how many people stand behind it, and the keys the rules are made of.</summary>
public sealed record AdminEntry(string Kind, string Id, string Name, int People, IReadOnlyList<string> Keys);

public sealed record AdminRebuild(int Roasters, int Beans);

/// <summary>
/// The owner's moderation endpoints (reports, suspensions, the catalog). They are guarded by one secret, sent as <c>X-Admin-Key</c>. The key is
/// kept in memory only: never stored on the device, gone when the page is closed or locked.
/// </summary>
public sealed class AdminApi(HttpClient http)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private string? key;

    public bool IsUnlocked => key is not null;

    /// <summary>Tries the key. Throws <see cref="AdminException"/> when it is wrong, when admin tools are not switched on, or on no network.</summary>
    public async Task UnlockAsync(string candidate, CancellationToken cancellationToken = default)
    {
        key = candidate;
        try
        {
            using var response = await SendAsync(HttpMethod.Get, "admin/reports", null, cancellationToken);

            // On a check, 404 means the server has no admin key set; anywhere else it means the thing asked about is gone.
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                throw new AdminException(AdminProblem.NotEnabled);
            }

            Check(response);
        }
        catch
        {
            key = null;
            throw;
        }
    }

    public void Lock() => key = null;

    public Task<IReadOnlyList<AdminReport>> ReportsAsync(CancellationToken cancellationToken = default) => ListAsync<AdminReport>("admin/reports", cancellationToken);

    public Task<IReadOnlyList<AdminSuspended>> SuspendedAsync(CancellationToken cancellationToken = default) => ListAsync<AdminSuspended>("admin/users/suspended", cancellationToken);

    public Task SuspendAsync(string userId, CancellationToken cancellationToken = default) => PostAsync($"admin/users/{Uri.EscapeDataString(userId)}/suspend", null, cancellationToken);

    public Task RestoreAsync(string userId, CancellationToken cancellationToken = default) => PostAsync($"admin/users/{Uri.EscapeDataString(userId)}/restore", null, cancellationToken);

    /// <summary>Catalog entries, fewest people first.</summary>
    public Task<IReadOnlyList<AdminEntry>> EntriesAsync(int take = 50, CancellationToken cancellationToken = default) => ListAsync<AdminEntry>($"admin/catalog/entries?take={take}", cancellationToken);

    public Task HideAsync(string entryId, CancellationToken cancellationToken = default) => PostAsync($"admin/catalog/entries/{Uri.EscapeDataString(entryId)}/hide", null, cancellationToken);

    public Task MergeAsync(string fromId, string intoId, CancellationToken cancellationToken = default) => PostAsync("admin/catalog/merge", new { fromId, intoId }, cancellationToken);

    public async Task<AdminRebuild> RebuildAsync(CancellationToken cancellationToken = default)
    {
        using var response = await SendAsync(HttpMethod.Post, "admin/catalog/rebuild", null, cancellationToken);
        Check(response);
        return await Read<AdminRebuild>(response, cancellationToken) ?? new AdminRebuild(0, 0);
    }

    private async Task<IReadOnlyList<T>> ListAsync<T>(string path, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(HttpMethod.Get, path, null, cancellationToken);
        Check(response);
        return await Read<List<T>>(response, cancellationToken) ?? [];
    }

    private async Task PostAsync(string path, object? body, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(HttpMethod.Post, path, body, cancellationToken);
        Check(response);
    }

    private static void Check(HttpResponseMessage response)
    {
        switch (response.StatusCode)
        {
            case HttpStatusCode.Unauthorized:
                throw new AdminException(AdminProblem.WrongKey);
            case HttpStatusCode.TooManyRequests:
                throw new AdminException(AdminProblem.TooManyTries);
            case HttpStatusCode.NotFound:
                throw new AdminException(AdminProblem.NotFound);
            default:
                if (!response.IsSuccessStatusCode)
                {
                    throw new AdminException(AdminProblem.Unreachable);
                }

                break;
        }
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, object? body, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Add("X-Admin-Key", key ?? "");
        if (body is not null)
        {
            request.Content = JsonContent.Create(body, options: Json);
        }

        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, cancellationToken);
        }
        catch (HttpRequestException)
        {
            throw new AdminException(AdminProblem.Unreachable);
        }

        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            response.Dispose();
            throw new AdminException(AdminProblem.TooManyTries);
        }

        return response;
    }

    private static async Task<T?> Read<T>(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            return await response.Content.ReadFromJsonAsync<T>(Json, cancellationToken);
        }
        catch (JsonException)
        {
            throw new AdminException(AdminProblem.Unreachable);
        }
    }
}
