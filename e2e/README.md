# Dialed end-to-end tests

Playwright smoke tests in **WebKit** (the Safari engine) with iPhone and iPad profiles.

```bash
dotnet publish src/Dialed.Web/Dialed.Web.csproj -c Release -o publish
cd e2e
npm ci
npx playwright install webkit
E2E_ROOT=../publish/wwwroot npx playwright test
```

`serve.mjs` serves the published `wwwroot` like Static Web Apps does (static files, `index.html` for app routes).
Data is in memory today, so tests move by tapping links; a full page load starts with an empty store.
Sign-in flows (#33, #44) and a sync round trip follow once accounts and sync exist.
