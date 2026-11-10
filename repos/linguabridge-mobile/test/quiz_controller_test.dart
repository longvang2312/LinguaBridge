import 'dart:async';
import 'dart:math';

import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:linguabridge_mobile/core/network/api_exception.dart';
import 'package:linguabridge_mobile/features/quiz/application/quiz_controller.dart';
import 'package:linguabridge_mobile/features/quiz/data/quiz_models.dart';

import 'support/fakes.dart';

class _Run {
  _Run(this.repository, {int seed = 1})
      : container = ProviderContainer(overrides: [
          quizRepositoryProvider.overrideWithValue(repository),
          quizRandomProvider.overrideWithValue(Random(seed)),
        ]) {
    // autoDispose: keep the run alive for the length of the test.
    container.listen(quizControllerProvider('l1'), (_, _) {});
  }

  final FakeQuizRepository repository;
  final ProviderContainer container;

  QuizState get state => container.read(quizControllerProvider('l1'));
  QuizController get controller => container.read(quizControllerProvider('l1').notifier);

  Future<void> settle() async {
    for (var i = 0; i < 5; i++) {
      await Future<void>.delayed(Duration.zero);
    }
  }

  /// Gets to the first question.
  Future<void> open() => settle();

  /// Answers the current question rightly (typed or by choice) and waits for the feedback.
  Future<void> answerRight() async {
    final question = state.question!;
    if (question.isChoice) {
      controller.selectOption('b');
      await controller.submit();
    } else {
      await controller.submit(typedText: question.id == 'q1' ? 'Hello' : 'Thank you');
    }
  }

  void dispose() => container.dispose();
}

_Run _start({FakeQuizRepository? repository, int seed = 1}) {
  final run = _Run(repository ?? FakeQuizRepository(), seed: seed);
  addTearDown(run.dispose);
  return run;
}

