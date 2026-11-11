# Public profiles (#106)

Opt-in public profiles: share recipes and beans with everyone who uses Dialed, find people by name, report content. "Public" means **visible to any signed-in Dialed user**, not to the open web: there are no anonymous pages, so nothing is indexed by search engines. Not in this version: a public activity feed and "people to follow" suggestions (they need moderation tooling first).

## What a person sees and controls

- **Two opt-ins, both needed**: the *Public profile* switch (Settings > Profile, off by default) and, per bean or recipe, the visibility *Everyone on Dialed*. Turning the profile off hides everything from strangers at once and leaves each item's own setting alone (followers still see those items as "friends" items).
- **A stranger sees** a name, an avatar and the public beans and recipes. Never brews, bags, friends, email or follower counts.
- **Find people** (People > Find people): name search, at least 2 characters, at most 20 results, fuzzy (`FuzzyMatch`), rate limited. Blocked people (either direction), suspended people, the searcher and switched-off profiles never appear.
- **Follow** from the profile sends the normal request (`POST /friends/{userId}/follow`, only for open profiles); the owner accepts as usual. **Copy** works like for friends' items (#77) and the copy starts with the copier's own default visibility, never Public.
- **Report** on a profile, bean or recipe: a reason (spam, offensive, wrong, other) and an optional note. The reporter gets a plain confirmation and never a reply about the outcome.

## Rules on the server

- `Visibility.Public` (3) is allowed on beans, recipes and vendors only; a push of Public on any other table answers 400 (`VisibilityRules.PublicTables`).
- `VisibilityRules.CanRead` (tested for every level against every relationship): Public is readable by the author, household members, accepted followers and, through `Relationship.PublicViewer`, by strangers. `FriendService` gives a stranger `PublicViewer` only when the owner's profile is open (switched on and not suspended).
- A **suspended** person is hidden from everyone except themselves and their household (the profile, search, shared lists and items, including friends-level ones). Nothing is deleted. Restoring brings it all back.
- Reads are never cached on the server, so un-sharing, switching off and suspending apply on the next request.
- Account deletion removes the profile setting and every report by or about the person. The data export has `profile.publicProfile` and each item keeps its `visibility`.
- Server-only data: profile switches, suspensions and reports (in memory now, `IPublicProfileStore`; Cosmos DB with the other stores, #38). Nothing about them syncs to devices.

## Endpoints

| Endpoint | Who | What |
| --- | --- | --- |
| `GET`/`PUT /profile/public` | signed in | read or set the switch |
| `GET /people/search?q=` | signed in | find public profiles (friends rate limit) |
| `POST /friends/{userId}/follow` | signed in | request to follow an open public profile |
| `GET /friends/{userId}/profile`, `/shared/{table}`, `/shared/{table}/{id}` | signed in | as for friends; strangers get the public view; the profile has `relation` (`self`, `household`, `following`, `requested`, `none`) |
| `POST /reports` | signed in | report; 5 per minute per person (`Protection:ReportsPerMinute`) |
| `POST /admin/users/{id}/suspend`, `/restore`, `GET /admin/users/suspended`, `GET /admin/reports` | owner of the service | header `X-Admin-Key` (`Beta:AdminKey`); without a configured key they answer 404 |

## Moderating (for the owner of the service)

**The screen**: open `/admin` in the app (it is not in the navigation), type the admin key, and use the tabs *Reports* (suspend the owner of a report), *People* (who is suspended, restore, or suspend by id) and *Catalog* (see `catalog.md`). The key is kept in memory only: never stored on the device, forgotten on *Lock* or when the page closes, and the `/admin` endpoints are rate limited per address (`Protection:AdminRequestsPerMinute`, 20 a minute) so it cannot be guessed. The steps below do the same with `curl`.

1. Reports arrive by email at `Reports:OwnerEmail` (through `IEmailSender`, the outbox stand-in until email exists, #199) and are listed at `GET /admin/reports` (newest first: reporter, owner, what, reason, note).
2. To hide someone: `curl -X POST -H "X-Admin-Key: <key>" https://<api>/admin/users/<ownerId>/suspend`. To bring them back: `/restore`.
3. Reports about a deleted person disappear with the account.

```bash
curl -H "X-Admin-Key: $KEY" https://<api>/admin/reports
```

## Testing

`PublicProfileTests` (API: visibility, search, reports, rate limit, suspension, deletion, export, follow), `VisibilityTests` (rules table), `PublicProfileUiTests`, `PublicProfileApiTests`, accessibility audits of the new components. Real device: the "Public profiles" list in `iphone-checks.md`.
