using System.IO.Compression;
using System.Net;
using System.Text.Json;
using Dialed.Api.Data;
using Dialed.Domain.Catalog;
using Microsoft.Azure.Cosmos;

namespace Dialed.Api.Catalog;

/// <summary>
/// The catalog's own container (#268), apart from user data: the latest build and the owner's rules, never a user id, household id or
/// contributor. Partitioned by <c>/pk</c>: <c>build</c> holds the latest build, <c>rules</c> the hide and merge rules.
/// </summary>
public static class CatalogContainer
{
    public static readonly CosmosContainerSpec Spec = new("catalog", "/pk");
}

/// <summary>
/// The latest catalog build in Cosmos DB. A build can outgrow one document (2 MB), so it is stored gzipped in parts
/// (<c>part-{buildId}-{n}</c>) with a small <c>current</c> document that points at them; a new build writes its parts first and then
/// switches the pointer, so a reader never sees half a build. The older build's parts are deleted after the switch; an instance that was
/// reading them just reads the pointer again. Searches read a copy kept in memory, checked against the pointer at most once a minute (one point
/// read), so every instance shows a new build within a minute and a search costs no request units.
/// </summary>
public sealed class CosmosCatalogStore(DialedCosmos cosmos, TimeProvider time) : ICatalogStore
{
    /// <summary>How long a copy in memory is trusted before the pointer is read again.</summary>
    public static readonly TimeSpan RefreshAfter = TimeSpan.FromMinutes(1);

    private const string Partition = "build";
    private const string CurrentId = "current";

    /// <summary>Base64 characters per part: well under the 2 MB document limit.</summary>
    private const int PartChars = 1_000_000;

    private const int MaxRetries = 5;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly SemaphoreSlim refresh = new(1, 1);
    private Cached? cached;

    public async Task<CatalogBuild> GetAsync(CancellationToken cancellationToken = default)
    {
        var copy = Volatile.Read(ref cached);
        if (copy is not null && time.GetUtcNow() - copy.CheckedAt < RefreshAfter)
        {
            return copy.Build;
        }

        await refresh.WaitAsync(cancellationToken);
        try
        {
            copy = Volatile.Read(ref cached);
            if (copy is not null && time.GetUtcNow() - copy.CheckedAt < RefreshAfter)
            {
                return copy.Build;
            }

            var container = await cosmos.ContainerAsync(CatalogContainer.Spec, cancellationToken);
            var fresh = await LoadAsync(container, copy, cancellationToken);
            Volatile.Write(ref cached, fresh);
            return fresh.Build;
        }
        finally
        {
            refresh.Release();
        }
    }

    public async Task SaveAsync(CatalogBuild build, CancellationToken cancellationToken = default)
    {
        var container = await cosmos.ContainerAsync(CatalogContainer.Spec, cancellationToken);
        var key = new PartitionKey(Partition);
        var buildId = Guid.NewGuid().ToString("N");
        var parts = Split(Compress(build));
        for (var i = 0; i < parts.Count; i++)
        {
            await container.CreateItemAsync(new PartDocument { Id = PartId(buildId, i), BuildId = buildId, Data = parts[i] }, key, cancellationToken: cancellationToken);
        }

        var previous = await ReadAsync<CurrentDocument>(container, CurrentId, cancellationToken);
        var current = new CurrentDocument { Id = CurrentId, BuildId = buildId, Parts = parts.Count, BuiltAt = build.BuiltAt };
        await container.UpsertItemAsync(current, key, cancellationToken: cancellationToken);
        Volatile.Write(ref cached, new Cached(build, buildId, time.GetUtcNow()));

        if (previous.Document is { } old && old.BuildId != buildId)
        {
            for (var i = 0; i < old.Parts; i++)
            {
                await DeleteAsync(container, PartId(old.BuildId, i), cancellationToken);
            }
        }
    }

