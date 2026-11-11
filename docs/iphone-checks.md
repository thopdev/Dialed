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

## Accessibility (#49)
- [ ] VoiceOver reads every control with a name, in light and dark mode: Home, Brew, Beans, Recipes, History, Settings, People, Scan.
- [ ] The Copy button reads the item's name; feed rows read as one sentence; the rating reads "4 of 5 stars".
- [ ] Large text sizes: the key screens do not clip.
