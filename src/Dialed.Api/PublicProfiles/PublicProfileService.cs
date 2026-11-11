using Dialed.Api.Auth;
using Dialed.Api.Friends;
using Dialed.Api.Passkeys;
using Dialed.Domain.Text;
using Microsoft.Extensions.Options;

namespace Dialed.Api.PublicProfiles;

/// <summary>Configuration section <c>Reports</c>: where reports are mailed (the outbox stand-in until email is connected, #199).</summary>
public sealed class ReportOptions
{
    public const string Section = "Reports";

    public string? OwnerEmail { get; set; }
}

public sealed record PublicProfileDto(bool Enabled);

public sealed record ReportRequest(string OwnerId, string Target, string? ItemId, string Reason, string? Note);

public sealed record ReportDto(string Id, string ReporterId, string OwnerId, string OwnerName, string Target, string? ItemId, string Reason, string? Note, DateTimeOffset CreatedAt);

public sealed record SuspendedDto(string UserId, string DisplayName);

public enum PublicOutcome { Ok, NotFound, Conflict, Invalid }

public sealed record PublicResult<T>(PublicOutcome Outcome, T? Value = default, string? Message = null);

/// <summary>
/// Opt-in public profiles (#106): the switch, finding people, reports and moderation. A profile is only found, shown or searched while
/// it is switched on and not suspended; blocked people never see each other.
/// </summary>
public sealed class PublicProfileService(IUserDirectory users, IPublicProfileStore store, IFriendStore friends, IEmailSender email, IOptions<ReportOptions> options, TimeProvider time, ILogger<PublicProfileService> log)
{
    public const int MinQueryLength = 2;
    public const int MaxResults = 20;
    public const int MaxNoteLength = 500;

    public async Task<PublicProfileDto> GetAsync(string userId, CancellationToken ct = default) =>
        new((await store.GetAsync(userId, ct)).Enabled);

    public async Task<PublicProfileDto> SetEnabledAsync(string userId, bool enabled, CancellationToken ct = default)
    {
        var current = await store.GetAsync(userId, ct);
        await store.SaveAsync(current with { Enabled = enabled }, ct);
        return new(enabled);
    }

    /// <summary>Whether strangers may see this person's profile and public items right now.</summary>
    public async Task<bool> IsOpenAsync(string userId, CancellationToken ct = default) => (await store.GetAsync(userId, ct)).IsOpen;

    public async Task<bool> IsSuspendedAsync(string userId, CancellationToken ct = default) => (await store.GetAsync(userId, ct)).Suspended;

    /// <summary>
    /// Public profiles whose name matches, best first. At least two characters, at most 20 results; the searcher, blocked people (either
    /// way), suspended people and profiles that are switched off never appear.
    /// </summary>
    public async Task<IReadOnlyList<PersonDto>> SearchAsync(string viewerId, string? query, CancellationToken ct = default)
    {
        if (query is null || query.Trim().Length < MinQueryLength)
        {
            return [];
        }

        List<AppUser> candidates = [];
        foreach (var id in await store.OpenUserIdsAsync(ct))
        {
            if (id == viewerId
                || await users.FindByIdAsync(id, ct) is not { } user
                || await friends.IsBlockedAsync(id, viewerId, ct) || await friends.IsBlockedAsync(viewerId, id, ct))
            {
                continue;
            }

            candidates.Add(user);
        }

        return [.. FuzzyMatch.Rank(candidates, query.Trim(), u => u.DisplayName).Take(MaxResults).Select(u => new PersonDto(u.Id, u.DisplayName))];
    }

    /// <summary>Stores a report and passes it on to the owner of the service by email. The reporter never hears the outcome.</summary>
    public async Task<PublicResult<bool>> ReportAsync(string reporterId, ReportRequest request, CancellationToken ct = default)
    {
        if (!Enum.TryParse<ReportTarget>(request.Target, ignoreCase: true, out var target)
            || !Enum.TryParse<ReportReason>(request.Reason, ignoreCase: true, out var reason)
            || (target != ReportTarget.Profile && string.IsNullOrWhiteSpace(request.ItemId))
            || request.Note is { Length: > MaxNoteLength })
        {
            return new(PublicOutcome.Invalid, Message: "That report is not complete.");
        }

        // Anyone who may not see the profile cannot report it, and learns nothing about it.
        if (request.OwnerId == reporterId
            || await users.FindByIdAsync(request.OwnerId, ct) is not { } owner
            || await friends.IsBlockedAsync(owner.Id, reporterId, ct) || await friends.IsBlockedAsync(reporterId, owner.Id, ct))
        {
            return new(PublicOutcome.NotFound);
        }

        var report = new Report(Guid.NewGuid().ToString("N"), reporterId, owner.Id, target, target == ReportTarget.Profile ? null : request.ItemId, reason, request.Note?.Trim(), time.GetUtcNow());
        if (!await store.AddReportAsync(report, ct))
        {
            // Already reported: the reporter sees the same confirmation, and the owner is not mailed twice.
            return new(PublicOutcome.Ok, true);
        }

        if (string.IsNullOrWhiteSpace(options.Value.OwnerEmail))
        {
            return new(PublicOutcome.Ok, true); // no address configured: the report is still stored and listed for the admin
        }

        try
        {
            await email.SendAsync(
                options.Value.OwnerEmail,
                $"Dialed report: {reason} ({target})",
                $"Report {report.Id}\nReported person: {owner.DisplayName} ({owner.Id})\nWhat: {target}{(report.ItemId is null ? "" : $" {report.ItemId}")}\nReason: {reason}\nNote: {report.Note ?? "-"}\nAt: {report.CreatedAt:u}",
                ct);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // The report is stored and listed for the admin; a mail problem is not the reporter's.
            log.LogWarning(e, "A report could not be mailed.");
        }

        return new(PublicOutcome.Ok, true);
    }

    /// <summary>Hides the profile and every public item from everyone else, without deleting anything.</summary>
    public async Task<bool> SetSuspendedAsync(string userId, bool suspended, CancellationToken ct = default)
    {
        if (await users.FindByIdAsync(userId, ct) is null)
        {
            return false;
        }

        await store.SaveAsync((await store.GetAsync(userId, ct)) with { Suspended = suspended }, ct);
        return true;
    }

    /// <summary>Who is suspended now, by name.</summary>
    public async Task<IReadOnlyList<SuspendedDto>> SuspendedAsync(CancellationToken ct = default)
    {
        List<SuspendedDto> list = [];
        foreach (var id in await store.SuspendedUserIdsAsync(ct))
        {
            list.Add(new SuspendedDto(id, (await users.FindByIdAsync(id, ct))?.DisplayName ?? ""));
        }

        return list;
    }

    public async Task<IReadOnlyList<ReportDto>> ReportsAsync(CancellationToken ct = default)
    {
        List<ReportDto> list = [];
        foreach (var r in await store.ReportsAsync(ct))
        {
            var name = (await users.FindByIdAsync(r.OwnerId, ct))?.DisplayName ?? "";
            list.Add(new ReportDto(r.Id, r.ReporterId, r.OwnerId, name, r.Target.ToString().ToLowerInvariant(), r.ItemId, r.Reason.ToString().ToLowerInvariant(), r.Note, r.CreatedAt));
        }

        return list;
    }
}
