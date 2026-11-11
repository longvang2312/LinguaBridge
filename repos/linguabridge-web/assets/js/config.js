// Settings of the web client. Change them here; nothing else in the app hard-codes an address.

const origin = window.location.origin;

export const config = {
  // The STS as the BROWSER reaches it. It must be this exact text: the API only accepts tokens issued through the
  // address it was configured with (Authentication:Authority = http://localhost:5001).
  stsUrl: 'http://localhost:5001',

  // Public client seeded in the STS (Authorization Code + PKCE, no secret).
  clientId: 'linguabridge-app',
  scope: 'openid profile email offline_access linguabridge_api',

  // The STS only knows http://localhost:3000/callback (and :5173) for this client. start-web.ps1 serves callback.html
  // at /callback, so the page can keep its name while the redirect address matches what the STS has on record.
  redirectUri: `${origin}/callback`,

  // Where the browser sends token and API requests. Neither the STS nor the API sends CORS headers, so a page on
  // localhost:3000 cannot call them directly. start-web.ps1 forwards these two prefixes (same origin, no CORS):
  //   /sts/*  ->  STS                (the token endpoint)
  //   /lb/*   ->  LinguaBridge API   (http://localhost:5020)
  // Once the backend allows this origin (CORS), set apiBaseUrl to 'http://localhost:5020' and tokenEndpoint to
  // `${stsUrl}/connect/token` to talk to them directly.
  apiBaseUrl: '/lb',
  tokenEndpoint: '/sts/connect/token',

  // Language the labels (titles, translations) are shown in: 'vi' or 'en'. The learner can switch it in the navbar.
  defaultBaseLanguage: 'vi',
};
