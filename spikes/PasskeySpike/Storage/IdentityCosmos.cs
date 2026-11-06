using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Azure.Cosmos;

namespace PasskeySpike.Storage;

/// <summary>
/// The <c>identity</c> container, partitioned by <c>/pk</c>. Every document is its own partition (pk == id), so
/// all lookups are point reads (1 RU): the user, plus small lookup documents for unique keys
/// (<c>email:{hash}</c>, <c>passkey:{credentialId}</c>) and single-use ceremony markers (<c>ceremony:{nonce}</c>, TTL).
/// </summary>
public sealed class IdentityCosmos(CosmosClient client)
{
    public const string DatabaseId = "dialed-spike";
    public const string ContainerId = "identity";

    public Container Container { get; } = client.GetContainer(DatabaseId, ContainerId);

    public static CosmosClient CreateClient(string connectionString) => new(connectionString, new CosmosClientOptions
    {
        // The Linux (vNext) emulator only supports gateway mode.
        ConnectionMode = connectionString.Contains("localhost", StringComparison.OrdinalIgnoreCase) || connectionString.Contains("127.0.0.1", StringComparison.Ordinal)
            ? ConnectionMode.Gateway
            : ConnectionMode.Direct,
        LimitToEndpoint = true,
        UseSystemTextJsonSerializerWithOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        },
    });

    public static async Task EnsureContainerAsync(CosmosClient client)
    {
        Database database = await client.CreateDatabaseIfNotExistsAsync(DatabaseId);
        await database.CreateContainerIfNotExistsAsync(new ContainerProperties(ContainerId, "/pk")
        {
            DefaultTimeToLive = -1, // TTL on, but only for documents that set "ttl" (ceremony markers).
        });
    }

    public static string EmailKey(string normalizedEmail) =>
        "email:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(normalizedEmail)));

    public static string PasskeyKey(byte[] credentialId) => "passkey:" + System.Buffers.Text.Base64Url.EncodeToString(credentialId);

    public async Task<T?> ReadAsync<T>(string id, CancellationToken ct) where T : class
    {
        try
        {
            var response = await Container.ReadItemAsync<T>(id, new PartitionKey(id), cancellationToken: ct);
            return response.Resource;
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    /// <summary>Creates a lookup document; false when the key is already taken.</summary>
    public async Task<bool> TryCreateLookupAsync(string id, string userId, int? ttlSeconds, CancellationToken ct)
    {
        try
        {
            await Container.CreateItemAsync(new LookupDocument { Id = id, Pk = id, UserId = userId, Ttl = ttlSeconds }, new PartitionKey(id), cancellationToken: ct);
            return true;
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.Conflict)
        {
            return false;
        }
    }

    public async Task DeleteIfExistsAsync(string id, CancellationToken ct)
    {
        try
        {
            await Container.DeleteItemAsync<LookupDocument>(id, new PartitionKey(id), cancellationToken: ct);
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
        }
    }
}

public sealed class LookupDocument
{
    public string Id { get; set; } = "";
    public string Pk { get; set; } = "";
    public string Type { get; set; } = "lookup";
    public string UserId { get; set; } = "";
    public int? Ttl { get; set; }
}
