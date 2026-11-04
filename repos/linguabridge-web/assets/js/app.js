// Screen logic. Every page loads this module and names itself with <body data-page="...">.

import { completeLogin, currentUser, isSignedIn, login, loginPageUrl, logout, safeReturnUrl } from './auth.js';
import { api, ApiError, AuthLoopError } from './api.js';
import { applyTranslations, getLanguage, setLanguage, t } from './i18n.js';

// ---- Helpers -------------------------------------------------------------------------------------------------------

const $ = (selector, root = document) => root.querySelector(selector);

/** Text coming from the API is data, never markup: every value put into a template goes through this. */
export function esc(value) {
  return String(value ?? '').replace(/[&<>"']/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' })[c]);
}

// The interface language is also the language the API is asked for (see i18n.js).
const baseLanguage = getLanguage;
const targetName = (code) => t(`lang.${code}`);

function describeError(error) {
  if (error instanceof AuthLoopError) return t('error.authLoop');
  if (error instanceof ApiError) {
    if (error.status === 403) return t('error.forbidden');
    return error.message;
  }
  return t('error.network');
}

function alertHtml(message, kind = 'danger') {
  return `<div class="alert alert-${kind} d-flex align-items-start gap-2" role="alert"><i class="bi bi-exclamation-triangle-fill"></i><div>${esc(message)}</div></div>`;
}

const spinner = (label = t('common.loading')) =>
  `<div class="text-center text-secondary py-5"><div class="spinner-border" role="status"></div><div class="mt-2">${esc(label)}</div></div>`;

// ---- Navbar --------------------------------------------------------------------------------------------------------

function renderNavbar(page) {
  const user = currentUser();
  const link = (href, key, icon, text) =>
    `<li class="nav-item"><a class="nav-link${page === key ? ' active' : ''}" href="${href}"><i class="bi ${icon} me-1"></i>${text}</a></li>`;

  $('#navbar').innerHTML = `
    <nav class="navbar navbar-expand-lg lb-navbar sticky-top">
      <div class="container">
        <a class="navbar-brand fw-bold" href="/index.html"><i class="bi bi-translate me-2"></i>LinguaBridge</a>
        <button class="navbar-toggler" type="button" data-bs-toggle="collapse" data-bs-target="#nav" aria-controls="nav" aria-expanded="false" aria-label="${esc(t('nav.menu'))}">
          <span class="navbar-toggler-icon"></span>
        </button>
        <div class="collapse navbar-collapse" id="nav">
          <ul class="navbar-nav me-auto mb-2 mb-lg-0">
            ${link('/index.html', 'home', 'bi-house-door', t('nav.home'))}
            ${link('/courses.html', 'courses', 'bi-journal-richtext', t('nav.courses'))}
            ${link('/flashcards.html', 'flashcards', 'bi-layers', t('nav.review'))}
          </ul>
          <div class="d-flex align-items-center gap-2 flex-wrap">
            <select id="base-language" class="form-select form-select-sm w-auto" aria-label="${esc(t('nav.language'))}" title="${esc(t('nav.language'))}">
              <option value="vi">Tiếng Việt</option>
              <option value="en">English</option>
            </select>
            ${page === 'login' ? '' : user
              ? `<span class="navbar-text text-white-50 small" id="user-label"><i class="bi bi-person-circle me-1"></i>${esc(user.email ?? user.name)}</span>
                 <button class="btn btn-outline-light btn-sm" id="logout-button"><i class="bi bi-box-arrow-right me-1"></i>${esc(t('nav.logout'))}</button>`
              : `<button class="btn btn-warning btn-sm" id="login-button"><i class="bi bi-box-arrow-in-right me-1"></i>${esc(t('nav.login'))}</button>`}
          </div>
        </div>
      </div>
    </nav>`;

  const select = $('#base-language');
  select.value = baseLanguage();
  select.addEventListener('change', () => {
    setLanguage(select.value);
    // Course, lesson and card text comes from the API in the chosen language, so the page is rebuilt from scratch.
    document.body.style.transition = 'opacity .15s ease';
    document.body.style.opacity = '0';
    window.setTimeout(() => window.location.reload(), 150);
  });
  $('#login-button')?.addEventListener('click', () => window.location.assign(loginPageUrl()));
  $('#logout-button')?.addEventListener('click', () => logout(window.location.pathname + window.location.search));
}

// ---- Home / dashboard ----------------------------------------------------------------------------------------------

async function loadCourses() {
  const [en, zh] = await Promise.all([api.courses('en', baseLanguage()), api.courses('zh', baseLanguage())]);
  return [...en, ...zh];
}

async function initHome() {
  const hero = $('#hero');
  const dashboard = $('#dashboard');
  if (!isSignedIn()) {
    hero.classList.remove('d-none');
    $('#hero-login').addEventListener('click', () => window.location.assign(loginPageUrl('/index.html')));
    return;
  }
  dashboard.classList.remove('d-none');
  const user = currentUser();
  $('#welcome-name').textContent = user?.email ?? user?.name ?? t('dash.fallbackName');

  try {
    const [me, due, entitlement, courses] = await Promise.all([
      api.me(),
      api.dueCards({ baseLanguage: baseLanguage(), limit: 100 }),
      api.entitlement(),
      loadCourses(),
    ]);

    $('#stat-user').textContent = user?.email ?? me.userId;
    $('#stat-user-id').textContent = me.userId;
    $('#stat-due').textContent = String(due.length);
    if (due.length >= 100) $('#stat-due').title = t('stat.dueMax');
    $('#stat-tier').textContent = entitlement.tier;
    $('#stat-courses').textContent = String(courses.length);

    const progress = await Promise.all(courses.map((course) => api.progress(course.id).catch(() => null)));
    const done = progress.reduce((sum, p) => sum + (p?.completedLessonsCount ?? 0), 0);
    const total = progress.reduce((sum, p) => sum + (p?.totalLessonsCount ?? 0), 0);
    const percent = total === 0 ? 0 : Math.round((done * 100) / total);
    $('#xp-bar').style.width = `${percent}%`;
    $('#xp-bar').setAttribute('aria-valuenow', String(percent));
    $('#xp-text').textContent = total === 0 ? t('progress.none') : t('progress.summary', { done, total, percent });

    $('#course-list').innerHTML = courses.length === 0
      ? `<div class="col-12 text-secondary">${esc(t('myCourses.none'))}</div>`
      : courses.map((course, index) => courseCard(course, progress[index])).join('');
  } catch (error) {
    $('#dashboard-error').innerHTML = alertHtml(describeError(error));
  }
}

function courseCard(course, progress) {
  const total = progress?.totalLessonsCount ?? 0;
  const done = progress?.completedLessonsCount ?? 0;
  const percent = total === 0 ? 0 : Math.round((done * 100) / total);
  return `
    <div class="col-md-6 col-lg-4">
      <a class="text-decoration-none" href="/courses.html?course=${encodeURIComponent(course.id)}&lang=${encodeURIComponent(course.targetLanguage)}">
        <div class="card lb-card h-100">
          <div class="card-body">
            <div class="d-flex justify-content-between align-items-start mb-2">
              <span class="badge text-bg-primary">${esc(targetName(course.targetLanguage))}</span>
              <span class="badge text-bg-light border">${esc(course.level)}</span>
            </div>
            <h3 class="h6 card-title text-body">${esc(course.title ?? course.code)}</h3>
            <div class="progress xp-progress mt-3" role="progressbar" aria-label="${esc(t('progress.course'))}" aria-valuenow="${percent}" aria-valuemin="0" aria-valuemax="100">
              <div class="progress-bar xp-fill" style="width:${percent}%"></div>
            </div>
            <div class="small text-secondary mt-1">${esc(t('common.lessonsCount', { done, total }))}</div>
          </div>
        </div>
      </a>
    </div>`;
}

// ---- Courses and lessons -------------------------------------------------------------------------------------------

async function initCourses() {
  if (!isSignedIn()) return showSignInPrompt('#content');

  const params = new URLSearchParams(window.location.search);
  let target = params.get('lang') === 'zh' ? 'zh' : 'en';
  const tabs = document.querySelectorAll('[data-target]');
  const grid = $('#courses');
  let courses = [];

  const render = () => {
    tabs.forEach((tab) => tab.classList.toggle('active', tab.dataset.target === target));
    grid.innerHTML = courses.length === 0
      ? `<div class="col-12 text-secondary py-4 text-center"><i class="bi bi-inbox fs-1 d-block"></i>${esc(t('courses.empty', { lang: targetName(target).toLowerCase() }))}</div>`
      : courses.map((course) => `
        <div class="col-md-6 col-xl-4">
          <div class="card lb-card h-100">
            <div class="card-body d-flex flex-column">
              <div class="d-flex justify-content-between align-items-start mb-2">
                <span class="badge text-bg-primary">${esc(targetName(course.targetLanguage))}</span>
                <span class="badge text-bg-light border">${esc(course.level)}</span>
              </div>
              <h3 class="h5 card-title">${esc(course.title ?? course.code)}</h3>
              <p class="card-text text-secondary flex-grow-1">${esc(course.description ?? t('courses.noDescription'))}</p>
              <div class="mb-3">
                <span class="badge rounded-pill text-bg-secondary"><i class="bi bi-hash"></i>${esc(course.code)}</span>
                <span class="badge rounded-pill text-bg-secondary">v${esc(course.version)}</span>
              </div>
              <button class="btn btn-outline-primary" data-course="${esc(course.id)}"><i class="bi bi-list-ul me-1"></i>${esc(t('courses.viewLessons'))}</button>
            </div>
          </div>
        </div>`).join('');
    grid.querySelectorAll('[data-course]').forEach((button) =>
      button.addEventListener('click', () => openLessons(courses.find((c) => c.id === button.dataset.course))));
  };

  const load = async () => {
    grid.innerHTML = `<div class="col-12">${spinner()}</div>`;
    $('#courses-error').innerHTML = '';
    try {
      courses = await api.courses(target, baseLanguage());
      render();
    } catch (error) {
      grid.innerHTML = '';
      $('#courses-error').innerHTML = alertHtml(describeError(error));
    }
  };

  tabs.forEach((tab) => tab.addEventListener('click', () => { target = tab.dataset.target; load(); }));
  await load();

  const requested = params.get('course');
  const course = courses.find((c) => c.id === requested);
  if (course) openLessons(course);
}

async function openLessons(course) {
  if (!course) return;
  const modalElement = $('#lessons-modal');
  $('#lessons-title').textContent = course.title ?? course.code;
  const body = $('#lessons-body');
  body.innerHTML = spinner();
  const modal = bootstrap.Modal.getOrCreateInstance(modalElement);
  modal.show();

  try {
    const [lessons, progress] = await Promise.all([api.lessons(course.id, baseLanguage()), api.progress(course.id).catch(() => null)]);
    const header = progress
      ? `<div class="small text-secondary mb-3">${esc(t('lessons.completed', { done: progress.completedLessonsCount, total: progress.totalLessonsCount }))}${progress.isCompleted ? ' <i class="bi bi-patch-check-fill text-success"></i>' : ''}</div>`
      : '';
    body.innerHTML = header + (lessons.length === 0
      ? `<div class="text-secondary">${esc(t('lessons.none'))}</div>`
      : `<ol class="list-group list-group-numbered">${lessons.map((lesson) => `
          <li class="list-group-item d-flex justify-content-between align-items-start gap-2">
            <div class="ms-2 me-auto">
              <div class="fw-semibold">${esc(lesson.title ?? t('lessons.defaultTitle', { n: lesson.orderIndex }))}</div>
              ${lesson.guideText ? `<div class="small text-secondary">${esc(lesson.guideText)}</div>` : ''}
            </div>
            <span class="badge text-bg-light border">${esc(t(`lessonType.${lesson.lessonType}`))}</span>
            <span class="badge text-bg-warning"><i class="bi bi-lightning-charge-fill"></i> ${esc(t('common.xp', { n: lesson.xpReward }))}</span>
          </li>`).join('')}</ol>`);
  } catch (error) {
    body.innerHTML = alertHtml(describeError(error));
  }
}

// ---- Flashcards ----------------------------------------------------------------------------------------------------

// The four SM-2 grades the API accepts (1-4); the names are the standard ones, the hint is translated.
const RATINGS = [
  { value: 1, label: 'Again', hint: 'rating.again', css: 'danger' },
  { value: 2, label: 'Hard', hint: 'rating.hard', css: 'warning' },
  { value: 3, label: 'Good', hint: 'rating.good', css: 'primary' },
  { value: 4, label: 'Easy', hint: 'rating.easy', css: 'success' },
];

async function initFlashcards() {
  if (!isSignedIn()) return showSignInPrompt('#content');

  const stage = $('#stage');
  const error = $('#flash-error');
  const state = { cards: [], index: 0, flipped: false, busy: false, reviewed: 0 };

  const showCard = () => {
    const card = state.cards[state.index];
    state.flipped = false;
    $('#deck-position').textContent = `${state.index + 1}/${state.cards.length}`;
    $('#deck-bar').style.width = `${(state.index * 100) / state.cards.length}%`;
    $('#flashcard').classList.remove('is-flipped');
    $('#front-text').textContent = card.targetText;
    $('#front-meta').textContent = targetName(card.targetLanguage);
    $('#back-ipa').textContent = card.pinyinOrIpa ?? '';
    $('#back-meaning').textContent = card.promptTranslation ?? t('flash.noMeaning');
    $('#back-state').textContent = t('flash.state', { state: t(`srs.${card.state}`), n: card.repetitions, days: card.intervalDays });
    $('#rating-row').classList.add('invisible');
    $('#flip-hint').classList.remove('d-none');
  };

  const flip = () => {
    if (state.busy || state.cards.length === 0 || state.index >= state.cards.length) return;
    state.flipped = !state.flipped;
    $('#flashcard').classList.toggle('is-flipped', state.flipped);
    $('#rating-row').classList.toggle('invisible', !state.flipped);
    $('#flip-hint').classList.toggle('d-none', state.flipped);
  };

  const finish = () => {
    $('#deck').classList.add('d-none');
    $('#done').classList.remove('d-none');
    $('#done-text').textContent = t('flash.doneDesc', { n: state.reviewed });
  };

  const rate = async (value) => {
    if (!state.flipped || state.busy) return;
    state.busy = true;
    error.innerHTML = '';
    $('#rating-row').querySelectorAll('button').forEach((b) => { b.disabled = true; });
    try {
      await api.reviewCard(state.cards[state.index].cardId, value);
      state.reviewed += 1;
      state.index += 1;
      if (state.index >= state.cards.length) finish(); else showCard();
    } catch (e) {
      // 409: the card was reviewed elsewhere in the meantime; skip it. Anything else: let the learner retry.
      if (e instanceof ApiError && e.status === 409) {
        state.index += 1;
        if (state.index >= state.cards.length) finish(); else showCard();
      } else {
        error.innerHTML = alertHtml(describeError(e));
      }
    } finally {
      state.busy = false;
      $('#rating-row').querySelectorAll('button').forEach((b) => { b.disabled = false; });
    }
  };

  $('#rating-row').innerHTML = RATINGS.map((r) => `
    <div class="col-6 col-md-3">
      <button class="btn btn-${r.css} w-100 py-2" data-rating="${r.value}">
        <div class="fw-semibold">${r.label}</div><div class="small">${esc(t(r.hint))} · <kbd>${r.value}</kbd></div>
      </button>
    </div>`).join('');
  $('#rating-row').querySelectorAll('[data-rating]').forEach((b) => b.addEventListener('click', () => rate(Number(b.dataset.rating))));
  $('#flashcard').addEventListener('click', flip);
  $('#flashcard').addEventListener('keydown', (event) => { if (event.key === 'Enter') flip(); });
  document.addEventListener('keydown', (event) => {
    if (event.target.closest('select, input, textarea, .modal')) return;
    if (event.code === 'Space') { event.preventDefault(); flip(); }
    else if (['1', '2', '3', '4'].includes(event.key)) rate(Number(event.key));
  });

  const load = async () => {
    stage.classList.add('d-none');
    $('#loading').classList.remove('d-none');
    try {
      const language = $('#language-filter').value;
      state.cards = await api.dueCards({ baseLanguage: baseLanguage(), targetLanguage: language, limit: 50 });
      state.index = 0;
      state.reviewed = 0;
      $('#loading').classList.add('d-none');
      stage.classList.remove('d-none');
      $('#done').classList.add('d-none');
      $('#empty').classList.toggle('d-none', state.cards.length > 0);
      $('#deck').classList.toggle('d-none', state.cards.length === 0);
      if (state.cards.length > 0) showCard();
    } catch (e) {
      $('#loading').classList.add('d-none');
      error.innerHTML = alertHtml(describeError(e));
    }
  };

  $('#language-filter').addEventListener('change', load);
  $('#reload').addEventListener('click', load);
  await load();
}

// ---- OIDC callback -------------------------------------------------------------------------------------------------

async function initCallback() {
  $('#callback-status').querySelector('p').textContent = t('callback.finishing');
  document.title = t('title.callback');
  try {
    const returnTo = await completeLogin();
    window.location.replace(safeReturnUrl(returnTo));
  } catch (e) {
    $('#callback-status').innerHTML = `${alertHtml(e.message)}<a class="btn btn-primary" href="/index.html">${esc(t('common.backHome'))}</a>`;
  }
}

// ---- Shared --------------------------------------------------------------------------------------------------------

function showSignInPrompt(selector) {
  $(selector).innerHTML = `
    <div class="text-center py-5">
      <i class="bi bi-lock fs-1 text-secondary"></i>
      <h2 class="h4 mt-2">${esc(t('prompt.title'))}</h2>
      <p class="text-secondary">${esc(t('prompt.desc'))}</p>
      <button class="btn btn-primary" id="prompt-login"><i class="bi bi-box-arrow-in-right me-1"></i>${esc(t('prompt.button'))}</button>
    </div>`;
  $('#prompt-login').addEventListener('click', () => window.location.assign(loginPageUrl()));
}

function initLogin() {
  const params = new URLSearchParams(window.location.search);
  const returnUrl = safeReturnUrl(params.get('returnUrl'));
  const loggedOut = params.get('loggedOut') === '1';

  // Already signed in (for example the learner opened this page by hand): nothing to ask, go where they were headed.
  if (isSignedIn() && !loggedOut) {
    window.location.replace(returnUrl);
    return;
  }

  if (loggedOut) {
    $('#logout-alert').innerHTML = `
      <div class="alert alert-success d-flex align-items-center gap-2 mb-3" role="alert">
        <i class="bi bi-check-circle-fill"></i>
        <div>${esc(t('login.loggedOut'))}</div>
      </div>`;
  }

  // After a logout the STS session may still be alive, so make it ask for the password again.
  $('#btn-login-oidc').addEventListener('click', () => login(returnUrl, { forceLogin: loggedOut }));
}

// Static text first (it may sit inside areas the page code then fills in), then the page.
applyTranslations();
const pages = { home: initHome, courses: initCourses, flashcards: initFlashcards, callback: initCallback, login: initLogin };
const page = document.body.dataset.page;
if (page !== 'callback') renderNavbar(page);
pages[page]?.();
