// Interface language (Vietnamese or English) of the web client.
//
// The chosen language is stored in localStorage under 'lb.baseLanguage'. It is also the language the API is asked to
// return course, lesson and flashcard text in ("baseLanguage"), so the whole screen follows one choice.
//
// Static text in the HTML carries data-i18n="key" (text), data-i18n-attr="attribute:key;attribute:key" (attributes).
// Text built by script calls t('key', { name: value }); {name} in a message is replaced by the value.

import { config } from './config.js';

export const SUPPORTED_LANGUAGES = ['vi', 'en'];
const STORAGE_KEY = 'lb.baseLanguage';

const dictionary = {
  vi: {
    // ---- Navbar -------------------------------------------------------------------------------------------------
    'nav.home': 'Trang chủ',
    'nav.courses': 'Khóa học',
    'nav.review': 'Ôn tập',
    'nav.login': 'Đăng nhập',
    'nav.logout': 'Đăng xuất',
    'nav.language': 'Ngôn ngữ',
    'nav.menu': 'Menu',

    // ---- Page titles --------------------------------------------------------------------------------------------
    'title.home': 'LinguaBridge - Học tiếng Anh & tiếng Trung giao tiếp',
    'title.courses': 'Khóa học - LinguaBridge',
    'title.flashcards': 'Ôn tập từ vựng - LinguaBridge',
    'title.login': 'Đăng nhập - LinguaBridge',
    'title.callback': 'Đang đăng nhập - LinguaBridge',

    // ---- Languages and common words -----------------------------------------------------------------------------
    'lang.en': 'Tiếng Anh',
    'lang.zh': 'Tiếng Trung',
    'common.loading': 'Đang tải...',
    'common.close': 'Đóng',
    'common.backHome': 'Về trang chủ',
    'common.lessonsCount': '{done}/{total} bài học',
    'common.xp': '{n} XP',

    // ---- Home: signed out ---------------------------------------------------------------------------------------
    'hero.badge': 'Tiếng Anh & Tiếng Trung',
    'hero.title1': 'Giao tiếp công việc tự tin',
    'hero.title2': 'bằng hai ngoại ngữ',
    'hero.lead': 'Bài học ngắn theo tình huống thực tế, ôn từ vựng bằng thẻ ghi nhớ thông minh (SRS) và luyện nói cùng gia sư AI.',
    'hero.cta': 'Đăng Nhập Ngay',
    'feature.courses.title': 'Khóa học theo cấp độ',
    'feature.courses.desc': 'Lộ trình từ cơ bản đến nâng cao cho tiếng Anh và tiếng Trung, hiển thị bằng ngôn ngữ bạn chọn.',
    'feature.review.title': 'Ôn tập thông minh',
    'feature.review.desc': 'Thẻ lật 3D và thuật toán lặp lại ngắt quãng SM-2 nhắc bạn đúng lúc sắp quên.',
    'feature.xp.title': 'Tích lũy XP',
    'feature.xp.desc': 'Hoàn thành bài học để nhận điểm kinh nghiệm và theo dõi tiến độ mỗi ngày.',

    // ---- Home: dashboard ----------------------------------------------------------------------------------------
    'dash.hello': 'Xin chào,',
    'dash.fallbackName': 'bạn',
    'dash.subtitle': 'Đây là tiến độ học tập của bạn hôm nay.',
    'stat.learner': 'Học viên',
    'stat.due': 'Từ cần ôn hôm nay',
    'stat.dueCta': 'Ôn tập ngay',
    'stat.dueMax': 'Hiển thị tối đa 100 thẻ mỗi lượt',
    'stat.courses': 'Khóa học',
    'stat.plan': 'Gói học',
    'progress.title': 'Tiến độ học tập',
    'progress.aria': 'Tiến độ tổng',
    'progress.none': 'Chưa có bài học nào.',
    'progress.summary': '{done}/{total} bài học đã hoàn thành ({percent}%)',
    'progress.course': 'Tiến độ',
    'myCourses.title': 'Khóa học của bạn',
    'myCourses.viewAll': 'Xem tất cả',
    'myCourses.none': 'Chưa có khóa học nào được xuất bản.',

    // ---- Courses ------------------------------------------------------------------------------------------------
    'courses.title': 'Khám phá khóa học',
    'courses.empty': 'Chưa có khóa {lang} nào được xuất bản.',
    'courses.noDescription': 'Chưa có mô tả.',
    'courses.viewLessons': 'Xem bài học',
    'lessons.completed': 'Đã hoàn thành {done}/{total} bài học',
    'lessons.none': 'Khóa học này chưa có bài học nào.',
    'lessons.defaultTitle': 'Bài {n}',
    'lessonType.Standard': 'Cơ bản',
    'lessonType.Vocabulary': 'Từ vựng',
    'lessonType.Pinyin': 'Phiên âm',
    'lessonType.Grammar': 'Ngữ pháp',

    // ---- Flashcards ---------------------------------------------------------------------------------------------
    'flash.title': 'Ôn tập từ vựng',
    'flash.filter': 'Ngôn ngữ cần ôn',
    'flash.filterAll': 'Tất cả ngôn ngữ',
    'flash.reload': 'Tải lại',
    'flash.loading': 'Đang tải thẻ...',
    'flash.emptyTitle': 'Hôm nay không còn thẻ nào cần ôn',
    'flash.emptyDesc': 'Hãy hoàn thành thêm bài học để thêm từ vựng mới, rồi quay lại sau.',
    'flash.toCourses': 'Đến khóa học',
    'flash.doneTitle': 'Hoàn thành lượt ôn tập!',
    'flash.doneDesc': 'Bạn đã ôn {n} thẻ.',
    'flash.again': 'Ôn tiếp',
    'flash.deckAria': 'Tiến độ lượt ôn',
    'flash.cardAria': 'Lật thẻ (Space)',
    'flash.hintPrefix': 'Chạm vào thẻ hoặc nhấn',
    'flash.hintSuffix': 'để lật',
    'flash.noMeaning': '(chưa có bản dịch)',
    'flash.state': '{state} · lặp {n} lần · khoảng {days} ngày',
    'rating.again': 'Quên',
    'rating.hard': 'Khó',
    'rating.good': 'Tốt',
    'rating.easy': 'Dễ',
    'srs.Learning': 'Đang học',
    'srs.Review': 'Ôn tập',
    'srs.Graduated': 'Đã thuộc',

    // ---- Login --------------------------------------------------------------------------------------------------
    'login.heading': 'Đăng Nhập LinguaBridge',
    'login.desc': 'Học tiếng Anh & tiếng Trung giao tiếp thông minh cùng AI Tutor và Flashcard SRS.',
    'login.button': 'Đăng Nhập Bằng AppInHouse ID',
    'login.loggedOut': 'Bạn đã đăng xuất thành công. Vui lòng đăng nhập lại để tiếp tục học tập.',
    'prompt.title': 'Bạn cần đăng nhập',
    'prompt.desc': 'Đăng nhập bằng tài khoản AppInHouse để xem nội dung này.',
    'prompt.button': 'Đăng nhập ngay',
    'callback.finishing': 'Đang hoàn tất đăng nhập...',

    // ---- Errors -------------------------------------------------------------------------------------------------
    'error.forbidden': 'Tài khoản này không có quyền thực hiện thao tác đó.',
    'error.network': 'Không kết nối được máy chủ. Hãy kiểm tra Docker/API đang chạy (docker-all-up.ps1) rồi thử lại.',
    'error.authLoop': 'API từ chối phiên đăng nhập vừa được cấp. Có thể API và STS cấu hình không khớp (Authority hoặc secret). Hãy báo quản trị viên.',
  },

  en: {
    // ---- Navbar -------------------------------------------------------------------------------------------------
    'nav.home': 'Home',
    'nav.courses': 'Courses',
    'nav.review': 'Review',
    'nav.login': 'Sign In',
    'nav.logout': 'Sign Out',
    'nav.language': 'Language',
    'nav.menu': 'Menu',

    // ---- Page titles --------------------------------------------------------------------------------------------
    'title.home': 'LinguaBridge - Learn conversational English & Chinese',
    'title.courses': 'Courses - LinguaBridge',
    'title.flashcards': 'Vocabulary Review - LinguaBridge',
    'title.login': 'Sign In - LinguaBridge',
    'title.callback': 'Signing in - LinguaBridge',

    // ---- Languages and common words -----------------------------------------------------------------------------
    'lang.en': 'English',
    'lang.zh': 'Chinese',
    'common.loading': 'Loading...',
    'common.close': 'Close',
    'common.backHome': 'Back to home',
    'common.lessonsCount': '{done}/{total} lessons',
    'common.xp': '{n} XP',

    // ---- Home: signed out ---------------------------------------------------------------------------------------
    'hero.badge': 'English & Chinese',
    'hero.title1': 'Communicate at work with confidence',
    'hero.title2': 'in two foreign languages',
    'hero.lead': 'Short lessons built around real situations, vocabulary review with smart flashcards (SRS) and speaking practice with an AI tutor.',
    'hero.cta': 'Sign In Now',
    'feature.courses.title': 'Courses by level',
    'feature.courses.desc': 'A path from beginner to advanced for English and Chinese, shown in the language you choose.',
    'feature.review.title': 'Smart review',
    'feature.review.desc': '3D flip cards and the SM-2 spaced repetition algorithm remind you just before you forget.',
    'feature.xp.title': 'Earn XP',
    'feature.xp.desc': 'Complete lessons to earn experience points and track your progress every day.',

    // ---- Home: dashboard ----------------------------------------------------------------------------------------
    'dash.hello': 'Hello,',
    'dash.fallbackName': 'there',
    'dash.subtitle': 'Here is your learning progress for today.',
    'stat.learner': 'Learner',
    'stat.due': 'Words to review today',
    'stat.dueCta': 'Review now',
    'stat.dueMax': 'At most 100 cards are shown per session',
    'stat.courses': 'Courses',
    'stat.plan': 'Plan',
    'progress.title': 'Learning progress',
    'progress.aria': 'Overall progress',
    'progress.none': 'No lessons yet.',
    'progress.summary': '{done}/{total} lessons completed ({percent}%)',
    'progress.course': 'Progress',
    'myCourses.title': 'Your courses',
    'myCourses.viewAll': 'View all',
    'myCourses.none': 'No courses have been published yet.',

    // ---- Courses ------------------------------------------------------------------------------------------------
    'courses.title': 'Explore Courses',
    'courses.empty': 'No {lang} courses have been published yet.',
    'courses.noDescription': 'No description yet.',
    'courses.viewLessons': 'View Lessons',
    'lessons.completed': '{done}/{total} lessons completed',
    'lessons.none': 'This course has no lessons yet.',
    'lessons.defaultTitle': 'Lesson {n}',
    'lessonType.Standard': 'Standard',
    'lessonType.Vocabulary': 'Vocabulary',
    'lessonType.Pinyin': 'Pinyin',
    'lessonType.Grammar': 'Grammar',

    // ---- Flashcards ---------------------------------------------------------------------------------------------
    'flash.title': 'Vocabulary Review',
    'flash.filter': 'Language to review',
    'flash.filterAll': 'All languages',
    'flash.reload': 'Reload',
    'flash.loading': 'Loading cards...',
    'flash.emptyTitle': 'No cards left to review today',
    'flash.emptyDesc': 'Complete more lessons to add new vocabulary, then come back later.',
    'flash.toCourses': 'Go to courses',
    'flash.doneTitle': 'Review session complete!',
    'flash.doneDesc': 'You reviewed {n} cards.',
    'flash.again': 'Review more',
    'flash.deckAria': 'Session progress',
    'flash.cardAria': 'Flip the card (Space)',
    'flash.hintPrefix': 'Click the card or press',
    'flash.hintSuffix': 'to flip',
    'flash.noMeaning': '(no translation yet)',
    'flash.state': '{state} · repeated {n} times · about {days} days',
    'rating.again': 'Forgot',
    'rating.hard': 'Hard',
    'rating.good': 'Good',
    'rating.easy': 'Easy',
    'srs.Learning': 'Learning',
    'srs.Review': 'Review',
    'srs.Graduated': 'Graduated',

    // ---- Login --------------------------------------------------------------------------------------------------
    'login.heading': 'Sign In to LinguaBridge',
    'login.desc': 'Learn conversational English and Chinese with an AI tutor and SRS flashcards.',
    'login.button': 'Sign In with AppInHouse ID',
    'login.loggedOut': 'You have signed out successfully. Please sign in again to continue learning.',
    'prompt.title': 'You need to sign in',
    'prompt.desc': 'Sign in with your AppInHouse account to see this content.',
    'prompt.button': 'Sign in now',
    'callback.finishing': 'Finishing sign-in...',

    // ---- Errors -------------------------------------------------------------------------------------------------
    'error.forbidden': 'This account is not allowed to do that.',
    'error.network': 'Could not reach the server. Check that Docker and the API are running (docker-all-up.ps1) and try again.',
    'error.authLoop': 'The API rejected the session it has just been given. The API and the STS may be configured differently (Authority or secret). Please tell an administrator.',
  },
};

