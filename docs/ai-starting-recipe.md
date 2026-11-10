# Starting recipe for a new bean (#87)

On a bean's page, **Suggest a recipe** takes a method and a grinder and returns a recipe (dose, water or yield, grind, temperature, time, only what the method uses) with a reason per number. **Save as recipe** stores it as a normal recipe linked to the bean.

- **Endpoint:** `POST /ai/recipe-suggestions` (signed in) takes a `RecipeSuggestionContext`: method, the bean's origin, process, roast level and varieties, days since roast, the grinder's name and scale, and the person's best recipe for a similar bean. It returns a `RecipeSuggestionResult` with `reasons`, `promptVersion` and `modelId`.
- **Similar bean:** `SimilarBeanRecipes.Best` takes the best recipe of the same method: this bean first, then same country and process, same country, same process; best average rating first.
- **Rules only for now:** `IRecipeSuggester` is `RulesRecipeSuggester` (classic starting points by method category and roast, version `recipe-rules-1`), until the Claude call is verified against real brews (ADR-0006). The versioned prompt (`recipe-v1`), tool schema and message builder are written and tested.
- **Reasons:** the rules return a code (`Baseline`, `LightRoast`, `DarkRoast`, `SimilarBest`, `OnScale`, `Fresh`, `Ratio`) which the app words in English or Dutch; the model returns its own sentence, which is shown as is.
- **Stored with the prompt version:** the saved recipe has `suggestedBy` (for example `recipe-rules-1`).
- **Kill switch and quota:** the same as the dial-in assistant: `Ai:Enabled=false` answers 503 `ai-disabled`; the call counts against `Ai:SuggestionsPerDay` and answers 429 `ai-quota` when used up; a failed model call is not counted.
- **Privacy:** only attributes of the bean and numbers go out; the grinder name is shortened and stripped of line breaks. No user ids, notes or photos.
