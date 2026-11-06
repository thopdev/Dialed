# ADR-0010: Passkeys with ASP.NET Core Identity, API-issued tokens

**Status:** Accepted — to be validated by the M0 spike · 2026-11-05 · Supersedes [ADR-0005](0005-authentication.md)

## Context
The app is a PWA (ADR-0009), there is no Apple Developer Program membership (needed for Sign in with Apple, even on the web), and sign-in should be passwordless. ASP.NET Core Identity in .NET 10 supports passkeys (WebAuthn) out of the box; the built-in flow keeps the ceremony state in a Data Protection cookie and is documented for the Blazor Web App template.

## Decision
- **ASP.NET Core Identity** (.NET 10) in `Dialed.Api` with **passkeys as the only sign-in method** (no passwords).
- **Sign-up:** invite code (beta) + email + display name → create passkey → confirm email (code/link sent with Azure Communication Services Email).
- **Sign-in:** discoverable credentials (no username needed), autofill/conditional UI where the browser supports it.
- **Ceremony endpoints** on the API (creation options, register, request options, sign-in) wrap `IPasskeyHandler`. After a successful ceremony the API issues its **own short-lived JWT access token + rotating refresh token** — the API stays token-based so a future MAUI host can use it unchanged.
- **Cookie-less ceremony:** instead of Identity's default cookie, the ceremony state (challenge, user binding, expiry) is returned in the options response as a Data Protection–protected token and sent back with the attestation/assertion. It is single-use (tracked server-side) and valid for ~5 minutes. Works when app and API are on different sites (Azure default hostnames, dev tunnels) and in a future MAUI host.
- **RP ID per environment** (configuration: `RpId` + allowed origins):

  | Environment | App origin | RP ID |
  |---|---|---|
  | Local (PC browser, Windows Hello) | `https://localhost:<port>` | `localhost` |
  | Local on iPhone (Face ID) | persistent Microsoft dev tunnel URL | the tunnel host |
  | Azure `dev` (+ PR previews) | `*.azurestaticapps.net` default hostname(s) | the app's hostname (previews: derived from an allow-listed origin) |
  | `prod` (beta) | `https://app.<domain>` | `<domain>` |

  Passkeys are bound to their RP ID, so test passkeys per environment are separate and throwaway.
- **User store on Cosmos DB:** custom Identity stores (users, emails, passkeys, tokens) on Cosmos DB with point-read lookup documents (see spike results).
- **Recovery:** recovery codes shown at sign-up, email-based recovery, and a prompt to add a second passkey when the passkey is not backed up (`IsBackedUp` false). On Apple devices passkeys sync through iCloud Keychain.
- **Beta gate:** sign-up requires an invite code while the app is in private beta.
- **Dev-only test login** for integration tests and local development.

## Spike results (M0, #29 — server side)
Prototype in `spikes/PasskeySpike`, 23 tests against the Cosmos DB emulator with a software authenticator (ES256, `none` attestation).

- **Cookie-less works with Identity as-is.** `PasskeyHandler<TUser>` returns its attestation/assertion state as a string; we wrap it with origin, RP ID, expiry and a nonce in a Data Protection token. Single use = creating a `ceremony:{nonce}` document (409 on replay, TTL cleans up). No `SignInManager`, no cookies.
- **RP ID per request.** Identity's RP ID (`IdentityPasskeyOptions.ServerDomain`) is one global option, and its default (the API's own host) is wrong when app and API are on different hosts. We construct a `PasskeyHandler` per request with the RP ID resolved from the calling `Origin` against an allowlist (wildcards such as `https://*.euw.devtunnels.ms`), plus a custom `ValidateOrigin` that requires `clientData.origin` to equal the origin bound in the state. One API can therefore serve localhost, tunnels, the SWA default host **and** PR previews.
- **PR previews get their own passkeys.** `azurestaticapps.net` is a public suffix, so each preview hostname is its own RP ID: passkeys from the dev app don't work on a preview (covered by a test) — testers sign up again per preview. Acceptable for dev.
- **Own stores on Cosmos DB** (`identity` container, partition key `/pk`, pk = id): user document with embedded passkeys, plus `email:{sha256}` and `passkey:{credentialId}` lookup documents → every lookup is a point read; email uniqueness via create-conflict; optimistic concurrency with `_etag`. The EF Core Cosmos provider isn't needed.
- **Discoverable credentials:** `residentKey: required` and `userVerification: required`; sign-in options carry no `allowCredentials`, so no email is needed to sign in.
- **Data Protection keys must be shared and persisted in Azure** (Blob Storage + Key Vault), otherwise state minted by one replica can't be opened by another or after a restart. → #24/#26.
- Sign-up options reveal whether an email is registered (409). Fine for the invite-only beta; revisit in #30.
- **iPhone (Safari, Face ID) via dev tunnel: sign-up, discoverable sign-in and passkey autofill work** (2026-11-06).
- **Dev tunnels rewrite the `Origin` header to `localhost` by default**, which breaks passkeys (the API binds the wrong origin/RP ID). Tunnels used for passkeys must keep it: `devtunnel update <tunnel> --origin-header unchanged` (the VS Code task does this). The Aspire dev tunnel for the app (#30) needs the same setting.
- **Still to verify on devices:** the installed home-screen app on iPhone, Windows Hello on localhost, and app/API on different Azure hostnames (needs #24–#26).

## Consequences
- No passwords, no Apple fee; Face ID / Touch ID sign-in on iPhone and iPad.
- **The production domain must be chosen before the first beta tester registers** (milestone M4) — changing the RP ID invalidates all existing passkeys. Development doesn't need it.
- We own the ceremony-state handling (integrity, single use, expiry) instead of relying on Identity's cookie; covered by tests.
- Needs transactional email (Azure Communication Services Email) for confirmation and recovery.
- A future native MAUI app needs the Associated Domains entitlement for the same domain (and the Apple Developer Program at that point).
- Attestation is not validated (not needed for this app).

## Alternatives considered
- **Sign in with Apple**: needs the Apple Developer Program ($99/year) even for the web.
- **Microsoft Entra External ID**: managed, free up to 50,000 monthly active users, MSAL for WASM and MAUI; less control over the experience, more configuration.
- **Email + password**: weakest option, more to secure.
