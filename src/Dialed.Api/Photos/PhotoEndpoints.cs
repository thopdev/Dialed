using Dialed.Images;
using System.Security.Claims;
using System.Text.Json.Nodes;
using Dialed.Api.Auth;
using Dialed.Api.Sync;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.IdentityModel.JsonWebTokens;

namespace Dialed.Api.Photos;

/// <summary>What the app gets back from <c>/photos/{id}/upload-url</c> and <c>/photos/{id}/url</c>.</summary>
public sealed record PhotoUrlResponse(string Url, string Method, IReadOnlyDictionary<string, string> Headers, DateTimeOffset ExpiresAt);

/// <summary>
/// Photo files (#68, ADR-0008). The app syncs a photo's metadata like any record and then asks for a short-lived URL to upload the file
/// directly to storage, so photo bytes never pass through the API in Azure. Until Azure is set up the URLs point at a signed local
/// stand-in on this API (<see cref="LocalSignedPhotoUrls"/>).
/// </summary>
public static class PhotoEndpoints
{
    public static IServiceCollection AddDialedPhotos(this IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        var options = configuration.GetSection(PhotoStorageOptions.Section).Get<PhotoStorageOptions>() ?? new PhotoStorageOptions();
        if (string.IsNullOrEmpty(options.LocalFolder) && environment.IsDevelopment())
        {
            options.LocalFolder = Path.Combine(Path.GetTempPath(), "dialed-dev-photos");
        }

        services.AddSingleton(options);
        services.TryAddSingleton(TimeProvider.System);
        services.AddHttpContextAccessor();

        // Azure: register an issuer that makes user-delegation SAS URLs and a Blob Storage-backed store instead (docs/photo-uploads.md).
        services.TryAddSingleton<LocalSignedPhotoUrls>();
        services.TryAddSingleton<IPhotoUrlIssuer>(sp => sp.GetRequiredService<LocalSignedPhotoUrls>());
        // One store for uploads, processing and reads: in memory (registered by AddDialedPhotoProcessing), or a folder in development so photos
        // survive an API restart.
        if (!string.IsNullOrEmpty(options.LocalFolder))
        {
            var files = new FileSystemPhotoBlobStore(options.LocalFolder);
            services.Replace(ServiceDescriptor.Singleton<IPhotoBlobStore>(files));
            services.Replace(ServiceDescriptor.Singleton<IPhotoBlobEvents>(files));
        }
        return services;
    }

    public static IEndpointRouteBuilder MapDialedPhotos(this IEndpointRouteBuilder endpoints)
    {
        var photos = endpoints.MapGroup("/photos").RequireAuthorization();

        // A URL to upload the photo's file, after checking the photo's record is in the caller's household and visible to them. The record
        // syncs first; until it has, the answer is 404 and the app tries again after the next sync.
        photos.MapPost("/{id}/upload-url", async (string id, ClaimsPrincipal user, ISyncStore store, IPhotoUrlIssuer issuer, CancellationToken ct) =>
            await FindForCallerAsync(id, user, store, ct) is { } blob ? Results.Ok(ToResponse(issuer.IssueUpload(blob))) : NotFound());

        // A URL to read the photo's file (for devices that did not take it), with the same check.
        photos.MapGet("/{id}/url", async (string id, ClaimsPrincipal user, ISyncStore store, IPhotoUrlIssuer issuer, CancellationToken ct) =>
            await FindForCallerAsync(id, user, store, ct) is { } blob ? Results.Ok(ToResponse(issuer.IssueRead(blob))) : NotFound());

        // The local stand-in for Blob Storage. The signature in the URL is the authorisation, as with a SAS.
        if (endpoints.ServiceProvider.GetService<IPhotoUrlIssuer>() is LocalSignedPhotoUrls)
        {
            var blobs = endpoints.MapGroup(LocalSignedPhotoUrls.PathPrefix).AllowAnonymous();
            blobs.MapPut("/{householdId}/{file}", UploadAsync);
            blobs.MapGet("/{householdId}/{file}", ReadAsync);
        }

        return endpoints;
    }

