using System.Text.Json;
using Dialed.Api.Auth;
using Dialed.Api.PublicProfiles;
using Dialed.Api.Sync;
using Dialed.Domain.Catalog;
using Dialed.Domain.Model;
using Dialed.Domain.Text;
using Microsoft.Extensions.Options;

namespace Dialed.Api.Catalog;

/// <summary>Configuration section <c>Catalog</c> (#105).</summary>
public sealed class CatalogOptions
{
    public const string Section = "Catalog";

    /// <summary>
    /// How many different people must have made the same bean or roaster public before it is in the catalog. Never below 2 (see
    /// <see cref="CatalogBuilder.MinimumPeople"/>): with 2, an entry cannot be traced to one person's item.
    /// </summary>
    public int MinContributors { get; set; } = CatalogBuilder.MinimumPeople;

    /// <summary>Hours between rebuilds by the hosted job.</summary>
    public int RebuildHours { get; set; } = 6;
}

public sealed record CatalogRoasterDto(string Id, string Name, string? Country, string? City, string? Website, int People);

public sealed record CatalogBeanDto(
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
    int People,
    IReadOnlyList<string> Barcodes);

/// <summary>What the owner of the service sees when moderating: includes the keys the rules are made of, still no contributors.</summary>
public sealed record CatalogEntryAdminDto(string Kind, string Id, string Name, int People, IReadOnlyList<string> Keys);

public sealed record CatalogMergeRequest(string FromId, string IntoId);

public sealed record CatalogUnmergeRequest(string KeyA, string KeyB);

/// <summary>The owner's rules as they stand, for the moderation tools.</summary>
public sealed record CatalogRulesDto(IReadOnlyList<string> HiddenKeys, IReadOnlyList<CatalogUnmergeRequest> Merges);

/// <summary>The newest build, kept apart from user data: no user ids, household ids or links back to contributors.</summary>
public interface ICatalogStore
{
    BeanCatalog Current { get; }

    DateTimeOffset? BuiltAt { get; }

    /// <summary>Entries the owner hid, as the last build found them, so they can be looked at and brought back.</summary>
    IReadOnlyList<CatalogEntryAdminDto> Hidden { get; }

    void Save(BeanCatalog catalog, DateTimeOffset builtAt, IReadOnlyList<CatalogEntryAdminDto> hidden);
}

public sealed class InMemoryCatalogStore : ICatalogStore
{
    private readonly Lock gate = new();
    private BeanCatalog current = BeanCatalog.Empty;
    private DateTimeOffset? builtAt;
    private IReadOnlyList<CatalogEntryAdminDto> hidden = [];

    public IReadOnlyList<CatalogEntryAdminDto> Hidden
    {
        get
        {
            lock (gate)
            {
                return hidden;
            }
        }
    }

    public BeanCatalog Current
    {
        get
        {
            lock (gate)
            {
                return current;
            }
        }
    }

    public DateTimeOffset? BuiltAt
    {
        get
        {
            lock (gate)
            {
                return builtAt;
            }
        }
    }

    public void Save(BeanCatalog catalog, DateTimeOffset at, IReadOnlyList<CatalogEntryAdminDto> hiddenEntries)
    {
        lock (gate)
        {
            (current, builtAt, hidden) = (catalog, at, hiddenEntries);
        }
    }
}

/// <summary>The owner's hand-made rules (hide, merge), kept as keys so they survive rebuilds. In memory now; Cosmos DB with the other stores.</summary>
public interface ICatalogRulesStore
{
    CatalogRules Get();

    void Hide(IEnumerable<string> keys);

    void Merge(string keyA, string keyB);

    /// <summary>Takes the keys off the hidden list. <c>false</c> when none of them was hidden.</summary>
    bool Unhide(IEnumerable<string> keys);

    /// <summary>Removes one merge rule. <c>false</c> when there was none.</summary>
    bool Unmerge(string keyA, string keyB);
}

