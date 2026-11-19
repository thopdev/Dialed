# Label scan API (#81)

`POST /ai/label-scans` (signed in; rate limited under the `ai` limit, `Protection:AiRequestsPerMinute`)

- Body: a JPEG photo (`Content-Type: image/jpeg`, at most 1 MB; the app resizes labels to about 2 megapixels first).
- Answer `200`: `{ draft, promptVersion, modelId }`. `draft` has the fields name, roaster, country, region, farm, process, varieties, roastLevel, tastingNotes, roastDate, weightG, each `{ value, confidence }` (confidence 0 to 1, value `null` when the label does not say).
- `415` wrong type, `400` empty, `413` too large, `503` when the model cannot be reached or answers something unusable (the app then offers manual entry).

## How it works
- `Dialed.AI.Labels`: `ILabelExtractor`; `ClaudeLabelExtractor` calls the Messages API with the label photo and the tool `record_bean_label` (JSON schema in `LabelPrompt`). The prompt asks for the tool with `tool_choice: auto`, because Claude Opus 5.5 and Sonnet 5.5 reject a forced tool choice. It validates the answer (tool call present, confidence clamped, unknown fields ignored; a refusal or a missing tool call fails the scan and the app offers manual entry), retries once on 429/5xx, and times out after `Ai:TimeoutSeconds`.
- `Ai:MaxTokens` (16000) is generous because thinking counts against it on current models; `Ai:Effort` (empty: the model default) can lower cost and latency once the golden set shows quality holds. On Anthropic's own API, Claude Opus 5.x, Fable 5.x and Sonnet 5.5 also get the server-side refusal fallback (`fallbacks: "default"`).
- `FakeLabelExtractor` is used when `Ai:Endpoint`, `Ai:ApiKey` and `Ai:ExtractionModel` are not all set (development, tests, before Azure exists).
- Prompt and schema carry `LabelPrompt.Version` (`label-v2`); change it with every change, so quality can be compared per version (#89).
- Tokens and latency go to the `dialed.ai.tokens` and `dialed.ai.latency` metrics (tag `operation=label`).

## Turning on the real reader in Azure
Off by default: without `AI_EXTRACTION_MODEL` the API uses the fake. The key is a Key Vault secret named `ai-api-key` in the environment's own vault (`kv-dialed-<env>-<token>`); the Container App reads it with its managed identity (Key Vault reference), so it never appears in a variable, a workflow or the Bicep.

1. **Key (owner):** create an API key at console.anthropic.com (a separate workspace with a monthly spend limit is a good idea).
2. **Put it in Key Vault (owner, your own shell):** you need *Key Vault Secrets Officer* on the vault once, then store it without it landing in your shell history:
   ```powershell
   $vault = az keyvault list -g rg-dialed-prod --query [0].name -o tsv
   az role assignment create --role "Key Vault Secrets Officer" --assignee (az ad signed-in-user show --query id -o tsv) --scope (az keyvault show -n $vault --query id -o tsv)
   az keyvault secret set --vault-name $vault --name ai-api-key --value (Read-Host 'Anthropic API key') -o none
   ```
3. **Switch it on:** `gh variable set AI_EXTRACTION_MODEL --env prod --body claude-opus-5-5` (dev: the repository variable, without `--env`), then run the deploy. Set the variable only after step 2: a Key Vault reference to a missing secret stops the new revision from starting.
4. **Prices for the cost metric:** set `Ai__InputUsdPerMillionTokens` / `Ai__OutputUsdPerMillionTokens` to the model's prices (Claude Opus 5.5: 4 and 20) when the cost alert is set up.

`AI_ENDPOINT` (default `https://api.anthropic.com`) points it at a Foundry resource instead (#79). To turn the reader off again, clear `AI_EXTRACTION_MODEL` and deploy, or set `Ai__Enabled=false` (kill switch, below).

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
