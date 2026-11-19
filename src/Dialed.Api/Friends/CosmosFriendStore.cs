using System.Net;
using Dialed.Api.Data;
using Microsoft.Azure.Cosmos;

namespace Dialed.Api.Friends;

/// <summary>
/// Friend codes, follows and blocks in Cosmos DB (#38). The <c>friends</c> container is partitioned by <c>/pk</c>:
/// <list type="bullet">
/// <item><c>user:{id}</c> holds the person's own friend code (<c>code</c>), the people they follow or asked to (<c>follow:{followee}</c>)
/// and the people they blocked (<c>block:{blocked}</c>), so "is this person blocked", "do I follow them" and "who do I follow" are point
/// reads or single-partition queries.</item>
/// <item><c>code:{CODE}</c> points from a friend code to its person. Creating it fails when the code is taken, which keeps codes unique.</item>
/// </list>
/// Only the reverse lists (who follows me, for the followers screen and account deletion) read across partitions.
/// </summary>
public sealed class CosmosFriendStore(DialedCosmos cosmos) : IFriendStore
{
    public static readonly CosmosContainerSpec Container = new("friends", "/pk", DefaultTimeToLive: null);

    private const string CodeId = "code";
    private const int MaxRetries = 10;

    public async Task<string> GetOrCreateCodeAsync(string userId, Func<string> newCode, CancellationToken cancellationToken = default)
    {
        var container = await cosmos.ContainerAsync(Container, cancellationToken);
        for (var attempt = 0; attempt < MaxRetries; attempt++)
        {
            var (existing, _) = await ReadAsync<UserCodeDocument>(container, UserKey(userId), CodeId, cancellationToken);
            if (existing is not null)
            {
                return existing.Code;
            }

            var code = await ClaimCodeAsync(container, userId, newCode, cancellationToken);
            try
            {
                await container.CreateItemAsync(new UserCodeDocument { Pk = UserKey(userId), UserId = userId, Code = code }, new PartitionKey(UserKey(userId)), cancellationToken: cancellationToken);
                return code;
            }
            catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.Conflict)
            {
                // Another request gave the person a code at the same moment: drop ours and use theirs.
                await DeleteIfThereAsync(container, CodeKey(code), CodeId, cancellationToken);
            }
        }

