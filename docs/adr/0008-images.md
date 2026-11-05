# ADR-0008: Images in Blob Storage, direct upload with SAS, thumbnails in Functions

**Status:** Accepted · 2026-11-05 (revised the same day for the PWA client)

## Context
Photos of labels, bags, roasters, brews and avatars. They can be taken offline and must not bloat the sync payloads or Cosmos DB.

## Decision
- The app takes or picks a photo in the browser, **resizes and re-encodes it to JPEG on a canvas** (max 2048 px; re-encoding drops EXIF/GPS) and stores it locally (OPFS/IndexedDB) until uploaded.
- Photos are stored in **Azure Blob Storage** (private container), path `photos/{householdId}/{photoId}.jpg`. CORS on the storage account allows only the app's origin.
- Only `Photo` **metadata** is synced; binaries go through an **upload queue** in `Dialed.Client`.
- The API issues short-lived, single-blob **user-delegation SAS** URLs for upload and read.
- A **Function** on blob-created events strips any remaining metadata (defence in depth) and writes thumbnails (256 px and 768 px).

## Consequences
- Cheap storage, no binary data through the API or the database.
- Upload queue must survive app restarts and retry with backoff; if iOS clears storage before upload, the photo is lost (the UI shows pending uploads).
- Deleting a household or account must also delete its blobs (Function job).

## Alternatives considered
- Images in Cosmos DB as base64: expensive RU and size limits.
- Upload through the API: simpler auth, but extra load and latency on the API.
