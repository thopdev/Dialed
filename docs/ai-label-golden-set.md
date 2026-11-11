# Label extraction golden set (#84)

A fixed set of label photos with what each label says, scored field by field against any `ILabelExtractor`. Use it to see how well a model reads labels and to catch regressions when `LabelPrompt` changes.

| Part | Where |
| --- | --- |
| Golden set (JPEG + JSON per label) | `tests/Dialed.AI.Tests/golden/` |
| Scorer, loader, evaluator, report | `src/Dialed.AI/Labels/Evaluation/` (`FieldMatcher`, `GoldenSet`, `LabelEvaluator`, `EvaluationReport`) |
| Command line runner | `tools/Dialed.LabelEval` |
| Synthetic label generator | `tools/Dialed.LabelEval/synthetic/New-SyntheticLabels.ps1` |
| CI | `tests/Dialed.AI.Tests` (every PR, fake and test doubles) and `.github/workflows/label-eval.yml` (PR comment) |

## Run it

```bash
dotnet run --project tools/Dialed.LabelEval -- --extractor fake
```

Options: `--extractor auto|fake|claude` (auto: Claude when configured, else fake), `--golden DIR`, `--case ID` (repeatable), `--markdown FILE`, `--json FILE`, `--min-accuracy 0.8`. Exit code 0 is done, 1 is below `--min-accuracy`, 2 is wrong arguments or a broken golden set. It prints one line per label and then the Markdown summary: overall accuracy, accuracy per field, and a table of every field that was not right.

The fake always returns the same draft, so it scores about 28%. That is expected: the fake run checks the harness, not a model. One label (`synthetic-fake-match`) says exactly what the fake returns and must score 100%.

## How fields are scored

Each of the 11 fields of every label gets a score from 0 to 1:

| Outcome | Score | When |
| --- | --- | --- |
| match | 1 | Same value after normalisation (rules below) |
| both empty | 1 | The label does not have the field and the extraction leaves it out |
| partial | 0 to 1 | List field with some items right: F1 of the items |
| wrong | 0 | Both have a value and they differ |
| missed | 0 | On the label, left out by the extraction |
| unexpected | 0 | Not on the label, but the extraction has a value (a guess) |

- **Field accuracy** (the number the target applies to) is the mean score over all fields of all labels, so guessing a field that is not on the label costs as much as missing one that is. **On-label accuracy** only counts fields that are on the label.
- Text (name, roaster, country, region, farm): lower case, accents removed, `&` read as `and`, punctuation ignored, then a match when the similarity (1 minus edit distance over length) is at least 0.85, which forgives about one misread letter in seven. A few aliases (`USA`, `DRC`, `PNG`) are folded.
- Process: as text, after dropping words like "process" and folding synonyms (fully washed / wet / gewassen → washed; dry / sun dried → natural; pulped natural → honey; giling basah / semi washed → wet hulled). `Red Honey` and `Honey` are different.
- Roast level: one of light, medium-light, medium, medium-dark, dark; spaces or hyphens and the word "roast" do not matter.
- Varieties and tasting notes: split on `,` `;` `/` `&` `·` `•`, spaced dashes, new lines, "and" and "en"; order does not matter; each item matches as text.
- Roast date: the extraction may use any common format (`2026-10-03`, `03.10.2026`, `3 October 2026`, Dutch month names); it matches when one reading gives the expected day.
- Weight: the number of grams, units read (`g`, `kg`, `oz`, `lb`); within 2% (so `12 oz` matches 340).

The confidence the extractor gives is not scored but reported: mean confidence of values that were right and of values that were wrong. A useful model shows a clear gap, which tells us whether the scan screen can trust high-confidence fields.

## Add a case

1. Put the photo in `tests/Dialed.AI.Tests/golden/<id>.jpg`. JPEG, under 1 MB (the API limit), roughly what the app sends: about 2 megapixels, the bag filling most of the frame.
2. Next to it, `<id>.json`:

   ```json
   {
     "id": "<id>",
     "image": "<id>.jpg",
     "source": "photo",
     "license": "Own photo by <name>, 2026, may be used in this repository",
     "notes": "What makes this label hard.",
     "tags": ["rotated"],
     "expected": {
       "name": "…", "roaster": "…", "country": "…", "region": "…", "farm": "…", "process": "…",
       "varieties": "a, b", "roastLevel": "medium-light", "tastingNotes": "a, b, c",
       "roastDate": "2026-09-14", "weightG": "250"
     }
   }
   ```

   - Write what the **label prints**, in the label's language, the way the prompt asks for it. Leave a field out (or `null`) when the label does not say it: the extractor should then leave it out too.
   - `roastDate` is `yyyy-MM-dd`, `weightG` whole grams, `roastLevel` one of the five levels. Only set `roastLevel` when the label states it.
   - `license` is required. Only commit photos you took yourself or that are explicitly free to use; no photos copied from roaster websites or shops. Crop out faces, addresses and handwriting that is not part of the label.
3. Run `dotnet test --project tests/Dialed.AI.Tests`: `GoldenSetTests` checks the file name, the image, field names and formats, and lists every problem at once.

### Synthetic labels

The starter set is ten synthetic labels drawn by `New-SyntheticLabels.ps1` (Windows, System.Drawing, made-up roasters and farms, CC0). They cover clean labels, dates and weights in other formats, a Dutch label, a brew-guide dose and a best-before date that must not be read as weight and roast date, printed instructions aimed at the model (prompt injection), a sparse blend where every guess costs points, tilted and low-contrast text, and accented names. To change one, edit the script and the matching JSON, then run:

```bash
powershell -File tools/Dialed.LabelEval/synthetic/New-SyntheticLabels.ps1
```

Synthetic labels are clean and evenly lit, so they flatter a model. The issue asks for 30+ real photos: add real ones as described above (tag them `real`), and judge the target on those.

## Run it against Claude in Foundry (after #79 and #27)

The runner builds the same `ClaudeLabelExtractor` the API uses, from the same settings as environment variables:

```bash
export Ai__Endpoint="<Foundry endpoint; the extractor adds /v1/messages>"
export Ai__ApiKey="<key from Key Vault>"
export Ai__ExtractionModel="<deployment name>"
dotnet run --project tools/Dialed.LabelEval -- --extractor claude --min-accuracy 0.8 --markdown label-eval.md --json label-eval.json
```

Each label is one call, run one at a time; ten labels cost about as much as ten scans. Never commit the key.

In CI, add the repository secrets `AI_ENDPOINT`, `AI_API_KEY` and `AI_EXTRACTION_MODEL`. From then on `label-eval.yml` runs against Claude on every PR that touches `src/Dialed.AI/Labels/` (the prompt and schema), the golden set or the runner, posts the summary as one PR comment (updated on each push), uploads the JSON report, and fails when field accuracy is below 80%. Run it by hand from the Actions tab (`workflow_dispatch`, with a choice of extractor and target). Without the secrets, and on PRs from forks, it runs against the fake and never fails on accuracy.

### First baseline (to do once Azure exists)

1. Run against Claude with the current `LabelPrompt.Version` and save the JSON report as the baseline (attach it to #84).
2. Read the "not right" table: fix golden JSON that was wrong, and change the prompt only for real model mistakes, bumping `LabelPrompt.Version` with every change.
3. Compare field accuracy per prompt version in the PR comments; production quality per version is in [ai-quality.md](ai-quality.md).
