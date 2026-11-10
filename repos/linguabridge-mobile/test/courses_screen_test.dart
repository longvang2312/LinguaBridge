import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:linguabridge_mobile/features/courses/data/course_models.dart';
import 'package:linguabridge_mobile/features/courses/presentation/course_card.dart';

import 'support/fakes.dart';
import 'support/pump_app.dart';

Future<TestApp> _openCourses(WidgetTester tester, {FakeCoursesRepository? repository}) async {
  final app = await pumpApp(tester, session: makeSession(), repository: repository);
  await tester.tap(find.descendant(of: find.byType(NavigationBar), matching: find.text('Khóa học')));
  await tester.pumpAndSettle();
  return app;
}

void main() {
  group('CoursesScreen', () {
    testWidgets('lists the English courses first, with language and level badges, code, title and description', (tester) async {
      final app = await _openCourses(tester);

      expect(find.byType(CourseCard), findsNWidgets(2));
      expect(find.text('Tiếng Anh giao tiếp nhập môn'), findsOneWidget);
      expect(find.text('Những mẫu câu đầu tiên để chào hỏi.'), findsOneWidget);
      expect(find.text('ENG-A1-COMM'), findsOneWidget);
      expect(find.text('ENG-BIZ-A2'), findsOneWidget);
      expect(find.text('English'), findsNWidgets(2));
      expect(find.text('A1'), findsOneWidget);
      expect(find.text('A2'), findsOneWidget);
      expect(find.text('Tiếng Trung giao tiếp HSK 1'), findsNothing);
      expect(app.repository.courseCalls, contains('en/vi'), reason: 'titles are asked for in Vietnamese');
    });

    testWidgets('switching to Tiếng Trung shows the Chinese course and back again', (tester) async {
      final app = await _openCourses(tester);

      await tester.tap(find.text('Tiếng Trung'));
      await tester.pumpAndSettle();
      expect(find.byType(CourseCard), findsOneWidget);
      expect(find.text('ZH-HSK1-COMM'), findsOneWidget);
      expect(find.text('HSK1'), findsOneWidget);
      expect(find.text('Chinese'), findsOneWidget);
      expect(find.text('ENG-A1-COMM'), findsNothing);
      expect(app.repository.courseCalls, contains('zh/vi'));

      await tester.tap(find.text('Tiếng Anh'));
      await tester.pumpAndSettle();
      expect(find.text('ENG-A1-COMM'), findsOneWidget);
      expect(find.text('ZH-HSK1-COMM'), findsNothing);
    });

    testWidgets('a language already loaded is not fetched again when its tab is reopened', (tester) async {
      final app = await _openCourses(tester);
      await tester.tap(find.text('Tiếng Trung'));
      await tester.pumpAndSettle();
      await tester.tap(find.text('Tiếng Anh'));
      await tester.pumpAndSettle();
      await tester.tap(find.text('Tiếng Trung'));
      await tester.pumpAndSettle();
      expect(app.repository.courseCalls.where((c) => c == 'zh/vi'), hasLength(1));
    });

    testWidgets('the search box narrows the list by title or code', (tester) async {
      await _openCourses(tester);

      await tester.enterText(find.byKey(const Key('courses-search')), 'công sở');
      await tester.pumpAndSettle();
      expect(find.byType(CourseCard), findsOneWidget);
      expect(find.text('ENG-BIZ-A2'), findsOneWidget);

      await tester.enterText(find.byKey(const Key('courses-search')), 'a1-comm');
      await tester.pumpAndSettle();
      expect(find.text('ENG-A1-COMM'), findsOneWidget);
      expect(find.byType(CourseCard), findsOneWidget);

      await tester.enterText(find.byKey(const Key('courses-search')), 'zzz');
      await tester.pumpAndSettle();
      expect(find.byType(CourseCard), findsNothing);
      expect(find.textContaining('Không có khóa học nào khớp'), findsOneWidget);
    });

    testWidgets('a language without courses says so', (tester) async {
      await _openCourses(tester, repository: FakeCoursesRepository(courseList: {'en': [englishCourse]}));
      await tester.tap(find.text('Tiếng Trung'));
      await tester.pumpAndSettle();
      expect(find.textContaining('Chưa có khóa học nào'), findsOneWidget);
    });

    testWidgets('a failed load shows the error and a retry button that loads the list', (tester) async {
      final repository = sampleRepository()..courseFailures = 1;
      final app = await _openCourses(tester, repository: repository);

      expect(find.textContaining('Không kết nối được máy chủ'), findsOneWidget);
      expect(find.byType(CourseCard), findsNothing);
      expect(app.repository.courseCalls.where((c) => c == 'en/vi'), hasLength(1), reason: 'no silent retries behind the learner');

      await tester.tap(find.text('Thử lại'));
      await tester.pumpAndSettle();

      expect(find.byType(CourseCard), findsNWidgets(2));
      expect(find.textContaining('Không kết nối được máy chủ'), findsNothing);
    });

    testWidgets('a card shows the learner progress when it is known', (tester) async {
      final repository = FakeCoursesRepository(
        courseList: {'en': [englishCourse]},
        progressList: {englishCourse.id: const CourseProgress(completedLessons: 1, totalLessons: 2, isCompleted: false)},
      );
      await _openCourses(tester, repository: repository);
      expect(find.text('Đã hoàn thành 1/2 bài học'), findsOneWidget);
    });

    testWidgets('Xem bài học opens a sheet with the lessons in order, each with its XP', (tester) async {
      final app = await _openCourses(tester);

      await tester.tap(find.text('Xem bài học').first);
      await tester.pumpAndSettle();

      expect(app.repository.lessonCalls, [englishCourse.id]);
      expect(find.text('Chào hỏi'), findsOneWidget);
      expect(find.text('Nhờ giúp đỡ'), findsOneWidget);
      expect(find.text('Học cách chào.'), findsOneWidget);
      expect(find.text('15 XP'), findsOneWidget);
      expect(find.text('10 XP'), findsOneWidget);
      // Lesson 1 is listed above lesson 2 although the API sent them the other way round.
      expect(tester.getTopLeft(find.text('Chào hỏi')).dy, lessThan(tester.getTopLeft(find.text('Nhờ giúp đỡ')).dy));
    });

    testWidgets('tapping the card itself opens the lessons too', (tester) async {
      await _openCourses(tester);
      await tester.tap(find.text('Tiếng Anh giao tiếp nhập môn'));
      await tester.pumpAndSettle();
      expect(find.text('Chào hỏi'), findsOneWidget);
    });

    testWidgets('lessons that fail to load can be retried', (tester) async {
      final repository = sampleRepository()..lessonFailures = 1;
      await _openCourses(tester, repository: repository);

      await tester.tap(find.text('Xem bài học').first);
      await tester.pumpAndSettle();
      expect(find.text('Lỗi tải bài học.'), findsOneWidget);

      await tester.tap(find.text('Thử lại'));
      await tester.pumpAndSettle();
      expect(find.text('Chào hỏi'), findsOneWidget);
    });

    testWidgets('a course without lessons says so', (tester) async {
      await _openCourses(tester, repository: FakeCoursesRepository(courseList: {'en': [businessCourse]}));
      await tester.tap(find.text('Xem bài học').first);
      await tester.pumpAndSettle();
      expect(find.text('Khóa học này chưa có bài học.'), findsOneWidget);
    });
  });

  group('HomeScreen courses', () {
    testWidgets('suggests courses of both languages when nothing is started', (tester) async {
      await pumpApp(tester, session: makeSession());
      expect(find.text('Gợi ý cho bạn'), findsOneWidget);
      expect(find.text('ENG-A1-COMM'), findsOneWidget);
      expect(find.text('ENG-BIZ-A2'), findsOneWidget);
      expect(find.text('ZH-HSK1-COMM'), findsOneWidget);
    });

    testWidgets('puts the course in progress first under Tiếp tục học', (tester) async {
      final repository = FakeCoursesRepository(
        courseList: {'en': [englishCourse, businessCourse], 'zh': [chineseCourse]},
        progressList: {chineseCourse.id: const CourseProgress(completedLessons: 1, totalLessons: 3, isCompleted: false)},
      );
      await pumpApp(tester, session: makeSession(), repository: repository);

      expect(find.text('Tiếp tục học'), findsOneWidget);
      expect(tester.getTopLeft(find.text('ZH-HSK1-COMM')).dy, lessThan(tester.getTopLeft(find.text('ENG-A1-COMM')).dy));
    });

    testWidgets('a failed load can be retried from the dashboard', (tester) async {
      final repository = sampleRepository()..courseFailures = 1;
      await pumpApp(tester, session: makeSession(), repository: repository);
      expect(find.text('Thử lại'), findsOneWidget);

      await tester.tap(find.text('Thử lại'));
      await tester.pumpAndSettle();
      expect(find.text('ENG-A1-COMM'), findsOneWidget);
    });

    testWidgets('a course opens its lessons from the dashboard', (tester) async {
      await pumpApp(tester, session: makeSession());
      await tester.tap(find.text('Xem bài học').first);
      await tester.pumpAndSettle();
      expect(find.text('Chào hỏi'), findsOneWidget);
    });
  });
}
