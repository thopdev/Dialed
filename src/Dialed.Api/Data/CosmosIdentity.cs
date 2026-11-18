using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Azure.Cosmos;

namespace Dialed.Api.Data;

/// <summary>
/// The <c>identity</c> container (ADR-0010): users, their lookups, profiles, refresh tokens, ceremony nonces and beta invites.
/// Partition key <c>/pk</c> with <c>pk = id</c>, so every lookup by key is a point read (1 RU) and a document can be created
/// or replaced on its own with its ETag. Ids carry a type prefix (<c>user:</c>, <c>email:</c>, ...); see <c>docs/cosmos-stores.md</c>.
/// </summary>
public static class CosmosIdentity
{
    public static readonly CosmosContainerSpec Container = new("identity", "/pk");

    /// <summary>How often a store re-reads and tries again after another writer changed the document first.</summary>
    public const int MaxRetries = 5;

    /// <summary>Extra time a document is kept after it stops mattering, so TTL never removes it early.</summary>
    public static readonly TimeSpan TtlMargin = TimeSpan.FromMinutes(1);

    /// <summary>Seconds until Cosmos DB may remove the document (never less than the margin).</summary>
    public static int Ttl(TimeSpan untilIrrelevant) => (int)Math.Ceiling(Math.Max(0, (untilIrrelevant + TtlMargin).TotalSeconds));

    /// <summary>A lowercase hex SHA-256: keeps personal data (emails) and long keys (credential ids) out of document ids.</summary>
    public static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    public static string Hash(byte[] value) => Convert.ToHexStringLower(SHA256.HashData(value));

    public static Task<Container> ContainerAsync(this DialedCosmos cosmos, CancellationToken cancellationToken) =>
        cosmos.ContainerAsync(Container, cancellationToken);

    /// <summary>The document and its ETag, or <c>(null, null)</c> when there is none.</summary>
    public static async Task<(T? Document, string? ETag)> ReadAsync<T>(this Container container, string id, CancellationToken cancellationToken)
        where T : class
    {
        try
        {
            var response = await container.ReadItemAsync<T>(id, new PartitionKey(id), cancellationToken: cancellationToken);
            return (response.Resource, response.ETag);
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return (null, null);
        }
    }

    /// <summary>
    /// Creates the document when <paramref name="etag"/> is <c>null</c>, otherwise replaces exactly the version that was read.
    /// False when another writer was first (409, 412, or the document is gone).
    /// </summary>
    public static async Task<bool> TryWriteAsync<T>(this Container container, T document, string? etag, CancellationToken cancellationToken)
        where T : IdentityDocument
    {
        try
        {
            if (etag is null)
            {
                await container.CreateItemAsync(document, new PartitionKey(document.Pk), cancellationToken: cancellationToken);
            }
            else
            {
                await container.ReplaceItemAsync(document, document.Id, new PartitionKey(document.Pk), new ItemRequestOptions { IfMatchEtag = etag }, cancellationToken);
            }

            return true;
        }
        catch (CosmosException ex) when (ex.StatusCode is HttpStatusCode.Conflict or HttpStatusCode.PreconditionFailed or HttpStatusCode.NotFound)
        {
            return false;
        }
    }

    /// <summary>Deletes the document; with an ETag only that version. False when it was gone or changed.</summary>
    public static async Task<bool> TryDeleteAsync(this Container container, string id, string? etag, CancellationToken cancellationToken)
    {
        try
        {
            await container.DeleteItemAsync<object>(id, new PartitionKey(id), etag is null ? null : new ItemRequestOptions { IfMatchEtag = etag }, cancellationToken);
            return true;
        }
        catch (CosmosException ex) when (ex.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.PreconditionFailed)
        {
            return false;
        }
    }

    /// <summary>All results of a (cross-partition) query.</summary>
    public static async Task<List<T>> QueryAsync<T>(this Container container, QueryDefinition query, CancellationToken cancellationToken)
    {
        var results = new List<T>();
        using var iterator = container.GetItemQueryIterator<T>(query);
        while (iterator.HasMoreResults)
        {
            results.AddRange(await iterator.ReadNextAsync(cancellationToken));
        }

        return results;
    }
}

/// <summary>Every document in the <c>identity</c> container: <c>pk</c> equals <c>id</c>, and <c>type</c> says what it is.</summary>
public abstract record IdentityDocument
{
    public string Id { get; init; } = "";

    public string Pk => Id;

    public string Type { get; init; } = "";

    /// <summary>Seconds until Cosmos DB removes the document; <c>null</c> keeps it.</summary>
    public int? Ttl { get; init; }
}
