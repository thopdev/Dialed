# Dial-in assistant (#85, #86)

After a brew that did not taste right, the app says what to try next. The offline rules answer at once (#85); **Ask the assistant** refines that with the last brews of the bag, the bean and the grinder (#86).

## In the app

- **Where:** on a brew's detail page (History), and on the bag page right after logging a brew with a rating of 3 or lower or a taste that is off (`DialInHistory.NeedsAdvice`; the brew page opens as `brew/{bag}?logged={brew}`).
- **Rules first:** `DialInAdvisor` gives one or two changes with their reason, no network.
- **Ask the assistant:** a button under the rules (not automatic, so viewing old brews does not use the daily quota). The answer shows one or two changes as from → to and a short why. When the assistant cannot answer (signed out, offline, quota used, switched off, failed) the card says so and the rules advice stays.
- **Apply to next brew:** opens the brew form for the same bag, prefilled from the brew with the change applied (`?apply=primary|secondary` for the rules, `?apply=ai&kind=…&to=…` for the assistant).
- **Recorded:** applying or dismissing ("Not useful") saves a `DialInFeedback` with `Source` (`rules` or `ai`), the `PromptVersion`, the rating before and, once the next brew is saved, the brew and rating after (#89).

## Endpoint

`POST /ai/dial-in` (signed in) takes a `DialInContext` (the brew, up to 5 earlier brews of the bag and method, newest first, origin, process, roast, grinder name and range) and returns a `DialInRefinement`: one or two changes as `kind`, `from`, `to`, a short `why` in English, an optional `reasonCode` the app words in the user's language, and the `promptVersion` and `modelId` that produced it. The app calls it through `IDialInAssistant` (`Dialed.Client.DialIn`).

- **Rules until Foundry:** `IDialInRefiner` is `RulesDialInRefiner` (version `rules-1+recent`, model `rules`): deterministic, the offline rules plus one step the device does not take: when the latest earlier brew already made the first change without a better rating (`DialInHistory.AlreadyTried`), it leads with the other change (`reasonCode` `AlreadyTried`). The Claude call is not wired until Foundry is set up and its answers are checked against real brews (ADR-0006). `DialInPrompt` already holds the versioned system prompt (`dialin-v1`), the tool schema and the message builder, with tests. A model answer has no `reasonCode`, so its `why` is shown as is.
- **Kill switch and quota:** `Ai:Enabled=false` answers 503 `ai-disabled`; `Ai:SuggestionsPerDay` (default 50) answers 429 `ai-quota` with `Retry-After`. A failed model call answers 503 and is not counted.
- **Privacy:** only numbers about the brews go out; the grinder name is shortened and stripped of line breaks. No user ids, notes or photos.
- **Telemetry:** tokens, latency and estimated cost under operation `dialin`.

## When Foundry is ready

Add a `ClaudeDialInRefiner` (like `ClaudeLabelExtractor`) that sends `DialInPrompt.System`, the tool schema and `DialInPrompt.UserMessage`, validates the answer (known kinds, within the grinder range, at most two changes) and throws `DialInRefinementException` on anything else; register it when `Ai` is configured. The model should answer `why` in the user's language, so the request will need the app language.
