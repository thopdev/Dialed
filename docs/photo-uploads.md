# Photo uploads (#68)

How photo files get from the phone to storage, and what changes when Azure Blob Storage is set up. Background: [ADR-0008](adr/0008-images.md) and [architecture §6](architecture.md#6-images).

## Flow

1. The app takes or picks a photo (re-encoded to JPEG, max 2048 px), writes the file to the local file store (`ILocalFileStore`, OPFS in the browser) and saves a `Photo` record with `UploadState = Pending`. `PhotoService.AddAsync` then puts the photo in the upload queue.
2. The `Photo` record syncs like any other record.
3. `PhotoUploadQueue` (in `Dialed.Client.Uploads`) asks `POST /photos/{id}/upload-url` for a short-lived URL and sends the file there with the method and headers it was given. The API checks that the photo's record is in the caller's household, not deleted and visible to them; until the record has synced the answer is 404, and the queue tries again after the next sync.
4. On success the queue sets `UploadState = Uploaded` (this syncs, so other devices know the file is in storage) and drops the entry.
5. Devices without the file show it through `GET /photos/{id}/url`, a short-lived read URL (`PhotoService.GetImageSourceAsync`, used by `Thumb` and `PhotoStrip`). The browser caches the image (`Cache-Control: private`).

## The queue

- Entries live in the local store (collection `uploads`), the files in the local file store, so the queue survives restarts. Photos still `Pending` with a local file but no entry (taken before the queue existed) are picked up on start.
- One upload at a time. Runs on start, on enqueue, when the network comes back, when the app returns to the foreground, after a sync and when the next retry is due.
- Retries wait 10 s, 20 s, 40 s ... up to 30 minutes. Being offline, signed out or waiting for the record to sync never counts as a failure. Server or storage errors do: after 8 in a row (about an hour) the photo is marked `Failed` until the person taps **Retry upload** (on the photo) or **Try uploading again** (Settings, under sync). A refused photo (wrong type, too large) fails at once.
- If the browser cleared the file before it was uploaded (iOS storage eviction), the photo is marked `Failed` and shows "Photo not on this device"; there is nothing left to retry.
- Settings shows how many photos wait ("only on this device until then") and how many failed.

## API

| Endpoint | Auth | What |
| --- | --- | --- |
| `POST /photos/{id}/upload-url` | bearer | `{ url, method: "PUT", headers, expiresAt }`, valid 10 minutes |
| `GET /photos/{id}/url` | bearer | `{ url, method: "GET", headers, expiresAt }`, valid 30 minutes |
| `PUT /photo-blobs/{householdId}/{photoId}.jpg?sp=w&se=..&sig=..` | signature | local stand-in only: stores the JPEG |
| `GET /photo-blobs/{householdId}/{photoId}.jpg?sp=r&se=..&sig=..` | signature | local stand-in only: returns the JPEG |

The interfaces are in `src/Dialed.Api/Photos/PhotoStorage.cs`:

- `IPhotoUrlIssuer` makes the upload and read URLs (one permission, one photo, short expiry).
- `IPhotoBlobStore` (in `Dialed.Images`, paths like `photos/{household}/{photo}.jpg`) reads and writes the files, for the stand-in, for the processing of #69 (metadata stripping and thumbnails, run in place so the read URL serves the stripped photo) and for server-side readers such as a label scan by photo id. The in-memory store and the development folder store both raise `BlobWritten`, which stands in for Azure's blob-created event.
- `IPhotoUploadedHandler` runs after a photo arrived. Thumbnails and metadata stripping (#69) plug in here for local development; in Azure the same work is triggered by the blob-created event.

Settings live under `Photos` (`UploadUrlLifetime`, `ReadUrlLifetime`, `MaxBytes`, `LocalFolder`). Development keeps files in `%TEMP%/dialed-dev-photos`; tests keep them in memory. The stand-in signs URLs with an HMAC key made at startup, so restarting the API also ends every URL it handed out (the queue simply asks for a new one).

## Moving to Azure Blob Storage

Not done here because Azure is not available (#25). The steps:

1. **Storage account** with a private container `photos` (Bicep in `infra/`). The API's managed identity gets *Storage Blob Delegator* on the account and *Storage Blob Data Contributor* on the container.
2. **CORS** on the Blob service: allowed origins are only the app origin(s) (the same list as `App:Origins`), methods `PUT, GET, HEAD`, allowed headers `content-type, x-ms-blob-type`, max age 3600.
3. **`AzureBlobPhotoUrls : IPhotoUrlIssuer`** with `Azure.Storage.Blobs`:
   - Get a user delegation key with `BlobServiceClient.GetUserDelegationKeyAsync` (cache it, it is valid for up to 7 days; refresh well before it expires).
   - For each URL build a `BlobSasBuilder` for blob `{householdId}/{photoId}.jpg` in `photos`, `Resource = "b"`, `StartsOn = now - 5 min` (clock skew), `ExpiresOn = now + lifetime`, permissions `Create | Write` for upload or `Read` for read, `Protocol = HttpsOnly`, and for reads `ContentType = "image/jpeg"` and `CacheControl = "private, max-age=..."`.
   - Return `PUT` with header `x-ms-blob-type: BlockBlob` (the stand-in already sends it), or `GET`.
4. **`AzureBlobPhotoStore : IPhotoBlobStore`** on the same container for server-side reads, and register both in `AddDialedPhotos` instead of the stand-in. `MapDialedPhotos` maps the `/photo-blobs` endpoints only while the stand-in is the issuer, so they disappear by themselves.
5. **Size and type**: a SAS cannot limit size or content type. The Function from #69 checks each new blob (JPEG magic bytes, at most `MaxBytes`) and deletes anything else, and an Event Grid subscription on `BlobCreated` triggers it.
6. **Deletion**: deleting a photo record, a household or an account must also delete the blobs (Function job, ADR-0008).

The app needs no change: it already sends whatever method and headers the API returns, to whatever URL it returns, without the API token.

## Offline label scans (#83)

A label scan taken without a connection waits instead of failing.

1. **Scan** (offline): the camera still works. The scan fails as "offline" and offers **Save the photo and read it when I am online**. That saves the photo like any other (`PhotoService.AddAsync`, so the upload queue sends it) and adds a pending scan to `PendingScanQueue`.
2. **Reading**: `POST /ai/label-scans/{photoId}` takes only the id. The API checks the photo's record is in the caller's household, reads the file through `IPhotoBlobStore` and runs the same quota check, extraction and telemetry as the direct scan. It answers 404 (`photo-unknown` until the record has synced, `photo-not-uploaded` until the file has arrived) without counting against the quota.
3. **The queue** (`Dialed.Client.Scan.PendingScanQueue`, collection `scans` in the local store) reads one scan at a time when something may have changed: a scan was added, the network came back, the app returned to the foreground, or a photo finished uploading. Offline, signed out, or the photo not being on the server yet is never a failure and the scan keeps waiting. A used-up quota or AI switched off marks it failed with that reason (a **Try again** button restarts it); three server errors in a row do the same. The draft is kept with the scan, across restarts.
4. **Review**: Scan lists **Scans to review**; **Review** opens the existing review form with the kept draft and the photo that is already saved. A banner (`PendingScansNote`) shows "Scans to review: N" as a link, or a quiet "Label scans waiting for a connection: N".

