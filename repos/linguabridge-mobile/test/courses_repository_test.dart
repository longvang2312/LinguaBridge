import 'package:dio/dio.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:linguabridge_mobile/core/network/api_exception.dart';
import 'package:linguabridge_mobile/features/courses/data/course_models.dart';
import 'package:linguabridge_mobile/features/courses/data/courses_repository.dart';

import 'support/fakes.dart';

CoursesRepository _repository(FakeAdapter adapter) => CoursesRepository(Dio(BaseOptions(baseUrl: 'http://api.test'))..httpClientAdapter = adapter);

// What the API sends (CourseSummaryDto, camelCase).
Map<String, Object?> _courseJson(String code, String lang, String level) => {
      'id': '11111111-1111-1111-1111-111111111111',
      'code': code,
      'targetLanguage': lang,
      'level': level,
      'status': 'Published',
      'version': 1,
      'thumbnailUrl': null,
      'title': 'Tiêu đề $code',
      'description': 'Mô tả',
      'translationLanguage': 'vi',
      'updatedAt': '2026-11-05T00:00:00Z',
    };

void main() {
  test('courses asks for the target and base language and reads the list', () async {
    final adapter = FakeAdapter((_) => jsonBody(200, [_courseJson('ENG-A1-COMM', 'en', 'A1'), _courseJson('ENG-BIZ-A2', 'en', 'A2')]));
    final courses = await _repository(adapter).courses(targetLanguage: 'en', baseLanguage: 'vi');

    final request = adapter.requests.single;
    expect(request.path, '/api/content/courses');
    expect(request.queryParameters, {'targetLanguage': 'en', 'baseLanguage': 'vi'});
    expect(courses.map((c) => c.code), ['ENG-A1-COMM', 'ENG-BIZ-A2']);
    expect(courses.first.level, 'A1');
    expect(courses.first.title, 'Tiêu đề ENG-A1-COMM');
    expect(courses.first.targetLanguage, 'en');
  });

  test('lessons come back in lesson order', () async {
    Map<String, Object?> lesson(int n) => {
          'id': 'l$n',
          'courseId': 'c',
          'orderIndex': n,
          'lessonType': 'Vocabulary',
          'status': 'Published',
          'xpReward': 10 * n,
          'version': 1,
          'title': 'Bài $n',
          'guideText': null,
          'translationLanguage': 'vi',
        };
    final adapter = FakeAdapter((_) => jsonBody(200, [lesson(2), lesson(1), lesson(3)]));
    final lessons = await _repository(adapter).lessons('c-1', baseLanguage: 'vi');

    expect(adapter.requests.single.path, '/api/content/courses/c-1/lessons');
    expect(adapter.requests.single.queryParameters, {'baseLanguage': 'vi'});
    expect(lessons.map((l) => l.orderIndex), [1, 2, 3]);
    expect(lessons.first.xpReward, 10);
  });

  test('progress reads the counts', () async {
    final adapter = FakeAdapter((_) => jsonBody(200, {
          'courseId': 'c',
          'completedLessonsCount': 1,
          'totalLessonsCount': 2,
          'isCompleted': false,
          'lastAccessedAt': null,
        }));
    final progress = await _repository(adapter).progress('c');

    expect(adapter.requests.single.path, '/api/v1/learning/progress/c');
    expect(progress.fraction, 0.5);
    expect(progress.isStarted, isTrue);
  });

  test('an API error is an ApiException with the API message', () async {
    final adapter = FakeAdapter((_) => jsonBody(400, {'title': 'Bad', 'detail': 'targetLanguage is required.', 'code': 'validation'}));
    await expectLater(
      _repository(adapter).courses(targetLanguage: '', baseLanguage: 'vi'),
      throwsA(isA<ApiException>().having((e) => e.message, 'message', 'targetLanguage is required.')),
    );
  });

  test('being offline is an ApiException that says so', () async {
    final adapter = FakeAdapter((o) => throw DioException.connectionError(requestOptions: o, reason: 'offline'));
    await expectLater(
      _repository(adapter).courses(targetLanguage: 'en', baseLanguage: 'vi'),
      throwsA(isA<ApiException>().having((e) => e.isNetworkError, 'isNetworkError', isTrue)),
    );
  });

  test('an answer that is not a list is refused', () async {
    await expectLater(
      _repository(FakeAdapter((_) => jsonBody(200, {'unexpected': true}))).courses(targetLanguage: 'en', baseLanguage: 'vi'),
      throwsA(isA<ApiException>()),
    );
  });

  group('models', () {
    test('a course without a translation shows its code', () {
      const course = CourseModel(id: 'x', code: 'ENG-A1', targetLanguage: 'en', level: 'A1');
      expect(course.displayTitle, 'ENG-A1');
    });

    test('a lesson without a title is numbered', () {
      const lesson = LessonModel(id: 'x', courseId: 'c', orderIndex: 3, lessonType: 'Vocabulary', xpReward: 5);
      expect(lesson.displayTitle, 'Bài 3');
    });

    test('progress with no lessons is empty, not a division by zero', () {
      const progress = CourseProgress(completedLessons: 0, totalLessons: 0, isCompleted: false);
      expect(progress.fraction, 0);
    });
  });
}
