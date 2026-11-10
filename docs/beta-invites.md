# Beta invite codes (#113)

During the private beta, signing up needs an invite code. This page is how to turn the gate on and hand out codes.

## Settings (API)
| Setting | Meaning |
|---|---|
| `Beta__InviteRequired` | `true` = sign-up needs a code (the production default, see `appsettings.json`); `false` opens sign-up. In Development it is off. |
| `Beta__AdminKey` | The secret for creating codes. **Empty = the admin endpoint does not exist** (404), so leave it empty except while you are creating codes. In Azure keep it in a Container App secret or Key Vault, never in a file. |

## Create a code
```bash
curl -X POST https://<api-host>/admin/invites \
  -H "X-Admin-Key: <the admin key>" \
  -H "Content-Type: application/json" \
  -d '{"maxUses": 5, "daysValid": 14}'
```
The answer holds the code, how many people can use it and when it expires. All fields are optional:

| Field | Default | Limits |
|---|---|---|
| `maxUses` | 1 | 1 to 1000 |
| `daysValid` | 14 | 1 to 365 |
| `code` | a random 8-character code (no `0`, `O`, `1`, `I`) | any text; it is upper-cased |

A wrong or missing key answers 401. Give testers the code with the install steps (Safari > Share > Add to Home Screen, see #99); they type it in the sign-up form.

## What to know
- Codes live **in memory** for now, so restarting the API forgets them. Create them again after a deploy, until the Cosmos DB implementation exists (same work as #197).
- Using a code counts one use. A code that is used up, expired or unknown is refused with a friendly message in the app.
- Household invites (Settings > Household) are a different thing: they add someone to a household and need no admin key.
