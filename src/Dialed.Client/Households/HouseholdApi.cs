using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Dialed.Client.Account;
using Dialed.Client.Sync;

namespace Dialed.Client.Households;

public sealed record HouseholdMember(string UserId, string DisplayName, bool IsOwner);

public sealed record HouseholdInfo(string Id, string Name, IReadOnlyList<HouseholdMember> Members, bool YouAreOwner);

/// <summary>A household the person belongs to, for the picker.</summary>
public sealed record HouseholdSummary(string Id, string Name, bool IsPersonal, bool IsActive, bool IsOwner, int MemberCount);

/// <param name="MaxUses">How many people may join with this code.</param>
/// <param name="Uses">How many already did.</param>
public sealed record InviteInfo(string Code, DateTimeOffset ExpiresAt, int MaxUses = 1, int Uses = 0);

/// <summary>The API's household endpoints (see Dialed.Api): view, create, rename, delete, invite, join, leave and remove.</summary>
public sealed class HouseholdApi(HttpClient http, IAccessTokenProvider tokens)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public Task<HouseholdInfo> GetMineAsync(CancellationToken cancellationToken = default) =>
        SendAsync<HouseholdInfo>(HttpMethod.Get, "households/mine", null, cancellationToken);

    public Task<IReadOnlyList<HouseholdSummary>> ListAsync(CancellationToken cancellationToken = default) =>
        SendAsync<IReadOnlyList<HouseholdSummary>>(HttpMethod.Get, "households", null, cancellationToken);

    public Task<HouseholdInfo> SwitchAsync(string householdId, CancellationToken cancellationToken = default) =>
        SendAsync<HouseholdInfo>(HttpMethod.Post, "households/mine/switch", new { householdId }, cancellationToken);

    /// <summary>A new household owned by the person, which becomes the active one.</summary>
    public Task<HouseholdInfo> CreateAsync(string name, CancellationToken cancellationToken = default) =>
        SendAsync<HouseholdInfo>(HttpMethod.Post, "households", new { name }, cancellationToken);

    /// <summary>The owner renames the active household.</summary>
    public Task<HouseholdInfo> RenameAsync(string name, CancellationToken cancellationToken = default) =>
        SendAsync<HouseholdInfo>(HttpMethod.Put, "households/mine/name", new { name }, cancellationToken);

    /// <summary>The owner deletes the active household and its data; returns the household that is active now.</summary>
    public Task<HouseholdInfo> DeleteAsync(CancellationToken cancellationToken = default) =>
        SendAsync<HouseholdInfo>(HttpMethod.Delete, "households/mine", null, cancellationToken);

    /// <param name="maxUses">How many people may join with the code (1 to 10).</param>
    /// <param name="daysValid">1 to 30; the server's default (7) when <c>null</c>.</param>
    public Task<InviteInfo> CreateInviteAsync(int maxUses = 1, int? daysValid = null, CancellationToken cancellationToken = default) =>
        SendAsync<InviteInfo>(HttpMethod.Post, "households/mine/invites", new { maxUses, daysValid }, cancellationToken);

    /// <summary>The owner's open invites, soonest to expire first.</summary>
    public Task<IReadOnlyList<InviteInfo>> ListInvitesAsync(CancellationToken cancellationToken = default) =>
        SendAsync<IReadOnlyList<InviteInfo>>(HttpMethod.Get, "households/mine/invites", null, cancellationToken);

    public async Task RevokeInviteAsync(string code, CancellationToken cancellationToken = default) =>
        await SendNoContentAsync(HttpMethod.Delete, $"households/mine/invites/{Uri.EscapeDataString(code)}", cancellationToken);

    public Task<HouseholdInfo> JoinAsync(string code, CancellationToken cancellationToken = default) =>
        SendAsync<HouseholdInfo>(HttpMethod.Post, "households/mine/join", new { code }, cancellationToken);

    public Task<HouseholdInfo> LeaveAsync(CancellationToken cancellationToken = default) =>
        SendAsync<HouseholdInfo>(HttpMethod.Post, "households/mine/leave", null, cancellationToken);

    public Task<HouseholdInfo> TransferOwnershipAsync(string userId, CancellationToken cancellationToken = default) =>
        SendAsync<HouseholdInfo>(HttpMethod.Post, "households/mine/transfer", new { userId }, cancellationToken);

    public Task<HouseholdInfo> RemoveMemberAsync(string userId, CancellationToken cancellationToken = default) =>
        SendAsync<HouseholdInfo>(HttpMethod.Delete, $"households/mine/members/{Uri.EscapeDataString(userId)}", null, cancellationToken);

    private async Task SendNoContentAsync(HttpMethod method, string path, CancellationToken cancellationToken)
    {
        // The answer has no body: reuse the checks of SendAsync by asking for JSON of "nothing" only on success.
        _ = await SendAsync<JsonElement?>(method, path, null, cancellationToken, allowEmpty: true);
    }

    private async Task<T> SendAsync<T>(HttpMethod method, string path, object? body, CancellationToken cancellationToken, bool allowEmpty = false)
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
            if (allowEmpty && response.StatusCode == HttpStatusCode.NoContent)
            {
                return default!;
            }

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
