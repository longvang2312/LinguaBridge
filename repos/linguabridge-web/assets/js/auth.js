// OpenID Connect sign-in: Authorization Code flow with PKCE, done by hand with the Web Crypto API.
// Tokens live in sessionStorage: they go away with the tab, and no other origin can read them.

import { config } from './config.js';
import { sha256 } from './sha256.js';

const TOKENS_KEY = 'lb.tokens';
const FLOW_KEY = 'lb.flow';
const EXPIRY_MARGIN_SECONDS = 30;

// ---- Small helpers -------------------------------------------------------------------------------------------------

const read = (key) => {
  try { return JSON.parse(sessionStorage.getItem(key) ?? 'null'); } catch { return null; }
};
const write = (key, value) => sessionStorage.setItem(key, JSON.stringify(value));

export function base64Url(bytes) {
  let text = '';
  for (const byte of bytes) text += String.fromCharCode(byte);
  return btoa(text).replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '');
}

export function randomString(byteLength = 32) {
  return base64Url(crypto.getRandomValues(new Uint8Array(byteLength)));
}

/** RFC 7636: BASE64URL(SHA-256(code_verifier)). */
export async function codeChallengeFor(verifier) {
  const bytes = new TextEncoder().encode(verifier);
  // crypto.subtle exists only in a secure context (https or http://localhost); elsewhere (http://<server-ip>) use the fallback.
  const digest = globalThis.crypto?.subtle ? new Uint8Array(await crypto.subtle.digest('SHA-256', bytes)) : sha256(bytes);
  return base64Url(digest);
}

export function decodeJwtPayload(jwt) {
  try {
    const payload = jwt.split('.')[1].replace(/-/g, '+').replace(/_/g, '/');
    const json = new TextDecoder().decode(Uint8Array.from(atob(payload), (c) => c.charCodeAt(0)));
    return JSON.parse(json);
  } catch {
    return null;
  }
}

// ---- Session -------------------------------------------------------------------------------------------------------

export const isSignedIn = () => !!read(TOKENS_KEY)?.accessToken;

/** The signed-in learner as the id_token describes them, or null. */
export function currentUser() {
  const tokens = read(TOKENS_KEY);
  const claims = tokens?.idToken ? decodeJwtPayload(tokens.idToken) : null;
  if (!claims) return null;
  return { id: claims.sub, email: claims.email ?? null, name: claims.name ?? claims.preferred_username ?? claims.email ?? claims.sub };
}

function storeTokenResponse(response, previous) {
  write(TOKENS_KEY, {
    accessToken: response.access_token,
    idToken: response.id_token ?? previous?.idToken ?? null,
    // A refresh response may or may not rotate the refresh token; keep the old one if it does not.
    refreshToken: response.refresh_token ?? previous?.refreshToken ?? null,
    expiresAt: Math.floor(Date.now() / 1000) + (response.expires_in ?? 300),
  });
}

async function requestToken(parameters) {
  const response = await fetch(config.tokenEndpoint, {
    method: 'POST',
    headers: { 'Content-Type': 'application/x-www-form-urlencoded', Accept: 'application/json' },
    body: new URLSearchParams({ client_id: config.clientId, ...parameters }),
  });
  const body = await response.json().catch(() => ({}));
  if (!response.ok) {
    const error = new Error(body.error_description ?? body.error ?? `Token request failed (${response.status})`);
    error.code = body.error ?? 'token_error';
    throw error;
  }
  return body;
}

// ---- Sign in / out -------------------------------------------------------------------------------------------------

export const LOGIN_PAGE = '/login.html';
const HOME_PAGE = '/index.html';

/**
 * A return address is only ever a path on this site. Anything else (another origin, "//host", "/\host", a scheme) would
 * make the login page an open redirect, and the login and callback pages themselves would loop, so both fall back to home.
 */
export function safeReturnUrl(value, fallback = HOME_PAGE) {
  if (typeof value !== 'string' || value === '') return fallback;
  try {
    const url = new URL(value, window.location.origin);
    if (url.origin !== window.location.origin) return fallback;
    if (url.pathname === LOGIN_PAGE || url.pathname === '/callback' || url.pathname === '/callback.html') return fallback;
    return url.pathname + url.search + url.hash;
  } catch {
    return fallback;
  }
}

/** The address of the login page that brings the learner back to returnTo afterwards. */
export const loginPageUrl = (returnTo = window.location.pathname + window.location.search, { loggedOut = false } = {}) =>
  `${LOGIN_PAGE}?returnUrl=${encodeURIComponent(safeReturnUrl(returnTo))}${loggedOut ? '&loggedOut=1' : ''}`;

/**
 * Sends the browser to the STS login page. After signing in the STS returns to /callback, which sends the learner on to
 * returnTo. With forceLogin the STS must ask for credentials even if its own session is still alive (prompt=login).
 */
export async function login(returnTo = window.location.pathname + window.location.search, { forceLogin = false } = {}) {
  const verifier = randomString(48);
  const flow = { verifier, state: randomString(16), nonce: randomString(16), returnTo: safeReturnUrl(returnTo) };
  write(FLOW_KEY, flow);

  const query = new URLSearchParams({
    client_id: config.clientId,
    response_type: 'code',
    redirect_uri: config.redirectUri,
    scope: config.scope,
    state: flow.state,
    nonce: flow.nonce,
    code_challenge: await codeChallengeFor(verifier),
    code_challenge_method: 'S256',
  });
  if (forceLogin) query.set('prompt', 'login');
  window.location.assign(`${config.stsUrl}/connect/authorize?${query}`);
}

