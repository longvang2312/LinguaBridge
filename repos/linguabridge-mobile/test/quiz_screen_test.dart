import 'dart:async';

import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:linguabridge_mobile/core/network/api_exception.dart';
import 'package:linguabridge_mobile/core/router/app_router.dart';
import 'package:linguabridge_mobile/features/courses/data/course_models.dart';
import 'package:linguabridge_mobile/features/courses/presentation/lessons_sheet.dart';
import 'package:linguabridge_mobile/features/quiz/data/quiz_models.dart';
import 'package:linguabridge_mobile/features/quiz/presentation/quiz_screen.dart';

import 'support/fakes.dart';
import 'support/pump_app.dart';

final _answerInput = find.byKey(const Key('answer-input'));
final _check = find.byKey(const Key('check-button'));

/// Courses tab → "Xem bài học" of the first course → "Học ngay" of lesson 1.
Future<TestApp> _openLesson(WidgetTester tester, {FakeQuizRepository? quiz, FakeCoursesRepository? courses}) async {
  final app = await pumpApp(tester, session: makeSession(), quiz: quiz, repository: courses);
  await tester.tap(find.descendant(of: find.byType(NavigationBar), matching: find.text('Khóa học')));
  await tester.pumpAndSettle();
  await tester.tap(find.text('Xem bài học').first);
  await tester.pumpAndSettle();
  await tester.tap(find.byKey(const Key('start-lesson-l1')));
  await tester.pumpAndSettle();
  return app;
}

void _expectOnLesson(WidgetTester tester, {String lessonId = 'l1'}) {
  expect(find.byType(QuizScreen), findsOneWidget);
  expect(tester.widget<QuizScreen>(find.byType(QuizScreen)).lessonId, lessonId);
}

String _buttonLabel(WidgetTester tester) => (tester.widget<FilledButton>(_check).child as Text).data ?? '';

Future<void> _typeAndCheck(WidgetTester tester, String text) async {
  await tester.enterText(_answerInput, text);
  await tester.pump();
  await tester.tap(_check);
  await tester.pumpAndSettle();
}

Future<void> _continue(WidgetTester tester) async {
  await tester.tap(_check);
  await tester.pumpAndSettle();
}

/// Answers the three questions of the sample lesson rightly and presses the last button.
Future<void> _finishLesson(WidgetTester tester) async {
  await _typeAndCheck(tester, 'Hello');
  await _continue(tester);
  await tester.tap(find.text('Rất vui được gặp bạn.'));
  await tester.pump();
  await tester.tap(_check);
  await tester.pumpAndSettle();
  await _continue(tester);
  await _typeAndCheck(tester, 'Thank you');
  await _continue(tester);
}