        throw new InvalidOperationException("Could not create a friend code.");
    }

    public async Task<string> RotateCodeAsync(string userId, Func<string> newCode, CancellationToken cancellationToken = default)
    {
        var container = await cosmos.ContainerAsync(Container, cancellationToken);
        var key = new PartitionKey(UserKey(userId));
        for (var attempt = 0; attempt < MaxRetries; attempt++)
        {
            var (existing, etag) = await ReadAsync<UserCodeDocument>(container, UserKey(userId), CodeId, cancellationToken);
            var code = await ClaimCodeAsync(container, userId, newCode, cancellationToken);
            var next = new UserCodeDocument { Pk = UserKey(userId), UserId = userId, Code = code };
            try
            {
                if (etag is null)
                {
                    await container.CreateItemAsync(next, key, cancellationToken: cancellationToken);
                }
                else
                {
                    await container.ReplaceItemAsync(next, CodeId, key, new ItemRequestOptions { IfMatchEtag = etag }, cancellationToken);
                }
            }
            catch (CosmosException ex) when (ex.StatusCode is HttpStatusCode.Conflict or HttpStatusCode.PreconditionFailed or HttpStatusCode.NotFound)
            {
                await DeleteIfThereAsync(container, CodeKey(code), CodeId, cancellationToken);
                continue;
            }

            if (existing is not null)
            {
                await DeleteIfThereAsync(container, CodeKey(existing.Code), CodeId, cancellationToken); // the old code stops working
            }

            return code;
        }

        throw new InvalidOperationException("Could not rotate the friend code.");
    }

    public async Task<string?> FindUserByCodeAsync(string code, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            return null;
        }

        var container = await cosmos.ContainerAsync(Container, cancellationToken);
        return (await ReadAsync<CodePointerDocument>(container, CodeKey(code), CodeId, cancellationToken)).Document?.UserId;
    }

    public async Task<Follow?> FindFollowAsync(string followerId, string followeeId, CancellationToken cancellationToken = default)
    {
        var container = await cosmos.ContainerAsync(Container, cancellationToken);
        return (await ReadAsync<FollowDocument>(container, UserKey(followerId), FollowId(followeeId), cancellationToken)).Document?.ToFollow();
    }

    public async Task SaveFollowAsync(Follow follow, CancellationToken cancellationToken = default)
    {
        var container = await cosmos.ContainerAsync(Container, cancellationToken);
        await container.UpsertItemAsync(FollowDocument.From(follow), new PartitionKey(UserKey(follow.FollowerId)), cancellationToken: cancellationToken);
    }

    public async Task<bool> RemoveFollowAsync(string followerId, string followeeId, CancellationToken cancellationToken = default)
    {
        var container = await cosmos.ContainerAsync(Container, cancellationToken);
        return await DeleteIfThereAsync(container, UserKey(followerId), FollowId(followeeId), cancellationToken);
    }

    public async Task<IReadOnlyList<Follow>> FollowsAsync(string userId, bool asFollowee, CancellationToken cancellationToken = default)
    {
        var container = await cosmos.ContainerAsync(Container, cancellationToken);
        var documents = asFollowee
            ? await QueryAsync<FollowDocument>(container, null, new QueryDefinition("SELECT * FROM c WHERE c.type = 'follow' AND c.followeeId = @user").WithParameter("@user", userId), cancellationToken)
            : await QueryAsync<FollowDocument>(container, UserKey(userId), new QueryDefinition("SELECT * FROM c WHERE c.type = 'follow'"), cancellationToken);
        return [.. documents.Select(d => d.ToFollow()).OrderBy(f => f.CreatedAt)];
    }

    public async Task<bool> AddBlockAsync(string blockerId, string blockedId, CancellationToken cancellationToken = default)
    {
        var container = await cosmos.ContainerAsync(Container, cancellationToken);
        try
        {
            await container.CreateItemAsync(
                new BlockDocument { Id = BlockId(blockedId), Pk = UserKey(blockerId), BlockerId = blockerId, BlockedId = blockedId },
                new PartitionKey(UserKey(blockerId)),
                cancellationToken: cancellationToken);
            return true;
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.Conflict)
        {
            return false;
        }
    }

    public async Task<bool> RemoveBlockAsync(string blockerId, string blockedId, CancellationToken cancellationToken = default)
    {
        var container = await cosmos.ContainerAsync(Container, cancellationToken);
        return await DeleteIfThereAsync(container, UserKey(blockerId), BlockId(blockedId), cancellationToken);
    }

    public async Task<IReadOnlyList<string>> BlockedByAsync(string blockerId, CancellationToken cancellationToken = default)
    {
        var container = await cosmos.ContainerAsync(Container, cancellationToken);
        var documents = await QueryAsync<BlockDocument>(container, UserKey(blockerId), new QueryDefinition("SELECT * FROM c WHERE c.type = 'block'"), cancellationToken);
        return [.. documents.Select(d => d.BlockedId)];
    }

    public async Task<bool> IsBlockedAsync(string blockerId, string blockedId, CancellationToken cancellationToken = default)
    {
        var container = await cosmos.ContainerAsync(Container, cancellationToken);
        return (await ReadAsync<BlockDocument>(container, UserKey(blockerId), BlockId(blockedId), cancellationToken)).Document is not null;
    }

    public async Task RemoveUserAsync(string userId, CancellationToken cancellationToken = default)
    {
        var container = await cosmos.ContainerAsync(Container, cancellationToken);

        // Their own partition: code, follows and blocks they made.
        var (code, _) = await ReadAsync<UserCodeDocument>(container, UserKey(userId), CodeId, cancellationToken);
        if (code is not null)
        {
            await DeleteIfThereAsync(container, CodeKey(code.Code), CodeId, cancellationToken);
        }

        foreach (var own in await QueryAsync<KeyedId>(container, UserKey(userId), new QueryDefinition("SELECT c.id, c.pk FROM c"), cancellationToken))
        {
            await DeleteIfThereAsync(container, own.Pk, own.Id, cancellationToken);
        }

        // What others hold about them: follows of them and blocks of them.
        var query = new QueryDefinition("SELECT c.id, c.pk FROM c WHERE (c.type = 'follow' AND c.followeeId = @user) OR (c.type = 'block' AND c.blockedId = @user)")
            .WithParameter("@user", userId);
        foreach (var other in await QueryAsync<KeyedId>(container, null, query, cancellationToken))
        {
            await DeleteIfThereAsync(container, other.Pk, other.Id, cancellationToken);
        }
    }

    /// <summary>Reserves a free code for the person (the pointer document is created only when nobody has the code).</summary>
    private static async Task<string> ClaimCodeAsync(Container container, string userId, Func<string> newCode, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < MaxRetries; attempt++)
        {
            var code = newCode();
            try
            {
                await container.CreateItemAsync(new CodePointerDocument { Pk = CodeKey(code), UserId = userId }, new PartitionKey(CodeKey(code)), cancellationToken: cancellationToken);
                return code;
            }
            catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.Conflict)
            {
                // taken: draw another
            }
        }

        throw new InvalidOperationException("Could not find a free friend code.");
    }

    private static string UserKey(string userId) => "user:" + userId;

    /// <summary>Codes are looked up regardless of case and surrounding spaces, as in memory.</summary>
    private static string CodeKey(string code) => "code:" + code.Trim().ToUpperInvariant();

    private static string FollowId(string followeeId) => "follow:" + followeeId;

    private static string BlockId(string blockedId) => "block:" + blockedId;

    private static async Task<List<T>> QueryAsync<T>(Container container, string? partition, QueryDefinition query, CancellationToken cancellationToken)
    {
        var options = partition is null ? new QueryRequestOptions() : new QueryRequestOptions { PartitionKey = new PartitionKey(partition) };
        using var iterator = container.GetItemQueryIterator<T>(query, requestOptions: options);
        var results = new List<T>();
        while (iterator.HasMoreResults)
        {
            results.AddRange(await iterator.ReadNextAsync(cancellationToken));
        }

        return results;
    }

    private static async Task<(T? Document, string? ETag)> ReadAsync<T>(Container container, string partition, string id, CancellationToken cancellationToken)
        where T : class
    {
        try
        {
            var response = await container.ReadItemAsync<T>(id, new PartitionKey(partition), cancellationToken: cancellationToken);
            return (response.Resource, response.ETag);
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return (null, null);
        }
    }

    private static async Task<bool> DeleteIfThereAsync(Container container, string partition, string id, CancellationToken cancellationToken)
    {
        try
        {
            await container.DeleteItemAsync<KeyedId>(id, new PartitionKey(partition), cancellationToken: cancellationToken);
            return true;
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return false;
        }
    }

    internal sealed record UserCodeDocument
    {
        public string Id { get; init; } = CodeId;
        public string Pk { get; init; } = "";
        public string Type { get; init; } = "code";
        public string UserId { get; init; } = "";
        public string Code { get; init; } = "";
    }

    internal sealed record CodePointerDocument
    {
        public string Id { get; init; } = CodeId;
        public string Pk { get; init; } = "";
        public string Type { get; init; } = "codepointer";
        public string UserId { get; init; } = "";
    }

    internal sealed record FollowDocument
    {
        public string Id { get; init; } = "";
        public string Pk { get; init; } = "";
        public string Type { get; init; } = "follow";
        public string FollowerId { get; init; } = "";
        public string FolloweeId { get; init; } = "";
        public FollowStatus Status { get; init; }
        public DateTimeOffset CreatedAt { get; init; }

        public static FollowDocument From(Follow follow) => new()
        {
            Id = FollowId(follow.FolloweeId),
            Pk = UserKey(follow.FollowerId),
            FollowerId = follow.FollowerId,
            FolloweeId = follow.FolloweeId,
            Status = follow.Status,
            CreatedAt = follow.CreatedAt,
        };

        public Follow ToFollow() => new(FollowerId, FolloweeId, Status, CreatedAt);
    }

    internal sealed record BlockDocument
    {
        public string Id { get; init; } = "";
        public string Pk { get; init; } = "";
        public string Type { get; init; } = "block";
        public string BlockerId { get; init; } = "";
        public string BlockedId { get; init; } = "";
    }

    private sealed record KeyedId(string Id, string Pk);
}