    private async Task<Cached> LoadAsync(Container container, Cached? copy, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < MaxRetries; attempt++)
        {
            var (current, _) = await ReadAsync<CurrentDocument>(container, CurrentId, cancellationToken);
            if (current is null)
            {
                return new Cached(CatalogBuild.Empty, null, time.GetUtcNow());
            }

            if (copy is not null && copy.BuildId == current.BuildId)
            {
                return copy with { CheckedAt = time.GetUtcNow() };
            }

            List<string> parts = [];
            for (var i = 0; i < current.Parts; i++)
            {
                var (part, _) = await ReadAsync<PartDocument>(container, PartId(current.BuildId, i), cancellationToken);
                if (part is null)
                {
                    break; // a newer build replaced this one while reading: read the pointer again
                }

                parts.Add(part.Data);
            }

            if (parts.Count == current.Parts)
            {
                return new Cached(Decompress(string.Concat(parts)), current.BuildId, time.GetUtcNow());
            }
        }

        throw new InvalidOperationException("The catalog build could not be read: it kept changing.");
    }

    private static string PartId(string buildId, int index) => $"part-{buildId}-{index}";

    private static string Compress(CatalogBuild build)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Optimal))
        {
            JsonSerializer.Serialize(gzip, StoredBuild.From(build), Json);
        }

        return Convert.ToBase64String(output.ToArray());
    }

    private static CatalogBuild Decompress(string data)
    {
        using var input = new GZipStream(new MemoryStream(Convert.FromBase64String(data)), CompressionMode.Decompress);
        return (JsonSerializer.Deserialize<StoredBuild>(input, Json) ?? throw new JsonException("Empty catalog build.")).ToBuild();
    }

    private static List<string> Split(string data)
    {
        List<string> parts = [];
        for (var start = 0; start < data.Length; start += PartChars)
        {
            parts.Add(data.Substring(start, Math.Min(PartChars, data.Length - start)));
        }

        return parts.Count == 0 ? [""] : parts;
    }

    private static async Task DeleteAsync(Container container, string id, CancellationToken cancellationToken)
    {
        try
        {
            await container.DeleteItemAsync<PartDocument>(id, new PartitionKey(Partition), cancellationToken: cancellationToken);
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            // Already gone.
        }
    }

    private static async Task<(T? Document, string? ETag)> ReadAsync<T>(Container container, string id, CancellationToken cancellationToken)
        where T : class
    {
        try
        {
            var response = await container.ReadItemAsync<T>(id, new PartitionKey(Partition), cancellationToken: cancellationToken);
            return (response.Resource, response.ETag);
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return (null, null);
        }
    }

    private sealed record Cached(CatalogBuild Build, string? BuildId, DateTimeOffset CheckedAt);

    /// <summary>The build as stored: the Domain records as they are, with the time and the hidden entries.</summary>
    private sealed record StoredBuild(IReadOnlyList<CatalogRoaster> Roasters, IReadOnlyList<CatalogBean> Beans, DateTimeOffset? BuiltAt, IReadOnlyList<CatalogEntryAdminDto> Hidden)
    {
        public static StoredBuild From(CatalogBuild build) => new(build.Catalog.Roasters, build.Catalog.Beans, build.BuiltAt, build.Hidden);

        public CatalogBuild ToBuild() => new(new BeanCatalog(Roasters ?? [], Beans ?? []), BuiltAt, Hidden ?? []);
    }

    internal sealed record CurrentDocument
    {
        public string Id { get; init; } = "";
        public string Pk { get; init; } = Partition;
        public string BuildId { get; init; } = "";
        public int Parts { get; init; }
        public DateTimeOffset? BuiltAt { get; init; }
    }

    internal sealed record PartDocument
    {
        public string Id { get; init; } = "";
        public string Pk { get; init; } = Partition;
        public string BuildId { get; init; } = "";
        public string Data { get; init; } = "";
    }
}

/// <summary>
/// The owner's hide and merge rules in Cosmos DB: one <c>rules</c> document in the <c>rules</c> partition. Every change reads it with its
/// ETag and writes with <c>IfMatchEtag</c>, so two moderators (or instances) at once never lose a rule.
/// </summary>
public sealed class CosmosCatalogRulesStore(DialedCosmos cosmos) : ICatalogRulesStore
{
    private const string Partition = "rules";
    private const string RulesId = "rules";
    private const int MaxRetries = 5;

