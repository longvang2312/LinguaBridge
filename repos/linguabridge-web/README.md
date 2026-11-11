# LinguaBridge Web

Web client for learners: HTML5, CSS3, Bootstrap 5.3 (CDN) and vanilla JavaScript (ES modules). No bundler, no build step, nothing to install.

| Page | What it does |
|---|---|
| `index.html` | Signed out: hero and a sign-in button. Signed in: dashboard (learner from `/api/v1/me`, cards due today, plan, progress bar, course list). |
| `courses.html` | Published courses per language (English / Chinese) with title, level, description, code; a modal lists a course's lessons and your progress. |
| `flashcards.html` | SRS review: 3D flip card, four rating buttons (keyboard: `Space` flips, `1`-`4` rate). |
| `login.html` | Dedicated sign-in page: remembers where the learner was, shows "signed out" after a logout. |
| `callback.html` | OIDC redirect target: exchanges the code for tokens. Served at `/callback`. |

## Run

1. Start the backend: the STS (`http://localhost:5001`) and the LinguaBridge API (`http://localhost:5020`), with SQL Server and PostgreSQL running on the machine (see `docs/guides/RUN_AND_SETUP_GUIDE.md`).
2. Start the web client, one of:
   - **Visual Studio**: open `AppInHouse.sln` and press F5 with the multi-startup profile (STS, Admin Web, LinguaBridge.Api, **LinguaBridge.Web.Host**). The web host opens http://localhost:3000.
   - **Command line**: `dotnet run --project repos/linguabridge-web/LinguaBridge.Web.Host --launch-profile http`
   - **Script** (same job, no .NET project needed): `./scripts/start-web.ps1` (`-NoBrowser` to skip the browser, `-Port` to change). If PowerShell refuses to run it (execution policy): `powershell -ExecutionPolicy Bypass -File scripts/start-web.ps1`.

   The web host and the script both listen on port 3000: run only one of them.
3. Sign in with the seeded account `admin@appinhouse.local` / `Admin@AppInHouse123!` (development only).

Open the site at **http://localhost:3000** exactly, not `127.0.0.1`: the STS only knows `http://localhost:3000/callback` (and `:5173`) as redirect addresses of the `linguabridge-app` client.

## How sign-in works

`assets/js/auth.js` implements Authorization Code + PKCE by hand:

1. `login()` creates a random `code_verifier`, derives `code_challenge = BASE64URL(SHA-256(verifier))` with `crypto.subtle`, plus `state` and `nonce`, keeps them in `sessionStorage`, and redirects to `{STS}/connect/authorize` (client `linguabridge-app`, scope `openid profile email offline_access linguabridge_api`).
2. After the STS login page it returns to `/callback`. `completeLogin()` checks `state`, exchanges the code (with the verifier) at the token endpoint, checks the id_token `nonce`, and stores access, id and refresh tokens in `sessionStorage` (gone when the tab closes).
3. `assets/js/api.js` adds `Authorization: Bearer <token>` to every call. A token about to expire is refreshed first; a 401 triggers one refresh and one retry, then a new sign-in (silent when the STS session is still alive).
4. Every sign-in button leads to `login.html?returnUrl=<current page>`; its button starts the flow with that address, and `/callback` sends the learner back to it. The address is checked (`safeReturnUrl`): only paths on this site are accepted, anything else (another origin, `//host`, the login/callback pages) falls back to the home page.
5. Logout is a real single sign-out (OpenID Connect RP-Initiated Logout): the app forgets its tokens and sends the browser to `{STS}/connect/logout?id_token_hint=<id_token>&post_logout_redirect_uri=<origin>/login.html&state=lb-logout.<returnUrl>`. The STS ends its own session (Identity cookie) and redirects straight back, without a confirmation page. `login.html` then shows "signed out" and the next sign-in asks for the password in every tab. The STS accepts only post-logout addresses registered exactly as written (no query string), so the page to return to travels in `state` and `auth.js` turns it back into `login.html?returnUrl=...&loggedOut=1` on arrival. The client's registered addresses are seeded by the STS (`Seeding:LinguaBridgeAppPostLogoutRedirectUris`; default `http://localhost:3000/login.html`, `http://localhost:5173/login.html`, `http://localhost:3000/callback`) and added to an existing database on the next STS start.
6. Loop guard: after a 401 the app signs in again at most once per minute. If the API still refuses the fresh token (for example API and STS configured with different addresses or secrets), the page shows an error instead of redirecting forever. Any successful API call resets the guard.

