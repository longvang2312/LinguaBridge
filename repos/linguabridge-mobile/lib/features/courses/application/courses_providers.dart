import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../../core/network/dio_client.dart';
import '../data/course_models.dart';
import '../data/courses_repository.dart';

final coursesRepositoryProvider = Provider<CoursesRepository>((ref) => CoursesRepository(ref.watch(dioProvider)));

/// The language titles and descriptions are shown in. The app is Vietnamese-only for now.
final baseLanguageProvider = Provider<String>((ref) => 'vi');

/// The language being learned that the courses tab shows: `en` or `zh`.
class TargetLanguage extends Notifier<String> {
  @override
  String build() => 'en';

  void select(String language) => state = language;
}

final targetLanguageProvider = NotifierProvider<TargetLanguage, String>(TargetLanguage.new);

// A failed load shows its error at once (with a retry button) instead of silently retrying with a growing delay.
Duration? _noRetry(int retryCount, Object error) => null;

/// The published courses of one target language, kept while the app runs so switching tabs is instant. Refresh with
/// `ref.invalidate(coursesProvider(language))`.
final coursesProvider = FutureProvider.family<List<CourseModel>, String>(
  (ref, targetLanguage) => ref.watch(coursesRepositoryProvider).courses(
        targetLanguage: targetLanguage,
        baseLanguage: ref.watch(baseLanguageProvider),
      ),
  retry: _noRetry,
);

final lessonsProvider = FutureProvider.family<List<LessonModel>, String>(
  (ref, courseId) => ref.watch(coursesRepositoryProvider).lessons(courseId, baseLanguage: ref.watch(baseLanguageProvider)),
  retry: _noRetry,
);

/// The learner's progress in a course; null when it cannot be read (the course is shown without a bar).
final courseProgressProvider = FutureProvider.family<CourseProgress?, String>(
  (ref, courseId) async {
    try {
      return await ref.watch(coursesRepositoryProvider).progress(courseId);
    } on Object {
      return null;
    }
  },
  retry: _noRetry,
);
