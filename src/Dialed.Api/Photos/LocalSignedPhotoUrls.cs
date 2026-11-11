using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.WebUtilities;

namespace Dialed.Api.Photos;

/// <summary>
/// The stand-in for Blob Storage SAS URLs while Azure is not available: URLs on this API (<c>/photo-blobs/...</c>) signed with an HMAC
/// over the permission, the photo's path and the expiry. Like a SAS they carry their own authorisation (no bearer token), allow one
/// operation on one photo and stop working when they expire. The key is made at startup, so a restart also ends every URL.
/// </summary>
public sealed class LocalSignedPhotoUrls(IHttpContextAccessor http, PhotoStorageOptions options, TimeProvider time) : IPhotoUrlIssuer
{
    public const string PathPrefix = "/photo-blobs";

    private readonly byte[] key = RandomNumberGenerator.GetBytes(32);

    public PhotoUrl IssueUpload(PhotoBlobRef blob) => Issue(blob, "w", "PUT", options.UploadUrlLifetime);

    public PhotoUrl IssueRead(PhotoBlobRef blob) => Issue(blob, "r", "GET", options.ReadUrlLifetime);

    /// <summary>Whether the signature in a request allows <paramref name="permission"/> on the photo right now.</summary>
    public bool Verify(PhotoBlobRef blob, string permission, string? signedPermission, string? expiresAt, string? signature)
    {
        if (signedPermission != permission
            || !long.TryParse(expiresAt, NumberStyles.None, CultureInfo.InvariantCulture, out var expiry)
            || time.GetUtcNow().ToUnixTimeSeconds() > expiry
            || string.IsNullOrEmpty(signature))
        {
            return false;
        }

        byte[] given;
        try
        {
            given = WebEncoders.Base64UrlDecode(signature);
        }
        catch (FormatException)
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(given, Sign(permission, blob, expiry));
    }

    private PhotoUrl Issue(PhotoBlobRef blob, string permission, string method, TimeSpan lifetime)
    {
        var request = http.HttpContext?.Request ?? throw new InvalidOperationException("Photo URLs are issued while handling a request.");
        var expiresAt = time.GetUtcNow().Add(lifetime);
        var expiry = expiresAt.ToUnixTimeSeconds();
        var signature = WebEncoders.Base64UrlEncode(Sign(permission, blob, expiry));
        var url = $"{request.Scheme}://{request.Host}{request.PathBase}{PathPrefix}/{blob.HouseholdId}/{blob.PhotoId}.jpg"
            + $"?sp={permission}&se={expiry.ToString(CultureInfo.InvariantCulture)}&sig={signature}";

        IReadOnlyDictionary<string, string> headers = method == "PUT"
            ? new Dictionary<string, string> { ["x-ms-blob-type"] = "BlockBlob" } // what Azure requires; the stand-in ignores it
            : new Dictionary<string, string>();
        return new PhotoUrl(new Uri(url), method, headers, DateTimeOffset.FromUnixTimeSeconds(expiry));
    }

    private byte[] Sign(string permission, PhotoBlobRef blob, long expiry) =>
        HMACSHA256.HashData(key, Encoding.UTF8.GetBytes($"{permission}\n{blob.Path}\n{expiry.ToString(CultureInfo.InvariantCulture)}"));
}
