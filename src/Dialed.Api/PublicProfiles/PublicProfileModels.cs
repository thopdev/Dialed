namespace Dialed.Api.PublicProfiles;

/// <summary>A person's public profile (#106): off until they switch it on; <see cref="Suspended"/> is set by the owner of the service.</summary>
public sealed record PublicProfile(string UserId, bool Enabled = false, bool Suspended = false)
{
    /// <summary>Whether strangers may see the profile and its public items.</summary>
    public bool IsOpen => Enabled && !Suspended;
}

public enum ReportReason
{
    Spam,
    Offensive,
    Wrong,
    Other,
}

/// <summary>What was reported: a profile, or one of its beans or recipes.</summary>
public enum ReportTarget
{
    Profile,
    Bean,
    Recipe,
}

public sealed record Report(
    string Id,
    string ReporterId,
    string OwnerId,
    ReportTarget Target,
    string? ItemId,
    ReportReason Reason,
    string? Note,
    DateTimeOffset CreatedAt);

/// <summary>Public profiles, suspensions and reports. Server-only data: none of it syncs to devices. Cosmos DB takes over with the other stores.</summary>
public interface IPublicProfileStore
{
    Task<PublicProfile> GetAsync(string userId, CancellationToken cancellationToken = default);

    Task SaveAsync(PublicProfile profile, CancellationToken cancellationToken = default);

    /// <summary>Everyone whose profile is on and not suspended (the index people are found in).</summary>
    Task<IReadOnlyList<string>> OpenUserIdsAsync(CancellationToken cancellationToken = default);

    /// <summary>Everyone who is suspended now (for the owner's moderation screen).</summary>
    Task<IReadOnlyList<string>> SuspendedUserIdsAsync(CancellationToken cancellationToken = default);

    /// <summary>Saves the report unless this reporter already reported this target. <c>false</c> for a duplicate.</summary>
    Task<bool> AddReportAsync(Report report, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<Report>> ReportsAsync(CancellationToken cancellationToken = default);

    /// <summary>Removes the profile and every report by or about the person (account deletion).</summary>
    Task RemoveUserAsync(string userId, CancellationToken cancellationToken = default);
}

public sealed class InMemoryPublicProfileStore : IPublicProfileStore
{
    private readonly Lock gate = new();
    private readonly Dictionary<string, PublicProfile> profiles = new(StringComparer.Ordinal);
    private readonly List<Report> reports = [];

    public Task<PublicProfile> GetAsync(string userId, CancellationToken cancellationToken = default)
    {
        lock (gate)
        {
            return Task.FromResult(profiles.GetValueOrDefault(userId) ?? new PublicProfile(userId));
        }
    }

    public Task SaveAsync(PublicProfile profile, CancellationToken cancellationToken = default)
    {
        lock (gate)
        {
            profiles[profile.UserId] = profile;
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<string>> OpenUserIdsAsync(CancellationToken cancellationToken = default)
    {
        lock (gate)
        {
            return Task.FromResult<IReadOnlyList<string>>([.. profiles.Values.Where(p => p.IsOpen).Select(p => p.UserId)]);
        }
    }

    public Task<IReadOnlyList<string>> SuspendedUserIdsAsync(CancellationToken cancellationToken = default)
    {
        lock (gate)
        {
            return Task.FromResult<IReadOnlyList<string>>([.. profiles.Values.Where(p => p.Suspended).Select(p => p.UserId).Order(StringComparer.Ordinal)]);
        }
    }

    public Task<bool> AddReportAsync(Report report, CancellationToken cancellationToken = default)
    {
        lock (gate)
        {
            if (reports.Any(r => r.ReporterId == report.ReporterId && r.OwnerId == report.OwnerId && r.Target == report.Target && r.ItemId == report.ItemId))
            {
                return Task.FromResult(false);
            }

            reports.Add(report);
            return Task.FromResult(true);
        }
    }

    public Task<IReadOnlyList<Report>> ReportsAsync(CancellationToken cancellationToken = default)
    {
        lock (gate)
        {
            return Task.FromResult<IReadOnlyList<Report>>([.. reports.OrderByDescending(r => r.CreatedAt)]);
        }
    }

    public Task RemoveUserAsync(string userId, CancellationToken cancellationToken = default)
    {
        lock (gate)
        {
            profiles.Remove(userId);
            reports.RemoveAll(r => r.ReporterId == userId || r.OwnerId == userId);
        }

        return Task.CompletedTask;
    }
}
