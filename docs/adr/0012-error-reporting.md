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
- **JavaScript errors:** `js/errors.js`, the first script on the page, catches uncaught errors (`window` `error`), unhandled promise rejections and errors the service worker posts to the page (install and activate failures, its own uncaught errors; failed fetches are expected offline and left out). Errors caught before the app is ready wait in a short queue. `BrowserScriptErrors` (Dialed.Web) hands them to `ErrorReporter.ReportScriptErrorAsync`, so they get the same opt-out, scrubbing and limits. Only the error name and stack frames are kept: lines that are not frames (where Chrome puts the message) are dropped, as are URL origins and query strings, and a rejection with a plain value is reported as `UnhandledRejection` without the value. Kinds: `script`, `rejection`, `service-worker`.

## To do with Azure (#25)
Create the Application Insights resource, put its connection string in the app configuration, load the SDK in `index.html` (and allow `js.monitor.azure.com` in the CSP, see #196), and verify reports arrive with the app version and device. Turn off the SDK's own exception tracking (`disableExceptionTracking: true`): it would send unscrubbed messages next to ours.
