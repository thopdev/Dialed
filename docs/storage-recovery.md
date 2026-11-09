# Storage and sync recovery (#114)

What the app says when saving or syncing goes wrong, and how to check it by hand.

| Situation | What the user sees |
|---|---|
| First sign-in on a device, data coming from the account | "Loading your data from your account… N items so far." (`#sync-restoring`), gone when the pull ends |
| Changes waiting to be sent | A plain note with the count (`#sync-indicator`) |
| Changes waiting for more than 5 minutes (signed in) | A warning (`role=alert`) that they are safe on this device and to check the connection |
| The browser refuses a write (full disk, private mode, evicted storage) | A banner (`#storage-warning`); signed in, it adds that the change is still sent to the account. A later good write clears it |

Reads are never wrapped: a failing read is a real error. Writes go through `ResilientLocalStore`, which reports to `StorageStatus` and rethrows nothing for the caller's UI path.

## Manual checks (real device or browser)
1. Sign in on a second browser profile: the restore count appears and goes away.
2. Go offline, save a bean, wait 5 minutes: the warning appears; go online: it clears.
3. Fill the quota (DevTools > Application > Storage, simulate custom quota) and save: the storage warning appears.
4. iPhone (user): Private Browsing and low-storage behaviour.
