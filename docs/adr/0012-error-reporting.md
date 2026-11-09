# ADR-0012: Error reporting from the PWA with the Application Insights JavaScript SDK

**Status:** Accepted · 2026-11-09 · Story #91

## Context
Testers' devices will hit errors we never see: unhandled exceptions in the Blazor app, failing syncs. We need reports with the app version and device type, without personal data, and a way to opt out. Candidates: the **Application Insights JavaScript SDK** and **Sentry for Blazor WebAssembly**.

## Decision
Use the **Application Insights JavaScript SDK**, loaded by the page only when a connection string is configured, behind our own `IErrorSink` interface so the choice stays replaceable.

Why: the backend already exports to Application Insights (#90), so API traces and client errors share one place and one correlation id (`X-Correlation-Id`); no new vendor, account or data processor for a private beta; no extra cost. Sentry's Blazor support is good for stack traces but means a second service, a second privacy review and another bill. If Application Insights proves too limited for WebAssembly stacks, only the sink changes.

## How it works
- `ErrorReporter` (Dialed.Client) receives exceptions from the shell's error boundary and from errors written to the log, and sync failures from `SyncCoordinator`.
- **No personal data:** the report holds the kind, exception type, stack frames (method names), app version, a timestamp and the platform's device type (phone, tablet, desktop). The message of an unexpected exception is never sent because it can contain what the person typed. Sync reports hold counts only. Text is scrubbed anyway (emails, tokens, ids, URL query strings are removed).
- **Limits:** the same error at most once a minute, at most 20 reports per launch.
- **Opt-out:** Settings > Privacy, on by default, kept in localStorage.
- **JavaScript errors** (outside .NET) are not reported yet.

## To do with Azure (#25)
Create the Application Insights resource, put its connection string in the app configuration, load the SDK in `index.html` (and allow `js.monitor.azure.com` in the CSP, see #196), and verify reports arrive with the app version and device.
