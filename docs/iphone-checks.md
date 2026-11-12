# Real-iPhone checks

Things only a person with a real iPhone (and sometimes two accounts) can check. They come from the PRs, collected here so one pass covers them. Tick them off; for each failure write down the iOS version and whether the app was installed or in a Safari tab. Do this with the installed Home Screen app unless it says otherwise.

Setup: one iPhone, ideally an iPad too, two accounts that follow each other (Alice and Carol) and one household with two members.

## Install and sign-in
- [ ] Add to Home Screen from Safari (steps in [beta-testing.md](beta-testing.md)); the app opens full screen with the icon and splash screen.
- [ ] Sign up with an invite code and a passkey (Face ID or Touch ID); the recovery codes are shown once.
- [ ] Sign in on a second device with the passkey. **Lost your passkey?** works with a recovery code.
- [ ] The app works under the enforced Content-Security-Policy: scan a label, take a photo, sign in with a passkey, use it for a minute. If something looks broken, open the Safari web inspector and look for "Refused to ..." messages (#231).

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
- [ ] In Safari (not installed): Settings > Notifications says to add Dialed to the Home Screen first and has no Allow button.
- [ ] Installed app (iOS 16.4 or later): **Allow notifications** shows the system prompt after the tap; allowing turns the switches on.
- [ ] Carol follows Alice's code: with the app **closed**, Alice gets a banner "New follow request"; tapping it opens People.
- [ ] Same with the app **open** on another screen: no banner interrupts, the request shows in People.
- [ ] Alice accepts: Carol gets "Request accepted".
- [ ] Someone joins Alice's household with an invite: Alice gets "New household member"; tapping it opens Settings at the household.
- [ ] Switching a type off stops it at once; **Turn off on this device** stops all of them; signing out does too.
- [ ] Denying the permission shows how to allow it in Settings; nothing breaks.
- [ ] Low stock and peak (#259): switch both on, set the low-stock threshold above a bag's weight; the notification arrives the next morning around 08:00 (or run the job against a dev API), says the bean, and tapping it opens Inventory showing only those bags.
- [ ] At most one low-stock and one peak notification per day, none at night; changing the threshold in Settings > Inventory changes what is announced.
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
- [ ] Settings > Notifications > **Peak** on: a bag whose rest window ends tomorrow gives "reaches its peak tomorrow" the day before (needs the push keys of #260 to deliver for real).

## Brew card
- [ ] Brew history > a brew of mine > **Share a picture of this brew**: the iOS share sheet opens with a PNG (Save Image, Messages and Instagram accept it), the card is readable and not cut off, and long bean names wrap.
- [ ] In the installed PWA and in Safari; in airplane mode the card still appears (nothing is fetched).

## Accessibility (#49)
- [ ] VoiceOver reads every control with a name, in light and dark mode: Home, Brew, Beans, Recipes, History, Settings, People, Scan.
- [ ] The Copy button reads the item's name; feed rows read as one sentence; the rating reads "4 of 5 stars".
- [ ] Large text sizes: the key screens do not clip.
