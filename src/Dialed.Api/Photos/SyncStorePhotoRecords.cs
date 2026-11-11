using System.Text.Json.Nodes;
using Dialed.Api.Sync;
using Dialed.Images;

namespace Dialed.Api.Photos;

/// <summary><see cref="IPhotoRecords"/> on the sync store: the new <c>thumbPath</c> reaches the app with the next pull.</summary>
public sealed class SyncStorePhotoRecords(ISyncStore store) : IPhotoRecords
{
    private const int Attempts = 3;

    public async Task<bool> SetThumbPathAsync(string householdId, string photoId, string thumbPath, CancellationToken cancellationToken = default)
    {
        for (var attempt = 1; ; attempt++)
        {
            var row = await store.GetAsync(householdId, "photos", photoId, cancellationToken);
            if (row is null)
            {
                return false;
            }

            if (row.Entity["thumbPath"]?.GetValue<string>() == thumbPath)
            {
                return true;
            }

            var entity = (JsonObject)row.Entity.DeepClone();
            entity["thumbPath"] = thumbPath;

            // Written as the photo's author, so the update passes the same visibility rule as their own edits.
            var author = row.Entity["createdBy"]?.GetValue<string>() ?? "";
            var result = await store.PushAsync(householdId, author, "photos", photoId, entity, row.Version.ToString(), cancellationToken);
            switch (result.Outcome)
            {
                case StoreOutcome.Stored:
                    return true;
                case StoreOutcome.VersionMismatch when attempt < Attempts:
                    continue; // the app changed the record meanwhile: read it again
                default:
                    throw new InvalidOperationException($"Could not set the thumbnails of photo {photoId}: {result.Outcome}.");
            }
        }
    }
}
