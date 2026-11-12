# UI working notes

Things learned while building the warm look, so the next session does not have to rediscover them. The look itself is in [look-and-feel.md](look-and-feel.md).

## Owner preferences (design)
- Give options (multiple choice) before big visual changes; mock-ups as HTML screenshots at iPhone width are welcome.
- Brown is the default colour. Gold or other accent fills only for actions (buttons, selected chips). A cream palette and gold icon chips were tried and reverted, so do not add decorative gold, and no backgrounds behind icons.
- Roast is shown as a lighter or darker brown, never yellow, orange or red.
- Cups instead of stars or smileys. Latte art only on milk drinks (rosetta, tulip, heart, dot); filter and espresso get a plain cup with five stars.
- Lists: one cup in rows, five cups in forms. Empty states have their own animated drawing per screen.
- Dates in History: a big serif day on a white rounded bar with the page margins, row hairlines kept.
- Fix merge conflicts in open PRs without being asked.

## Look at the real screen
- Servers: `dotnet run --project src/Dialed.Api --launch-profile https --no-build` with `Seed__Enabled=true` (port 7100) and `dotnet run --project src/Dialed.Web --launch-profile https --no-build` (port 7200). The API stops when the machine restarts: start it again before a screenshot run, or every login fails.
- Log in as alice with a POST to `https://localhost:7100/auth/dev` and store the answer in `localStorage['dialed.session.v1']`; carol has an empty account (empty states).
- Playwright is pre-installed (`require('playwright')`): launch with `--ignore-certificate-errors`, viewport 390x844 for iPhone and 820x1180 for iPad, `colorScheme: 'light' | 'dark'`, wait about 12 s after the first load, then `Blazor.navigateTo(route)` per screen. A row that looks dim in a screenshot is usually an animation frame, not a bug: check the computed colour before changing CSS.
- Contact sheets (PIL, 5 screens side by side) find regressions after a CSS merge much faster than single screens. Do one in light and one in dark, plus iPad, after any palette or token change.
- UI PR media goes on the `pr-media` branch (one folder per PR, never merged), linked with `?raw=true`.

## Gotchas
- `.gitignore` has `Backup*/`: a folder with that name is silently not committed. `BackupService` lives in `src/Dialed.Client/Data/` for that reason.
- Razor reserves `<text>`: build SVG text with a `MarkupString` helper. An enum cannot be declared after `@code` in a `.razor` file. `aria-pressed` with a bool renders "True": pass explicit "true"/"false" strings.
- The page background must be applied on `.dialed-shell` (a rule on `html` or `body` did not win). Fluent's colour variables live on `body`, so `--dialed-*` surface tokens are defined on `body` too.
- Fluent's own panels (the sidebar `FluentNav`, its items) paint their own background. Override with a more specific selector such as `nav.fluent-nav a.fluent-navitem:not(.active)`.
- Class names collide quietly across files (`dialed-flavor-slice` existed for the flavour chart, so the picker uses `dialed-fw-*`). Search before adding a class.
- Dark mode: use Fluent variables or mix with `var(--colorNeutralBackground1)`. A colour mixed into the dark surface turns every level dark; for something that must differ by level in both modes, use fixed tokens (the roast gradients).
- CSS conflicts: the stylesheets are split per area now, so conflicts are rare. If one still happens in a file with several hunks, do not use a naive "keep both" script: rebuild the file from `main` plus the PR's own rules, then look at the screen.
- A merge loop over many branches: check for conflict markers only in source files (not `bin/`), and remember that a branch that auto-merged still needs a push.
- Flaky API test: `CatalogTests` (roaster count) failed once and passes alone; re-run once before digging.
- Real-device cloud limits: a dev tunnel cannot be started from the cloud session. The README has "Test on your iPhone or iPad" with the local commands.

## Open ideas
- Coffee passport stamps (country stamps with date and bean) plus a hideable streak and level badge on Home. Not built.
- Dutch check of the newest screens (cups, flavour wheel, taste scales, empty states, More and Settings).
- Home's open-bag thumbnails still use another component than the roast gradient thumbnails.