void main() {
  group('starting a lesson', () {
    testWidgets('every lesson in the sheet has a Học ngay button', (tester) async {
      await pumpApp(tester, session: makeSession());
      await tester.tap(find.descendant(of: find.byType(NavigationBar), matching: find.text('Khóa học')));
      await tester.pumpAndSettle();
      await tester.tap(find.text('Xem bài học').first);
      await tester.pumpAndSettle();

      expect(find.text('Học ngay'), findsNWidgets(englishLessons.length));
      expect(find.byType(LessonsSheet), findsOneWidget);
    });

    testWidgets('opens the lesson full screen (no bottom bar) with the quiz and a new attempt', (tester) async {
      final app = await _openLesson(tester);

      _expectOnLesson(tester);
      expect(find.byType(NavigationBar), findsNothing);
      expect(find.byType(LessonsSheet), findsNothing);
      expect(app.quiz.calls, ['quiz:l1', 'start:l1']);
      expect(find.text('Câu 1/3'), findsOneWidget);
      expect(find.text('Tối đa 15 XP'), findsOneWidget);
    });

    testWidgets('the route names the course the lesson came from', (tester) async {
      await _openLesson(tester);
      // (go_router keeps showing the tab's address for a pushed screen, so the screen itself is asked.)
      expect(tester.widget<QuizScreen>(find.byType(QuizScreen)).courseId, englishCourse.id);
      expect(AppRoutes.lessonLocation('l1', courseId: 'c'), '/lesson/l1?courseId=c');
      expect(AppRoutes.lessonLocation('l1'), '/lesson/l1');
    });

    testWidgets('a lesson that does not exist says so and does not offer a retry', (tester) async {
      final app = await pumpApp(tester, session: makeSession(), quiz: FakeQuizRepository(quizzes: {}));
      app.container.read(appRouterProvider).push(AppRoutes.lessonLocation('missing'));
      await tester.pumpAndSettle();

      expect(find.textContaining('Không tìm thấy bài học'), findsOneWidget);
      expect(find.text('Thử lại'), findsNothing);
      expect(app.quiz.calls, ['quiz:missing']);

      await tester.tap(find.text('Về danh sách bài học'));
      await tester.pumpAndSettle();
      expect(app.location, AppRoutes.home);
    });

    testWidgets('a lesson that fails to load can be retried', (tester) async {
      final quiz = FakeQuizRepository()..loadErrors.add(const ApiException(message: 'Không kết nối được máy chủ. Hãy kiểm tra mạng và API đang chạy.'));
      await _openLesson(tester, quiz: quiz);

      expect(find.textContaining('Không kết nối được máy chủ'), findsOneWidget);
      await tester.tap(find.text('Thử lại'));
      await tester.pumpAndSettle();
      expect(find.text('Câu 1/3'), findsOneWidget);
    });
  });

  group('answering', () {
    testWidgets('a typed question shows only the prompt, focuses the box, and Kiểm tra waits for text', (tester) async {
      await _openLesson(tester);

      expect(find.byKey(const Key('question-prompt')), findsOneWidget);
      expect(find.text('Xin chào'), findsOneWidget);
      expect(find.text('Hello'), findsNothing, reason: 'the target text is the answer and is never shown');
      expect(tester.widget<TextField>(_answerInput).autofocus, isTrue);
      expect(tester.widget<FilledButton>(_check).onPressed, isNull);
      expect(_buttonLabel(tester), 'Kiểm tra');

      await tester.enterText(_answerInput, 'Hello');
      await tester.pump();
      expect(tester.widget<FilledButton>(_check).onPressed, isNotNull);

      await tester.enterText(_answerInput, '   ');
      await tester.pump();
      expect(tester.widget<FilledButton>(_check).onPressed, isNull);
    });

    testWidgets('a right answer turns the bar green with praise, and Tiếp tục moves on', (tester) async {
      final app = await _openLesson(tester);
      await _typeAndCheck(tester, 'Hello');

      final bar = tester.widget<AnimatedContainer>(find.byKey(const Key('answer-bar')));
      expect((bar.decoration as BoxDecoration).color, Colors.green.shade100);
      expect(find.byIcon(Icons.check_circle), findsOneWidget);
      expect(find.textContaining('Chính xác'), findsOneWidget);
      expect(_buttonLabel(tester), 'Tiếp tục');
      expect(app.quiz.answers.single.userInputText, 'Hello');

      await _continue(tester);
      expect(find.text('Câu 2/3'), findsOneWidget);
    });

    testWidgets('a wrong answer turns the bar red and shows the correct answer, explanation and grammar note', (tester) async {
      await _openLesson(tester);
      await _typeAndCheck(tester, 'Goodbye');

      final bar = tester.widget<AnimatedContainer>(find.byKey(const Key('answer-bar')));
      expect((bar.decoration as BoxDecoration).color, Colors.red.shade100);
      expect(find.text('Chưa đúng'), findsOneWidget);
      expect(find.textContaining('Đáp án đúng'), findsOneWidget);
      expect(find.textContaining('Hello'), findsWidgets);
      expect(find.textContaining('Giải thích cho q1.'), findsOneWidget);
      expect(find.textContaining('Ngữ pháp của q1.'), findsOneWidget);
      expect(tester.widget<TextField>(_answerInput).readOnly, isTrue);
    });

    testWidgets('the keyboard Done key checks the answer', (tester) async {
      final app = await _openLesson(tester);
      await tester.enterText(_answerInput, 'Hello');
      await tester.testTextInput.receiveAction(TextInputAction.done);
      await tester.pumpAndSettle();

      expect(app.quiz.answers, hasLength(1));
      expect(_buttonLabel(tester), 'Tiếp tục');
    });

    testWidgets('the answer box has the keyboard focus on every typed question, also right after another typed one', (tester) async {
      final twoTyped = Quiz(lessonId: 'l1', courseId: englishCourse.id, xpReward: 10, questions: [sampleQuiz.questions[0], sampleQuiz.questions[2]]);
      await _openLesson(tester, quiz: FakeQuizRepository(quizzes: {'l1': twoTyped}));
      bool focused() => tester.widget<EditableText>(find.descendant(of: _answerInput, matching: find.byType(EditableText))).focusNode.hasFocus;

      expect(focused(), isTrue, reason: 'first question');
      await _typeAndCheck(tester, 'Hello');
      await _continue(tester);
      expect(find.text('Câu 2/2'), findsOneWidget);
      expect(focused(), isTrue, reason: 'second typed question, straight after the first');
    });

    testWidgets('the answer box is empty again on the next typed question', (tester) async {
      await _openLesson(tester);
      await _typeAndCheck(tester, 'Hello');
      await _continue(tester);
      await tester.tap(find.text('Rất vui được gặp bạn.'));
      await tester.pump();
      await tester.tap(_check);
      await tester.pumpAndSettle();
      await _continue(tester);

      expect(find.text('Câu 3/3'), findsOneWidget);
      expect(tester.widget<TextField>(_answerInput).controller!.text, isEmpty);
      expect(tester.widget<FilledButton>(_check).onPressed, isNull);
    });

    testWidgets('a multiple-choice question shows numbered cards, the hint and the IPA; picking one enables Kiểm tra', (tester) async {
      await _openLesson(tester);
      await _typeAndCheck(tester, 'Hello');
      await _continue(tester);

      expect(find.byKey(const Key('question-target')), findsOneWidget);
      expect(find.text('Nice to meet you.'), findsOneWidget);
      expect(find.text('/naɪs/'), findsOneWidget);
      expect(find.text('Gợi ý: Rất vui được gặp bạn'), findsOneWidget);
      for (final n in ['1', '2', '3']) {
        expect(find.text(n), findsOneWidget);
      }
      for (final id in ['a', 'b', 'c']) {
        expect(find.byKey(Key('option-$id')), findsOneWidget);
      }
      expect(tester.widget<FilledButton>(_check).onPressed, isNull);

      await tester.tap(find.text('I am hungry.'));
      await tester.pump();
      expect(tester.widget<FilledButton>(_check).onPressed, isNotNull);
    });

    testWidgets('the options are shown in the order the controller shuffled them, and the pick is sent by id', (tester) async {
      final app = await _openLesson(tester);
      await _typeAndCheck(tester, 'Hello');
      await _continue(tester);

      final shown = ['a', 'b', 'c']..sort((x, y) => tester.getTopLeft(find.byKey(Key('option-$x'))).dy.compareTo(tester.getTopLeft(find.byKey(Key('option-$y'))).dy));
      final thirdText = {'a': 'See you tomorrow.', 'b': 'Rất vui được gặp bạn.', 'c': 'I am hungry.'}[shown[2]]!;
      await tester.tap(find.text(thirdText));
      await tester.pump();
      await tester.tap(_check);
      await tester.pumpAndSettle();

      expect(app.quiz.answers.last.selectedOptionId, shown[2]);
      expect(app.quiz.answers.last.userInputText, isNull);
    });

    testWidgets('a wrong choice is marked red and the right one green; cards are locked', (tester) async {
      await _openLesson(tester);
      await _typeAndCheck(tester, 'Hello');
      await _continue(tester);
      await tester.tap(find.text('I am hungry.'));
      await tester.pump();
      await tester.tap(_check);
      await tester.pumpAndSettle();

      Color border(String id) {
        final material = tester.widget<Material>(find.descendant(of: find.byKey(Key('option-$id')), matching: find.byType(Material)).first);
        return ((material.shape as RoundedRectangleBorder).side).color;
      }

      expect(border('c'), Colors.red.shade600);
      expect(border('b'), Colors.green.shade600);
      expect(find.text('Chưa đúng'), findsOneWidget);
      expect(find.textContaining('Đáp án đúng'), findsOneWidget);

      // Tapping another card after the check changes nothing.
      await tester.tap(find.text('See you tomorrow.'));
      await tester.pump();
      expect(border('a'), isNot(Colors.green.shade600));
      expect(_buttonLabel(tester), 'Tiếp tục');
    });

    testWidgets('the progress bar and the position move forward question by question', (tester) async {
      await _openLesson(tester);
      double progress() => tester.widget<LinearProgressIndicator>(find.byKey(const Key('quiz-progress'))).value!;

      expect(progress(), 0);
      await _typeAndCheck(tester, 'Hello');
      await _continue(tester);
      await tester.pumpAndSettle();
      expect(find.text('Câu 2/3'), findsOneWidget);
      expect(progress(), closeTo(1 / 3, 0.001));
    });

    testWidgets('a check that fails keeps the question and says why; checking again works', (tester) async {
      final quiz = FakeQuizRepository()..answerErrors.add(const ApiException(message: 'Không kết nối được máy chủ.'));
      await _openLesson(tester, quiz: quiz);
      await _typeAndCheck(tester, 'Hello');

      expect(find.byKey(const Key('answer-error')), findsOneWidget);
      expect(find.text('Không kết nối được máy chủ.'), findsOneWidget);
      expect(_buttonLabel(tester), 'Kiểm tra');
      expect(find.text('Câu 1/3'), findsOneWidget);
      expect(tester.widget<TextField>(_answerInput).controller!.text, 'Hello', reason: 'the answer is not lost');

      await tester.tap(_check);
      await tester.pumpAndSettle();
      expect(_buttonLabel(tester), 'Tiếp tục');
      expect(find.byKey(const Key('answer-error')), findsNothing);
    });

    testWidgets('an answer already on record shows a neutral notice and lets the learner continue', (tester) async {
      final quiz = FakeQuizRepository();
      await _openLesson(tester, quiz: quiz);
      quiz.answeredInAttempt['attempt-1'] = {'q1'};
      await _typeAndCheck(tester, 'Hello');

      expect(find.text('Đã ghi nhận câu trả lời'), findsOneWidget);
      expect(find.byIcon(Icons.cloud_done), findsOneWidget);
      expect(find.byIcon(Icons.check_circle), findsNothing);
      expect(find.byIcon(Icons.warning_amber_rounded), findsNothing);
      expect(find.byKey(const Key('answer-error')), findsNothing);
      expect(tester.widget<TextField>(_answerInput).readOnly, isTrue);
      expect(_buttonLabel(tester), 'Tiếp tục');

      await _continue(tester);
      expect(find.text('Câu 2/3'), findsOneWidget);
    });

    testWidgets('while the answer is on its way the button shows the wait and cannot be pressed again', (tester) async {
      final quiz = FakeQuizRepository()..answerGate = Completer<void>();
      await _openLesson(tester, quiz: quiz);
      await tester.enterText(_answerInput, 'Hello');
      await tester.pump();
      await tester.tap(_check);
      await tester.pump();

      expect(find.text('Đang kiểm tra...'), findsOneWidget);
      expect(tester.widget<FilledButton>(_check).onPressed, isNull);

      quiz.answerGate!.complete();
      await tester.pumpAndSettle();
      expect(quiz.answers, hasLength(1));
      expect(_buttonLabel(tester), 'Tiếp tục');
    });
  });

  group('leaving', () {
    testWidgets('the X asks first; staying keeps the lesson, leaving returns to the courses', (tester) async {
      final app = await _openLesson(tester);
      await _typeAndCheck(tester, 'Hello');

      await tester.tap(find.byKey(const Key('quiz-exit')));
      await tester.pumpAndSettle();
      expect(find.text('Thoát bài học?'), findsOneWidget);
      expect(find.textContaining('không nhận được XP'), findsOneWidget);

      await tester.tap(find.text('Ở lại học tiếp'));
      await tester.pumpAndSettle();
      expect(find.text('Thoát bài học?'), findsNothing);
      _expectOnLesson(tester);
      expect(_buttonLabel(tester), 'Tiếp tục');

      await tester.tap(find.byKey(const Key('quiz-exit')));
      await tester.pumpAndSettle();
      await tester.tap(find.text('Thoát'));
      await tester.pumpAndSettle();
      expect(app.location, AppRoutes.courses);
      expect(find.byType(NavigationBar), findsOneWidget);
      expect(app.quiz.calls.where((c) => c.startsWith('complete')), isEmpty, reason: 'an abandoned attempt is never completed');
    });

    testWidgets('the system back button asks the same question', (tester) async {
      await _openLesson(tester);
      await tester.binding.handlePopRoute();
      await tester.pumpAndSettle();

      expect(find.text('Thoát bài học?'), findsOneWidget);
      _expectOnLesson(tester);
    });

    testWidgets('before the quiz is open the X leaves without asking', (tester) async {
      final quiz = FakeQuizRepository()..loadErrors.add(const ApiException(message: 'Không kết nối được máy chủ.'));
      final app = await _openLesson(tester, quiz: quiz);
      await tester.tap(find.byKey(const Key('quiz-exit')));
      await tester.pumpAndSettle();

      expect(find.text('Thoát bài học?'), findsNothing);
      expect(app.location, AppRoutes.courses);
    });
  });

  group('completing', () {
    testWidgets('after the last question the attempt is completed and the result is shown', (tester) async {
      final app = await _openLesson(tester);
      await _finishLesson(tester);

      expect(app.quiz.calls.where((c) => c.startsWith('complete')), ['complete:attempt-1']);
      expect(find.text('Xuất sắc!'), findsOneWidget);
      expect(tester.widget<Text>(find.descendant(of: find.byKey(const Key('stat-score')), matching: find.textContaining('%'))).data, '100%');
      expect(find.descendant(of: find.byKey(const Key('stat-correct')), matching: find.text('3/3')), findsOneWidget);
      expect(find.descendant(of: find.byKey(const Key('stat-xp')), matching: find.text('11')), findsOneWidget);
      expect(find.text('Tiến độ khóa học: 1/2 bài học'), findsOneWidget);
      expect(find.text('Về danh sách bài học'), findsOneWidget);
      expect(find.text('Làm lại bài này'), findsOneWidget);
    });

    testWidgets('the last button says Hoàn thành', (tester) async {
      await _openLesson(tester);
      await _typeAndCheck(tester, 'Hello');
      await _continue(tester);
      await tester.tap(find.text('Rất vui được gặp bạn.'));
      await tester.pump();
      await tester.tap(_check);
      await tester.pumpAndSettle();
      await _continue(tester);
      await _typeAndCheck(tester, 'Thank you');
      expect(_buttonLabel(tester), 'Hoàn thành');
    });

    testWidgets('a score under 100 shows the real counts', (tester) async {
      await _openLesson(tester);
      await _typeAndCheck(tester, 'wrong');
      await _continue(tester);
      await tester.tap(find.text('Rất vui được gặp bạn.'));
      await tester.pump();
      await tester.tap(_check);
      await tester.pumpAndSettle();
      await _continue(tester);
      await _typeAndCheck(tester, 'Thank you');
      await _continue(tester);

      expect(find.descendant(of: find.byKey(const Key('stat-score')), matching: find.text('67%')), findsOneWidget);
      expect(find.descendant(of: find.byKey(const Key('stat-correct')), matching: find.text('2/3')), findsOneWidget);
    });

    testWidgets('a repeat run explains that it earns no XP', (tester) async {
      await _openLesson(tester, quiz: FakeQuizRepository(firstCompletion: false));
      await _finishLesson(tester);
      expect(find.textContaining('không được thêm XP'), findsOneWidget);
      expect(find.descendant(of: find.byKey(const Key('stat-xp')), matching: find.text('0')), findsOneWidget);
    });

    testWidgets('a completion that fails can be tried again from the same button', (tester) async {
      final quiz = FakeQuizRepository()..completeErrors.add(const ApiException(message: 'Không kết nối được máy chủ.'));
      await _openLesson(tester, quiz: quiz);
      await _finishLesson(tester);

      expect(find.byKey(const Key('answer-error')), findsOneWidget);
      expect(_buttonLabel(tester), 'Hoàn thành');
      await _continue(tester);
      expect(find.text('Xuất sắc!'), findsOneWidget);
    });

    testWidgets('Về danh sách bài học returns to the courses with the progress updated', (tester) async {
      final courses = FakeCoursesRepository(
        courseList: {'en': [englishCourse, businessCourse], 'zh': [chineseCourse]},
        lessonList: {englishCourse.id: englishLessons},
        progressList: <String, CourseProgress>{englishCourse.id: const CourseProgress(completedLessons: 0, totalLessons: 2, isCompleted: false)},
      );
      final quiz = FakeQuizRepository()..onCompleted = (result) => courses.progressList[englishCourse.id] = result.progress!;
      final app = await _openLesson(tester, quiz: quiz, courses: courses);
      expect(find.text('Đã hoàn thành 0/2 bài học'), findsNothing, reason: 'the lesson screen covers the list');

      await _finishLesson(tester);
      await tester.tap(find.text('Về danh sách bài học'));
      await tester.pumpAndSettle();

      expect(app.location, AppRoutes.courses);
      expect(find.byType(NavigationBar), findsOneWidget);
      expect(find.text('Đã hoàn thành 1/2 bài học'), findsOneWidget);
      expect(find.text('Đã hoàn thành 0/2 bài học'), findsNothing);
    });

    testWidgets('Làm lại bài này opens a new attempt from the first question', (tester) async {
      final app = await _openLesson(tester);
      await _finishLesson(tester);
      await tester.tap(find.text('Làm lại bài này'));
      await tester.pumpAndSettle();

      expect(app.quiz.calls.where((c) => c.startsWith('start')), ['start:l1', 'start:l1']);
      expect(find.text('Câu 1/3'), findsOneWidget);
      expect(find.text('Xin chào'), findsOneWidget);
      expect(tester.widget<FilledButton>(_check).onPressed, isNull);

      // The new attempt takes answers again (the fake only rejects repeats inside one attempt).
      await _typeAndCheck(tester, 'Hello');
      expect(_buttonLabel(tester), 'Tiếp tục');
    });

    testWidgets('after the result the X leaves without asking', (tester) async {
      final app = await _openLesson(tester);
      await _finishLesson(tester);
      await tester.tap(find.byKey(const Key('quiz-exit')));
      await tester.pumpAndSettle();

      expect(find.text('Thoát bài học?'), findsNothing);
      expect(app.location, AppRoutes.courses);
    });
  });

  testWidgets('the lesson also opens from the dashboard', (tester) async {
    final app = await pumpApp(tester, session: makeSession());
    await tester.tap(find.text('Xem bài học').first);
    await tester.pumpAndSettle();
    await tester.tap(find.byKey(const Key('start-lesson-l1')));
    await tester.pumpAndSettle();

    _expectOnLesson(tester);
    expect(find.text('Câu 1/3'), findsOneWidget);

    await tester.tap(find.byKey(const Key('quiz-exit')));
    await tester.pumpAndSettle();
    await tester.tap(find.text('Thoát'));
    await tester.pumpAndSettle();
    expect(app.location, AppRoutes.home, reason: 'back to where the lesson was started from');
  });
}

