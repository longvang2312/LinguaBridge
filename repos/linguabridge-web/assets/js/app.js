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

  const state = { quiz: null, attemptId: null, index: 0, selectedOptionId: null, phase: 'answering', busy: false, shownAt: 0, finished: false };

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
    let body;
    if (isChoice(q)) {
      // The question is the target text; its translation (when there is one) is a hint under it.
      body = `
        <div class="quiz-type mb-2">${esc(typeLabel)}</div>
        <div class="quiz-target">${esc(q.targetText ?? q.promptTranslation ?? '')}</div>
        ${q.pinyinOrIpa ? `<div class="quiz-ipa mt-1">${esc(q.pinyinOrIpa)}</div>` : ''}
        ${q.promptTranslation && q.targetText ? `<div class="text-secondary mt-2">${esc(t('quiz.hint', { text: q.promptTranslation }))}</div>` : ''}
        <div class="d-grid gap-2 mt-4" id="quiz-options" role="group" aria-label="${esc(typeLabel)}">
          ${q.options.slice().sort((a, b) => a.orderIndex - b.orderIndex).map((option, i) => `
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
      setCheckState({ label: t('quiz.check'), enabled: true });
      let message = describeError(error);
      if (error instanceof ApiError && error.code === 'response.already_answered') message = t('error.answered');
      if (error instanceof ApiError && error.code === 'attempt.already_completed') message = t('error.attemptClosed');
      feedback.innerHTML = alertHtml(message);
      feedback.classList.remove('d-none');
      return;
    }
    state.busy = false;
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
      const option = isChoice(q) ? q.options.slice().sort((a, b) => a.orderIndex - b.orderIndex)[Number(event.key) - 1] : null;
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

// Static text first (it may sit inside areas the page code then fills in), then the page.
applyTranslations();
const pages = { home: initHome, courses: initCourses, flashcards: initFlashcards, lesson: initLesson, callback: initCallback, login: initLogin };
const page = document.body.dataset.page;
if (page !== 'callback' && page !== 'lesson') renderNavbar(page);
pages[page]?.();