public sealed class InMemoryCatalogRulesStore : ICatalogRulesStore
{
    private readonly Lock gate = new();
    private readonly HashSet<string> hidden = new(StringComparer.Ordinal);
    private readonly List<(string A, string B)> merges = [];

    public CatalogRules Get()
    {
        lock (gate)
        {
            return new CatalogRules(new HashSet<string>(hidden, StringComparer.Ordinal), [.. merges]);
        }
    }

    public void Hide(IEnumerable<string> keys)
    {
        lock (gate)
        {
            hidden.UnionWith(keys);
        }
    }

    public bool Unhide(IEnumerable<string> keys)
    {
        lock (gate)
        {
            return keys.Aggregate(false, (any, key) => hidden.Remove(key) || any);
        }
    }

    public bool Unmerge(string keyA, string keyB)
    {
        lock (gate)
        {
            return merges.Remove((keyA, keyB));
        }
    }

    public void Merge(string keyA, string keyB)
    {
        lock (gate)
        {
            if (!merges.Contains((keyA, keyB)))
            {
                merges.Add((keyA, keyB));
            }
        }
    }
}

/// <summary>
/// Reads what people chose to share publicly, for the build (#105): beans and vendors with visibility Public, owned by a person whose public
/// profile is on and not suspended and who still exists. Un-sharing, switching the profile off, suspension and deletion therefore all drop
/// out on the next build.
/// </summary>
public sealed class CatalogSource(ISyncStore sync, IPublicProfileStore profiles, IUserDirectory users)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<(IReadOnlyList<CatalogBeanInput> Beans, IReadOnlyList<CatalogRoasterInput> Roasters)> ReadAsync(CancellationToken ct = default)
    {
        var eligible = new Dictionary<string, bool>(StringComparer.Ordinal);

        async Task<bool> Eligible(string? author)
        {
            if (string.IsNullOrEmpty(author))
            {
                return false;
            }

            if (!eligible.TryGetValue(author, out var ok))
            {
                ok = (await profiles.GetAsync(author, ct)).IsOpen && await users.FindByIdAsync(author, ct) is not null;
                eligible[author] = ok;
            }

            return ok;
        }

        List<CatalogBeanInput> beans = [];
        List<CatalogRoasterInput> roasters = [];

        foreach (var row in await sync.ListPublicAsync("vendors", ct))
        {
            if (Read<Vendor>(row) is { } vendor && vendor.Kinds.HasFlag(VendorKinds.Roaster) && await Eligible(Author(row)))
            {
                roasters.Add(new CatalogRoasterInput(Author(row)!, vendor.Name, vendor.Country, vendor.City, vendor.Website));
            }
        }

        foreach (var row in await sync.ListPublicAsync("beans", ct))
        {
            if (Read<Bean>(row) is not { } bean || !await Eligible(Author(row)))
            {
                continue;
            }

            // The roaster a public bean names is part of what was shared with it.
            Vendor? vendor = null;
            if (bean.RoasterId is { Length: > 0 } roasterId && await sync.GetAsync(row.HouseholdId, "vendors", roasterId, ct) is { } vendorRow && Read<Vendor>(vendorRow) is { Deleted: false } found)
            {
                vendor = found;
                roasters.Add(new CatalogRoasterInput(Author(row)!, found.Name, found.Country, found.City, found.Website));
            }

            beans.Add(new CatalogBeanInput(
                Author(row)!,
                bean.Name,
                vendor?.Name,
                bean.Origin.Country,
                bean.Origin.Region,
                bean.Process,
                bean.Varieties,
                bean.RoastLevel,
                bean.TastingNotes,
                bean.Barcodes));
        }

        return (beans, roasters);
    }

    private static string? Author(SyncRow row) => row.Entity["createdBy"]?.GetValue<string>();

    private static T? Read<T>(SyncRow row)
        where T : class
    {
        try
        {
            return row.Entity.Deserialize<T>(Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>Builds the catalog and answers searches over the latest build (#105).</summary>
public sealed class CatalogService(CatalogSource source, ICatalogStore store, ICatalogRulesStore rules, IOptions<CatalogOptions> options, TimeProvider time)
{
    public const int MaxResults = 20;
    public const int MinQueryLength = 2;

    public async Task RebuildAsync(CancellationToken ct = default)
    {
        var (beans, roasters) = await source.ReadAsync(ct);
        var current = rules.Get();
        var visible = CatalogBuilder.Build(beans, roasters, options.Value.MinContributors, current);

        // The same build without the hide rules shows what was hidden, so it can be looked at and brought back (merges still apply).
        IReadOnlyList<CatalogEntryAdminDto> hidden = [];
        if (current.HiddenKeys.Count > 0)
        {
            var full = CatalogBuilder.Build(beans, roasters, options.Value.MinContributors, current with { HiddenKeys = new HashSet<string>() });
            hidden =
            [
                .. full.Roasters.Where(r => r.Keys.Any(current.HiddenKeys.Contains)).Select(r => new CatalogEntryAdminDto("roaster", r.Id, r.Name, r.People, r.Keys))
                    .Concat(full.Beans.Where(b => b.Keys.Any(current.HiddenKeys.Contains)).Select(b => new CatalogEntryAdminDto("bean", b.Id, b.Name, b.People, b.Keys)))
                    .OrderBy(e => e.Kind, StringComparer.Ordinal).ThenBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase),
            ];
        }

        store.Save(visible, time.GetUtcNow(), hidden);
    }

    public IReadOnlyList<CatalogRoasterDto> SearchRoasters(string? query)
    {
        if (query is null || query.Trim().Length < MinQueryLength)
        {
            return [];
        }

        var found = FuzzyMatch.Rank(store.Current.Roasters, query.Trim(), r => r.Name)
            .Select(r => (Entry: r, Score: FuzzyMatch.Score(query, r.Name)));
        return [.. found.OrderByDescending(x => x.Score).ThenByDescending(x => x.Entry.People).ThenBy(x => x.Entry.Name, StringComparer.CurrentCultureIgnoreCase)
            .Take(MaxResults).Select(x => new CatalogRoasterDto(x.Entry.Id, x.Entry.Name, x.Entry.Country, x.Entry.City, x.Entry.Website, x.Entry.People))];
    }

    /// <summary>Beans by name, optionally narrowed to a roaster. A roaster alone lists its beans, most popular first.</summary>
    public IReadOnlyList<CatalogBeanDto> SearchBeans(string? query, string? roaster)
    {
        var hasQuery = query is not null && query.Trim().Length >= MinQueryLength;
        var hasRoaster = !string.IsNullOrWhiteSpace(LabelMatcher.RoasterKey(roaster));
        if (!hasQuery && !hasRoaster)
        {
            return [];
        }

        var beans = store.Current.Beans.AsEnumerable();
        if (hasRoaster)
        {
            beans = beans.Where(b => LabelMatcher.RoasterScore(roaster, b.RoasterName) >= LabelMatcher.MinCandidate);
        }

        var scored = beans
            .Select(b => (Entry: b, Score: hasQuery ? LabelMatcher.Similarity(query, b.Name) : 1d))
            .Where(x => x.Score >= (hasQuery ? LabelMatcher.MinCandidate : 0d));
        return [.. scored.OrderByDescending(x => x.Score).ThenByDescending(x => x.Entry.People).ThenBy(x => x.Entry.Name, StringComparer.CurrentCultureIgnoreCase)
            .Take(MaxResults).Select(x => ToDto(x.Entry))];
    }

    /// <summary>Beans that carry this barcode (any spelling of it: UPC-A or EAN-13), most popular first.</summary>
    public IReadOnlyList<CatalogBeanDto> FindByBarcode(string? code)
    {
        if (Dialed.Domain.Barcodes.BarcodeMatcher.Normalize(code) is not { } normalized)
        {
            return [];
        }

        return [.. store.Current.Beans
            .Where(b => (b.Barcodes ?? []).Any(c => Dialed.Domain.Barcodes.BarcodeMatcher.Normalize(c) == normalized))
            .OrderByDescending(b => b.People).ThenBy(b => b.Name, StringComparer.CurrentCultureIgnoreCase)
            .Take(MaxResults).Select(ToDto)];
    }

    private static CatalogBeanDto ToDto(CatalogBean b) =>
        new(b.Id, b.Name, b.RoasterId, b.RoasterName, b.Country, b.Region, b.Process, b.Varieties, b.RoastLevel, b.TastingNotes, b.People, b.Barcodes ?? []);

    // ---- Moderation (the owner of the service, with the admin key) ----------------------------

    /// <summary>Every entry, fewest contributors first (the ones most worth a look).</summary>
    public IReadOnlyList<CatalogEntryAdminDto> Entries(int take = 100) =>
    [
        .. store.Current.Roasters.Select(r => new CatalogEntryAdminDto("roaster", r.Id, r.Name, r.People, r.Keys))
            .Concat(store.Current.Beans.Select(b => new CatalogEntryAdminDto("bean", b.Id, b.Name, b.People, b.Keys)))
            .OrderBy(e => e.People).ThenBy(e => e.Kind, StringComparer.Ordinal).ThenBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase)
            .Take(Math.Clamp(take, 1, 1000)),
    ];

    private CatalogEntryAdminDto? Find(string id) => Entries(1000).FirstOrDefault(e => e.Id == id);

    /// <summary>Hides an entry. The rule is kept as the entry's keys, so it holds after a rebuild; the current catalog is rebuilt at once.</summary>
    public async Task<bool> HideAsync(string id, CancellationToken ct = default)
    {
        if (Find(id) is not { } entry)
        {
            return false;
        }

        rules.Hide(entry.Keys);
        await RebuildAsync(ct);
        return true;
    }

    /// <summary>The entries that are hidden now (with their keys), most recently built state.</summary>
    public IReadOnlyList<CatalogEntryAdminDto> HiddenEntries() => store.Hidden;

    /// <summary>Brings a hidden entry back (it shows again after the rebuild this triggers, if it still has enough people).</summary>
    public async Task<bool> UnhideAsync(string id, CancellationToken ct = default)
    {
        if (store.Hidden.FirstOrDefault(e => e.Id == id) is not { } entry || !rules.Unhide(entry.Keys))
        {
            return false;
        }

        await RebuildAsync(ct);
        return true;
    }

    public CatalogRulesDto Rules()
    {
        var current = rules.Get();
        return new CatalogRulesDto([.. current.HiddenKeys.Order(StringComparer.Ordinal)], [.. current.Merges.Select(m => new CatalogUnmergeRequest(m.A, m.B))]);
    }

    /// <summary>Takes a merge back; the two entries separate again at the rebuild this triggers.</summary>
    public async Task<bool> UnmergeAsync(string keyA, string keyB, CancellationToken ct = default)
    {
        if (!rules.Unmerge(keyA, keyB))
        {
            return false;
        }

        await RebuildAsync(ct);
        return true;
    }

    /// <summary>Merges two entries of the same kind into one.</summary>
    public async Task<bool> MergeAsync(string fromId, string intoId, CancellationToken ct = default)
    {
        if (Find(fromId) is not { } from || Find(intoId) is not { } into || from.Kind != into.Kind || from.Id == into.Id)
        {
            return false;
        }

        rules.Merge(from.Keys[0], into.Keys[0]);
        await RebuildAsync(ct);
        return true;
    }
}

/// <summary>Rebuilds the catalog now and then. A hosted service now; in Azure a timer-triggered Function does it (filed with #260).</summary>
public sealed class CatalogRebuildWorker(CatalogService catalog, IOptions<CatalogOptions> options, TimeProvider time, ILogger<CatalogRebuildWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromHours(Math.Max(1, options.Value.RebuildHours)), time);
        do
        {
            try
            {
                await catalog.RebuildAsync(stoppingToken);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                log.LogWarning(e, "The catalog build failed; it tries again next time.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
