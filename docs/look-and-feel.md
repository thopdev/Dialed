# Look and feel: the warm roast style

How Dialed looks and where each piece lives. The rule from `CLAUDE.md` still holds: colours only from Fluent `--color*` variables, spacing and type from `tokens.css` (`--dialed-*`).

## Palette and surfaces
- **Brand-tinted surfaces.** `--dialed-surface` (page, set on `body`, applied on `.dialed-shell`) mixes the brand colour into Fluent's background, so it follows light and dark mode. Cards are plain Fluent surfaces. Icons on More and Settings have no background chip, and the friend code is a neutral label. Gold and brown fills are for actions (buttons, selected chips) and for roast colours that carry meaning (a bean's roast); a cream palette was tried and reverted, so do not add decorative gold.
- **Tokens** (`tokens.css`): `--dialed-shadow-card` and `--dialed-shadow-raised`, `--dialed-font-display` (serif for titles and big numbers), `--dialed-radius-xl`, roast colours `--dialed-roast-{light..dark}-{start,end,fg}`, flavour colours `--dialed-flavor-N`.
- **Roast colours are one brown scale.** Light roast is a pale latte brown, dark roast is espresso; no yellow, orange or red. Bean thumbnails mix the same brown into the surface (more brown = darker roast).
- **Roast mood.** A bean's screens take the colour of its roast: `.dialed-mood` / `.dialed-mood-<level>` sets `--dialed-mood-start`, `--dialed-mood-end` and `--dialed-mood-fg`. Used on the bag screen hero, the bean page card and the colour edge of bean rows.

## Components
| Component | What it is |
| --- | --- |
| `CupRating` | Five cups instead of stars, `ShowWord` adds the word. One filled cup (`RatingBadge`) in lists and headers. |
| `TasteScale` | Five steps from -2 to 2 with "Balanced" in the middle, per taste axis. |
| `FlavorPicker` | SVG flavour wheel with zoom into a category (classes `dialed-fw-*`, notes from `FlavorWheel.NotesOf`). Used in the bean form, brew form and the quick log notes. |
| `EmptyArt` / `EmptyArtKind` | Animated drawings for empty screens (cup, bag, recipe, people, search, cloud, kettle, chart). `EmptyState` picks one from its icon name; a screen can pass `Art`. Reduced motion turns the animation off. |
| `BeanIcon`, `StockRing` | Bean mark in roast colour, ring showing how much of a bag is left. |
| `PourFigure` | The timer drawing: brewer, cup that fills, crema and steam. |
| `BrewCard` | Share picture. Milk drinks get latte art in the foam (rosetta, tulip, heart, dot), filter and espresso get a plain cup with five stars. |

## Screens worth knowing
- **Home:** hero with the greeting, "Log brew" floating over its lower edge, bag rings, "Today's pick" card.
- **Beans and History:** rows as soft cards with the roast colour on the left edge. History groups by day with a big serif day on a rounded bar (`.dialed-day-title`).
- **Quick log:** summary card, cups, one taste scale per axis, optional notes behind `#quick-notes` (saved comma-separated in `Brew.Notes`).

## Stylesheets
`wwwroot/css` in `Dialed.UI`, loaded in this order from `index.html` (a test checks each file is linked): `tokens`, `shell`, `components` (core components, forms, timer), `screens` (Home, Beans, Recipes, History, Settings, More, People), `social`, `insights`, `warm-look`. Add new rules to the file of their area. When a merge conflicts in one of them, keep both sides.

## Checking a change
`/dev/gallery` shows every component in light and dark. Check iPhone width (about 390 px) and iPad, light and dark, and look at the screen: a rule can silently not apply (a missing brace is caught by `StylesheetTests`, a losing selector is not).
