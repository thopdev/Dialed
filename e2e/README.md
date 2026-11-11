# Dialed end-to-end tests

Playwright smoke tests in **WebKit** (the Safari engine) with iPhone and iPad profiles.

```bash
dotnet publish src/Dialed.Web/Dialed.Web.csproj -c Release -o publish
cd e2e
npm ci
npx playwright install webkit
E2E_ROOT=../publish/wwwroot npx playwright test
```

`serve.mjs` serves the published `wwwroot` like Static Web Apps does (static files, `index.html` for app routes, the real headers).

Playwright also starts the API in Development (`dotnet run`, port 5190, data in memory), and `serve.mjs` passes the app's API calls
(`/auth/`, `/sync/`, `/households`, ...) on to it. The app and the API share one origin, so the real Content-Security-Policy applies unchanged.
The account tests sign in through the Development-only dev login: they fetch tokens from `POST /auth/dev` as alice or bob (no credentials)
and put them where the app keeps its sign-in. Alice and Bob share a household, which the sync round trip uses.
Locally, an API or server already running on those ports is reused; set `E2E_API_PORT` or `E2E_PORT` to use others.
