# Label scan API (#81)

`POST /ai/label-scans` (signed in; rate limited under the `ai` limit, `Protection:AiRequestsPerMinute`)

- Body: a JPEG photo (`Content-Type: image/jpeg`, at most 1 MB; the app resizes labels to about 2 megapixels first).
- Answer `200`: `{ draft, promptVersion, modelId }`. `draft` has the fields name, roaster, country, region, farm, process, varieties, roastLevel, tastingNotes, roastDate, weightG, each `{ value, confidence }` (confidence 0 to 1, value `null` when the label does not say).
- `415` wrong type, `400` empty, `413` too large, `503` when the model cannot be reached or answers something unusable (the app then offers manual entry).

## How it works
- `Dialed.AI.Labels`: `ILabelExtractor`; `ClaudeLabelExtractor` calls the Messages API with the label photo and **forced tool use** (`record_bean_label`, JSON schema in `LabelPrompt`), validates the answer (tool call present, confidence clamped, unknown fields ignored), retries once on 429/5xx, and times out after `Ai:TimeoutSeconds`.
- `FakeLabelExtractor` is used when `Ai:Endpoint`, `Ai:ApiKey` and `Ai:ExtractionModel` are not all set (development, tests, before Azure exists).
- Prompt and schema carry `LabelPrompt.Version` (`label-v1`); change it with every change, so quality can be compared per version (#89).
- Tokens and latency go to the `dialed.ai.tokens` and `dialed.ai.latency` metrics (tag `operation=label`).

## Not done yet (needs Azure or later stories)
- Reading the photo by id from Blob Storage (#68, SAS upload queue); the endpoint takes the bytes for now.
- Storing a `LabelScan` with prompt version and model id (comes with the app side, #83).
- Per-user daily quota, cost estimate and global kill switch (#88).
- Real model validation against Claude in Foundry (#79) and the golden test set (#84). Neither can be done without the Azure resource.
