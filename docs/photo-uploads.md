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

- `IPhotoUrlIssuer` makes the upload and read URLs (one permission, one photo, short expiry): `LocalSignedPhotoUrls` locally, `AzureBlobPhotoUrls` in Azure.
- `IPhotoBlobStore` (in `Dialed.Images`, paths like `photos/{household}/{photo}.jpg`) reads and writes the files, for the stand-in, for the processing of #69 (metadata stripping and thumbnails, run in place so the read URL serves the stripped photo) and for server-side readers such as a label scan by photo id. The in-memory store and the development folder store both raise `BlobWritten`, which stands in for Azure's blob-created event.
- `IPhotoUploadedHandler` runs after a photo arrived. Thumbnails and metadata stripping (#69) plug in here for local development; in Azure the same work is triggered by the blob-created event.

Settings live under `Photos` (`UploadUrlLifetime`, `ReadUrlLifetime`, `MaxBytes`, `LocalFolder`). Development keeps files in `%TEMP%/dialed-dev-photos`; tests keep them in memory. The stand-in signs URLs with an HMAC key made at startup, so restarting the API also ends every URL it handed out (the queue simply asks for a new one).

## Blob Storage (Azure)

With `Storage__BlobEndpoint` set (`infra/main.bicep` sets it and `Storage__PhotosContainer` on the API), `AddDialedPhotos` uses Blob Storage instead of the stand-in. Without it the local stand-in stays: a folder in development (`Photos:LocalFolder`), memory in tests.

- **Account**: private container `photos`, shared keys and public access off (`infra/modules/storage.bicep`). The API reaches it with its user-assigned managed identity (`DefaultAzureCredential`, `AZURE_CLIENT_ID`), which has *Storage Blob Data Contributor* and *Storage Blob Delegator* on the account.
- **Blob names** are the store paths: `photos/{household}/{photo}.jpg` for originals and `thumbnails/{household}/{photo}-{size}.jpg`, both in the `photos` container.
- **URLs** (`AzureBlobPhotoUrls`): user delegation SAS, signed with a key from `GetUserDelegationKey` that is cached for a day (`CachedUserDelegationKeys`; a new one is fetched when the cached key would expire before the URL). Each SAS is for one blob (`sr=b`), HTTPS only, starts 5 minutes in the past for clock skew and expires after `UploadUrlLifetime` (10 min) or `ReadUrlLifetime` (30 min). Uploads may only create and write (`sp=cw`) and need `x-ms-blob-type: BlockBlob`, which the API returns as a header; reads may only read (`sp=r`) and are served as `image/jpeg` with `Cache-Control: private, max-age=1800`.
- **CORS** on the Blob service allows only the Static Web App origin, methods `GET, PUT, HEAD, OPTIONS`, headers `content-type` and `x-ms-*`. Storage CORS has no partial wildcards, so PR preview origins cannot be listed without allowing every origin: photo uploads and reads do not work on previews.
- **Store** (`AzureBlobPhotoStore : IPhotoBlobStore`) on the same container, for the processing and server-side readers such as label scans by photo id.
- **After an upload** (#69): the app uploads straight to the container, so the API does not see the upload. `BlobStoragePhotoWatcher` lists the container every `Storage:PollInterval` (15 s) and raises `BlobWritten` for each original that has no thumbnails yet, or that was written again more than a minute after its thumbnails (a new upload; the stripped original written by the processing itself lands seconds after the thumbnails). Each version (ETag) is raised once per process; after a restart the listing catches up. `InMemoryPhotoTrigger` then runs `PhotoProcessingHandler` as before: strips metadata, writes the 256 and 768 px thumbnails, sets `thumbPath`, retries and parks poison events. Originals larger than `MaxBytes` are deleted, since a SAS cannot limit size.
- **Tests**: `AzureBlobPhotoStorageTests` (SAS scope and expiry, key caching, the watcher with a fake listing, the wiring) and `AzuriteBlobPhotoStoreTests` (the store and the watcher with real processing against Azurite, in CI's `azurite` job; locally set `AZURITE_CONNECTION_STRING`). Azurite uses account keys, so the delegation SAS itself is checked in dev.

### Follow-up: the blob-triggered Function

The watcher is fine for the beta (one listing per interval per API replica; the handler is idempotent, so two replicas only repeat work). At more photos it should move to the Functions app:

1. A blob trigger on `photos/photos/{household}/{name}.jpg` with an Event Grid source (`BlobCreated`), the Functions identity already has the storage roles. It calls the same `PhotoProcessingHandler` with `AzureBlobPhotoStore` and an `IPhotoRecords` on Cosmos DB; the runtime gives the retries and the poison queue.
2. The Function also checks each new blob (JPEG magic bytes, at most `MaxBytes`) and deletes anything else.
3. Then the API drops `BlobStoragePhotoWatcher` and `InMemoryPhotoTrigger` in Azure.
4. **Deletion**: deleting a photo record, a household or an account must also delete the blobs (Function job, ADR-0008).

The app needs no change: it already sends whatever method and headers the API returns, to whatever URL it returns, without the API token.

## Offline label scans (#83)

A label scan taken without a connection waits instead of failing.

1. **Scan** (offline): the camera still works. The scan fails as "offline" and offers **Save the photo and read it when I am online**. That saves the photo like any other (`PhotoService.AddAsync`, so the upload queue sends it) and adds a pending scan to `PendingScanQueue`.
2. **Reading**: `POST /ai/label-scans/{photoId}` takes only the id. The API checks the photo's record is in the caller's household, reads the file through `IPhotoBlobStore` and runs the same quota check, extraction and telemetry as the direct scan. It answers 404 (`photo-unknown` until the record has synced, `photo-not-uploaded` until the file has arrived) without counting against the quota.
3. **The queue** (`Dialed.Client.Scan.PendingScanQueue`, collection `scans` in the local store) reads one scan at a time when something may have changed: a scan was added, the network came back, the app returned to the foreground, or a photo finished uploading. Offline, signed out, or the photo not being on the server yet is never a failure and the scan keeps waiting. A used-up quota or AI switched off marks it failed with that reason (a **Try again** button restarts it); three server errors in a row do the same. The draft is kept with the scan, across restarts.
4. **Review**: Scan lists **Scans to review**; **Review** opens the existing review form with the kept draft and the photo that is already saved. A banner (`PendingScansNote`) shows "Scans to review: N" as a link, or a quiet "Label scans waiting for a connection: N".