    public async Task<CatalogRules> GetAsync(CancellationToken cancellationToken = default)
    {
        var container = await cosmos.ContainerAsync(CatalogContainer.Spec, cancellationToken);
        var (document, _) = await ReadAsync(container, cancellationToken);
        return document is null
            ? CatalogRules.Empty
            : new CatalogRules(new HashSet<string>(document.HiddenKeys, StringComparer.Ordinal), [.. document.Merges.Select(m => (m.A, m.B))]);
    }

    public Task HideAsync(IEnumerable<string> keys, CancellationToken cancellationToken = default)
    {
        var list = keys.ToList();
        return ChangeAsync(rules => rules with { HiddenKeys = [.. rules.HiddenKeys.Union(list, StringComparer.Ordinal).Order(StringComparer.Ordinal)] }, cancellationToken);
    }

    public async Task<bool> UnhideAsync(IEnumerable<string> keys, CancellationToken cancellationToken = default)
    {
        var list = keys.ToHashSet(StringComparer.Ordinal);
        var changed = false;
        await ChangeAsync(
            rules =>
            {
                changed = rules.HiddenKeys.Any(list.Contains);
                return changed ? rules with { HiddenKeys = [.. rules.HiddenKeys.Where(k => !list.Contains(k))] } : null;
            },
            cancellationToken);
        return changed;
    }

    public Task MergeAsync(string keyA, string keyB, CancellationToken cancellationToken = default) =>
        ChangeAsync(rules => rules.Merges.Any(m => m.A == keyA && m.B == keyB) ? null : rules with { Merges = [.. rules.Merges, new MergeRule(keyA, keyB)] }, cancellationToken);

    public async Task<bool> UnmergeAsync(string keyA, string keyB, CancellationToken cancellationToken = default)
    {
        var changed = false;
        await ChangeAsync(
            rules =>
            {
                changed = rules.Merges.Any(m => m.A == keyA && m.B == keyB);
                return changed ? rules with { Merges = [.. rules.Merges.Where(m => !(m.A == keyA && m.B == keyB))] } : null;
            },
            cancellationToken);
        return changed;
    }

    /// <summary>Reads the rules with their ETag, applies <paramref name="change"/> (null: nothing to write) and writes that version only; retries a lost race.</summary>
    private async Task ChangeAsync(Func<RulesDocument, RulesDocument?> change, CancellationToken cancellationToken)
    {
        var container = await cosmos.ContainerAsync(CatalogContainer.Spec, cancellationToken);
        var key = new PartitionKey(Partition);
        for (var attempt = 0; attempt < MaxRetries; attempt++)
        {
            var (current, etag) = await ReadAsync(container, cancellationToken);
            if (change(current ?? new RulesDocument()) is not { } next)
            {
                return;
            }

            try
            {
                if (etag is null)
                {
                    await container.CreateItemAsync(next, key, cancellationToken: cancellationToken);
                }
                else
                {
                    await container.ReplaceItemAsync(next, RulesId, key, new ItemRequestOptions { IfMatchEtag = etag }, cancellationToken);
                }

                return;
            }
            catch (CosmosException ex) when (ex.StatusCode is HttpStatusCode.Conflict or HttpStatusCode.PreconditionFailed or HttpStatusCode.NotFound)
            {
                // Someone else changed the rules at the same time: read again.
            }
        }

        throw new InvalidOperationException("The catalog rules could not be saved: they kept changing.");
    }

    private static async Task<(RulesDocument? Document, string? ETag)> ReadAsync(Container container, CancellationToken cancellationToken)
    {
        try
        {
            var response = await container.ReadItemAsync<RulesDocument>(RulesId, new PartitionKey(Partition), cancellationToken: cancellationToken);
            return (response.Resource, response.ETag);
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return (null, null);
        }
    }

    internal sealed record MergeRule(string A, string B);

    internal sealed record RulesDocument
    {
        public string Id { get; init; } = RulesId;
        public string Pk { get; init; } = Partition;
        public IReadOnlyList<string> HiddenKeys { get; init; } = [];
        public IReadOnlyList<MergeRule> Merges { get; init; } = [];
    }
}
