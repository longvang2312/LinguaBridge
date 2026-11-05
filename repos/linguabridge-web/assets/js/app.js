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
            ${link('/tutor.html', 'tutor', 'bi-robot', t('nav.tutor'))}
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
            <a class="btn btn-sm btn-primary" data-start-lesson href="/lesson.html?lessonId=${encodeURIComponent(lesson.id)}&courseId=${encodeURIComponent(course.id)}"><i class="bi bi-play-fill"></i> ${esc(t('lesson.start'))}</a>
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


// ---- Lesson player -------------------------------------------------------------------------------------------------

const GUID = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;

async function initLesson() {
  const params = new URLSearchParams(window.location.search);
  const lessonId = params.get('lessonId');
  const courseId = GUID.test(params.get('courseId') ?? '') ? params.get('courseId') : null;

  // Not signed in: sign in first and come back to this very lesson (the address keeps its query string).
  if (!isSignedIn()) {
    window.location.replace(loginPageUrl());
    return;
  }

  const loading = $('#quiz-loading');
  const errorBox = $('#quiz-error');
  const questionBox = $('#quiz-question');
  const completeBox = $('#quiz-complete');
  const answerBar = $('#answer-bar');
  const feedback = $('#feedback');
  const checkButton = $('#check-button');

  const showFatal = (message, { retry = false } = {}) => {
    loading.classList.add('d-none');
    questionBox.classList.add('d-none');
    answerBar.classList.add('d-none');
    errorBox.innerHTML = `${alertHtml(message)}<div class="d-flex gap-2">
      ${retry ? `<button class="btn btn-primary" id="quiz-retry">${esc(t('quiz.retry'))}</button>` : ''}
      <a class="btn btn-outline-secondary" href="${esc(backToLessonsUrl())}">${esc(t('quiz.done.toLessons'))}</a></div>`;
    $('#quiz-retry')?.addEventListener('click', () => window.location.reload());
  };

  // Where "back" goes: the lessons modal of this course, on the tab of its language when it is known.
  let courseLanguage = null;
  const backToLessonsUrl = () => (courseId
    ? `/courses.html?course=${encodeURIComponent(courseId)}${courseLanguage ? `&lang=${courseLanguage}` : ''}`
    : '/courses.html');

  if (!GUID.test(lessonId ?? '')) {
    showFatal(t('quiz.invalid'));
    return;
  }

  const state = { quiz: null, attemptId: null, index: 0, selectedOptionId: null, phase: 'answering', busy: false, shownAt: 0, finished: false, optionOrder: [] };

  // ---- Leaving -------------------------------------------------------------------------------------------------------
  const leave = () => { window.location.assign(backToLessonsUrl()); };
  $('#exit-button').addEventListener('click', () => {
    if (state.finished || !state.quiz) { leave(); return; }   // nothing to lose yet
    bootstrap.Modal.getOrCreateInstance($('#exit-modal')).show();
  });
  $('#exit-confirm').addEventListener('click', leave);

  // ---- Loading ----------------------------------------------------------------------------------------------------------
  try {
    // The quiz first: it answers "not found" before an attempt is created for a lesson that cannot be played.
    const quiz = await api.quiz(lessonId, baseLanguage());
    state.quiz = { ...quiz, questions: [...quiz.questions].sort((a, b) => a.orderIndex - b.orderIndex) };
    const attempt = await api.startAttempt(lessonId);
    state.attemptId = attempt.attemptId;
  } catch (error) {
    showFatal(error instanceof ApiError && error.status === 404 ? t('quiz.notFound') : describeError(error), { retry: !(error instanceof ApiError && error.status === 404) });
    return;
  }

  // Optional extras for the top bar and the way back; the lesson works without them.
  if (courseId) {
    api.course(courseId, baseLanguage()).then((detail) => { courseLanguage = detail?.course?.targetLanguage ?? null; }).catch(() => {});
  }

  loading.classList.add('d-none');
  $('#quiz-xp').textContent = t('quiz.xpMax', { n: state.quiz.xpReward });
  answerBar.classList.remove('d-none');

  if (state.quiz.questions.length === 0) {
    questionBox.innerHTML = `<div class="quiz-card text-center text-secondary">${esc(t('quiz.noQuestions'))}</div>`;
    questionBox.classList.remove('d-none');
    await finishLesson();
    return;
  }
  showQuestion();

  // ---- One question ------------------------------------------------------------------------------------------------------
  function currentQuestion() { return state.quiz.questions[state.index]; }
  function isChoice(q) { return Array.isArray(q.options) && q.options.length > 0; }

  // Fisher-Yates on a copy. The options keep their ids, so the server still gets the id of the option that was chosen.
  function shuffled(items) {
    const copy = items.slice();
    for (let i = copy.length - 1; i > 0; i -= 1) {
      const j = Math.floor(Math.random() * (i + 1));
      [copy[i], copy[j]] = [copy[j], copy[i]];
    }
    return copy;
  }

  function updateProgress() {
    const total = state.quiz.questions.length;
    $('#quiz-position').textContent = t('quiz.position', { n: Math.min(state.index + 1, total), total });
    const percent = Math.round((state.index * 100) / total);
    $('#quiz-bar').style.width = `${percent}%`;
    $('.quiz-progress').setAttribute('aria-valuenow', String(percent));
  }

  function setCheckState({ label, enabled, kind = 'primary' }) {
    checkButton.textContent = label;
    checkButton.disabled = !enabled;
    checkButton.className = `btn btn-${kind} btn-lg w-100 fw-semibold`;
  }

  function showQuestion() {
    const q = currentQuestion();
    state.phase = 'answering';
    state.selectedOptionId = null;
    state.shownAt = Date.now();
    feedback.classList.add('d-none');
    feedback.innerHTML = '';
    answerBar.classList.remove('is-correct', 'is-wrong');
    updateProgress();

    const typeLabel = t(`quiz.type.${q.questionType}`);
    // The order shown is chosen once per question: keys 1-9 and the numbers on the cards both follow state.optionOrder.
    state.optionOrder = isChoice(q) ? shuffled(q.options) : [];
    let body;
    if (isChoice(q)) {
      // The question is the target text; its translation (when there is one) is a hint under it.
      body = `
        <div class="quiz-type mb-2">${esc(typeLabel)}</div>
        <div class="quiz-target">${esc(q.targetText ?? q.promptTranslation ?? '')}</div>
        ${q.pinyinOrIpa ? `<div class="quiz-ipa mt-1">${esc(q.pinyinOrIpa)}</div>` : ''}
        ${q.promptTranslation && q.targetText ? `<div class="text-secondary mt-2">${esc(t('quiz.hint', { text: q.promptTranslation }))}</div>` : ''}
        <div class="d-grid gap-2 mt-4" id="quiz-options" role="group" aria-label="${esc(typeLabel)}">
          ${state.optionOrder.map((option, i) => `
            <button type="button" class="quiz-option" data-option-id="${esc(option.id)}">
              <span class="quiz-key">${i + 1}</span>
              <span>${esc(option.text)}${option.pinyinOrIpa ? ` <span class="text-secondary small">${esc(option.pinyinOrIpa)}</span>` : ''}</span>
            </button>`).join('')}
        </div>
        <div class="small text-secondary mt-3">${esc(t('quiz.optionHint', { n: q.options.length }))}</div>`;
    } else {
      // Typed answers: the target text is the answer, so the only thing shown is what to say, in the learner's language.
      body = `
        <div class="quiz-type mb-2">${esc(typeLabel)}</div>
        <div class="quiz-prompt">${esc(q.promptTranslation ?? q.targetText ?? '')}</div>
        <label class="visually-hidden" for="answer-input">${esc(t('quiz.inputLabel'))}</label>
        <input id="answer-input" class="form-control quiz-input mt-4" type="text" autocomplete="off" autocapitalize="off" autocorrect="off" spellcheck="false"
               placeholder="${esc(t('quiz.inputPlaceholder'))}" maxlength="500">`;
    }

    questionBox.innerHTML = `<div class="quiz-card">${body}</div>`;
    questionBox.classList.remove('d-none');
    setCheckState({ label: t('quiz.check'), enabled: false });

    if (isChoice(q)) {
      questionBox.querySelectorAll('[data-option-id]').forEach((button) => button.addEventListener('click', () => selectOption(button.dataset.optionId)));
    } else {
      const input = $('#answer-input');
      input.addEventListener('input', () => { if (state.phase === 'answering') checkButton.disabled = input.value.trim() === ''; });
      input.focus();
    }
  }

  function selectOption(optionId) {
    if (state.phase !== 'answering' || state.busy) return;
    state.selectedOptionId = optionId;
    questionBox.querySelectorAll('[data-option-id]').forEach((button) => button.classList.toggle('is-selected', button.dataset.optionId === optionId));
    checkButton.disabled = false;
  }

  async function checkAnswer() {
    if (state.phase !== 'answering' || state.busy) return;
    const q = currentQuestion();
    const answer = { questionId: q.id, timeSpentMs: Math.max(0, Date.now() - state.shownAt) };
    if (isChoice(q)) {
      if (!state.selectedOptionId) return;
      answer.selectedOptionId = state.selectedOptionId;
    } else {
      const text = $('#answer-input').value.trim();
      if (text === '') return;
      answer.userInputText = text;
    }

    state.busy = true;
    setCheckState({ label: t('quiz.checking'), enabled: false });
    try {
      const result = await api.submitResponse(state.attemptId, answer, baseLanguage());
      showResult(q, result);
    } catch (error) {
      state.busy = false;
      if (error instanceof ApiError && error.code === 'response.already_answered') {
        // The first request got through but its reply was lost: the answer is on record, so the learner moves on.
        showRecorded(q);
        return;
      }
      setCheckState({ label: t('quiz.check'), enabled: true });
      let message = describeError(error);
      if (error instanceof ApiError && error.code === 'attempt.already_completed') message = t('error.attemptClosed');
      feedback.innerHTML = alertHtml(message);
      feedback.classList.remove('d-none');
      return;
    }
    state.busy = false;
  }

  // Kept here, next to its only use, because this task may not touch the dictionary file.
  const RECORDED = {
    vi: ['Đã ghi nhận câu trả lời', 'Câu trả lời của bạn đã được lưu trước đó (có thể do kết nối bị gián đoạn). Hãy tiếp tục sang câu kế tiếp.'],
    en: ['Answer recorded', 'Your answer was already saved (the connection was probably interrupted). Please continue to the next question.'],
  };

  /** The server already has an answer to this question but we never saw whether it was right: lock it and offer to continue. */
  function showRecorded(q) {
    state.phase = 'feedback';
    if (isChoice(q)) {
      questionBox.querySelectorAll('[data-option-id]').forEach((button) => { button.disabled = true; });
    } else {
      $('#answer-input').readOnly = true;
    }
    answerBar.classList.remove('is-correct', 'is-wrong');
    feedback.innerHTML = `
      <div class="d-flex align-items-start gap-3">
        <i class="bi bi-cloud-check-fill feedback-icon text-secondary"></i>
        <div>
          <div class="h5 mb-1">${esc((RECORDED[baseLanguage()] ?? RECORDED.vi)[0])}</div>
          <div>${esc((RECORDED[baseLanguage()] ?? RECORDED.vi)[1])}</div>
        </div>
      </div>`;
    feedback.classList.remove('d-none');
    const last = state.index >= state.quiz.questions.length - 1;
    setCheckState({ label: last ? t('quiz.finish') : t('quiz.continue'), enabled: true, kind: 'secondary' });
    checkButton.focus();
  }

  function showResult(q, result) {
    state.phase = 'feedback';
    const correct = result.isCorrect === true;

    // Colour the options (or the input) so the learner sees what was right.
    if (isChoice(q)) {
      questionBox.querySelectorAll('[data-option-id]').forEach((button) => {
        button.disabled = true;
        const id = button.dataset.optionId;
        button.classList.remove('is-selected');
        if (correct && id === state.selectedOptionId) button.classList.add('is-correct');
        if (!correct && id === state.selectedOptionId) button.classList.add('is-wrong');
        if (!correct && id === result.correctOptionId) button.classList.add('is-correct');
      });
    } else {
      const input = $('#answer-input');
      input.readOnly = true;
      input.classList.add(correct ? 'is-correct' : 'is-wrong');
    }

    const praise = t(`quiz.correct.${(state.index % 3) + 1}`);
    const details = [];
    if (!correct && result.correctAnswer) {
      details.push(`<div><span class="fw-semibold">${esc(t('quiz.correctAnswer'))}:</span> <span class="fs-5">${esc(result.correctAnswer)}</span></div>`);
    }
    if (!correct && result.explanation) {
      details.push(`<div class="mt-1"><span class="fw-semibold">${esc(t('quiz.explanation'))}:</span> ${esc(result.explanation)}</div>`);
    }
    if (!correct && result.grammarNotes) {
      details.push(`<div class="mt-1"><span class="fw-semibold">${esc(t('quiz.grammar'))}:</span> ${esc(result.grammarNotes)}</div>`);
    }

    answerBar.classList.toggle('is-correct', correct);
    answerBar.classList.toggle('is-wrong', !correct);
    feedback.innerHTML = `
      <div class="d-flex align-items-start gap-3">
        <i class="bi ${correct ? 'bi-check-circle-fill' : 'bi-exclamation-triangle-fill'} feedback-icon"></i>
        <div>
          <div class="h5 mb-1 feedback-title">${esc(correct ? praise : t('quiz.wrong'))}</div>
          ${details.join('')}
        </div>
      </div>`;
    feedback.classList.remove('d-none');

    const last = state.index >= state.quiz.questions.length - 1;
    setCheckState({ label: last ? t('quiz.finish') : t('quiz.continue'), enabled: true, kind: correct ? 'success' : 'danger' });
    checkButton.focus();
  }

  async function next() {
    if (state.phase !== 'feedback' || state.busy) return;
    if (state.index >= state.quiz.questions.length - 1) {
      state.index += 1;
      updateProgress();
      await finishLesson();
      return;
    }
    state.index += 1;
    showQuestion();
  }

  async function finishLesson() {
    state.busy = true;
    state.phase = 'done';
    setCheckState({ label: t('quiz.checking'), enabled: false });
    let result;
    try {
      result = await api.completeAttempt(state.attemptId);
    } catch (error) {
      state.busy = false;
      state.phase = 'feedback';
      setCheckState({ label: t('quiz.finish'), enabled: true, kind: 'primary' });
      feedback.innerHTML = alertHtml(describeError(error));
      feedback.classList.remove('d-none');
      return;
    }
    state.busy = false;
    state.finished = true;
    $('#quiz-bar').style.width = '100%';
    renderComplete(result);
  }

  function renderComplete(result) {
    questionBox.classList.add('d-none');
    answerBar.classList.add('d-none');
    answerBar.classList.remove('is-correct', 'is-wrong');
    const great = result.scorePercentage >= 80;
    const progress = result.progress;
    completeBox.innerHTML = `
      <div class="trophy">${great ? '🏆' : result.scorePercentage >= 50 ? '⭐' : '💪'}</div>
      <h1 class="h2 mt-3">${esc(t(great ? 'quiz.done.titleGreat' : result.scorePercentage >= 50 ? 'quiz.done.title' : 'quiz.done.titleKeepGoing'))}</h1>
      <div class="row g-3 my-3 justify-content-center">
        <div class="col-4"><div class="quiz-stat"><div class="value text-primary">${esc(result.scorePercentage)}%</div><div class="small text-secondary">${esc(t('quiz.done.score'))}</div></div></div>
        <div class="col-4"><div class="quiz-stat"><div class="value text-success">${esc(result.correctCount)}/${esc(result.totalQuestions)}</div><div class="small text-secondary">${esc(t('quiz.done.correct'))}</div></div></div>
        <div class="col-4"><div class="quiz-stat"><div class="value text-warning-emphasis"><i class="bi bi-lightning-charge-fill text-warning"></i>${esc(result.xpEarned)}</div><div class="small text-secondary">${esc(t('quiz.done.xp'))}</div></div></div>
      </div>
      ${result.isFirstCompletion ? '' : `<p class="text-secondary">${esc(t('quiz.done.noXp'))}</p>`}
      ${progress ? `<p class="text-secondary mb-1">${esc(t('quiz.done.progress', { done: progress.completedLessonsCount, total: progress.totalLessonsCount }))}${progress.isCompleted ? ` <i class="bi bi-patch-check-fill text-success"></i><br><strong>${esc(t('quiz.done.courseDone'))}</strong>` : ''}</p>` : ''}
      <div class="d-grid gap-2 col-md-8 mx-auto mt-4">
        <a class="btn btn-primary btn-lg" id="done-lessons" href="${esc(backToLessonsUrl())}"><i class="bi bi-list-ul me-1"></i>${esc(t('quiz.done.toLessons'))}</a>
        <a class="btn btn-outline-primary btn-lg" id="done-flashcards" href="/flashcards.html"><i class="bi bi-layers me-1"></i>${esc(t('quiz.done.toFlashcards'))}</a>
        <a class="btn btn-link" id="done-again" href="${esc(window.location.pathname + window.location.search)}">${esc(t('quiz.done.again'))}</a>
      </div>`;
    completeBox.classList.remove('d-none');
    $('#exit-button').setAttribute('aria-label', t('quiz.done.toLessons'));
  }

  // ---- Wiring --------------------------------------------------------------------------------------------------------------
  checkButton.addEventListener('click', () => { if (state.phase === 'feedback') next(); else checkAnswer(); });

  document.addEventListener('keydown', (event) => {
    if (event.isComposing || event.defaultPrevented || state.finished || !state.quiz) return;   // isComposing: Enter confirms a Chinese IME candidate
    if (document.querySelector('.modal.show')) return;

    const typing = event.target instanceof HTMLInputElement;
    if (event.key === 'Enter') {
      // A focused button already turns Enter into a click; handling it here too would count it twice.
      if (event.target instanceof HTMLButtonElement || event.target instanceof HTMLAnchorElement) return;
      event.preventDefault();
      if (state.phase === 'feedback') next(); else checkAnswer();
      return;
    }
    if (!typing && state.phase === 'answering' && /^[1-9]$/.test(event.key)) {
      const q = currentQuestion();
      const option = isChoice(q) ? state.optionOrder[Number(event.key) - 1] : null;
      if (option) { event.preventDefault(); selectOption(option.id); }
    }
  });
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

// ---- AI tutor ------------------------------------------------------------------------------------------------------

const TUTOR_SCENARIOS = [
  { code: 'JobInterview', icon: 'bi-briefcase' },
  { code: 'BusinessEmail', icon: 'bi-envelope-paper' },
  { code: 'FreeTalk', icon: 'bi-cup-hot' },
];
const TUTOR_LANGUAGES = ['en', 'zh'];
const TUTOR_SESSION_KEY = 'lb.tutor.session';
const TUTOR_MAX_LENGTH = 1000;   // the API's default AiTutor:MaxMessageLength; the textarea stops here, the API has the last word

// The tutor's opening line, in the language being practised (the translation under it follows the interface language).
const TUTOR_GREETINGS = {
  JobInterview: {
    en: { text: 'Hello! Thank you for coming to the interview today. I will be your interviewer. To begin, please tell me a little about yourself.' },
    zh: { text: '你好！感谢你今天来参加面试。我是你的面试官。首先，请简单介绍一下你自己。', ipa: 'Nǐ hǎo! Gǎnxiè nǐ jīntiān lái cānjiā miànshì. Wǒ shì nǐ de miànshìguān. Shǒuxiān, qǐng jiǎndān jièshào yíxià nǐ zìjǐ.' },
  },
  BusinessEmail: {
    en: { text: 'Hello! I will help you write and polish work emails. What would you like to write about?' },
    zh: { text: '你好！我来帮你撰写和修改工作邮件。你想写一封关于什么的邮件？', ipa: 'Nǐ hǎo! Wǒ lái bāng nǐ zhuànxiě hé xiūgǎi gōngzuò yóujiàn. Nǐ xiǎng xiě yì fēng guānyú shénme de yóujiàn?' },
  },
  FreeTalk: {
    en: { text: 'Hello! I am your AI tutor. What would you like to talk about today?' },
    zh: { text: '你好！我是你的 AI 老师。今天你想聊点什么？', ipa: 'Nǐ hǎo! Wǒ shì nǐ de AI lǎoshī. Jīntiān nǐ xiǎng liáo diǎn shénme?' },
  },
};

function readTutorSession() {
  try {
    const saved = JSON.parse(sessionStorage.getItem(TUTOR_SESSION_KEY) ?? 'null');
    const valid = saved && GUID.test(saved.id ?? '') && TUTOR_LANGUAGES.includes(saved.targetLanguage)
      && TUTOR_SCENARIOS.some((s) => s.code === saved.scenarioCode);
    return valid ? saved : null;
  } catch { return null; }
}
const saveTutorSession = (session) => { try { sessionStorage.setItem(TUTOR_SESSION_KEY, JSON.stringify(session)); } catch { /* the room just is not restored after a reload */ } };
const clearTutorSession = () => { try { sessionStorage.removeItem(TUTOR_SESSION_KEY); } catch { /* nothing to clear */ } };

function formatDuration(milliseconds) {
  const minutes = Math.ceil(milliseconds / 60000);
  if (!(minutes > 0)) return t('tutor.time.soon');
  const h = Math.floor(minutes / 60);
  return h > 0 ? t('tutor.time.hm', { h, m: minutes % 60 }) : t('tutor.time.m', { m: minutes });
}

/** The allowance card. Free learners are limited by requests, paid learners by tokens (the other limit is null). */
function quotaCard(quota) {
  const byRequests = quota.dailyRequestLimit != null;
  const limit = byRequests ? quota.dailyRequestLimit : quota.dailyTokenLimit;
  const used = byRequests ? quota.requestsToday : quota.tokensToday;
  const remaining = byRequests ? quota.requestsRemaining : quota.tokensRemaining;
  const fraction = limit > 0 ? Math.min(1, used / limit) : 0;
  const state = remaining <= 0 ? ' is-empty' : fraction >= 0.8 ? ' is-low' : '';
  const planKey = `tutor.quota.plan.${quota.plan}`;
  const plan = t(planKey) === planKey ? quota.plan : t(planKey);
  const usedText = byRequests ? t('tutor.quota.requests', { used, limit }) : t('tutor.quota.tokens', { used: used.toLocaleString(), limit: limit.toLocaleString() });
  const leftText = byRequests ? t('tutor.quota.remaining', { n: remaining }) : t('tutor.quota.tokensRemaining', { n: remaining.toLocaleString() });

  return `
    <div class="quota-card${state}">
      <div class="d-flex flex-wrap justify-content-between align-items-center gap-2 mb-2">
        <div class="fw-semibold"><i class="bi bi-speedometer2 me-1 text-primary"></i>${esc(t('tutor.quota.title'))}
          <span class="badge text-bg-primary ms-1">${esc(plan)}</span></div>
        <div class="small text-secondary"><i class="bi bi-clock-history me-1"></i><span id="quota-reset" data-resets-at="${esc(quota.resetsAt)}">${esc(t('tutor.quota.reset', { time: formatDuration(new Date(quota.resetsAt) - Date.now()) }))}</span></div>
      </div>
      <div class="progress mb-2" role="progressbar" aria-label="${esc(t('tutor.quota.title'))}" aria-valuemin="0" aria-valuemax="100" aria-valuenow="${Math.round(fraction * 100)}">
        <div class="progress-bar" style="width:${Math.round(fraction * 100)}%"></div>
      </div>
      <div class="d-flex flex-wrap justify-content-between small"><span>${esc(usedText)}</span><span class="fw-semibold">${esc(leftText)}</span></div>
    </div>`;
}

const quotaExhausted = (quota) => (quota.dailyRequestLimit != null ? quota.requestsRemaining <= 0 : quota.tokensRemaining <= 0);

async function initTutor() {
  if (!isSignedIn()) return showSignInPrompt('#content');

  const setup = $('#tutor-setup');
  const room = $('#tutor-room');
  const startButton = $('#tutor-start');
  const log = $('#chat-log');
  const input = $('#chat-text');
  const sendButton = $('#chat-send');
  const notice = $('#chat-notice');
  const endButton = $('#chat-end');
  const choice = { language: TUTOR_LANGUAGES[0], scenario: TUTOR_SCENARIOS[0].code };
  let session = null;
  let busy = false;
  let locked = false;   // no more messages can be sent in this room (allowance used up, session over)
  let unlockTimer = null;
  let quotaTimer = null;
  let quotaIsExhausted = false;

  // ---- Choosing a scenario -----------------------------------------------------------------------------------------
  function renderChoices() {
    const option = (group, value, icon, title, desc, selected) => `
      <div class="col-sm-${group === 'language' ? 6 : 4}">
        <button type="button" class="tutor-choice" role="radio" aria-checked="${selected}" data-group="${group}" data-value="${esc(value)}">
          <span class="feature-icon"><i class="bi ${icon}"></i></span>
          <div class="fw-semibold">${esc(title)}</div>
          ${desc ? `<div class="small text-secondary">${esc(desc)}</div>` : ''}
        </button>
      </div>`;
    $('#tutor-languages').innerHTML = TUTOR_LANGUAGES
      .map((code) => option('language', code, code === 'en' ? 'bi-alphabet-uppercase' : 'bi-translate', targetName(code), '', choice.language === code)).join('');
    $('#tutor-scenarios').innerHTML = TUTOR_SCENARIOS
      .map((s) => option('scenario', s.code, s.icon, t(`tutor.scenario.${s.code}`), t(`tutor.scenario.${s.code}.desc`), choice.scenario === s.code)).join('');
  }

  setup.addEventListener('click', (event) => {
    const button = event.target.closest('.tutor-choice');
    if (!button) return;
    choice[button.dataset.group] = button.dataset.value;
    renderChoices();
    $(`.tutor-choice[data-group="${button.dataset.group}"][data-value="${button.dataset.value}"]`)?.focus();
  });

  function updateStartButton() {
    startButton.disabled = quotaIsExhausted;
  }

  async function loadQuota() {
    const box = $('#tutor-quota');
    try {
      const quota = await api.tutorQuota();
      quotaIsExhausted = quotaExhausted(quota);
      box.innerHTML = quotaCard(quota)
        + (quotaIsExhausted ? `<div class="alert alert-warning mt-2 mb-0" role="alert">${esc(t('tutor.quota.exhausted', { time: formatDuration(new Date(quota.resetsAt) - Date.now()) }))}</div>` : '');
      window.clearInterval(quotaTimer);
      quotaTimer = window.setInterval(() => {
        const label = $('#quota-reset');
        if (label) label.textContent = t('tutor.quota.reset', { time: formatDuration(new Date(label.dataset.resetsAt) - Date.now()) });
      }, 30000);
    } catch (error) {
      quotaIsExhausted = false;   // the quota is only information: the API enforces it when a message is sent
      box.innerHTML = error instanceof AuthLoopError ? alertHtml(describeError(error)) : alertHtml(t('tutor.quota.unavailable'), 'warning');
    }
    updateStartButton();
  }

  function showSetup({ error } = {}) {
    session = null;
    clearTutorSession();
    window.clearTimeout(unlockTimer);
    room.classList.add('d-none');
    setup.classList.remove('d-none');
    $('#tutor-setup-error').innerHTML = error ? alertHtml(error) : '';
    renderChoices();
    startButton.querySelector('span').textContent = t('tutor.start');
    updateStartButton();
    loadQuota();
  }

  startButton.addEventListener('click', async () => {
    if (startButton.disabled) return;
    startButton.disabled = true;
    startButton.querySelector('span').textContent = t('tutor.starting');
    $('#tutor-setup-error').innerHTML = '';
    try {
      const created = await api.createTutorSession({
        targetLanguage: choice.language,
        scenarioCode: choice.scenario,
        title: `${t(`tutor.scenario.${choice.scenario}`)} · ${targetName(choice.language)}`,
      });
      session = { id: created.id, targetLanguage: created.targetLanguage, scenarioCode: created.scenarioCode };
      saveTutorSession(session);
      await openRoom({ fresh: true });
    } catch (error) {
      $('#tutor-setup-error').innerHTML = alertHtml(describeError(error));
      startButton.querySelector('span').textContent = t('tutor.start');
      updateStartButton();
    }
  });

  // ---- The room ----------------------------------------------------------------------------------------------------
  const scrollToEnd = () => { log.scrollTo({ top: log.scrollHeight }); };

  function bubbleHtml({ sender, content, ipa, translation, tips }) {
    if (sender === 'User') {
      return `<div class="msg msg-user"><div class="bubble"><div class="bubble-who">${esc(t('tutor.you'))}</div><div class="bubble-text">${esc(content)}</div></div></div>`;
    }
    return `
      <div class="msg msg-ai"><div class="bubble">
        <div class="bubble-who"><i class="bi bi-robot me-1"></i>${esc(t('tutor.tutor'))}</div>
        <div class="bubble-text">${esc(content)}</div>
        ${ipa ? `<div class="bubble-ipa">${esc(ipa)}</div>` : ''}
        ${translation ? `<div class="bubble-translation"><i class="bi bi-translate me-1"></i>${esc(translation)}</div>` : ''}
        ${tips ? `<div class="grammar-tips"><span class="tips-title"><i class="bi bi-lightbulb-fill me-1"></i>${esc(t('tutor.grammar'))}</span>${esc(tips)}</div>` : ''}
      </div></div>`;
  }

  function appendHtml(html) {
    log.insertAdjacentHTML('beforeend', html);
    scrollToEnd();
    return log.lastElementChild;
  }

  const fromApi = (m) => ({ sender: m.sender, content: m.content, ipa: m.pinyinOrIpa, translation: m.translation, tips: m.grammarTips });
  const fromReply = (r) => ({ sender: 'Assistant', content: r.replyText, ipa: r.pinyinOrIpa, translation: r.translation, tips: r.grammarTips });

  function showNotice(html, kind = 'danger') {
    notice.className = `chat-notice${kind === 'warning' ? ' is-warning' : ''}`;
    notice.innerHTML = html;
  }
  const hideNotice = () => { notice.className = 'chat-notice d-none'; notice.innerHTML = ''; };

  function updateControls() {
    const empty = input.value.trim().length === 0;
    sendButton.disabled = busy || locked || empty;
    sendButton.innerHTML = busy ? '<span class="spinner-border spinner-border-sm" role="status" aria-hidden="true"></span>' : '<i class="bi bi-send-fill"></i>';
    input.disabled = locked;
    $('#chat-hint').innerHTML = `<span>${esc(t('tutor.hint'))}</span>`
      + (input.value.length >= TUTOR_MAX_LENGTH * 0.8 ? `<span>${esc(t('tutor.count', { n: input.value.length, max: TUTOR_MAX_LENGTH }))}</span>` : '');
  }

  function autosize() {
    input.style.height = 'auto';
    input.style.height = `${Math.min(input.scrollHeight, 128)}px`;
  }

  function lock(until) {
    locked = true;
    window.clearTimeout(unlockTimer);
    if (until) {
      // Setting a timer far in the future overflows; a learner who waits that long has reloaded the page anyway.
      unlockTimer = window.setTimeout(() => { locked = false; hideNotice(); updateControls(); }, Math.min(Math.max(until - Date.now(), 1000), 2_000_000_000));
    }
    updateControls();
  }

  async function openRoom({ fresh = false } = {}) {
    const scenarioName = t(`tutor.scenario.${session.scenarioCode}`);
    const scenario = TUTOR_SCENARIOS.find((s) => s.code === session.scenarioCode);
    $('#chat-title').textContent = scenarioName;
    $('#chat-meta').textContent = `${targetName(session.targetLanguage)} · ${t('tutor.tutor')}`;
    $('#chat-icon').innerHTML = `<i class="bi ${scenario.icon}"></i>`;
    log.innerHTML = '';
    hideNotice();
    busy = false;
    locked = false;
    input.value = '';
    autosize();
    updateControls();
    window.clearInterval(quotaTimer);

    setup.classList.add('d-none');
    room.classList.remove('d-none');

    const greeting = TUTOR_GREETINGS[session.scenarioCode][session.targetLanguage];
    appendHtml(bubbleHtml({ sender: 'Assistant', content: greeting.text, ipa: greeting.ipa, translation: t(`tutor.greet.${session.scenarioCode}`) }));

    // A reload (or a language switch, which reloads) brings the room back: the history comes from the API.
    const items = [];
    let total = fresh ? 0 : 1;   // a brand-new session has no history to ask for
    while (items.length < total) {
      const page = await api.tutorMessages(session.id, { offset: items.length, limit: 200 });
      items.push(...page.items);
      total = page.total;
      if (page.items.length === 0) break;
    }
    for (const message of items) log.insertAdjacentHTML('beforeend', bubbleHtml(fromApi(message)));
    scrollToEnd();
    input.focus();
  }

  const newSessionButton = `<button type="button" class="btn btn-sm btn-outline-danger ms-2" id="notice-new">${esc(t('tutor.error.newSession'))}</button>`;

  function failSending(error, text) {
    const problem = error instanceof ApiError ? error : null;
    if (problem?.status === 429) {
      const resetsAt = problem.problem?.resetsAt ? new Date(problem.problem.resetsAt) : null;
      const known = resetsAt && !Number.isNaN(resetsAt.getTime());
      showNotice(`<i class="bi bi-hourglass-split me-1"></i>${esc(known
        ? t('tutor.error.quota', { time: formatDuration(resetsAt - Date.now()) })
        : t('tutor.error.quotaNoTime'))}`);
      lock(known ? resetsAt.getTime() : null);
    } else if (problem?.status === 503) {
      showNotice(`<i class="bi bi-exclamation-triangle-fill me-1"></i>${esc(t('tutor.error.provider'))}`, 'warning');
    } else if (problem?.status === 409 || problem?.status === 404) {
      showNotice(`<i class="bi bi-x-circle-fill me-1"></i>${esc(t(problem.status === 409 ? 'tutor.error.ended' : 'tutor.error.notFound'))}${newSessionButton}`);
      clearTutorSession();
      lock(null);
      $('#notice-new').addEventListener('click', () => showSetup());
    } else if (problem?.status === 400) {
      showNotice(`<i class="bi bi-exclamation-circle-fill me-1"></i>${esc(t('tutor.error.tooLong'))}`);
    } else {
      showNotice(`<i class="bi bi-exclamation-circle-fill me-1"></i>${esc(describeError(error))} ${esc(t('tutor.error.retryHint'))}`);
    }
    input.value = text;
    autosize();
  }

  async function send() {
    const text = input.value.trim();
    if (!text || busy || locked) return;

    busy = true;
    hideNotice();
    const mine = appendHtml(bubbleHtml({ sender: 'User', content: text }));
    input.value = '';
    autosize();
    const typing = appendHtml(`<div class="msg msg-ai msg-typing"><div class="bubble text-secondary"><span class="typing-dots" aria-hidden="true"><span></span><span></span><span></span></span> <span class="small">${esc(t('tutor.thinking'))}</span></div></div>`);
    updateControls();

    try {
      const reply = await api.sendTutorMessage(session.id, text, baseLanguage());
      typing.remove();
      appendHtml(bubbleHtml(fromReply(reply)));
    } catch (error) {
      typing.remove();
      mine.remove();   // it was never answered: the text goes back into the input to be sent again
      failSending(error, text);
    } finally {
      busy = false;
      updateControls();
      if (!locked) input.focus();
    }
  }

  $('#chat-form').addEventListener('submit', (event) => { event.preventDefault(); send(); });
  input.addEventListener('input', () => { autosize(); updateControls(); });
  input.addEventListener('keydown', (event) => {
    // Enter sends, Shift+Enter is a new line; Enter that confirms an IME candidate (Chinese input) is not a send.
    if (event.key !== 'Enter' || event.shiftKey || event.isComposing || event.keyCode === 229) return;
    event.preventDefault();
    send();
  });

  endButton.addEventListener('click', async () => {
    if (!session || endButton.disabled) return;
    endButton.disabled = true;
    const label = endButton.querySelector('span');
    label.textContent = t('tutor.ending');
    try {
      await api.endTutorSession(session.id);
    } catch (error) {
      // A session that is already gone is as good as ended; anything else keeps the learner here to try again.
      if (!(error instanceof ApiError && error.status === 404)) {
        showNotice(`<i class="bi bi-exclamation-circle-fill me-1"></i>${esc(describeError(error))}`);
        endButton.disabled = false;
        label.textContent = t('tutor.end');
        return;
      }
    }
    endButton.disabled = false;
    label.textContent = t('tutor.end');
    showSetup();
  });

  // ---- Start: pick up a room that was open before a reload, otherwise offer the scenarios ------------------------------
  const saved = readTutorSession();
  if (!saved) { showSetup(); return; }

  session = saved;
  setup.classList.add('d-none');
  room.classList.remove('d-none');
  log.innerHTML = `<div class="text-center text-secondary py-5"><div class="spinner-border" role="status"></div><div class="mt-2">${esc(t('tutor.loadingChat'))}</div></div>`;
  try {
    await openRoom();
  } catch (error) {
    const gone = error instanceof ApiError && error.status === 404;
    showSetup({ error: gone ? t('tutor.error.notFound') : describeError(error) });
  }
}

// Static text first (it may sit inside areas the page code then fills in), then the page.
applyTranslations();
const pages = { home: initHome, courses: initCourses, flashcards: initFlashcards, lesson: initLesson, tutor: initTutor, callback: initCallback, login: initLogin };
const page = document.body.dataset.page;
if (page !== 'callback' && page !== 'lesson') renderNavbar(page);
pages[page]?.();
