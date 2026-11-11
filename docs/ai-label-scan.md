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
- Real model validation against Claude in Foundry (#79) and the baseline run of the golden test set (#84, harness and synthetic starter set: [ai-label-golden-set.md](ai-label-golden-set.md)). Both need the Azure resource.

## Quotas, cost and kill switch (#88)
Configuration under `Ai` (reloadable, so the switch works without a deploy):

| Setting | Default | Meaning |
|---|---|---|
| `Ai:Enabled` | `true` | Global kill switch. `false`: every AI call answers `503` with `code: "ai-disabled"`; the app falls back to rules and manual entry. |
| `Ai:LabelScansPerDay` | 20 | Per user and UTC day. Over the limit: `429`, `Retry-After` (seconds to UTC midnight), `code: "ai-quota"` and a friendly message. |
| `Ai:SuggestionsPerDay` | 50 | Same, for dial-in suggestions (used when #86/#87 land). |
| `Ai:InputUsdPerMillionTokens` / `Ai:OutputUsdPerMillionTokens` | 1 / 5 | Prices used to estimate cost per call (`dialed.ai.cost`). Set them to the real prices of the deployed model. |

Calls that fail validation (wrong type, empty, too large) are never counted; a call that fails because of the model is refunded. Counters are in memory (reset on restart, like the recovery lockout, #197).

Azure side, when the subscription exists (#25): alert rules on `dialed.ai.cost` (daily total) and `dialed.ai.tokens`, and a way to flip `Ai:Enabled` (App Configuration or an environment variable on the Container App).
The app side (friendly message for 429/503 and the fallback) comes with the scan screen (#83).
