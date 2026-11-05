# ADR-0005: Sign in with Apple + API-issued JWT

**Status:** Accepted · 2026-11-05

## Context
iPhone/iPad-first app for a private beta. Sign-in should be one tap and not require passwords. Other platforms may come later.

## Decision
- The app signs in with **Sign in with Apple** (MAUI `AppleSignInAuthenticator`) and sends the identity token + authorization code to `POST /auth/apple`.
- The API validates the token against Apple's public keys (issuer, audience = bundle ID, expiry, nonce), exchanges the authorization code for an Apple refresh token (needed to revoke on account deletion) and upserts the user by Apple `sub`.
- The API issues its **own short-lived access JWT** (signed with a key in Key Vault) and a **rotating refresh token** (stored hashed, revocable). Tokens are stored in the iOS Keychain via MAUI `SecureStorage`.
- On first sign-in a **personal household** is created.
- Account deletion revokes the Apple token and all refresh tokens.
- A **dev-only test login** exists for integration tests and the simulator; it is disabled outside `Development`.

See [architecture.md §4](../architecture.md#4-authentication).

## Consequences
- No password storage, no email verification flows.
- Apple may hide the user's email (private relay) — the app must not rely on email.
- Adding Google/email login later means adding providers to `/auth/*`; the rest of the system only knows our own user ID.
- Requires an Apple Developer Program account, a Services/Key configuration and the Sign in with Apple capability on the bundle ID.

## Alternatives considered
- **Microsoft Entra External ID**: Azure-managed, more providers, more setup.
- **ASP.NET Core Identity**: full control, most work to secure.
