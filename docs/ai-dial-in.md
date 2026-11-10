# Dial-in assistant: the AI side (#86)

`POST /ai/dial-in` (signed in) takes a `DialInContext` (the brew, up to 5 earlier brews of the bag, origin, process, roast, grinder name and range) and returns a `DialInRefinement`: one or two changes as `kind`, `from`, `to`, a short `why`, and the `promptVersion` and `modelId` that produced it.

- **Off by default in effect:** `IDialInRefiner` is `RulesDialInRefiner`, which answers with the offline rules (`DialInAdvisor`, version `rules-1`). The Claude call is not wired until Foundry is set up and its answers are checked against real brews (ADR-0006). `DialInPrompt` already holds the versioned system prompt (`dialin-v1`), the tool schema and the message builder, with tests.
- **Kill switch and quota:** `Ai:Enabled=false` answers 503 `ai-disabled`; `Ai:SuggestionsPerDay` (default 50) answers 429 `ai-quota` with `Retry-After`. A failed model call answers 503 and is not counted.
- **Privacy:** only numbers about the brews go out; the grinder name is shortened and stripped of line breaks. No user ids, notes or photos.
- **Telemetry:** tokens, latency and estimated cost under operation `dialin`.

Not built yet: the app does not call this endpoint (it would show the same advice as the offline card). It is hooked up with the real model call, together with #87 and #89.
