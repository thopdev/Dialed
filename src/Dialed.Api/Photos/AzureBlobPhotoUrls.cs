using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Sas;

namespace Dialed.Api.Photos;

/// <summary>Hands out a user delegation key that is valid at least until a given time.</summary>
public interface IUserDelegationKeySource
{
    Task<UserDelegationKey> GetAsync(DateTimeOffset validUntil, CancellationToken cancellationToken = default);
}

/// <summary>
/// User delegation keys from Blob Storage, asked for with the API's managed identity (role Storage Blob Delegator) and kept for a day.
/// A new key is fetched when the cached one would expire before a URL signed with it.
/// </summary>
public sealed class CachedUserDelegationKeys(BlobServiceClient service, TimeProvider time) : IUserDelegationKeySource
{
    /// <summary>How long one key lives. The service allows up to 7 days.</summary>
    public static readonly TimeSpan KeyLifetime = TimeSpan.FromDays(1);

    private readonly SemaphoreSlim gate = new(1, 1);
    private UserDelegationKey? cached;

    public async Task<UserDelegationKey> GetAsync(DateTimeOffset validUntil, CancellationToken cancellationToken = default)
    {
        if (cached is { } key && key.SignedExpiresOn >= validUntil)
        {
            return key;
        }

        await gate.WaitAsync(cancellationToken);
        try
        {
            if (cached is null || cached.SignedExpiresOn < validUntil)
            {
                var now = time.GetUtcNow();
                cached = (await service.GetUserDelegationKeyAsync(now.AddMinutes(-5), now.Add(KeyLifetime), cancellationToken)).Value;
            }

            return cached;
        }
        finally
        {
            gate.Release();
        }
    }
}

/// <summary>
/// Short-lived user delegation SAS URLs for one photo blob (ADR-0008, #68, #198). Shared keys are off on the account, so every URL is
/// signed with a key from Microsoft Entra ID. Uploads may only create or write that one blob; reads may only read it, and are served as
/// <c>image/jpeg</c> with a private cache lifetime. Both stop working after minutes (<see cref="PhotoStorageOptions"/>).
/// </summary>
public sealed class AzureBlobPhotoUrls(BlobContainerClient container, IUserDelegationKeySource keys, PhotoStorageOptions options, TimeProvider time)
    : IPhotoUrlIssuer
{
    /// <summary>The SAS starts a little in the past, so a storage clock behind ours still accepts it.</summary>
    public static readonly TimeSpan ClockSkew = TimeSpan.FromMinutes(5);

    public Task<PhotoUrl> IssueUploadAsync(PhotoBlobRef blob, CancellationToken cancellationToken = default) =>
        IssueAsync(blob, BlobSasPermissions.Create | BlobSasPermissions.Write, "PUT", options.UploadUrlLifetime, cancellationToken);

    public Task<PhotoUrl> IssueReadAsync(PhotoBlobRef blob, CancellationToken cancellationToken = default) =>
        IssueAsync(blob, BlobSasPermissions.Read, "GET", options.ReadUrlLifetime, cancellationToken);

    private async Task<PhotoUrl> IssueAsync(
        PhotoBlobRef blob, BlobSasPermissions permissions, string method, TimeSpan lifetime, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var expiresAt = TruncateToSeconds(now.Add(lifetime));
        var key = await keys.GetAsync(expiresAt, cancellationToken);
        var target = container.GetBlobClient(blob.Path);

        var sas = new BlobSasBuilder(permissions, expiresAt)
        {
            BlobContainerName = container.Name,
            BlobName = blob.Path,
            Resource = "b",
            StartsOn = now.Subtract(ClockSkew),
            Protocol = target.Uri.Scheme == Uri.UriSchemeHttps ? SasProtocol.Https : SasProtocol.HttpsAndHttp,
        };
        IReadOnlyDictionary<string, string> headers;
        if (method == "PUT")
        {
            headers = new Dictionary<string, string> { ["x-ms-blob-type"] = "BlockBlob" };
        }
        else
        {
            sas.ContentType = "image/jpeg";
            sas.CacheControl = $"private, max-age={(int)lifetime.TotalSeconds}";
            headers = new Dictionary<string, string>();
        }

        var url = new UriBuilder(target.Uri) { Query = sas.ToSasQueryParameters(key, container.AccountName).ToString() }.Uri;
        return new PhotoUrl(url, method, headers, expiresAt);
    }

    /// <summary>A SAS expiry has whole seconds; the response says the same moment.</summary>
    private static DateTimeOffset TruncateToSeconds(DateTimeOffset at) => DateTimeOffset.FromUnixTimeSeconds(at.ToUnixTimeSeconds());
}
