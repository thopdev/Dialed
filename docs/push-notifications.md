# Push notifications, part 1 (#100)

Standard Web Push with our own VAPID keys, sent by the API. No Azure Notification Hubs. Part 1 sends two kinds of notification: follow requests (and accepted requests) and new household members. Low stock and "bag at its peak" are #259 (they need a daily server job); the Azure keys, the Cosmos DB subscription store and that job are #260.

## How it fits together

```
Settings > Notifications ──tap──> IPushNotifications (browser PushManager, permission prompt)
        │                                   │ subscription (endpoint, p256dh, auth)
        └── PushNotificationService ──> PUT /push/subscription ──> IPushStore
FriendService / HouseholdService ── INotifier ──> PushNotifier ──> IPushSender ──> push service ──> service worker ──> notification
```

- **Client**: `IPushNotifications` (platform interface, `Dialed.Client.Platform`; web implementation `BrowserPushNotifications` over `push*` in `platform.js`). `PushNotificationService` turns it on and off and keeps the switches; `PushApi` calls the endpoints below. The permission prompt only comes from the **Allow notifications** tap, because iOS requires a user gesture and an app added to the Home Screen (iOS 16.4 or later). In a Safari tab on iPhone or iPad the screen says to install first.
- **API** (`Dialed.Api/Push`): `GET /push/key` (public VAPID key, 503 when the server has none), `PUT /push/subscription`, `POST /push/unsubscribe`, `GET`/`PUT /push/settings`. All need sign-in.
- **Events**: `FriendService` (follow, accept) and `HouseholdService` (join) tell `INotifier` after the change succeeded. `PushNotifier` works in the background, never throws and never makes the request wait, so a failed push never fails following or joining.
- **Rules**: nobody is notified of their own action or by someone they blocked (either direction); the same event is not sent twice within an hour; a declined request tells nobody; household joins go to the other members.
- **Per device**: each device registers its own subscription with the app language (`en` or `nl`) stored next to it. A person with two devices gets the push on both. The push service answering 404 or 410 removes the subscription. Signing out, turning notifications off and deleting the account remove it too. At most 10 devices per person.
- **Content**: a title, one line with a name, an in-app link and a tag. Never beans, brews, codes or email addresses. It is encrypted (RFC 8291), so Apple, Google and Mozilla cannot read it.
- **Service worker** (`wwwroot/push-handlers.js`, loaded by both service workers): shows the notification; if the open window is visible and focused it posts the news to the page instead of showing a banner; a tap focuses the open app and asks it to navigate (`js/pwa.js`), or opens the link when the app is closed. Only relative in-app links are followed. `e2e/tests/push-handlers.spec.ts` tests the handlers against a fake worker.
- **Telemetry**: `dialed.push.sent` by `type` and `outcome` (sent, gone, failed, skipped). No user ids or endpoints.

## Keys and configuration

| Setting | Meaning |
| --- | --- |
| `Push:PublicKey`, `Push:PrivateKey` | The VAPID key pair (URL-safe base64). Without them push is off: `/push/key` answers 503 and Settings says notifications are not available. |
| `Push:Subject` | A `mailto:` or `https:` contact for the push services. |

In **Development** a key pair is generated when the API starts, so the whole flow works locally; subscriptions made against an earlier run stop working and are removed when the push service answers 410. Production keys are generated once (`WebPush.VapidHelper.GenerateVapidKeys()`) and kept in Key Vault (#260).

Subscriptions live in memory (`InMemoryPushStore`) until the Cosmos DB stores arrive (#38, pattern in `cosmos-stores.md`, filed in #260).

## Testing

- API: `tests/Dialed.Api.Tests/PushTests.cs` (subscribe, ownership, validation, language, switches, blocked users, no double sends, 404/410 cleanup, failure isolation, payload shape).
- UI: `NotificationsPanelTests` covers every state of Settings > Notifications and sign-out.
- Service worker: `e2e/tests/push-handlers.spec.ts`.
- Real device: the "Notifications" list in `iphone-checks.md`.
