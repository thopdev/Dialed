# Maps, charts and cards

Everything here is offline and local: it is calculated from the person's own beans, bags and brews on the device (or from the shared items a profile loads), and nothing is fetched to draw it. Domain code is pure and tested; the UI components are plain SVG with Fluent colour variables, a text description and a legend or list that says the same in words.

| Feature | Where | Code |
| --- | --- | --- |
| Origin map (world, with a marker per country) | Statistics, a friend's profile | `OriginMap`, `CountryCatalog`, `WorldMapProjection` |
| Origin map zoomed on one country | Bean page | `OriginMap` with `Zoom`, `CountryShapes` |
| Tap a marker or an origin to filter Beans | Statistics to `beans?country=ET` | `BeanFilter.Country`, `BeanQuery.CountryKey` |
| Countries tried (passport) | Statistics | `OriginPassport`, `CountryCatalog.Producers` |
| Flavour wheel | Statistics, bean page | `FlavorWheel`, `FlavorWheelChart` |
| Brewing calendar with roast days | Statistics | `BrewCalendar`, `BrewCalendarChart` |
| Milestones and streaks | Statistics | `MilestoneSummary`, `MilestoneBadges` |
| Dial-in trace | Bean page | `DialInTrace`, `DialInChart` |
| Compare two brews of a bean | Brew history > a brew > Compare brews | `BrewHistory.CompareBrews`, `BrewHistory.SameBean`, `HistoryCompare` |
| Brew card (share a picture) | Brew history, own brews | `BrewCard`, `IImageRenderer` |
| Peak heads-up (push) | Settings > Notifications > Peak | `StockAlerts`, see [push-notifications.md](push-notifications.md) |

## Origin maps
- The origin country on a bean is free text. `CountryCatalog.Find` turns it into a place (about 70 countries; English and Dutch names, accents and case ignored, e.g. "Ethiopië", "Ivoorkust", "Hawaii"). Text that is not a known country is simply not shown; the Beans filter then uses the typed text as the key.
- `WorldMapProjection` is a plain equirectangular projection (longitude -180 to 180, latitude 75 to -58) onto a 1000 wide canvas. The outlines use the same numbers, so a marker lands where the land is.
- The outlines are Natural Earth (public domain): 1:110m land for the world view and simplified 1:50m country shapes for the zoom. They are bundled as C# constants (`WorldMapLand`, `CountryShapes`, about 320 KB of source; the whole `Dialed.UI` assembly is about 312 KB brotli-compressed on the wire), so there is no request, no tile server and nothing for the CSP or the privacy page to cover.
- **Adding a country**: add it to `CountryCatalog` (name, ISO code, a point, other names), then regenerate the shapes. `tools/map-data` does that: `cd tools/map-data && npm install && npm run build`, then run the tests (one checks that every catalog country has a shape). Countries that only drink coffee go in `NotGrowing` so the passport leaves them out. Countries made of far-apart pieces (US, France, Indonesia...) need a zoom window in `build.mjs`.

## Flavour wheel
`FlavorWheel` sorts free-text tasting notes into the eight branches of the SCA wheel (fruity, floral, sweet, nutty and cocoa, spices, roasted, green and herbal, sour and fermented) from a word list in English and Dutch, matching the whole note first and then its words, with plurals. A note it cannot place is left out: nothing is guessed. To teach it a word, add it to the matching `Add(...)` line in `FlavorWheel.Build` and to the theory in `FlavorWheelTests`. Colours are the theme's palette variables (soft tones in light mode, deep tones in dark mode); only variables the theme defines can be used.

## Calendar, milestones and dial-in trace
- `BrewCalendar` shows the last 15 weeks (Monday first), darker for more brews relative to the busiest day, with a dot on the roast dates of live bags.
- `MilestoneSummary` counts brews, origins, roasters and your longest run of days with a brew. Quiet by design: nothing expires or nags, and per track only the highest step reached is shown. Steps are in `MilestoneSummary.Steps`.
- `DialInTrace` plots the grind setting of one bean per method over time. A method needs at least three brews with a grind setting; the best rated brew is ringed (the latest among equals), and "dialed in" means the last three brews used about the same grind.

## Comparing brews
A brew's page lists the other brews of the same bean (newest first, up to 20, through the bag) and opens `history/compare/{first}/{second}`. `BrewHistory.CompareBrews` reuses `ParameterDelta`: the first brew's value sits in `Planned`, the second's in `Actual`, and `Change` is second minus first. The page shows method, dose, water, yield, grind, temperature and time, the rating and the four taste axes with how each moved, plus both brews' notes. A value only one brew recorded has no change; temperature changes are shown as a difference (no 32 degree offset when units are imperial). Rows that differ are highlighted like the planned-versus-actual table.

