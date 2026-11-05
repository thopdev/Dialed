# ADR-0008: Images in Blob Storage, direct upload with SAS, thumbnails in Functions

**Status:** Accepted · 2026-11-05

## Context
Photos of labels, bags, roasters, brews and avatars. They can be taken offline and must not bloat the sync payloads or Cosmos DB.

## Decision
- Photos are stored in **Azure Blob Storage** (private container), path `photos/{householdId}/{photoId}.jpg`.
- Only `Photo` **metadata** is synced via Datasync; binaries go through an **upload queue** in the app.
- The API issues short-lived, single-blob **user-delegation SAS** URLs for upload and read.
- A **Function** on blob-created events strips EXIF/GPS and writes thumbnails (256 px and 768 px).
- The app resizes before upload (max 2048 px) and caches images on disk.

## Consequences
- Cheap storage, no binary data through the API or the database.
- Upload queue must survive app restarts and retry with backoff.
- Deleting a household or account must also delete its blobs (Function job).

## Alternatives considered
- Images in Cosmos DB as base64: expensive RU and size limits.
- Upload through the API: simpler auth, but extra load and latency on the API.
