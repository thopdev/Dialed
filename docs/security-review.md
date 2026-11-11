# Security review before the private beta (#93)

Checklist from #93. Status as of 2026-11-09. "Needs Azure" items cannot be verified until the Azure environment exists.

| Area | Status | Notes |
| --- | --- | --- |
| Authorization on every endpoint | Done | `SecurityTests.Every_endpoint_requires_a_signed_in_user_unless_it_is_listed_as_open_on_purpose` fails when an endpoint is added without authorization and without being listed as open on purpose. Role, visibility, follow and block rules have their own tests (`HouseholdTests`, `MultiHouseholdTests`, `VisibilityTests`, `FriendTests`, `DeleteAccountTests`). |
| Passkey config (RP ID, origins) | Done | RP ID derived from an allow-listed origin only (`OriginPolicy`); ceremony state is protected, single-use and bound to the origin. |
| Recovery flows | Done | Recovery codes are hashed, shown once, single use. Email codes: hashed, 15 minutes, 5 guesses, one email per minute per account. After 10 wrong codes in 15 minutes recovery is paused for the account. Recovery ends all old sessions. Unknown emails get the same answer. |
| Rate limits | Done | Per address on `/auth` (covers sign-up, sign-in, recovery), per user on `/friends`, `/ai` and the rest. |
| CORS | Done | Only configured app origins (`App:Origins`, patterns for dev tunnels). |
| API response headers | Done | `nosniff`, `Referrer-Policy: no-referrer`, a closed CSP, `Cache-Control: no-store` on everything personal, HSTS (off only in local development). |
| CSP on the PWA | Done | Enforced as `Content-Security-Policy` in `staticwebapp.config.json` (#196). The e2e smoke tests (WebKit, iPhone and iPad profiles) run under the same header (`e2e/serve.mjs`) and fail on any violation; `ContentSecurityPolicyTests` keeps it closed (no eval, no foreign scripts, no framing). When the API moves to the custom domain (#111), add its origin to `connect-src`. |
| HTTPS | Needs Azure | Container Apps ingress terminates TLS; verify HTTP is redirected once deployed. |
| SAS scope and expiry, Key Vault, managed identities | Needs Azure | Photos are not stored yet (#68/#69); check when they are. |
| Dependabot alerts | Open | #183 and the earlier ones are for the owner to merge. |
| Token lifetimes | Checked | Short access token, rotating refresh tokens with reuse detection; all sessions are revoked on recovery and on account deletion. |
| Logs without personal data | Checked | No log call includes an email, name, token or code (the email stand-in logs only the subject). Keep it so: review new `Log*` calls. |

## Known limits (filed as issues)

- Recovery lockout, email cooldown and email codes live in Cosmos DB (`CosmosEmailRecoveryStore`, container `recovery`, TTL per document) whenever a Cosmos DB connection string is set, as under the AppHost (#197). Without one (tests, a bare `dotnet run`) they stay in memory per instance. In Azure the connection uses managed identity rather than a key; see `docs/cosmos-stores.md` and #198.
- Recovery codes are 40 bits; with the lockout and rate limits guessing is not practical, but the length can grow if needed.
