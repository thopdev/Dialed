# Documentation index

Start with the [plan](PLAN.md) and the [architecture](architecture.md); decisions are in the [ADRs](adr/README.md). Rules for working in the repo are in [CONTRIBUTING.md](../CONTRIBUTING.md) and [CLAUDE.md](../CLAUDE.md).

## Product and architecture
| Doc | What it covers |
| --- | --- |
| [PLAN.md](PLAN.md) | Vision, scope, milestones, risks, the post-MVP backlog |
| [architecture.md](architecture.md) | Components, domain model, sync, auth, AI, Azure resources, platform interfaces (§10, including `IBarcodeScanner` and `IPushNotifications`), MAUI readiness |
| [adr/](adr/README.md) | Architecture decisions |
| [native-app.md](native-app.md) | Estimate for a native iOS app with MAUI Blazor Hybrid |
| [branding.md](branding.md) | Branding assets |

## Features
| Doc | What it covers |
| --- | --- |
| [ai-label-scan.md](ai-label-scan.md) | Label scan API, offline scans |
| [ai-label-golden-set.md](ai-label-golden-set.md) | The golden set for label extraction |
| [ai-dial-in.md](ai-dial-in.md) | Dial-in advice and the assistant |
| [ai-starting-recipe.md](ai-starting-recipe.md) | Starting recipe for a new bean |
| [ai-quality.md](ai-quality.md) | Suggestion quality per prompt version |
| [photo-uploads.md](photo-uploads.md) | Photo upload queue and short-lived URLs |
| [push-notifications.md](push-notifications.md) | Web Push: follow and household events, low stock and peak alerts, keys, the stock alert job |
| [public-profiles.md](public-profiles.md) | Opt-in public profiles, find people, reports, moderation with the admin key |
| [insights.md](insights.md) | Offline maps, flavour wheel, calendar, milestones, dial-in trace, brew and bean cards, bean journey, gear upkeep, grind drift, recipe from best brews, tasting suggestions, taste profile, bean compare, water, roast age tip, shopping list and timer voice: what they read, where the code is, how to extend them |
| [automerge.md](automerge.md) | Docs-only PRs from the owner merge themselves when `build-test` is green: the workflow and the settings to switch on |
| [app-size.md](app-size.md) | What the first load downloads, how to measure it, and how fast the insight views are on a big household |
| [catalog.md](catalog.md) | The shared bean and roaster catalog: what goes in, the 2-person rule, moderation, where it is used |

## Quality, security and operations
| Doc | What it covers |
| --- | --- |
| [accessibility.md](accessibility.md) | Accessibility and dark mode rules |
| [security-review.md](security-review.md) | Security checklist before the private beta, one row per area |
| [observability.md](observability.md) | Logs, metrics and traces |
| [cosmos-stores.md](cosmos-stores.md) | Cosmos DB stores in the API and the pattern for new ones |
| [storage-recovery.md](storage-recovery.md) | Storage and sync recovery |

## Beta
| Doc | What it covers |
| --- | --- |
| [beta-testing.md](beta-testing.md) | Install guide and test plan |
| [beta-invites.md](beta-invites.md) | Invite codes and the admin key |
| [beta-feedback.md](beta-feedback.md) | The feedback loop |
| [iphone-checks.md](iphone-checks.md) | The real-iPhone checklist, by feature (cannot be done in a simulator or headless browser) |

## Admin key
Several owner-only endpoints share one secret, `Beta:AdminKey`, sent as the `X-Admin-Key` header (they do not exist without it): beta invites, suspending and restoring profiles, reports, and catalog moderation. See `beta-invites.md`, `public-profiles.md` and `catalog.md`.
