# Beta testing: install guide and test plan (#99)

For the first testers: Thomas and household first, then a group of friends. Each tester gets an invite code ([beta-invites.md](beta-invites.md)), the install steps below and the test plan. Feedback goes through the app ([beta-feedback.md](beta-feedback.md)).

## Send to each tester
1. **Their invite code** (one code can serve several people: create it with `maxUses`).
2. **The install steps** below, as a message.
3. **The link to the app** and a note that it is a private beta and may lose data until the first public release.

## Install guide (iPhone and iPad)
Use **Safari**. Other browsers on iPhone cannot add an app to the Home Screen the same way.

1. Open the app link in Safari.
2. Tap the **Share** button (the square with an arrow, at the bottom of the screen on iPhone, at the top on iPad).
3. Scroll down and tap **Add to Home Screen**, then **Add**.
4. Open **Dialed** from the Home Screen icon (not from Safari). It opens full screen.
5. Sign up with the **invite code**, your name and a **passkey** (Face ID or Touch ID).
6. **Save the recovery codes** the app shows once: each works one time if you lose your passkeys.

The app also explains this itself under Settings (Add Dialed to your Home Screen).

Good to know: use the installed app, not the Safari tab, for the test. iOS can clear the data of a website you have not opened for about a week; an installed app is treated better, and everything you save is sent to your account when you are online.

## Test plan
Ask testers to do the scenarios in this order and to report what surprised them, not only what broke. Each is a few minutes. "Pass" means it works without help.

| # | Scenario | Steps | Pass when |
|---|---|---|---|
| 1 | Sign up and install | Install as above, sign up with the code, save the recovery codes | You are on Home, signed in, and the app opens full screen from the Home Screen |
| 2 | First bean and bag | Beans > add a bean (name, roaster, origin), add a bag with weight | The bag shows in Inventory with its weight |
| 3 | Scan a label | Beans > Scan label, photograph a bag, check the draft, save | The bean and bag appear, with the roaster matched if you already had it; uncertain fields were marked |
| 4 | Brew and rate | Brew > pick the bag > log a brew with dose, yield and time, rate it and say what tasted off | It is in History; the bag's weight went down by the dose; the "what to try next" card shows |
| 5 | Timer and recipe | Recipes > open a recipe > start the timer, follow the steps | The steps count down; the finished time can be saved to the brew |
| 6 | Dial-in | After a sour or bitter brew, tap **Apply to next brew** on the advice card | The next brew form has the change filled in |
| 7 | Offline | Turn on airplane mode, log a brew, add a bean, then turn it off | Nothing is lost; the "not yet synced" note goes away once you are online |
| 8 | Two devices | Sign in on a second device (passkey) | Beans, bags, recipes and brews are the same on both |
| 9 | Household | Settings > Household > create an invite, send it to a second person, they join | Both see the same beans and brews; the second person is listed |
| 10 | Friends | People > share your friend code; the other person follows you, you accept | They see what you shared with friends and can copy a bean or recipe into their own household |
| 11 | Photos | Add a photo to a bean in airplane mode, then go online | The photo shows "Waiting to upload", then uploads; a second device shows it |
| 12 | Lost passkey | On a device without your passkey: use "I lost my passkey" with a recovery code | You can register a new passkey and are signed in |
| 13 | Your data | Settings > Export my data | A JSON file with your data downloads |
| 14 | Feedback | Settings > Feedback > Send feedback | The mail app opens with the version and device filled in |
| 15 | Delete account (last) | Settings > Delete account | Your account is gone and you are signed out. Do this on a throwaway account |

## What to ask afterwards
- What was the first thing you did not understand?
- Did you ever lose something you typed or photographed?
- Which screen did you open most? Which did you never open?
- Would you keep using this instead of your notes app or paper?

## For Thomas
- Start with your own household (scenarios 1 to 11), fix what breaks, then invite friends.
- Real-device checks that only a person can do are listed in the PRs (wake lock, silent switch, storage eviction, passkeys on a phone). Run them with scenario 1, 7 and 12.
- Before inviting anyone: the Feedback address must be set (`Feedback:Email`), the invite gate on (`Beta__InviteRequired=true`) and the production domain chosen (#111), because passkeys are bound to it.
- Each week: the review in [beta-feedback.md](beta-feedback.md).