void main() {
  group('opening the lesson', () {
    test('asks for the quiz first, then opens an attempt, and shows the first question', () async {
      final run = _start();
      expect(run.state.phase, QuizPhase.loading);
      await run.open();

      expect(run.repository.calls, ['quiz:l1', 'start:l1']);
      expect(run.state.phase, QuizPhase.answering);
      expect(run.state.attemptId, 'attempt-1');
      expect(run.state.index, 0);
      expect(run.state.total, 3);
      expect(run.state.question?.id, 'q1');
      expect(run.state.progress, 0);
    });

    test('a lesson that does not exist is reported without opening an attempt', () async {
      final run = _start(repository: FakeQuizRepository(quizzes: {}));
      await run.open();

      expect(run.state.phase, QuizPhase.error);
      expect(run.state.notFound, isTrue);
      expect(run.repository.calls, ['quiz:l1'], reason: 'no attempt for a lesson that cannot be played');
    });

    test('a failed load can be retried and then opens normally', () async {
      final repository = FakeQuizRepository()..loadErrors.add(const ApiException(message: 'Không kết nối được máy chủ.'));
      final run = _start(repository: repository);
      await run.open();
      expect(run.state.phase, QuizPhase.error);
      expect(run.state.notFound, isFalse);
      expect(run.state.error, contains('Không kết nối'));

      await run.controller.restart();
      expect(run.state.phase, QuizPhase.answering);
    });

    test('a lesson without questions is completed straight away', () async {
      final empty = Quiz(lessonId: 'l1', courseId: englishCourse.id, xpReward: 5, questions: const []);
      final run = _start(repository: FakeQuizRepository(quizzes: {'l1': empty}));
      await run.open();
      expect(run.repository.calls, ['quiz:l1', 'start:l1', 'complete:attempt-1']);
      expect(run.state.phase, QuizPhase.completed);
    });
  });

  group('shuffling', () {
    test('keeps the options and their ids, only the order changes', () async {
      final run = _start();
      await run.open();
      await run.answerRight();
      await run.controller.next();

      final shown = run.state.options.map((o) => o.id).toList();
      expect(shown, unorderedEquals(['a', 'b', 'c']));
      expect(run.state.options.firstWhere((o) => o.id == 'b').text, 'Rất vui được gặp bạn.');
    });

    test('the right answer does not always sit at the same position', () async {
      final positions = <int>{};
      final orders = <String>{};
      for (var seed = 0; seed < 30; seed++) {
        final run = _Run(FakeQuizRepository(), seed: seed);
        await run.open();
        await run.answerRight();
        await run.controller.next();
        positions.add(run.state.options.indexWhere((o) => o.id == 'b'));
        orders.add(run.state.options.map((o) => o.id).join());
        run.dispose();
      }
      expect(positions, {0, 1, 2});
      expect(orders.length, greaterThan(3));
    });

    test('the order is chosen once per question, not again on every change', () async {
      final run = _start();
      await run.open();
      await run.answerRight();
      await run.controller.next();
      final before = run.state.options.map((o) => o.id).toList();

      run.controller.selectOption('a');
      run.controller.selectOption('c');
      expect(run.state.options.map((o) => o.id), before);
    });
  });

  group('answering', () {
    test('picking an option can be changed until it is checked', () async {
      final run = _start();
      await run.open();
      await run.answerRight();
      await run.controller.next();

      run.controller.selectOption('a');
      expect(run.state.selectedOptionId, 'a');
      run.controller.selectOption('c');
      expect(run.state.selectedOptionId, 'c');
      run.controller.selectOption('zzz');
      expect(run.state.selectedOptionId, 'c', reason: 'not an option of this question');
    });

    test('a typed answer is sent trimmed with the question id and the time spent', () async {
      final run = _start();
      await run.open();
      await run.controller.submit(typedText: '  Hello  ');

      final answer = run.repository.answers.single;
      expect(answer.questionId, 'q1');
      expect(answer.userInputText, 'Hello');
      expect(answer.selectedOptionId, isNull);
      expect(answer.timeSpentMs, greaterThanOrEqualTo(0));
      expect(run.state.phase, QuizPhase.feedback);
      expect(run.state.result?.isCorrect, isTrue);
    });

    test('an empty typed answer, or a choice with nothing picked, is not sent', () async {
      final run = _start();
      await run.open();
      await run.controller.submit(typedText: '   ');
      await run.controller.submit();
      expect(run.repository.answers, isEmpty);

      await run.answerRight();
      await run.controller.next();
      await run.controller.submit(); // choice question, nothing picked
      expect(run.repository.answers, hasLength(1));
      expect(run.state.phase, QuizPhase.answering);
    });

    test('a wrong answer brings the correct answer and the explanation from the server', () async {
      final run = _start();
      await run.open();
      await run.controller.submit(typedText: 'definitely wrong');

      expect(run.state.phase, QuizPhase.feedback);
      expect(run.state.result?.isCorrect, isFalse);
      expect(run.state.result?.correctAnswer, 'Hello');
      expect(run.state.result?.explanation, contains('q1'));
      expect(run.state.result?.grammarNotes, contains('q1'));
    });

    test('a choice answer sends the id of the option picked, wherever it is shown', () async {
      final run = _start(seed: 5);
      await run.open();
      await run.answerRight();
      await run.controller.next();

      final thirdShown = run.state.options[2].id;
      run.controller.selectOption(thirdShown);
      await run.controller.submit();
      expect(run.repository.answers.last.selectedOptionId, thirdShown);
    });

    test('pressing check twice sends the answer once', () async {
      final repository = FakeQuizRepository()..answerGate = Completer<void>();
      final run = _start(repository: repository);
      await run.open();

      final first = run.controller.submit(typedText: 'Hello');
      final second = run.controller.submit(typedText: 'Hello');
      expect(run.state.busy, isTrue);
      repository.answerGate!.complete();
      await Future.wait([first, second]);

      expect(repository.answers, hasLength(1));
      expect(run.state.busy, isFalse);
    });

    test('nothing can be picked while the answer is on its way, nor after it is checked', () async {
      final repository = FakeQuizRepository()..answerGate = Completer<void>();
      final run = _start(repository: repository);
      await run.open();
      repository.answerGate!.complete();
      await run.answerRight();
      await run.controller.next();

      run.controller.selectOption('a');
      await run.controller.submit();
      run.controller.selectOption('c');
      expect(run.state.selectedOptionId, 'a', reason: 'checked answers are final');
    });

    test('a failed check keeps the question open with the reason, and checking again works', () async {
      final repository = FakeQuizRepository()..answerErrors.add(const ApiException(message: 'Không kết nối được máy chủ.'));
      final run = _start(repository: repository);
      await run.open();

      await run.controller.submit(typedText: 'Hello');
      expect(run.state.phase, QuizPhase.answering);
      expect(run.state.busy, isFalse);
      expect(run.state.error, contains('Không kết nối'));

      await run.controller.submit(typedText: 'Hello');
      expect(run.state.phase, QuizPhase.feedback);
      expect(run.state.error, isNull);
    });
  });

  group('a reply that was lost (already answered)', () {
    test('moves to a neutral "recorded" feedback so the learner is never stuck', () async {
      final repository = FakeQuizRepository();
      final run = _start(repository: repository);
      await run.open();
      // The server has the answer; the app never saw its reply.
      repository.answeredInAttempt['attempt-1'] = {'q1'};

      await run.controller.submit(typedText: 'Hello');

      expect(run.state.phase, QuizPhase.feedback);
      expect(run.state.recorded, isTrue);
      expect(run.state.result, isNull, reason: 'whether it was right is not known');
      expect(run.state.error, isNull);

      await run.controller.next();
      expect(run.state.phase, QuizPhase.answering);
      expect(run.state.index, 1);
      expect(run.state.recorded, isFalse);
    });

    test('on the last question the same way out leads to completing', () async {
      final repository = FakeQuizRepository();
      final run = _start(repository: repository);
      await run.open();
      await run.answerRight();
      await run.controller.next();
      await run.answerRight();
      await run.controller.next();
      repository.answeredInAttempt['attempt-1']!.add('q3');

      await run.controller.submit(typedText: 'Thank you');
      expect(run.state.recorded, isTrue);
      await run.controller.next();
      expect(run.state.phase, QuizPhase.completed);
    });

    test('an attempt that is already closed says so', () async {
      final repository = FakeQuizRepository()
        ..answerErrors.add(const ApiException(message: 'closed', statusCode: 409, code: 'attempt.already_completed'));
      final run = _start(repository: repository);
      await run.open();
      await run.controller.submit(typedText: 'Hello');
      expect(run.state.phase, QuizPhase.answering);
      expect(run.state.error, contains('kết thúc'));
    });
  });

  group('completing', () {
    test('walks through every question, then completes the attempt once and keeps the result', () async {
      final run = _start();
      await run.open();

      final progress = <double>[];
      for (var i = 0; i < 3; i++) {
        progress.add(run.state.progress);
        expect(run.state.index, i);
        await run.answerRight();
        expect(run.state.phase, QuizPhase.feedback);
        await run.controller.next();
      }

      expect(progress, [0.0, 1 / 3, 2 / 3]);
      expect(run.repository.calls.where((c) => c.startsWith('complete')), ['complete:attempt-1']);
      expect(run.state.phase, QuizPhase.completed);
      expect(run.state.progress, 1);
      expect(run.state.outcome?.scorePercentage, 100);
      expect(run.state.outcome?.correctCount, 3);
      expect(run.state.outcome?.xpEarned, 11);
    });

    test('continue does nothing before an answer is checked', () async {
      final run = _start();
      await run.open();
      await run.controller.next();
      expect(run.state.index, 0);
      expect(run.state.phase, QuizPhase.answering);
    });

    test('a failed completion stays on the last question, and the next try completes', () async {
      final repository = FakeQuizRepository()..completeErrors.add(const ApiException(message: 'Không kết nối được máy chủ.'));
      final run = _start(repository: repository);
      await run.open();
      for (var i = 0; i < 3; i++) {
        await run.answerRight();
        await run.controller.next();
      }
      expect(run.state.phase, QuizPhase.feedback);
      expect(run.state.isLast, isTrue);
      expect(run.state.error, contains('Không kết nối'));
      expect(run.state.busy, isFalse);

      await run.controller.next();
      expect(run.state.phase, QuizPhase.completed);
      expect(run.state.error, isNull);
    });

    test('doing it again opens a new attempt and starts from the first question', () async {
      final run = _start();
      await run.open();
      for (var i = 0; i < 3; i++) {
        await run.answerRight();
        await run.controller.next();
      }
      await run.controller.restart();

      expect(run.state.phase, QuizPhase.answering);
      expect(run.state.index, 0);
      expect(run.state.attemptId, 'attempt-2');
      expect(run.state.outcome, isNull);
      expect(run.repository.calls.where((c) => c.startsWith('start')), ['start:l1', 'start:l1']);
    });

    test('a repeat run reports no XP', () async {
      final run = _start(repository: FakeQuizRepository(firstCompletion: false));
      await run.open();
      for (var i = 0; i < 3; i++) {
        await run.answerRight();
        await run.controller.next();
      }
      expect(run.state.outcome?.isFirstCompletion, isFalse);
      expect(run.state.outcome?.xpEarned, 0);
    });
  });
}