## Brew card
The share button on your own brew builds a 1080 by 1350 SVG (`BrewCard`, fixed colours because it leaves the app as an image, system fonts, all text escaped) and asks `IImageRenderer` for a PNG. On the web that draws the SVG as a data-URL image on a canvas (`svgToPng` in `platform.js`; the CSP already allows `img-src data:`), then `IFileExport` hands it to the share sheet or downloads it. If a browser cannot draw it the page says so. Check it on a real iPhone: see [iphone-checks.md](iphone-checks.md).

## Bean card
The share button in a bean's header builds the same kind of picture as the brew card (`BeanCard`, 1080 by 1350 SVG, fixed colours, escaped text) and saves it as `dialed-bean.png` through `IImageRenderer` and `IFileExport`. It shows the name, roaster, origin, rating stars, a few facts, the tasting notes and the bean's country zoomed on the offline map (`MapView.Zoom`, shared with the origin map). The map is clipped with a `<g clip-path>` wrapper: a clip on a nested `<svg>` with a viewBox is applied in the zoomed coordinate system and would hide the map. Stars are five dim stars with the lit ones drawn over them, so the spacing cannot drift.

## Bean journey
On the bean page, a bean with at least two dated steps shows a timeline (`BeanJourney.Build`, `BeanJourneyList`): bought, roasted, peak window starts and ends (same rest windows as the freshness rules, so a window still ahead is shown muted as "Upcoming"), first brew and, for a finished bag, the last brew. Grouped per bag, earliest bag first.

## Gear upkeep
- Gear can carry an interval to service it (`EquipmentItem.ServiceEveryBrews`, `ServiceEveryKg`, `ServiceEveryDays`; 7 days is a weekly clean) and the date it was last serviced (`ServicedOn`). `GearWear.Evaluate` counts what was used since then (a grinder through `Brew.GrinderId`, a brewer or machine through the recipe it was brewed with) and the days since, and compares each with its interval. Whichever is furthest along sets the state: OK, soon (90 percent) or due. Time counts from the service date, or from when the gear was added when it was never serviced.
- Where it shows: a chip on the Gear list, a meter and "Mark serviced" on the gear page, and a banner on Home when something is due.
- **Push**: with the "service due" switch on, the daily alert job sends "Eureka Mignon is due for service" once per service. See [push-notifications.md](push-notifications.md).

## Grind drift
`GrindDrift.Detect` looks at one bag's brews of the same method and grinder (set by the newest brew with a setting). With at least 3 brews over at least 7 days, a net move of at least one grinder step, and no step back the other way, the bag screen (`/brew/{bag}`) shows "Your grind is drifting finer/coarser" with where the trend points today (snapped to the grinder's scale). It is a hint: beans change as they age, so many people grind finer to keep the taste. The demo data's Kenya V60 brews step finer on purpose.

## Recipe from your best brews
`BestBrewRecipe.Build` takes a bean's brews rated 4 or more, picks the method with the most of them (at least 2), and makes a recipe from the middle of what they used: dose, water or yield, temperature, time, plus the grind setting, grinder and brewer used most. The bean page offers it as a card; one tap saves the recipe and opens it on the Recipes screen to edit.

## Tasting note suggestions
While typing tasting notes on the bean form, `FlavorWheel.Suggest` offers the known flavour words that start with what was typed (English and Dutch, shortest first, without notes already chosen). These are the same words the flavour wheel can place, so suggested notes always show up in the wheel. Teach it a word by adding it to `FlavorWheel.Build`.

## Taste profile and comparing beans
- `BeanTasteProfile` averages each taste axis (-2 to +2, 0 balanced) over a bean's brews that were not balanced, since a brew with no feedback looks the same as a balanced one. It needs two such brews and shows as bars on the bean page ("How it tends to taste"): sour/bitter, weak/strong, thin/heavy, flat/sweet.
- `BeanSummaries.Build` sums up a bean (brews, average rating, brews rated 4 or more, favourite method, last brewed, taste lean). The bean page's "Compare with another bean" opens `/beans/compare/{first}/{second}`, a table of two beans with the rows that differ highlighted.

## Water
Gear of kind Water (tap, filtered, a mineral recipe) can be chosen on a brew (`Brew.WaterId`, shown only when you have Water gear; "Brew again" keeps it). `WaterStats.Build` averages the rating per water, and the water's gear page says how its brews did and which other water scored higher.

## Roast age tip
`RoastAgeTip.Build` looks at a bean's brews rated 4 or more, works out the bag's age at each (frozen days not counted), and the bag screen says whether the bag in hand is inside, younger or older than that range. At least three such brews with a roast date are needed. It reports what happened, not why.

## Shopping list
`ShoppingList.Build` puts a bean on the Inventory screen's "To buy" list when everything left of it is an open bag below the low-stock threshold, or when it is all gone and your brews of it average 4 stars or more. Beans with a sealed, frozen or healthy open bag are left off. "Share the list" uses the share sheet (`IShareService`) or shows the text to copy.

