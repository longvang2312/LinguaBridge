import 'package:dio/dio.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:linguabridge_mobile/core/network/api_exception.dart';
import 'package:linguabridge_mobile/features/quiz/data/quiz_models.dart';
import 'package:linguabridge_mobile/features/quiz/data/quiz_repository.dart';

import 'support/fakes.dart';

QuizRepository _repository(FakeAdapter adapter) => QuizRepository(Dio(BaseOptions(baseUrl: 'http://api.test'))..httpClientAdapter = adapter);

// What the API sends (LearnerQuizDto, camelCase): typed questions have no target text, options have no correctness flag.
Map<String, Object?> get _quizJson => {
      'lessonId': 'l1',
      'courseId': 'c1',
      'xpReward': 15,
      'questions': [
        {
          'id': 'q2',
          'orderIndex': 2,
          'questionType': 'MultipleChoice',
          'audioPromptUrl': null,
          'targetText': 'Nice to meet you.',
          'pinyinOrIpa': '/naɪs/',
          'promptTranslation': 'Rất vui được gặp bạn',
          'options': [
            {'id': 'a', 'orderIndex': 1, 'text': 'See you', 'pinyinOrIpa': null},
            {'id': 'b', 'orderIndex': 2, 'text': 'Nice', 'pinyinOrIpa': 'naɪs'},
          ],
        },
        {
          'id': 'q1',
          'orderIndex': 1,
          'questionType': 'Translate',
          'audioPromptUrl': null,
          'targetText': null,
          'pinyinOrIpa': null,
          'promptTranslation': 'Xin chào',
          'options': <Object?>[],
        },
      ],
    };

