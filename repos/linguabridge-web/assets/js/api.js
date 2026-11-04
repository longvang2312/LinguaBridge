// The LinguaBridge API. Every call carries the learner's access token; a 401 means the token is no longer good.

import { config } from './config.js';
import { clearSession, getAccessToken, login } from './auth.js';

export class ApiError extends Error {
  constructor(status, problem) {
    super(problem?.detail ?? problem?.title ?? `Yêu cầu thất bại (${status})`);
    this.status = status;
    this.code = problem?.code ?? problem?.errors?.[0]?.code ?? null;
    this.problem = problem;
  }
}

async function send(path, options, token) {
  const headers = { Accept: 'application/json', Authorization: `Bearer ${token}`, ...(options.headers ?? {}) };
  if (options.body !== undefined) headers['Content-Type'] = 'application/json';
  return fetch(`${config.apiBaseUrl}${path}`, {
    method: options.method ?? 'GET',
    headers,
    body: options.body === undefined ? undefined : JSON.stringify(options.body),
  });
}

/** The API keeps refusing tokens the STS has just issued: signing in again cannot help, so we stop and say so. */
export class AuthLoopError extends Error {
  constructor() {
    super('API từ chối phiên đăng nhập vừa được cấp. Có thể API và STS cấu hình không khớp (Authority hoặc secret). Hãy báo quản trị viên.');
    this.name = 'AuthLoopError';
  }
}

const REAUTH_KEY = 'lb.reauth';
const REAUTH_WINDOW_MS = 60_000;
let leaving = false;

const recentlyReauthenticated = () => {
  try { return Date.now() - Number(sessionStorage.getItem(REAUTH_KEY) ?? 0) < REAUTH_WINDOW_MS; } catch { return false; }
};

/**
 * Signs the learner in again (and never returns): used when there is no token or the API rejects the one we have.
 * Only once per minute. The STS session usually survives, so the sign-in is silent and the page lands back here; if
 * the API then refuses the brand-new token again, another round would repeat forever (and hammer the STS), so that
 * case is reported as an error instead. A successful API call resets the guard.
 */
async function reauthenticate() {
  if (leaving) return new Promise(() => {}); // another call on this page already started the redirect
  if (recentlyReauthenticated()) throw new AuthLoopError();

  leaving = true;
  try { sessionStorage.setItem(REAUTH_KEY, String(Date.now())); } catch { /* without storage the guard cannot work; sign in anyway */ }
  clearSession();
  await login();
  return new Promise(() => {}); // the browser is leaving the page
}

export async function apiFetch(path, options = {}) {
  let token = await getAccessToken();
  if (!token) return reauthenticate();

  let response = await send(path, options, token);
  if (response.status === 401) {
    // The token may have been revoked or expired early: one refresh, one retry, then sign in again.
    token = await getAccessToken({ forceRefresh: true });
    if (!token) return reauthenticate();
    response = await send(path, options, token);
    if (response.status === 401) return reauthenticate();
  }

  try { sessionStorage.removeItem(REAUTH_KEY); } catch { /* nothing to reset */ }
  if (response.status === 204) return null;
  const body = await response.json().catch(() => null);
  if (!response.ok) throw new ApiError(response.status, body);
  return body;
}

const query = (values) => {
  const params = new URLSearchParams();
  for (const [key, value] of Object.entries(values)) if (value !== undefined && value !== null && value !== '') params.set(key, value);
  const text = params.toString();
  return text ? `?${text}` : '';
};

export const api = {
  me: () => apiFetch('/api/v1/me'),
  entitlement: () => apiFetch('/api/v1/billing/entitlement'),
  courses: (targetLanguage, baseLanguage) => apiFetch(`/api/content/courses${query({ targetLanguage, baseLanguage })}`),
  lessons: (courseId, baseLanguage) => apiFetch(`/api/content/courses/${courseId}/lessons${query({ baseLanguage })}`),
  course: (courseId, baseLanguage) => apiFetch(`/api/content/courses/${courseId}${query({ baseLanguage })}`),
  progress: (courseId) => apiFetch(`/api/v1/learning/progress/${courseId}`),
  dueCards: ({ baseLanguage, targetLanguage, limit } = {}) => apiFetch(`/api/v1/learning/srs/due${query({ baseLanguage, targetLanguage, limit })}`),
  // Lesson player: the learner's quiz, then an attempt that collects one graded answer per question and is completed once.
  quiz: (lessonId, baseLanguage) => apiFetch(`/api/v1/learning/lessons/${lessonId}/quiz${query({ baseLanguage })}`),
  startAttempt: (lessonId) => apiFetch('/api/v1/learning/attempts', { method: 'POST', body: { lessonId } }),
  // answer: { questionId, selectedOptionId } for multiple choice, { questionId, userInputText } for typed answers; plus timeSpentMs.
  submitResponse: (attemptId, answer, baseLanguage) =>
    apiFetch(`/api/v1/learning/attempts/${attemptId}/responses${query({ baseLanguage })}`, { method: 'POST', body: answer }),
  completeAttempt: (attemptId) => apiFetch(`/api/v1/learning/attempts/${attemptId}/complete`, { method: 'PUT' }),
  // rating: 1 Again, 2 Hard, 3 Good, 4 Easy
  reviewCard: (cardId, rating) => apiFetch('/api/v1/learning/srs/review', { method: 'POST', body: { cardId, rating } }),
};