The STS address in `assets/js/config.js` must be `http://localhost:5001`: the API only accepts tokens issued through the address it was configured with (`Authentication:Authority` in its `appsettings.Development.json`). Both must always be the same text. The Docker Compose stack (`docker-compose.yml`) uses `http://host.docker.internal:5001` instead; to use the web client against that stack, change `stsUrl` here and run the host with `Web__StsUrl=http://host.docker.internal:5001`.

## Run on a server (Docker)

`LinguaBridge.Web.Host` has a `Dockerfile` (context `repos/linguabridge-web`) and `docker-compose.web.server.yml` runs it as `linguabridge-web` on port 3000, next to the identity stack and the API (see the header of that file for the full command). In `.env` set `IDENTITY_AUTHORITY=http://<server-ip>:5001` and `LINGUABRIDGE_WEB_URL=http://<server-ip>:3000`. Then open `http://<server-ip>:3000`:

- `config.js` takes the STS host from the address the page was opened with (`http://<that host>:5001`), so the same files work on `localhost` and on the server.
- The STS must know the address: `docker-compose.identity.server.yml` passes `LINGUABRIDGE_WEB_URL` to it as sign-in and sign-out addresses of the `linguabridge-app` client. They are added to an existing database when the STS starts (nothing is removed).
- Over plain `http` from another machine the page is not a secure context and has no `crypto.subtle`; `assets/js/sha256.js` computes the PKCE challenge then. Prefer https for anything beyond a private network.

## Why the dev server proxies two prefixes

Neither the STS nor the LinguaBridge API sends CORS headers, so a page on `localhost:3000` cannot call them from the browser. `LinguaBridge.Web.Host` (and `start-web.ps1`, which does the same) is therefore also a tiny same-origin proxy:

| Browser calls | Forwarded to |
|---|---|
| `/sts/*` (token endpoint) | `http://localhost:5001/*` (`Web:StsUrl`) |
| `/lb/*` | `http://localhost:5020/*` (`Web:ApiUrl`) |

`Web:StsUrl`, `Web:ApiUrl` and `Web:Root` (the folder served, default: the parent of the project) can be set in `LinguaBridge.Web.Host/appsettings.json` or with environment variables (`Web__StsUrl`). `/` is `index.html` and `/callback` is `callback.html`. If a target is not running, the proxy answers 502 and names it. Only these two targets are reachable and cookies are not forwarded. Once the backend allows this origin (CORS), set `apiBaseUrl` and `tokenEndpoint` in `config.js` to the real addresses and the proxy is no longer needed (any static server will do, as long as `/callback` serves `callback.html`).

## API used

`GET /api/v1/me`, `GET /api/v1/billing/entitlement`, `GET /api/content/courses?targetLanguage=en|zh&baseLanguage=vi|en`, `GET /api/content/courses/{id}/lessons`, `GET /api/v1/learning/progress/{courseId}`, `GET /api/v1/learning/srs/due`, `POST /api/v1/learning/srs/review` (`rating`: 1 Again, 2 Hard, 3 Good, 4 Easy).

## Notes

- Bootstrap and Bootstrap Icons are loaded from jsDelivr (internet needed on first load).
- Text from the API is always escaped before it is put into the page.
- Files: `assets/js/{config,auth,api,app}.js`, `assets/css/style.css`; `LinguaBridge.Web.Host/` is the development host (.NET 10) that serves them and proxies the two prefixes.