void main() {
  test('quiz asks for the lesson in the base language and reads the questions in order', () async {
    final adapter = FakeAdapter((_) => jsonBody(200, _quizJson));
    final quiz = await _repository(adapter).quiz('l1', baseLanguage: 'vi');

    expect(adapter.requests.single.method, 'GET');
    expect(adapter.requests.single.path, '/api/v1/learning/lessons/l1/quiz');
    expect(adapter.requests.single.queryParameters, {'baseLanguage': 'vi'});
    expect(quiz.xpReward, 15);
    expect(quiz.courseId, 'c1');
    expect(quiz.questions.map((q) => q.id), ['q1', 'q2'], reason: 'sorted by orderIndex');
    expect(quiz.questions[0].isChoice, isFalse);
    expect(quiz.questions[0].targetText, isNull);
    expect(quiz.questions[1].isChoice, isTrue);
    expect(quiz.questions[1].options.map((o) => o.id), ['a', 'b']);
    expect(quiz.questions[1].pinyinOrIpa, '/naɪs/');
  });

  test('startAttempt posts the lesson id and returns the attempt id', () async {
    final adapter = FakeAdapter((_) => jsonBody(201, {'attemptId': 'att-1', 'lessonId': 'l1', 'totalQuestions': 3, 'startedAt': '2026-11-10T00:00:00Z'}));
    final id = await _repository(adapter).startAttempt('l1');

    expect(adapter.requests.single.method, 'POST');
    expect(adapter.requests.single.path, '/api/v1/learning/attempts');
    expect(adapter.requests.single.data, {'lessonId': 'l1'});
    expect(id, 'att-1');
  });

  test('a multiple-choice answer sends the option id and no text', () async {
    final adapter = FakeAdapter((_) => jsonBody(200, {
          'questionId': 'q2',
          'isCorrect': false,
          'correctOptionId': 'b',
          'correctAnswer': 'Nice',
          'explanation': 'Because.',
          'grammarNotes': null,
          'answeredCount': 1,
          'totalQuestions': 3,
        }));
    final result = await _repository(adapter)
        .submitResponse('att-1', const QuizAnswer.choice(questionId: 'q2', selectedOptionId: 'a', timeSpentMs: 1200), baseLanguage: 'vi');

    final request = adapter.requests.single;
    expect(request.method, 'POST');
    expect(request.path, '/api/v1/learning/attempts/att-1/responses');
    expect(request.queryParameters, {'baseLanguage': 'vi'});
    expect(request.data, {'questionId': 'q2', 'selectedOptionId': 'a', 'timeSpentMs': 1200});
    expect(result.isCorrect, isFalse);
    expect(result.correctOptionId, 'b');
    expect(result.correctAnswer, 'Nice');
    expect(result.explanation, 'Because.');
    expect(result.grammarNotes, isNull);
  });

  test('a typed answer sends the text and no option id', () async {
    final adapter = FakeAdapter((_) => jsonBody(200, {'questionId': 'q1', 'isCorrect': true, 'answeredCount': 1, 'totalQuestions': 3}));
    final result = await _repository(adapter)
        .submitResponse('att-1', const QuizAnswer.typed(questionId: 'q1', userInputText: 'Hello', timeSpentMs: 800), baseLanguage: 'en');

    expect(adapter.requests.single.data, {'questionId': 'q1', 'userInputText': 'Hello', 'timeSpentMs': 800});
    expect(adapter.requests.single.queryParameters, {'baseLanguage': 'en'});
    expect(result.isCorrect, isTrue);
  });

  test('completeAttempt reads the score, XP and course progress', () async {
    final adapter = FakeAdapter((_) => jsonBody(200, {
          'attemptId': 'att-1',
          'scorePercentage': 71,
          'correctCount': 5,
          'totalQuestions': 7,
          'xpEarned': 11,
          'isFirstCompletion': true,
          'progress': {'courseId': 'c1', 'completedLessonsCount': 1, 'totalLessonsCount': 2, 'isCompleted': false, 'lastAccessedAt': null},
        }));
    final result = await _repository(adapter).completeAttempt('att-1');

    expect(adapter.requests.single.method, 'PUT');
    expect(adapter.requests.single.path, '/api/v1/learning/attempts/att-1/complete');
    expect(result.scorePercentage, 71);
    expect(result.correctCount, 5);
    expect(result.totalQuestions, 7);
    expect(result.xpEarned, 11);
    expect(result.isFirstCompletion, isTrue);
    expect(result.progress?.completedLessons, 1);
    expect(result.progress?.totalLessons, 2);
  });

  test('already answered keeps its code so the screen can move on', () async {
    final adapter = FakeAdapter((_) => jsonBody(409, {'title': 'Conflict', 'detail': 'Already answered.', 'code': 'response.already_answered'}));
    await expectLater(
      _repository(adapter).submitResponse('att-1', const QuizAnswer.typed(questionId: 'q1', userInputText: 'x', timeSpentMs: 1), baseLanguage: 'vi'),
      throwsA(isA<ApiException>().having((e) => e.code, 'code', 'response.already_answered').having((e) => e.statusCode, 'status', 409)),
    );
  });

  test('a missing lesson is a 404 ApiException', () async {
    final adapter = FakeAdapter((_) => jsonBody(404, {'title': 'Not found', 'detail': 'Lesson not found.'}));
    await expectLater(_repository(adapter).quiz('x', baseLanguage: 'vi'), throwsA(isA<ApiException>().having((e) => e.statusCode, 'status', 404)));
  });

  test('being offline is an ApiException that says so', () async {
    final adapter = FakeAdapter((o) => throw DioException.connectionError(requestOptions: o, reason: 'offline'));
    await expectLater(_repository(adapter).startAttempt('l1'), throwsA(isA<ApiException>().having((e) => e.isNetworkError, 'network', isTrue)));
  });

  test('an answer that is not an object is refused', () async {
    await expectLater(_repository(FakeAdapter((_) => jsonBody(200, [1, 2]))).completeAttempt('att-1'), throwsA(isA<ApiException>()));
    await expectLater(_repository(FakeAdapter((_) => jsonBody(200, {'no': 'id'}))).startAttempt('l1'), throwsA(isA<ApiException>()));
  });
}
