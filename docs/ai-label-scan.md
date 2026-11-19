# Label scan API (#81)

`POST /ai/label-scans` (signed in; rate limited under the `ai` limit, `Protection:AiRequestsPerMinute`)

- Body: a JPEG photo (`Content-Type: image/jpeg`, at most 1 MB; the app resizes labels to about 2 megapixels first).
- Answer `200`: `{ draft, promptVersion, modelId }`. `draft` has the fields name, roaster, country, region, farm, process, varieties, roastLevel, tastingNotes, roastDate, weightG, each `{ value, confidence }` (confidence 0 to 1, value `null` when the label does not say).
- `415` wrong type, `400` empty, `413` too large, `503` when the model cannot be reached or answers something unusable (the app then offers manual entry).

## How it works
- `Dialed.AI.Labels`: `ILabelExtractor`; `ClaudeLabelExtractor` calls the Messages API with the label photo and the tool `record_bean_label` (JSON schema in `LabelPrompt`). The prompt asks for the tool with `tool_choice: auto`, because Claude Opus 5.5 and Sonnet 5.5 reject a forced tool choice. It validates the answer (tool call present, confidence clamped, unknown fields ignored; a refusal or a missing tool call fails the scan and the app offers manual entry), retries once on 429/5xx, and times out after `Ai:TimeoutSeconds`.
- `Ai:MaxTokens` (16000) is generous because thinking counts against it on current models; `Ai:Effort` (empty: the model default) can lower cost and latency once the golden set shows quality holds. On Anthropic's own API, Claude Opus 5.x, Fable 5.x and Sonnet 5.5 also get the server-side refusal fallback (`fallbacks: "default"`); Foundry does not support it, so there a refusal simply fails the scan.
- `FakeLabelExtractor` is used unless `Ai:Endpoint`, `Ai:ExtractionModel` and a way in are set: `Ai:ApiKey` (Anthropic's API, the label eval tool) or `Ai:UseManagedIdentity=true` (Microsoft Foundry: an Entra token for `Ai:TokenScope`, `https://ai.azure.com/.default`, from the API's managed identity).
- Prompt and schema carry `LabelPrompt.Version` (`label-v2`); change it with every change, so quality can be compared per version (#89).
- Tokens and latency go to the `dialed.ai.tokens` and `dialed.ai.latency` metrics (tag `operation=label`).

## Turning on the real reader in Azure (Claude in Microsoft Foundry)
Off by default. With `AI_ORGANIZATION_NAME` set, the deploy (`infra/modules/foundry.bicep`) creates a Foundry account `ai-dialed-<env>-<token>` in `swedencentral` (the only European region with Claude *Hosted on Azure*), deploys `claude-opus-5-5` version 2 (Hosted on Azure, Global Standard, capacity 25) and gives the API identity *Cognitive Services User* on it. The API calls `https://<account>.services.ai.azure.com/anthropic/v1/messages` with an Entra token from its managed identity. Keys are disabled on the account (`disableLocalAuth`), so there is no secret anywhere.

Requirements: a paid subscription with a pay-as-you-go billing method (Dialed's is `PayAsYouGo`; free, student, credit-only and CSP subscriptions cannot buy the offer). The first deployment **accepts Anthropic's offer in Azure Marketplace** with the details below; usage is billed through the Azure invoice at Anthropic's list prices (Claude Opus 5.5: $4 per million input tokens, $20 per million output tokens).

Owner steps, once per environment:
1. Register the providers (once per subscription):
   ```powershell
   az provider register --namespace Microsoft.CognitiveServices
   az provider register --namespace Microsoft.SaaS
   ```
2. Set the Marketplace details and deploy (dev: the repository variables, without `--env`):
   ```powershell
   gh variable set AI_ORGANIZATION_NAME --env prod --body "<your organization or name>"
   gh variable set AI_COUNTRY_CODE --env prod --body NL
   ```
   `AI_MODEL` (default `claude-opus-5-5`) picks another Claude model hosted on Azure, for example `claude-haiku-5-5`.
3. Prices for the cost metric: set `Ai__InputUsdPerMillionTokens` / `Ai__OutputUsdPerMillionTokens` to the model's prices (4 and 20) when the cost alert is set up.

To turn the reader off quickly, set `Ai__Enabled=false` (kill switch, below). Clearing `AI_ORGANIZATION_NAME` only stops the API from using it; the Foundry account stays until it is deleted.

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

Calls that fail validation (wrong type, empty, too large) are never counted; a call that fails because of the model is refunded. Counters are in the `aiquota` Cosmos DB container when Cosmos DB is configured (`CosmosAiQuotaStore`: one document per user, operation and UTC day, ETag concurrency, removed by TTL two days later), so a restart or a second instance does not reset them; without Cosmos DB they are in memory. The kill switch is checked first and never touches the store.

Azure side, when the subscription exists (#25): alert rules on `dialed.ai.cost` (daily total) and `dialed.ai.tokens`, and a way to flip `Ai:Enabled` (App Configuration or an environment variable on the Container App).
The app side (friendly message for 429/503 and the fallback) comes with the scan screen (#83).
