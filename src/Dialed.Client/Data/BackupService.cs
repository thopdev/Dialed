using System.Text.Json;
using System.Text.Json.Nodes;
using Dialed.Client.Data;
using Dialed.Client.Platform;
using Dialed.Domain.Model;

namespace Dialed.Client.Backup;

/// <summary>What a backup file would add: how many records per kind are new, how many are already here, and how many could not be read.</summary>
public sealed record RestorePlan(IReadOnlyDictionary<string, int> New, int AlreadyThere, int Unreadable, IReadOnlyList<SyncEntity> Records)
{
    public int Total => New.Values.Sum();
}

/// <summary>What was restored.</summary>
public sealed record RestoreResult(int Restored, int Skipped);

/// <summary>
/// A backup of everything you made, kept as one file on your own device, and restoring it into this or another device. It works offline: the file
/// is built from what the device holds, not from the server. A restore only adds what is missing (matched by id) and never changes or removes
/// anything. Beans, bags, sellers, recipes, gear and brews are included; photos are not (their files live elsewhere), so restored records come
/// back without photos. The server's own export file can be restored too.
/// </summary>
public sealed class BackupService(
    IRepository<Bean> beans,
    IRepository<Bag> bags,
    IRepository<Vendor> vendors,
    IRepository<Recipe> recipes,
    IRepository<EquipmentItem> gear,
    IRepository<Brew> brews,
    IFileExport files,
    TimeProvider time)
{
    public const string Format = "dialed-backup";

    public const int FormatVersion = 1;

    /// <summary>The kinds of record a backup holds, in the order they are restored (what others point to comes first).</summary>
    public static IReadOnlyList<string> Tables { get; } = ["vendors", "beans", "bags", "equipment", "recipes", "brews"];

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    /// <summary>Builds the backup file as bytes.</summary>
    public async Task<byte[]> BuildAsync(CancellationToken cancellationToken = default)
    {
        var tables = new JsonObject
        {
            ["vendors"] = Array(await vendors.ListAsync(cancellationToken)),
            ["beans"] = Array(await beans.ListAsync(cancellationToken)),
            ["bags"] = Array(await bags.ListAsync(cancellationToken)),
            ["equipment"] = Array(await gear.ListAsync(cancellationToken)),
            ["recipes"] = Array(await recipes.ListAsync(cancellationToken)),
            ["brews"] = Array(await brews.ListAsync(cancellationToken)),
        };
        var root = new JsonObject
        {
            ["format"] = Format,
            ["version"] = FormatVersion,
            ["exportedAt"] = time.GetUtcNow(),
            ["tables"] = tables,
        };
        return JsonSerializer.SerializeToUtf8Bytes(root, Json);
    }

    /// <summary>Saves the backup through the platform (share sheet or download). <c>false</c> when the person dismissed it.</summary>
    public async Task<bool> ExportAsync(CancellationToken cancellationToken = default) =>
        await files.SaveAsync($"dialed-backup-{time.GetUtcNow():yyyy-MM-dd}.json", "application/json", await BuildAsync(cancellationToken));

    /// <summary>Reads a backup (or the server's export) and works out what would be added. <c>null</c> when the file is not one of ours.</summary>
    public async Task<RestorePlan?> PlanAsync(byte[] file, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(file);
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(file);
        }
        catch (JsonException)
        {
            return null;
        }

        var source = root?["tables"] as JsonObject ?? (root?["items"] as JsonObject);
        var recognised = (root?["format"]?.GetValue<string>() == Format && root["tables"] is not null) || root?["items"] is not null;
        if (source is null || !recognised)
        {
            return null;
        }

        var newCounts = new Dictionary<string, int>();
        var records = new List<SyncEntity>();
        var existing = 0;
        var unreadable = 0;
        foreach (var table in Tables)
        {
            if (source[table] is not JsonArray rows)
            {
                continue;
            }

            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var row in rows)
            {
                var record = Read(table, row, ref unreadable);
                if (record is null || record.Deleted)
                {
                    continue;
                }

                if (!seen.Add(record.Id) || await ExistsAsync(table, record.Id, cancellationToken))
                {
                    existing++;
                    continue;
                }

                records.Add(record);
                newCounts[table] = newCounts.GetValueOrDefault(table) + 1;
            }
        }

        return new RestorePlan(newCounts, existing, unreadable, records);
    }

    /// <summary>Adds the planned records. Anything that turns out to exist already is skipped.</summary>
    public async Task<RestoreResult> RestoreAsync(RestorePlan plan, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var restored = 0;
        var skipped = 0;
        foreach (var table in Tables)
        {
            foreach (var record in plan.Records.Where(r => TableOf(r) == table))
            {
                try
                {
                    await SaveAsync(record, cancellationToken);
                    restored++;
                }
                catch (ConflictException)
                {
                    skipped++;
                }
            }
        }

        return new RestoreResult(restored, skipped);
    }

    private static JsonArray Array<T>(IEnumerable<T> items) =>
        [.. items.Select(i => JsonSerializer.SerializeToNode(i, Json))];

    private static SyncEntity? Read(string table, JsonNode? row, ref int unreadable)
    {
        if (row is null)
        {
            return null;
        }

        try
        {
            SyncEntity? record = table switch
            {
                "vendors" => row.Deserialize<Vendor>(Json),
                "beans" => row.Deserialize<Bean>(Json) is { } bean ? bean with { PhotoIds = [] } : null,
                "bags" => row.Deserialize<Bag>(Json),
                "equipment" => row.Deserialize<EquipmentItem>(Json) is { } item ? item with { PhotoIds = [] } : null,
                "recipes" => row.Deserialize<Recipe>(Json),
                "brews" => row.Deserialize<Brew>(Json) is { } brew ? brew with { PhotoIds = [] } : null,
                _ => null,
            };
            if (record is null || string.IsNullOrWhiteSpace(record.Id))
            {
                unreadable++;
                return null;
            }

            return record;
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or NotSupportedException)
        {
            unreadable++;
            return null;
        }
    }

    private static string TableOf(SyncEntity record) => record switch
    {
        Vendor => "vendors",
        Bean => "beans",
        Bag => "bags",
        EquipmentItem => "equipment",
        Recipe => "recipes",
        _ => "brews",
    };

    private async Task<bool> ExistsAsync(string table, string id, CancellationToken ct) => table switch
    {
        "vendors" => await vendors.GetAsync(id, ct) is not null,
        "beans" => await beans.GetAsync(id, ct) is not null,
        "bags" => await bags.GetAsync(id, ct) is not null,
        "equipment" => await gear.GetAsync(id, ct) is not null,
        "recipes" => await recipes.GetAsync(id, ct) is not null,
        _ => await brews.GetAsync(id, ct) is not null,
    };

    private Task SaveAsync(SyncEntity record, CancellationToken ct) => record switch
    {
        Vendor v => vendors.SaveAsync(v, ct),
        Bean b => beans.SaveAsync(b, ct),
        Bag b => bags.SaveAsync(b, ct),
        EquipmentItem g => gear.SaveAsync(g, ct),
        Recipe r => recipes.SaveAsync(r, ct),
        Brew b => brews.SaveAsync(b, ct),
        _ => Task.CompletedTask,
    };
}