    private static async Task<IResult> UploadAsync(
        string householdId, string file, HttpRequest request, LocalSignedPhotoUrls urls, IPhotoBlobStore store, PhotoStorageOptions options,
        IEnumerable<IPhotoUploadedHandler> handlers, CancellationToken ct)
    {
        if (Blob(householdId, file) is not { } blob
            || !urls.Verify(blob, "w", request.Query["sp"], request.Query["se"], request.Query["sig"]))
        {
            return Results.Problem("The upload URL is not valid or has expired.", statusCode: StatusCodes.Status403Forbidden);
        }

        if (!string.Equals(request.ContentType, "image/jpeg", StringComparison.OrdinalIgnoreCase))
        {
            return Results.Problem("Send the photo as image/jpeg.", statusCode: StatusCodes.Status415UnsupportedMediaType);
        }

        using var buffer = new MemoryStream();
        await request.Body.CopyToAsync(buffer, ct);
        if (buffer.Length > options.MaxBytes)
        {
            return Results.Problem("The photo is too large.", statusCode: StatusCodes.Status413PayloadTooLarge);
        }

        var content = buffer.ToArray();
        if (content is not [0xFF, 0xD8, 0xFF, ..])
        {
            return Results.Problem("The file is not a JPEG photo.", statusCode: StatusCodes.Status400BadRequest);
        }

        await store.WriteAsync(blob.Path, content, ct);
        foreach (var handler in handlers)
        {
            await handler.OnUploadedAsync(blob, ct);
        }

        return Results.Created();
    }

    private static async Task<IResult> ReadAsync(
        string householdId, string file, HttpRequest request, LocalSignedPhotoUrls urls, IPhotoBlobStore store, PhotoStorageOptions options, CancellationToken ct)
    {
        if (Blob(householdId, file) is not { } blob
            || !urls.Verify(blob, "r", request.Query["sp"], request.Query["se"], request.Query["sig"]))
        {
            return Results.Problem("The URL is not valid or has expired.", statusCode: StatusCodes.Status403Forbidden);
        }

        if (await store.ReadAsync(blob.Path, ct) is not { } content)
        {
            return Results.NotFound();
        }

        // The browser may keep the image as long as the URL is valid; it is private to this person.
        request.HttpContext.Response.Headers.CacheControl = $"private, max-age={(int)options.ReadUrlLifetime.TotalSeconds}";
        return Results.File(content, "image/jpeg");
    }

    private static PhotoBlobRef? Blob(string householdId, string file)
    {
        var photoId = file.EndsWith(".jpg", StringComparison.Ordinal) ? file[..^4] : null;
        return PhotoBlobRef.IsSafeId(householdId) && PhotoBlobRef.IsSafeId(photoId) ? new PhotoBlobRef(householdId, photoId!) : null;
    }

    /// <summary>The photo's file, when its record is in the caller's household, not deleted and readable by them.</summary>
    internal static async Task<PhotoBlobRef?> FindForCallerAsync(string id, ClaimsPrincipal user, ISyncStore store, CancellationToken ct)
    {
        if (user.FindFirstValue(TokenIssuer.HouseholdClaim) is not { Length: > 0 } household
            || !PhotoBlobRef.IsSafeId(household)
            || !PhotoBlobRef.IsSafeId(id)
            || await store.GetAsync(household, "photos", id, ct) is not { } row
            || row.Entity["deleted"] is JsonValue deleted && deleted.TryGetValue<bool>(out var gone) && gone)
        {
            return null;
        }

        var viewer = user.FindFirstValue(JwtRegisteredClaimNames.Sub) ?? "";
        return VisibilityRules.CanRead(VisibilityRules.Of(row.Entity), VisibilityRules.InHousehold(row.Entity, viewer))
            ? new PhotoBlobRef(household, id)
            : null;
    }

    private static IResult NotFound() =>
        Results.Problem("No such photo, or its details have not synced yet.", statusCode: StatusCodes.Status404NotFound);

    private static PhotoUrlResponse ToResponse(PhotoUrl url) => new(url.Url.ToString(), url.Method, url.Headers, url.ExpiresAt);
}
