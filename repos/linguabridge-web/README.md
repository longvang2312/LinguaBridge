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

1. Start the backend (Identity + LinguaBridge): `./scripts/docker-all-up.ps1`
2. Start the web client: `./scripts/start-web.ps1` (opens http://localhost:3000; `-NoBrowser` to skip, `-Port` to change)
3. Sign in with the seeded account `admin@appinhouse.local` / `Admin@AppInHouse123!` (development only).

If PowerShell refuses to run the script (execution policy): `powershell -ExecutionPolicy Bypass -File scripts/start-web.ps1`.

Open the site at **http://localhost:3000** exactly, not `127.0.0.1`: the STS only knows `http://localhost:3000/callback` (and `:5173`) as redirect addresses of the `linguabridge-app` client.

## How sign-in works

`assets/js/auth.js` implements Authorization Code + PKCE by hand:

1. `login()` creates a random `code_verifier`, derives `code_challenge = BASE64URL(SHA-256(verifier))` with `crypto.subtle`, plus `state` and `nonce`, keeps them in `sessionStorage`, and redirects to `{STS}/connect/authorize` (client `linguabridge-app`, scope `openid profile email offline_access linguabridge_api`).
2. After the STS login page it returns to `/callback`. `completeLogin()` checks `state`, exchanges the code (with the verifier) at the token endpoint, checks the id_token `nonce`, and stores access, id and refresh tokens in `sessionStorage` (gone when the tab closes).
3. `assets/js/api.js` adds `Authorization: Bearer <token>` to every call. A token about to expire is refreshed first; a 401 triggers one refresh and one retry, then a new sign-in (silent when the STS session is still alive).
4. Every sign-in button leads to `login.html?returnUrl=<current page>`; its button starts the flow with that address, and `/callback` sends the learner back to it. The address is checked (`safeReturnUrl`): only paths on this site are accepted, anything else (another origin, `//host`, the login/callback pages) falls back to the home page.
5. Logout clears the tokens, sets a flag in `localStorage` (shared by every tab) and goes to `login.html?returnUrl=<current page>&loggedOut=1`, which says the learner signed out; signing in again returns to the page they left. The STS session is not ended (the client has no `post_logout_redirect_uri`), so while the flag is set EVERY sign-in (any tab, any button) adds `prompt=login` and the STS asks for the password again; a successful sign-in clears the flag.
6. Loop guard: after a 401 the app signs in again at most once per minute. If the API still refuses the fresh token (for example API and STS configured with different addresses or secrets), the page shows an error instead of redirecting forever. Any successful API call resets the guard.

The STS address in `assets/js/config.js` must be `http://host.docker.internal:5001`: the API only accepts tokens issued through the address it was configured with.

## Why the dev server proxies two prefixes

Neither the STS nor the LinguaBridge API sends CORS headers, so a page on `localhost:3000` cannot call them from the browser. `start-web.ps1` is therefore also a tiny same-origin proxy:

| Browser calls | Forwarded to |
|---|---|
| `/sts/*` (token endpoint) | `http://host.docker.internal:5001/*` |
| `/lb/*` | `http://localhost:5020/*` |

Only these two targets are reachable and cookies are not forwarded. Once the backend allows this origin (CORS), set `apiBaseUrl` and `tokenEndpoint` in `config.js` to the real addresses and the proxy is no longer needed (any static server will do, as long as `/callback` serves `callback.html`).

## API used

`GET /api/v1/me`, `GET /api/v1/billing/entitlement`, `GET /api/content/courses?targetLanguage=en|zh&baseLanguage=vi|en`, `GET /api/content/courses/{id}/lessons`, `GET /api/v1/learning/progress/{courseId}`, `GET /api/v1/learning/srs/due`, `POST /api/v1/learning/srs/review` (`rating`: 1 Again, 2 Hard, 3 Good, 4 Easy).

## Notes

- Bootstrap and Bootstrap Icons are loaded from jsDelivr (internet needed on first load).
- Text from the API is always escaped before it is put into the page.
- Files: `assets/js/{config,auth,api,app}.js`, `assets/css/style.css`.
