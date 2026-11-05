# ADR-0009: Blazor WebAssembly PWA, structured to allow a MAUI app later

**Status:** Accepted · 2026-11-05 · Supersedes [ADR-0001](0001-dotnet-maui-client.md)

## Context
There is no Mac available. .NET MAUI needs a Mac with Xcode to build, debug and sign iOS apps; Hot Restart (deploy from Windows to an iPhone) is not included in Visual Studio 2026. Cloud-only iOS builds mean a 30–45 minute loop for every test on a device, which is not acceptable. The app is used mainly on iPhone and iPad, the developer prefers C#, and a native MAUI app should remain possible later.

## Decision
- Build Dialed as a **Blazor WebAssembly** app (.NET 10) delivered as an **installable PWA** (web manifest + service worker). On iPhone/iPad it is installed with Safari's *Add to Home Screen* and runs full-screen.
- **MAUI-ready structure** — a future MAUI Blazor Hybrid app reuses everything except the host:

  | Project | Contains | Browser-specific? |
  |---|---|---|
  | `Dialed.UI` (Razor Class Library) | Pages, components, layouts, UI state | No — only abstractions |
  | `Dialed.Client` (class library) | Local EF Core `DbContext`, sync engine, typed API clients | No |
  | `Dialed.Web` (Blazor WASM host) | `Program.cs`, service worker, manifest, JS interop implementations, SQLite-WASM provider | Yes |
  | *Later:* `Dialed.App` (MAUI Blazor Hybrid host) | Native implementations of the same interfaces | Native |

- **Platform services behind interfaces** in `Dialed.Client`/`Dialed.UI`, implemented in the host: photo capture/picker, local photo storage, connectivity, share, passkey (WebAuthn) client, storage persistence, screen wake lock, sound cues.
- **Rule:** `Dialed.UI` and `Dialed.Client` never reference `IJSRuntime` or browser-only packages directly. An architecture test enforces this.
- **UI library:** **Fluent UI Blazor** (`Microsoft.FluentUI.AspNetCore.Components`) with a custom coffee theme (design tokens, light/dark). It works in both WebAssembly and MAUI Blazor Hybrid.
- **Hosting:** **Azure Static Web Apps** (Free plan: 2 custom domains, 3 preview environments, 100 GB bandwidth/month) — default hostname in `dev`, custom domain in `prod`; the API stays on Container Apps (ADR-0002).
- **Language:** English UI for the beta; all UI text in resource files (`IStringLocalizer`) so Dutch can be added later.

## Consequences
- Fast loop: `dotnet watch` on Windows and test on the iPhone over the local network, or through the preview environment that Static Web Apps creates per pull request.
- No Apple Developer Program or TestFlight needed for the beta. Android and desktop browsers work too (not a test target).
- Less native than an App Store app: no haptics (the Vibration API isn't available in iOS Safari), no widgets/Watch, install via a manual *Add to Home Screen* step, first load downloads the .NET WebAssembly runtime (cached afterwards).
- iOS may clear browser storage; the local database must be treated as a cache of the server plus an outbox (ADR-0011).
- Links (e.g. invites) open in Safari, not in the installed home-screen app; flows such as joining a household use a code typed in the app.
- Switching to MAUI later = add a host project + native implementations, sign up for the Apple Developer Program, and use a Mac or cloud Mac for builds.

## Alternatives considered
- **.NET MAUI with a (used) Mac mini**: best native result, hardware cost.
- **.NET MAUI with cloud macOS builds only**: 30–45 min feedback loop, no device debugging.
- **React Native + Expo**: no Mac needed and native, but TypeScript instead of C# and no reuse with a MAUI path.