/**
 * Called by callback.html: checks the answer of the STS, exchanges the code for tokens and stores them.
 * Returns the address the learner was on when they asked to sign in.
 */
export async function completeLogin(search = window.location.search) {
  const params = new URLSearchParams(search);
  const flow = read(FLOW_KEY);
  sessionStorage.removeItem(FLOW_KEY); // a login attempt is single-use whatever happens next

  if (params.has('error')) throw new Error(params.get('error_description') ?? params.get('error'));
  if (!flow) throw new Error('Không tìm thấy phiên đăng nhập đang chờ. Hãy thử đăng nhập lại.');
  if (params.get('state') !== flow.state) throw new Error('Phản hồi đăng nhập không khớp (state). Hãy thử lại.');
  const code = params.get('code');
  if (!code) throw new Error('STS không trả về mã xác thực.');

  const response = await requestToken({
    grant_type: 'authorization_code',
    code,
    redirect_uri: config.redirectUri,
    code_verifier: flow.verifier,
  });

  const idClaims = response.id_token ? decodeJwtPayload(response.id_token) : null;
  if (idClaims && idClaims.nonce !== flow.nonce) throw new Error('id_token không khớp nonce.');

  storeTokenResponse(response, null);
  return safeReturnUrl(flow.returnTo);
}

// The STS only redirects to a post_logout_redirect_uri registered exactly as written (no query string allowed), so the
// page to come back to travels in the standard `state` parameter, which the STS echoes back.
const LOGOUT_STATE_PREFIX = 'lb-logout.';
const logoutStateFor = (returnTo) => LOGOUT_STATE_PREFIX + base64Url(new TextEncoder().encode(safeReturnUrl(returnTo)));

/**
 * On the login page, turns the STS's answer to a logout (/login.html?state=lb-logout.<returnUrl>) into the address the
 * page works with (/login.html?returnUrl=<returnUrl>&loggedOut=1). Runs when this module loads, before the page's own
 * code reads the address.
 */
function restoreLogoutLanding() {
  if (window.location.pathname !== LOGIN_PAGE) return;
  const state = new URLSearchParams(window.location.search).get('state');
  if (!state?.startsWith(LOGOUT_STATE_PREFIX)) return;
  let returnTo = null;
  try {
    const bytes = Uint8Array.from(atob(state.slice(LOGOUT_STATE_PREFIX.length).replace(/-/g, '+').replace(/_/g, '/')), (c) => c.charCodeAt(0));
    returnTo = new TextDecoder().decode(bytes);
  } catch { /* a state we did not make: ignore it */ }
  window.history.replaceState(null, '', loginPageUrl(returnTo ?? undefined, { loggedOut: returnTo !== null }));
}
restoreLogoutLanding();

/**
 * Single sign-out (OpenID Connect RP-Initiated Logout): forgets the tokens here, then sends the browser to the STS
 * logout endpoint with the id_token as proof of who is asking. The STS ends its own session and redirects straight to
 * post_logout_redirect_uri, the login page, which says the learner signed out and remembers where they were (returnTo).
 * Without an id_token (nothing to prove with) the STS would only show its confirmation page, so go to the login page
 * directly; that page then asks the STS for credentials again.
 */
export function logout(returnTo = window.location.pathname + window.location.search) {
  const idToken = read(TOKENS_KEY)?.idToken;
  clearSession();

  if (!idToken) {
    window.location.assign(loginPageUrl(returnTo, { loggedOut: true }));
    return;
  }
  const query = new URLSearchParams({
    id_token_hint: idToken,
    post_logout_redirect_uri: `${window.location.origin}${LOGIN_PAGE}`,
    state: logoutStateFor(returnTo),
  });
  window.location.assign(`${config.stsUrl}/connect/logout?${query}`);
}

export function clearSession() {
  sessionStorage.removeItem(TOKENS_KEY);
  sessionStorage.removeItem(FLOW_KEY);
}

// ---- Access token --------------------------------------------------------------------------------------------------

let refreshing = null;

async function refresh() {
  const tokens = read(TOKENS_KEY);
  if (!tokens?.refreshToken) return false;
  // Several requests may find the token expired at once; they share one refresh.
  refreshing ??= requestToken({ grant_type: 'refresh_token', refresh_token: tokens.refreshToken })
    .then((response) => { storeTokenResponse(response, tokens); return true; })
    .catch(() => false)
    .finally(() => { refreshing = null; });
  return refreshing;
}

/** A usable access token, refreshed when it is about to expire; null when the learner has to sign in again. */
export async function getAccessToken({ forceRefresh = false } = {}) {
  let tokens = read(TOKENS_KEY);
  if (!tokens?.accessToken) return null;

  const expired = tokens.expiresAt - Math.floor(Date.now() / 1000) <= EXPIRY_MARGIN_SECONDS;
  if (forceRefresh || expired) {
    if (!(await refresh())) return null;
    tokens = read(TOKENS_KEY);
  }
  return tokens?.accessToken ?? null;
}
