# Bean and roaster catalog (#105)

Suggestions from a shared catalog of roasters and beans, **built from what people choose to share publicly** (items with visibility `Public`, see `public-profiles.md`). Nothing private ever enters it and there is no hand-made seed list.

## What goes in

- Only beans and roaster vendors with visibility `Public`, owned by a person whose public profile is on, not suspended, and who still exists (`CatalogSource`). Un-sharing, switching the profile off, suspension and account deletion remove the contribution **on the next build**.
- A roaster that a public bean names counts as a contribution too (name, country, city, website of that vendor), even when the vendor itself is not public: the person shared the coffee and who roasted it. Shops (vendors that are not roasters) never count.
- **De-duplication** (`CatalogBuilder`, pure Domain code): roasters group by `LabelMatcher.RoasterKey` (so "Friedhats Coffee Roasters" and "Friedhats" are one) and by near matches (a small typo); beans group by roaster, name and country. A name that only *starts like* another ("Guji" and "Guji Natural") is another coffee, numbers matter ("Lot 12" and "Lot 13"), and a bean without a country joins a coffee only when exactly one could be meant.
- **Quality gate**: an entry appears only when at least **2 different people** made the same one public (`Catalog:MinContributors`, never below 2 whatever the setting). One person's typo or junk never becomes catalog data, and with 2 an entry cannot be traced to one person's item. Do not lower the minimum without revisiting the privacy page.
- **Entry data**: the most common value among contributors for each field (a tie goes to the alphabetically first, so rebuilds are stable); varieties and tasting notes keep only terms at least that many people used (at most 8); roast level is the most common one; and the number of people.
- The result (`BeanCatalog`: `CatalogRoaster`, `CatalogBean`) is not a `SyncEntity`, never syncs to devices, is stored apart from user data (`ICatalogStore`, in memory now) and holds **no user ids, household ids or links back to contributors**. Entry ids are hashes of normalised names.

## When it is built

`CatalogRebuildWorker` (a hosted service) rebuilds on start and every `Catalog:RebuildHours` (default 6); `POST /admin/catalog/rebuild` does it on demand. In Azure a timer-triggered Function replaces the worker, and the rules store moves to Cosmos DB (filed as a child issue). `CatalogSource` is the only code that reads across households (`ISyncStore.ListPublicAsync`) and it filters by the author's profile itself.

## API

| Endpoint | Who | What |
| --- | --- | --- |
| `GET /catalog/roasters?q=` | signed in | roasters by name; at least 2 characters |
| `GET /catalog/beans?q=&roaster=` | signed in | beans by name, narrowed to a roaster; a roaster alone lists its beans |
| `POST /admin/catalog/rebuild` | owner of the service | rebuild now |
| `GET /admin/catalog/entries?take=` | owner | entries with the fewest contributors first (with the keys the rules are made of) |
| `POST /admin/catalog/entries/{id}/hide` | owner | hide an entry |
| `POST /admin/catalog/merge` `{fromId, intoId}` | owner | merge two entries of the same kind |
| `GET /admin/catalog/hidden`, `POST /admin/catalog/entries/{id}/unhide`, `GET /admin/catalog/rules`, `POST /admin/catalog/unmerge` | owner | see what was hidden or merged and take it back |

Results are ranked by how well the name matches, then by popularity, at most 20, rate limited like the friend endpoints (`Protection:FriendsRequestsPerMinute`). The admin endpoints need the `X-Admin-Key` header (`Beta:AdminKey`); without a configured key they do not exist.

Hide and merge are kept as **rules on the entries' keys** (normalised names), not on ids, so they survive rebuilds and new contributors. Hidden entries leave the entry list but are kept apart so they can be brought back: `GET /admin/catalog/hidden` lists them (with their keys), `POST /admin/catalog/entries/{id}/unhide` restores one (it shows again if it still has enough people), `GET /admin/catalog/rules` lists the hidden keys and merge pairs, and `POST /admin/catalog/unmerge` `{keyA, keyB}` takes a merge back.

## In the app

- **Bean form** (new beans only): typing a name suggests catalog beans (a moment after the last key, at least 2 letters, online only). **Use this** *copies* the entry into the form: name, origin, process, varieties, roast level, tasting notes, and the roaster (the one I already have, or a new vendor made from its name). Nothing is linked; everything stays editable.
- **Roaster picker**: catalog roasters I do not have yet appear below my own; choosing one copies it into my vendors with its country, city and website.
- **Label scan**: when none of my beans matches the label, up to 3 catalog candidates are offered; **Use this** fills the origin fields (the label stays the truth for name and roaster) and the saved bean is a copy.
- **Barcode scan** (#104): catalog entries have no barcodes, so an unknown code leads to *Add as a new bean*, whose form has the typeahead.
- Online only: offline, signed out or on any error the lists are simply empty and nothing breaks (`ICatalogApi` never throws).

## Testing

`CatalogBuilderTests` (Domain: gate, grouping with typos, accents and suffixes, voting, terms, rules, no contributor in the output), `CatalogTests` (API: only public items of public profiles, drop-outs on the next build, no ids in responses, ranking and limits, rate limit, threshold, moderation), `CatalogUiTests`, accessibility audit of `CatalogSuggestions`. Real device: the "Catalog" list in `iphone-checks.md`.
