using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Dialed.Client.Account;
using Dialed.Client.Sync;

namespace Dialed.Client.Friends;

public sealed record Person(string UserId, string DisplayName);

public sealed record FriendProfileInfo(string UserId, string DisplayName, int Beans, int Recipes, int Brews, int Followers);

/// <summary>One page of what a friend shares: the records as the server sent them, so any field can be shown.</summary>
public sealed record SharedPage(IReadOnlyList<JsonElement> Items, int Total, bool HasMore);

/// <summary>One shared bean or recipe as the server sent it, with the names that live in the friend's household (#77).</summary>
public sealed record SharedItem(JsonElement Item, string OwnerName, string? RoasterName, string? BeanName);

/// <summary>
/// The API's friend endpoints (see Dialed.Api): friend code, follow requests, followers and a friend's shared items.
/// Online only; the last profile and first pages are remembered in memory so a friend's page still opens when the network is gone.
/// </summary>
public sealed class FriendsApi(HttpClient http, IAccessTokenProvider tokens)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly ConcurrentDictionary<string, FriendProfileInfo> profiles = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, SharedPage> firstPages = new(StringComparer.Ordinal);

    private sealed record CodeDto(string Code);

    public async Task<string> GetCodeAsync(CancellationToken cancellationToken = default) =>
        (await SendAsync<CodeDto>(HttpMethod.Get, "friends/code", null, cancellationToken)).Code;

    public async Task<string> RotateCodeAsync(CancellationToken cancellationToken = default) =>
        (await SendAsync<CodeDto>(HttpMethod.Post, "friends/code/rotate", null, cancellationToken)).Code;

    public Task<Person> FollowAsync(string code, CancellationToken cancellationToken = default) =>
        SendAsync<Person>(HttpMethod.Post, "friends/follow", new { code }, cancellationToken);

    public Task<IReadOnlyList<Person>> RequestsAsync(CancellationToken cancellationToken = default) => ListAsync("friends/requests", cancellationToken);

    public Task<IReadOnlyList<Person>> FollowersAsync(CancellationToken cancellationToken = default) => ListAsync("friends/followers", cancellationToken);

    public Task<IReadOnlyList<Person>> FollowingAsync(CancellationToken cancellationToken = default) => ListAsync("friends/following", cancellationToken);

    public Task<IReadOnlyList<Person>> PendingFollowingAsync(CancellationToken cancellationToken = default) => ListAsync("friends/following/pending", cancellationToken);

    public Task<IReadOnlyList<Person>> BlockedAsync(CancellationToken cancellationToken = default) => ListAsync("friends/blocks", cancellationToken);

    public Task AcceptAsync(string userId, CancellationToken cancellationToken = default) =>
        SendAsync<Person>(HttpMethod.Post, $"friends/requests/{Uri.EscapeDataString(userId)}/accept", null, cancellationToken);

    public Task DeclineAsync(string userId, CancellationToken cancellationToken = default) =>
        SendNoContentAsync(HttpMethod.Post, $"friends/requests/{Uri.EscapeDataString(userId)}/decline", cancellationToken);

    public Task UnfollowAsync(string userId, CancellationToken cancellationToken = default) =>
        SendNoContentAsync(HttpMethod.Delete, $"friends/following/{Uri.EscapeDataString(userId)}", cancellationToken);

    public Task RemoveFollowerAsync(string userId, CancellationToken cancellationToken = default) =>
        SendNoContentAsync(HttpMethod.Delete, $"friends/followers/{Uri.EscapeDataString(userId)}", cancellationToken);

    public Task BlockAsync(string userId, CancellationToken cancellationToken = default) =>
        SendNoContentAsync(HttpMethod.Post, $"friends/blocks/{Uri.EscapeDataString(userId)}", cancellationToken);

    public Task UnblockAsync(string userId, CancellationToken cancellationToken = default) =>
        SendNoContentAsync(HttpMethod.Delete, $"friends/blocks/{Uri.EscapeDataString(userId)}", cancellationToken);

    /// <summary>The friend's profile header, or <c>null</c> when you may not see it (not a friend, removed or blocked).</summary>
    public async Task<FriendProfileInfo?> ProfileAsync(string userId, CancellationToken cancellationToken = default)
    {
        try
        {
            var profile = await SendAsync<FriendProfileInfo>(HttpMethod.Get, $"friends/{Uri.EscapeDataString(userId)}/profile", null, cancellationToken);
            profiles[userId] = profile;
            return profile;
        }
        catch (AccountRefusedException e) when (e.Status == 404)
        {
            profiles.TryRemove(userId, out _);
            return null;
        }
    }

    /// <summary>One page of shared <c>beans</c>, <c>recipes</c> or <c>brews</c>, newest first; <c>null</c> when not allowed.</summary>
    public async Task<SharedPage?> SharedAsync(string userId, string table, int skip, int pageSize = 20, CancellationToken cancellationToken = default)
    {
        try
        {
            var page = await SendAsync<SharedPage>(HttpMethod.Get, $"friends/{Uri.EscapeDataString(userId)}/shared/{table}?skip={skip}&pageSize={pageSize}", null, cancellationToken);
            if (skip == 0)
            {
                firstPages[$"{userId}/{table}"] = page;
            }

            return page;
        }
        catch (AccountRefusedException e) when (e.Status == 404)
        {
            return null;
        }
    }

    /// <summary>One shared <c>beans</c> or <c>recipes</c> item to copy it; <c>null</c> when it is gone or not shared with you.</summary>
    public async Task<SharedItem?> SharedItemAsync(string userId, string table, string itemId, CancellationToken cancellationToken = default)
    {
        try
        {
            return await SendAsync<SharedItem>(HttpMethod.Get, $"friends/{Uri.EscapeDataString(userId)}/shared/{table}/{Uri.EscapeDataString(itemId)}", null, cancellationToken);
        }
        catch (AccountRefusedException e) when (e.Status == 404)
        {
            return null;
        }
    }

    /// <summary>What was last seen of this friend, for when the network is gone.</summary>
    public FriendProfileInfo? CachedProfile(string userId) => profiles.GetValueOrDefault(userId);

    public SharedPage? CachedFirstPage(string userId, string table) => firstPages.GetValueOrDefault($"{userId}/{table}");

    private async Task<IReadOnlyList<Person>> ListAsync(string path, CancellationToken cancellationToken) =>
        await SendAsync<List<Person>>(HttpMethod.Get, path, null, cancellationToken);

    private async Task SendNoContentAsync(HttpMethod method, string path, CancellationToken cancellationToken)
    {
        using var response = await SendRawAsync(method, path, null, cancellationToken);
    }

    private async Task<T> SendAsync<T>(HttpMethod method, string path, object? body, CancellationToken cancellationToken)
    {
        using var response = await SendRawAsync(method, path, body, cancellationToken);
        try
        {
            return await response.Content.ReadFromJsonAsync<T>(Json, cancellationToken)
                ?? throw new AccountApiException("The server sent an empty answer.");
        }
        catch (JsonException e)
        {
            throw new AccountApiException("The server sent an answer the app cannot read.", e);
        }
    }

    private async Task<HttpResponseMessage> SendRawAsync(HttpMethod method, string path, object? body, CancellationToken cancellationToken)
    {
        string token;
        try
        {
            token = await tokens.GetAccessTokenAsync(cancellationToken) ?? throw new AccountApiException("Not signed in.");
        }
        catch (SyncTransportException e)
        {
            throw new AccountApiException(e.Message, e);
        }

        using var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body, options: Json);
        }

        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, cancellationToken);
        }
        catch (HttpRequestException e)
        {
            throw new AccountApiException("The server could not be reached.", e);
        }
        catch (TaskCanceledException e) when (!cancellationToken.IsCancellationRequested)
        {
            throw new AccountApiException("The server did not answer in time.", e);
        }

        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            response.Dispose();
            throw new AccountApiException("Sign-in expired.");
        }

        try
        {
            await ApiResponses.EnsureSuccessAsync(response, cancellationToken);
        }
        catch
        {
            response.Dispose();
            throw;
        }

        return response;
    }
}
