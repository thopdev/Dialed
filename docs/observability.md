# Observability

## Metrics (meter `Dialed`, #90)

| Instrument | Tags | Meaning |
| --- | --- | --- |
| `dialed.sync.pulls` | `table` | Pull requests answered |
| `dialed.sync.pushes` | `table`, `outcome` (stored, conflict, forbidden, invalid) | Pushed records |
| `dialed.sync.conflicts` | `table` | Pushes that lost to a newer server version |
| `dialed.ai.tokens` | `direction` | AI tokens used (recorded by the AI stories) |
| `dialed.ai.latency` | none | AI request latency in ms |

Tags never hold personal data. Standard ASP.NET Core, HTTP client and runtime metrics come from OpenTelemetry instrumentation. Everything leaves through OTLP when `OTEL_EXPORTER_OTLP_ENDPOINT` is set (Aspire locally).

## Correlation id

Every API answer has `X-Correlation-Id` (the request's trace id). Error bodies (ProblemDetails, rate limit answers, unexpected errors) repeat it as `correlationId`, and every log line written while handling the request has it as scope. A tester quotes the id; search it as the operation id in Application Insights.

## To do when Azure exists (#25)

- Export to Application Insights (Azure Monitor exporter or an OTLP route) for the API and the Functions.
- Alerts: API 5xx rate above a threshold over 5 minutes; any Function failure; sync conflict rate spike.
