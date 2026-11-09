using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Dialed.Client.Account;
using Dialed.Client.Sync;

namespace Dialed.Client.Households;

public sealed record HouseholdMember(string UserId, string DisplayName, bool IsOwner);

public sealed record HouseholdInfo(string Id, string Name, IReadOnlyList<HouseholdMember> Members, bool YouAreOwner);

public sealed record InviteInfo(string Code, DateTimeOffset ExpiresAt);

/// <summary>The API's household endpoints (see Dialed.Api): view, invite, join, leave and remove.</summary>
public sealed class HouseholdApi(HttpClient http, IAccessTokenProvider tokens)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public Task<HouseholdInfo> GetMineAsync(CancellationToken cancellationToken = default) =>
        SendAsync<HouseholdInfo>(HttpMethod.Get, "households/mine", null, cancellationToken);

    public Task<InviteInfo> CreateInviteAsync(CancellationToken cancellationToken = default) =>
        SendAsync<InviteInfo>(HttpMethod.Post, "households/mine/invites", null, cancellationToken);

    public Task<HouseholdInfo> JoinAsync(string code, CancellationToken cancellationToken = default) =>
        SendAsync<HouseholdInfo>(HttpMethod.Post, "households/mine/join", new { code }, cancellationToken);

    public Task<HouseholdInfo> LeaveAsync(CancellationToken cancellationToken = default) =>
        SendAsync<HouseholdInfo>(HttpMethod.Post, "households/mine/leave", null, cancellationToken);

    public Task<HouseholdInfo> TransferOwnershipAsync(string userId, CancellationToken cancellationToken = default) =>
        SendAsync<HouseholdInfo>(HttpMethod.Post, "households/mine/transfer", new { userId }, cancellationToken);

    public Task<HouseholdInfo> RemoveMemberAsync(string userId, CancellationToken cancellationToken = default) =>
        SendAsync<HouseholdInfo>(HttpMethod.Delete, $"households/mine/members/{Uri.EscapeDataString(userId)}", null, cancellationToken);

    private async Task<T> SendAsync<T>(HttpMethod method, string path, object? body, CancellationToken cancellationToken)
    {
        string token;
        try
        {
            token = await tokens.GetAccessTokenAsync(cancellationToken)
                ?? throw new AccountApiException("Not signed in.");
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

        using (response)
        {
            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                throw new AccountApiException("Sign-in expired.");
            }

            await ApiResponses.EnsureSuccessAsync(response, cancellationToken);
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
    }
}
