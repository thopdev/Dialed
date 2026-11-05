# ADR-0010: Passkeys with ASP.NET Core Identity, API-issued tokens

**Status:** Accepted — to be validated by the M0 spike · 2026-11-05 · Supersedes [ADR-0005](0005-authentication.md)

## Context
The app is a PWA (ADR-0009), there is no Apple Developer Program membership (needed for Sign in with Apple, even on the web), and sign-in should be passwordless. ASP.NET Core Identity in .NET 10 supports passkeys (WebAuthn) out of the box; the built-in flow keeps the ceremony state in a Data Protection cookie and is documented for the Blazor Web App template.

## Decision
- **ASP.NET Core Identity** (.NET 10) in `Dialed.Api` with **passkeys as the only sign-in method** (no passwords).
- **Sign-up:** invite code (beta) + email + display name → create passkey → confirm email (code/link sent with Azure Communication Services Email).
- **Sign-in:** discoverable credentials (no username needed), autofill/conditional UI where the browser supports it.
- **Ceremony endpoints** on the API (creation options, register, request options, sign-in) wrap `SignInManager`/`IPasskeyHandler`. After a successful ceremony the API issues its **own short-lived JWT access token + rotating refresh token** — the API stays token-based so a future MAUI host can use it unchanged.
- **Domain:** app and API on one registrable domain (e.g. `app.<domain>` on Static Web Apps, `api.<domain>` on Container Apps) with **RP ID = `<domain>`**. This keeps the ceremony cookie same-site (Safari blocks cross-site cookies) and lets passkeys work on all subdomains. Fallback, validated in the spike: a custom `IPasskeyHandler` wrapper with short-lived, single-use server-side ceremony state and no cookie.
- **User store on Cosmos DB:** custom Identity stores (users, emails, passkeys, tokens) on Cosmos DB, or the EF Core Cosmos provider if it supports the Identity schema — decided in the spike.
- **Recovery:** recovery codes shown at sign-up, email-based recovery, and a prompt to add a second passkey when the passkey is not backed up (`IsBackedUp` false). On Apple devices passkeys sync through iCloud Keychain.
- **Beta gate:** sign-up requires an invite code while the app is in private beta.
- **Dev-only test login** for integration tests and local development.

## Consequences
- No passwords, no Apple fee; Face ID / Touch ID sign-in on iPhone and iPad.
- **The domain must be chosen before the first real user registers** — changing the RP ID invalidates all existing passkeys.
- Needs transactional email (Azure Communication Services Email) for confirmation and recovery.
- A future native MAUI app needs the Associated Domains entitlement for the same domain (and the Apple Developer Program at that point).
- Attestation is not validated (not needed for this app).

## Alternatives considered
- **Sign in with Apple**: needs the Apple Developer Program ($99/year) even for the web.
- **Microsoft Entra External ID**: managed, free up to 50,000 monthly active users, MSAL for WASM and MAUI; less control over the experience, more configuration.
- **Email + password**: weakest option, more to secure.
