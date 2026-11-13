# Real-iPhone checks

Things only a person with a real iPhone (and sometimes two accounts) can check. They come from the PRs, collected here so one pass covers them. Tick them off; for each failure write down the iOS version and whether the app was installed or in a Safari tab. Do this with the installed Home Screen app unless it says otherwise.

Setup: one iPhone, ideally an iPad too, two accounts that follow each other (Alice and Carol) and one household with two members.

## Install and sign-in
- [ ] Add to Home Screen from Safari (steps in [beta-testing.md](beta-testing.md)); the app opens full screen with the icon and splash screen.
- [ ] Sign up with an invite code and a passkey (Face ID or Touch ID); the recovery codes are shown once. Find sign-in through the **Sign in** banner on Home while signed out, or through Settings (the **Sign in** button in the account card at the top).
- [ ] Sign in on a second device with the passkey. **Lost your passkey?** works with a recovery code.
- [ ] The app works under the enforced Content-Security-Policy: scan a label, take a photo, sign in with a passkey, use it for a minute. If something looks broken, open the Safari web inspector and look for "Refused to ..." messages (#231).

## Sign-in page (#361)
The `/signin` page offers passkey sign-in, sign-up with an invite code and **Lost your passkey?**. Start each check below signed out.
- [ ] Home: the **Sign in** banner (#home-signin) is shown while signed out, opens the sign-in page, and is gone after signing in.
- [ ] Onboarding: **I already have an account** (#onboarding-signin) opens the sign-in page; after signing in the first-run setup is skipped and Home opens.
- [ ] Scan, signed out: the **Sign in** button opens the sign-in page.
- [ ] People and Activity, signed out: the sign-in prompt opens the sign-in page.
- [ ] Settings, signed out: the account card at the top has a **Sign in** button that opens the sign-in page.
- [ ] On the sign-in page the passkey prompt appears after the tap (Face ID or Touch ID), and cancelling it shows a message without breaking the page.
- [ ] The back arrow on the sign-in page returns to Settings; in the installed app nothing opens in Safari.

## Settings overview (#362)
Settings is an overview: the account card first (signed in with your name, or a **Sign in** button), then Profile, Preferences, Household, Data and sync, Privacy, and Help and feedback. Each opens its own page (`settings/{group}`).
- [ ] Signed in: the account card opens Settings > Account with passkeys, notifications, data controls and backup.
- [ ] Every row opens its page, and the back arrow returns to the overview, also in the installed app and with the iOS swipe-back gesture.
- [ ] The overview fits at iPhone width with no sideways scrolling, in light and dark mode and at the largest text size.
- [ ] VoiceOver reads each row with its name and its description.
- [ ] Reopening a group page directly (for example from a notification) works after the app was closed.

## Espresso timer (#363)
- [ ] An espresso recipe, before starting: the chips **1 Grind**, **2 WDT** and **3 Shot** show with an animated drawing; **Next step** moves through them and the drawing and hint change with the step.
- [ ] The Start button fits on the first screen at iPhone width (no scrolling), also at the largest text size.
- [ ] **Start** jumps straight to the shot: the cup fills over the target time window and the crema shows once the shot is in range.
- [ ] Pause stops the drawing; resume continues it.
- [ ] iOS Settings > Accessibility > Motion > **Reduce Motion** on: the animation stops (the drawing stays still) and the timer still works.
- [ ] Light and dark mode: the drawing is readable.

## Loading screen (#365)
A cold start of the installed app: delete the Home Screen app, clear Safari website data for the site, add it again, then open it (a slow connection or Safari's Network Link Conditioner makes the stages easier to see). On a fast or cached start some stages are very short, so one pass may only show a few of them.
- [ ] A kettle blooms, then pours twice into a V60 that drips into a cup; the cup fills as the download progresses.
- [ ] The stages follow the real progress: the word says **Bloom**, then **Pour**, then **Dialed** at about 90%.
- [ ] **Dialed** shows before the app takes over, and the hand-over to the app has no flash (no white or blank frame).
- [ ] Light and dark mode: the screen and the words are readable and match the app background.
- [ ] iOS Settings > Accessibility > Motion > **Reduce Motion** on: the kettle does not sway; the cup still fills and the words still change.
- [ ] A start from the Home Screen when the app is already cached does not get stuck on the loading screen.

## Offline, storage and sync
- [ ] Airplane mode: log a brew and add a bean; back online, they sync and the "not yet synced" note goes away.
- [ ] A change that stays unsent for more than 5 minutes shows the "safe on this device" warning; it clears when the connection is back (#236).
- [ ] Private Browsing or very low storage: a failed save shows the storage warning, and the next good save clears it (#236).
- [ ] Sign in on a second device: "Loading your data from your account… N items so far" counts up and goes away (#236).
- [ ] Safari tab and the installed app open at the same time: only one syncs; the other says "Syncing in another tab" and catches up (iOS 15.4 or later) (#249).
- [ ] Close a tab in the middle of a sync: the other one takes over (#249).
- [ ] Leave a test device unused for 8 days: its data is still there, or it is restored from the account (#37).

## Photos
- [ ] Airplane mode: add a photo to a bean, close the app, reopen online: it uploads ("Waiting to upload", then gone) (#252).
- [ ] A photo whose file was cleared by iOS before it uploaded shows "Photo not on this device" and counts as failed (#252).
- [ ] An upload over a slow mobile connection finishes or retries without blocking the screen (#252).
- [ ] A household member's second device shows the uploaded photo, also after the app goes to the background and returns (#252).

## Label scan
- [ ] Take a label photo with the camera: the draft is prefilled and uncertain fields are marked.
- [ ] Airplane mode: take a label photo, **Save the photo and read it when I am online**. Turn the network on and wait: the photo uploads, then "Scans to review: 1" appears (#83).
- [ ] Close the app with a scan waiting and reopen online: it is still there and gets read (#83).

## Brewing
- [ ] Timer: the screen stays awake while a recipe runs (wake lock), and the step sound plays; check with the silent switch on and off.
- [ ] Timer voice: switch **Say the steps out loud** on, start a recipe: the first step is spoken ("Bloom, up to 45 grams") from the tap, then each step as it starts and "Done" at the end, in the app language (try Dutch). Check with the silent switch on and off, and that speech and the beeps do not talk over each other.
- [ ] Timer countdown: three quick ticks in the last three seconds before each step.
- [ ] After a sour or bitter brew, **Apply to next brew** opens the form with the change filled in (#237).
- [ ] **Suggest a recipe** on a bean page with a grinder that has a grind scale: the numbers have reasons; **Save as recipe** works and opens the recipe (#240).
- [ ] **Ask the assistant** after a 2-star sour shot answers; in airplane mode it says so and the built-in advice stays (#250).
- [ ] **Scan a barcode** (Home or Beans): the camera asks once, the live view shows, and a retail bag (EAN-13) is read within a few seconds in normal light; a bean you saved with that code opens, an unknown code offers the three choices (#104).
- [ ] Barcode scan after denying the camera: the message says how to allow it and typing the number still works; leaving the screen turns the camera light off.
- [ ] Barcode scan works when the app is installed to the Home Screen (standalone), not only in Safari.

## People
- [ ] Household invite: **Share invite** opens the share sheet with the code and a link; a 2-person invite works for two people and the third gets "not valid" (#234).
- [ ] Friends: Alice logs a brew shared with friends; it shows in Carol's Friends activity after Refresh (#233).
- [ ] Carol copies a shared bean from Alice's page; it syncs to Carol's other device and says "Copied from Alice" (#232).
- [ ] A shared household is deleted by its owner while a member's device is open: after the next sync its beans are gone from that device (#251).

## Notifications (#100)
Needs a deployed API with real VAPID keys (#260), or the API run locally and reachable from the phone (dev tunnel).
- [ ] In Safari (not installed): Settings > Account (the notifications part) says to add Dialed to the Home Screen first and has no Allow button.
- [ ] Installed app (iOS 16.4 or later): **Allow notifications** shows the system prompt after the tap; allowing turns the switches on.
- [ ] Carol follows Alice's code: with the app **closed**, Alice gets a banner "New follow request"; tapping it opens People.
- [ ] Same with the app **open** on another screen: no banner interrupts, the request shows in People.
- [ ] Alice accepts: Carol gets "Request accepted".
- [ ] Someone joins Alice's household with an invite: Alice gets "New household member"; tapping it opens Settings > Household (`settings/household`).
- [ ] Switching a type off stops it at once; **Turn off on this device** stops all of them; signing out does too.
- [ ] Denying the permission shows how to allow it in Settings; nothing breaks.
- [ ] Low stock and peak (#259): switch both on, set the low-stock threshold above a bag's weight; the notification arrives the next morning around 08:00 (or run the job against a dev API), says the bean, and tapping it opens Inventory showing only those bags.
- [ ] At most one low-stock and one peak notification per day, none at night; changing the threshold in Settings > Preferences changes what is announced.
## Public profiles (#106)
- [ ] Settings > Profile: the **Public profile** switch is off by default; switching it on and off works with one tap and shows the status.
- [ ] A bean's visibility picker offers **Everyone on Dialed** with the explanation under it; brews do not.
- [ ] People > **Find people**: the keyboard's Search key submits; results open the profile; at least two letters are needed.
- [ ] A stranger's profile shows name, beans and recipes only (no brews tab), **Follow** sends a request, **Report** opens the form and confirms.
- [ ] Switching the profile off hides it from search and from a stranger's phone at once.

## Catalog (#105)
- [ ] New bean: typing two letters of a coffee other people share shows **From the catalog** a moment later; **Use this** fills the form and the keyboard stays usable.
- [ ] The roaster field offers catalog roasters below my own; choosing one adds it to my roasters.
- [ ] Scan a label of a bean I do not have: catalog candidates appear under the match section and **Use this** fills the origin fields.
- [ ] In airplane mode none of this shows and adding a bean works as before.

## Round 2: offline cache, maps and insights
The production app only started caching itself in #283 (before that the service worker could not install), so these are the first real offline checks. Do the first block on a **fresh install**: delete the Home Screen app, clear Safari website data for the site, add it again.

- [ ] Open the installed app once online and use it for a minute. Turn on airplane mode, force-quit the app and open it again: it starts and shows your data (no blank page, no "no connection" browser page).
- [ ] Offline: Beans > **Scan barcode** opens the camera and reads a barcode (the reader is cached; it needs no network).
- [ ] After a new version is deployed, the **Update available** banner appears once and **Update** reloads into the new version.
- [ ] Beans > a bean with an origin: the **map is zoomed on the country** and readable at your text size; light and dark mode.
- [ ] Statistics: the **origins map**, **Countries tried** (flags show; "Still to try" opens and closes), **flavour wheel**, **brewing calendar** and **Milestones** all fit the screen width with no sideways scrolling, in light and dark mode, and at the largest text size.
- [ ] Statistics > tap an origin name (and a marker): Beans opens already filtered to that country, with **Filters (1)**; clearing the Country filter shows all beans again.
- [ ] A bean with three or more brews that have a grind setting shows the **Dial-in trace**; long-press a dot shows its details (or just check the text under the chart).
- [ ] A friend's profile (Beans tab) shows the origin map; a profile without origins shows none.
- [ ] VoiceOver reads the maps and charts as one description each ("Map with a marker for each origin: ...", "Grind setting over 8 brews, from 22 to 16") and the legends as lists.
- [ ] Settings > Account > Notifications > **Peak** on: a bag whose rest window ends tomorrow gives "reaches its peak tomorrow" the day before (needs the push keys of #260 to deliver for real).

## Brew card
- [ ] Brew history > a brew of mine > **Share a picture of this brew**: the iOS share sheet opens with a PNG (Save Image, Messages and Instagram accept it), the card is readable and not cut off, and long bean names wrap.
- [ ] In the installed PWA and in Safari; in airplane mode the card still appears (nothing is fetched).

## Bean card
- [ ] Beans > a bean with an origin country > **share** icon in the header: the iOS share sheet opens with a PNG; the country is zoomed on the map, the stars match the rating, a long bean name wraps and nothing overlaps the footer.
- [ ] Works in the installed PWA, in Safari and in airplane mode.

## Gear upkeep push
- [ ] Gear > a grinder with "or every (days)" set to 1 and serviced two days ago, Settings > Account > Notifications > **A piece of gear is due for service** on: the next morning around 08:00 one notification names the gear and tapping it opens Gear.
- [ ] Mark it serviced: no notification until it is due again; switching the switch off stops it.

## Accessibility (#49)
- [ ] VoiceOver reads every control with a name, in light and dark mode: Home, Brew, Beans, Recipes, History, Settings, People, Scan.
- [ ] The Copy button reads the item's name; feed rows read as one sentence; the rating reads "4 of 5 stars".
- [ ] Large text sizes: the key screens do not clip.