export function getLanguage() {
  let stored = null;
  try { stored = localStorage.getItem(STORAGE_KEY); } catch { /* storage unavailable: use the default */ }
  return SUPPORTED_LANGUAGES.includes(stored) ? stored : config.defaultBaseLanguage;
}

export function setLanguage(language) {
  if (!SUPPORTED_LANGUAGES.includes(language)) return;
  try { localStorage.setItem(STORAGE_KEY, language); } catch { /* the choice just is not remembered */ }
}

/** The text for a key in the current language; falls back to Vietnamese, then to the key itself so a gap is visible. */
export function t(key, params = {}) {
  const text = dictionary[getLanguage()]?.[key] ?? dictionary.vi[key] ?? key;
  return text.replace(/\{(\w+)\}/g, (match, name) => (name in params ? String(params[name]) : match));
}

/** Translates the static text of the page (or of a part of it) and tells the browser which language it is in. */
export function applyTranslations(root = document) {
  document.documentElement.lang = getLanguage();

  root.querySelectorAll('[data-i18n]').forEach((element) => { element.textContent = t(element.dataset.i18n); });
  root.querySelectorAll('[data-i18n-attr]').forEach((element) => {
    for (const pair of element.dataset.i18nAttr.split(';')) {
      const [attribute, key] = pair.split(':').map((part) => part.trim());
      if (attribute && key) element.setAttribute(attribute, t(key));
    }
  });
  const title = document.querySelector('title[data-i18n]');
  if (title) document.title = t(title.dataset.i18n);
}
