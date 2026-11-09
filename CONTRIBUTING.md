# Contributing to Dialed

## Branching
Trunk-based. `main` is always releasable. One short-lived branch and one pull request per story, branched from a fresh `main`:

- `feat/<issue>-<slug>`, `fix/<slug>`, `chore/<issue>-<slug>`, `docs/<issue>-<slug>`
- Put `Closes #<issue>` in the PR description. The PR template holds the Definition of Done.
- Keep PRs independent; do not stack them. If `main` moved, merge `main` into your branch and resolve conflicts (for `.resx` files keep both sides).
- The maintainer reviews and merges (squash). CI (`build-test`) must be green.

## Commits
[Conventional Commits](https://www.conventionalcommits.org/): `feat:`, `fix:`, `chore:`, `docs:`, `test:`, `refactor:`. Subject in the imperative, issue number in parentheses, e.g. `feat: log a brew (#64)`.

## Code rules
- `Dialed.UI` and `Dialed.Client` have no browser APIs; platform services are interfaces in `Dialed.Client.Platform` with the browser implementation in `Dialed.Web` (architecture tests enforce this).
- Every UI string goes into `Strings.resx` and `Strings.nl.resx`.
- Warnings are errors. Tests use xUnit v3 and Shouldly (bUnit for screens).
- No raw colours in our CSS: use the Fluent `--color*` variables (`docs/accessibility.md`).

## Labels
| Label | Meaning |
|---|---|
| `epic` / `story` / `spike` / `chore` / `bug` | Kind of issue: parent, user-facing work, time-boxed investigation, maintenance, defect |
| `area:app` `area:api` `area:functions` `area:infra` `area:sync` `area:auth` `area:ai` `area:data` `area:ci` `area:docs` | Part of the system |
| `size:S` `size:M` `size:L` `size:XL` | S ≈ half a day, M ≈ 1-2 days, L ≈ 3-5 days, XL = split it |
| `priority:P0` `P1` `P2` | Must have for the beta, should have, nice to have |
| `beta-feedback` | Came from a beta tester (`docs/beta-feedback.md`) |

## More
Architecture: `docs/architecture.md` · decisions: `docs/adr/` · plan and way of working: `docs/PLAN.md`.