## Brew timer voice
On guided timers (not the espresso shot timer) the switch "Say the steps out loud" speaks each step with its target ("Pour, up to 150 grams", said in grams or ounces) and "Done", in the app language, through the `ISpeechCue` platform interface (web: speech synthesis, `speak` in `platform.js`). It is off by default and remembered in the profile (`UserProfile.TimerVoice`). Independent of it, the last three seconds before each step tick once each. Real-device checks are in [iphone-checks.md](iphone-checks.md).

## Brew timer controls and the bag screen
- **Controls like a music player**: round icon buttons pinned to the bottom of the timer: a big play/pause in the middle (play starts, pause freezes the clock without counting the pause, play resumes), reset, and finish-and-log (guided timers; the shot timer's big button is a stop that finishes). Big clock on the left hides the header, step list and voice switch and enlarges the clock. Every button has an accessible name and tooltip; the ids (`#timer-start`, `#timer-pause`, `#timer-finish`, `#timer-reset`, `#timer-focus`) are what tests use.
- **Add now**: beside each target the timer says how much water to add in this step (the target minus the previous target).
- **Bag screen** (`/brew/{bag}`): the roast age tip and the grind drift note share one collapsed "Tips for this bag" row, and "Brew again" has a timer button when the last brew used a recipe with steps, so the usual recipe is one tap from the guided timer.

## Pour routines and the pour figure
- `pour-routines.json` (embedded in `Dialed.Domain`, `DefaultRecipeCatalog.Routines`) holds well-known routines: 4:6 method, Hoffmann ultimate V60, Rao single pour, inverted AeroPress and a notes-only French press. Unlike the default recipes there may be several per method. They show on the "Starting points" page (`recipes/templates`) with a figure per pour and copy to your recipes with "Use as starting point". To add one, add it to the JSON (steps need the method to use steps; the French press has none) and run the catalog tests.
- `RecipeStep.Pattern` (`PourPattern`: centre, spiral, circles, pulse) is optional and only drawn; the recipe form has a "Pour pattern" choice for bloom and pour steps and keeps it when a routine is edited.
- `PourFigure` is an SVG of the brewer (a cone, or a straight-sided press for AeroPress and French press) with the water level and the pour. On the timer the level rises to the step's cumulative target and the pattern animates while a bloom or pour runs (a falling stream for centre and pulse, a dot circling a track for spiral and circles); it stops when paused. All motion is CSS and switches off for reduced motion. Elsewhere it is a still thumbnail. Each figure is one image with a description.

## Quick log, cost per cup and the Home timer button
- **Quick log** (#332): after a guided timer finishes, the log form opens in a short mode with just the rating and taste chips (`#quick-log`, `#quick-log-save`); "More details" (`#quick-log-more`) opens the full form. The brew is saved with what the recipe and timer already knew.
- **Cost per cup** (#333): `CupCost` (`Dialed.Domain.Statistics`) divides a bag's price by its weight and multiplies by the dose. `ForBrew` is one brew, `AverageForBag` the average over a bag's brews, `PerMonth` feeds a statistics tile. Brews of bags without a price or weight are left out. It shows on the brew detail, the bag screen and Statistics.
- **Home timer button** (#334): the "Brew again" rows on Home have a round timer button that starts the guided timer for the last recipe.

## Dial-in coach
`DialInCoach.Build` (`Dialed.Domain.DialIn`) reads the logged brews of one bag for the method brewed last: the number of tries, the best rating, and the stage. A bag is *dialled in* when the latest brew is rated 4 or more and tastes balanced. The Brew screen of a bag shows a coach card (`#brew-coach`) while it is still being dialled in, with the next step from `DialInAdvisor` and a button that opens the log form with that change applied (`#brew-coach-apply`). After at least two tries the card says "Dialled in" once it gets there. No new storage; it follows the brews.

## Equipment shelf and favourite settings
Equipment has a List and a Shelf view (`#gear-view-list`, `#gear-view-shelf`). The shelf shows a tile per item with its first photo or a kind icon and the service chip from the upkeep rules. A grinder's detail lists its favourite settings (`#gear-favorites`): `FavoriteGrinds.For` takes, per bean and method, the setting of the best-rated brew (4 or more, newest wins ties) made with that grinder.

## Backup and restore
Settings has "Save a backup" and a restore picker (`BackupPanel`, `BackupService` in `Dialed.Client.Backup`). The file (`dialed-backup-<date>.json`, `format: "dialed-backup"`, `version: 1`) holds vendors, beans, bags, gear, recipes and brews built from what the device has, so it works offline and signed out. A restore only adds records whose id is not here yet, restored in dependency order, and never changes or removes anything. Photos are not included, so restored records come back without them. The server's "Export my data" file is accepted too. To add a kind of record, add it to `Tables` and the two switches in `BackupService` and a test.

