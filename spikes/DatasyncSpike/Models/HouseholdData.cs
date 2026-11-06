using System.ComponentModel.DataAnnotations;
using System.Linq.Expressions;
using CommunityToolkit.Datasync.Server.CosmosDb;
using Microsoft.Azure.Cosmos;

namespace DatasyncSpike.Models;

/// <summary>Base for everything that lives in a household. Ids are packed: <c>{householdId}:{guid}</c>.</summary>
public abstract class HouseholdTableData<T> : CosmosTableData<T> where T : CosmosTableData
{
    [Required, MinLength(1)]
    public string HouseholdId { get; set; } = string.Empty;

    public string? CreatedBy { get; set; }
}

public sealed class Brew : HouseholdTableData<Brew>
{
    public double DoseG { get; set; }
    public double YieldG { get; set; }
    public int TimeS { get; set; }
    public int Rating { get; set; }
    public string? Notes { get; set; }
}

/// <summary>Packed ids: the household (= Cosmos partition key) is the part before the first colon.</summary>
public static class HouseholdIds
{
    public static string New(string householdId) => $"{householdId}:{Guid.NewGuid():N}";

    public static bool TryGetHousehold(string id, out string householdId)
    {
        var separator = id.IndexOf(':', StringComparison.Ordinal);
        householdId = separator > 0 ? id[..separator] : string.Empty;
        return separator > 0 && separator < id.Length - 1;
    }
}

/// <summary>
/// One shared container for all household data, partitioned by <c>/householdId</c>; an <c>entity</c> discriminator
/// separates the types. Point reads work because the partition key can be parsed from the id.
/// </summary>
public sealed class HouseholdTableOptions<T>(string databaseId, string containerId)
    : CosmosTableOptions<T>(databaseId, containerId) where T : HouseholdTableData<T>
{
    private static readonly string EntityName = typeof(T).Name;

    public override Func<T, string> IdGenerator => entity => HouseholdIds.New(entity.HouseholdId);

    public override string GetPartitionKey(T entity, out PartitionKey partitionKey)
    {
        partitionKey = new PartitionKey(entity.HouseholdId);
        return entity.Id;
    }

    public override string ParsePartitionKey(string id, out PartitionKey partitionKey)
    {
        if (!HouseholdIds.TryGetHousehold(id, out var householdId))
        {
            throw new ArgumentException($"Id '{id}' is not of the form '{{householdId}}:{{guid}}'.", nameof(id));
        }

        partitionKey = new PartitionKey(householdId);
        return id;
    }

    public override Expression<Func<T, bool>> QueryablePredicate() => entity => entity.Entity == EntityName;
}
