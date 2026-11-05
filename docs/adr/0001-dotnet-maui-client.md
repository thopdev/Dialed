# ADR-0001: .NET MAUI client for iPhone and iPad

**Status:** Accepted · 2026-11-05

## Context
Dialed is used mainly on iPhone and iPad. It needs the camera, on-device text recognition, an offline database, a guided brew timer and adaptive layouts for iPad. The developer's main stack is C#/.NET (ASP.NET Core, Blazor).

## Decision
Build the app with **.NET MAUI** targeting `net10.0-ios` (iOS + iPadOS), MVVM with **CommunityToolkit.Mvvm**, UI helpers from **CommunityToolkit.Maui**. Platform-specific features (Apple Vision OCR, Sign in with Apple, haptics) live behind interfaces with an iOS implementation.

## Consequences
- One language across app, API and Functions; domain model and validation are shared (`Dialed.Domain`).
- Works with the Datasync Community Toolkit offline client (ADR-0004).
- **A Mac with Xcode is required** to build, sign and deploy to devices/TestFlight (Pair to Mac or macOS CI runners).
- Some iOS-native polish (widgets, Live Activities, Watch) needs native extensions — deferred to post-MVP.
- Android/Windows targets stay possible later without a rewrite.

## Alternatives considered
- **SwiftUI**: best native experience, but a new language and no code sharing with the .NET backend.
- **React Native / Flutter**: cross-platform, but a different language from the backend and less native feel on iPad.
