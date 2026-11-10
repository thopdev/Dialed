# Measuring suggestion quality per prompt version (#89)

Every dial-in suggestion that a person applies or dismisses is saved as a `DialInFeedback` record (`source`, `promptVersion`, `outcome`) and synced. When the next brew is made from an applied suggestion, the record also gets that brew's id and rating (`ratingBefore`, `ratingAfter`). The API turns pushed records into two metrics (meter `Dialed`, see [observability](observability.md)):

| Metric | Tags | Meaning |
| --- | --- | --- |
| `dialed.ai.suggestion.outcomes` | `prompt_version`, `source` (`rules`, `ai`), `outcome` (`applied`, `dismissed`) | Counted once when a record is first pushed |
| `dialed.ai.suggestion.rating_change` | `prompt_version`, `source` | Histogram: rating of the next brew minus the rating of the brew the advice was about, when both are rated (1 to 5) |

Tags never hold personal data. A prompt version that is not a short name (letters, digits, `-`, `.`, `_`, at most 40) is folded into `other`.

## Queries (Application Insights, once Azure exists, #25)

Metric and column names depend on the exporter; adjust them when the first data arrives. This was written against the OpenTelemetry Azure Monitor exporter and has not been run against real data.

Acceptance rate per prompt version:

```kusto
customMetrics
| where name == "dialed.ai.suggestion.outcomes"
| extend version = tostring(customDimensions.prompt_version), outcome = tostring(customDimensions.outcome)
| summarize applied = sumif(valueSum, outcome == "applied"), dismissed = sumif(valueSum, outcome == "dismissed") by version
| extend acceptanceRate = round(todouble(applied) / (applied + dismissed), 3)
| order by version asc
```

Rating change of the next brew after an applied suggestion, per prompt version:

```kusto
customMetrics
| where name == "dialed.ai.suggestion.rating_change"
| extend version = tostring(customDimensions.prompt_version)
| summarize brews = sum(valueCount), averageChange = round(sum(valueSum) / sum(valueCount), 2) by version
| order by version asc
```

Read the rating change with care: only brews that were rated count, and the rating of a brew also depends on the beans and the day. Compare versions over many suggestions, not single ones.
