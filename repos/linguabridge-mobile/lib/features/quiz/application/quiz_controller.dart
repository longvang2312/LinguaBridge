import 'dart:math';

import 'package:flutter/foundation.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../../core/network/api_exception.dart';
import '../../../core/network/dio_client.dart';
import '../../courses/application/courses_providers.dart';
import '../data/quiz_models.dart';
import '../data/quiz_repository.dart';

final quizRepositoryProvider = Provider<QuizRepository>((ref) => QuizRepository(ref.watch(dioProvider)));

/// The source of the shuffle of answer choices; tests replace it with a seeded one.
final quizRandomProvider = Provider<Random>((ref) => Random());

/// Where a lesson run is.
enum QuizPhase {
  /// Fetching the quiz and opening the attempt.
  loading,

  /// A question is on screen and waiting for the learner's answer (or the answer is on its way).
  answering,

  /// The answer was graded (or was already on record): the learner reads the feedback and continues.
  feedback,

  /// Every question is answered and the attempt is scored.
  completed,

  /// The quiz could not be loaded.
  error,
}

@immutable
class QuizState {
  const QuizState({
    required this.phase,
    this.quiz,
    this.attemptId,
    this.index = 0,
    this.options = const [],
    this.selectedOptionId,
    this.busy = false,
    this.result,
    this.recorded = false,
    this.outcome,
    this.error,
    this.notFound = false,
  });

  const QuizState.loading() : this(phase: QuizPhase.loading);

  final QuizPhase phase;
  final Quiz? quiz;
  final String? attemptId;

  /// The question on screen.
  final int index;

  /// The choices of the question on screen in the order they are shown (shuffled once per question).
  final List<QuizOption> options;
  final String? selectedOptionId;

  /// A request is in flight: no second answer, no second "continue".
  final bool busy;

  /// The grading of the current question, in [QuizPhase.feedback].
  final AnswerResult? result;

  /// The answer was already on record (a reply that never arrived): no grading is known, the learner just moves on.
  final bool recorded;
  final AttemptResult? outcome;

  /// Something went wrong; in the answering and feedback phases the learner can try again, in [QuizPhase.error] it is why
  /// the quiz did not open.
  final String? error;

  /// The lesson does not exist or is not published.
  final bool notFound;

  QuizQuestion? get question => quiz == null || index >= quiz!.questions.length ? null : quiz!.questions[index];
  int get total => quiz?.questions.length ?? 0;
  bool get isLast => index >= total - 1;

  /// Share of the lesson that is behind the learner, for the progress bar.
  double get progress {
    if (phase == QuizPhase.completed) return 1;
    return total == 0 ? 0 : index / total;
  }

  QuizState copyWith({
    QuizPhase? phase,
    Quiz? quiz,
    String? attemptId,
    int? index,
    List<QuizOption>? options,
    String? selectedOptionId,
    bool clearSelected = false,
    bool? busy,
    AnswerResult? result,
    bool clearResult = false,
    bool? recorded,
    AttemptResult? outcome,
    String? error,
    bool clearError = false,
    bool? notFound,
  }) =>
      QuizState(
        phase: phase ?? this.phase,
        quiz: quiz ?? this.quiz,
        attemptId: attemptId ?? this.attemptId,
        index: index ?? this.index,
        options: options ?? this.options,
        selectedOptionId: clearSelected ? null : (selectedOptionId ?? this.selectedOptionId),
        busy: busy ?? this.busy,
        result: clearResult ? null : (result ?? this.result),
        recorded: recorded ?? this.recorded,
        outcome: outcome ?? this.outcome,
        error: clearError ? null : (error ?? this.error),
        notFound: notFound ?? this.notFound,
      );
}

/// Runs one lesson: opens the attempt, takes the answers one by one, completes the attempt. Everything the server decides
/// (grading, XP, progress) comes from the server; this only walks the learner through it.
class QuizController extends Notifier<QuizState> {
  QuizController(this.lessonId);

  final String lessonId;
  DateTime _shownAt = DateTime.now();

  QuizRepository get _repository => ref.read(quizRepositoryProvider);

  @override
  QuizState build() {
    Future.microtask(_load);
    return const QuizState.loading();
  }

  /// Opens the quiz and a new attempt. The quiz comes first: a lesson that does not exist answers "not found" before an
  /// attempt is created for it.
  Future<void> _load() async {
    state = const QuizState.loading();
    final Quiz quiz;
    final String attemptId;
    try {
      quiz = await _repository.quiz(lessonId, baseLanguage: ref.read(baseLanguageProvider));
      attemptId = await _repository.startAttempt(lessonId);
    } on ApiException catch (error) {
      if (!ref.mounted) return;
      state = QuizState(phase: QuizPhase.error, error: error.message, notFound: error.statusCode == 404);
      return;
    }
    if (!ref.mounted) return;

    state = QuizState(phase: QuizPhase.answering, quiz: quiz, attemptId: attemptId);
    if (quiz.questions.isEmpty) {
      await _complete();
    } else {
      _show(0);
    }
  }

  /// Opens the lesson again with a new attempt ("do this lesson again", or try again after a failed load).
  Future<void> restart() => _load();

  void _show(int index) {
    final question = state.quiz!.questions[index];
    final shuffled = question.options.toList();
    // Fisher-Yates: the options keep their ids, so the server gets the id of the option that was picked.
    final random = ref.read(quizRandomProvider);
    for (var i = shuffled.length - 1; i > 0; i--) {
      final j = random.nextInt(i + 1);
      final swap = shuffled[i];
      shuffled[i] = shuffled[j];
      shuffled[j] = swap;
    }
    _shownAt = DateTime.now();
    state = state.copyWith(
      phase: QuizPhase.answering,
      index: index,
      options: shuffled,
      busy: false,
      recorded: false,
      clearSelected: true,
      clearResult: true,
      clearError: true,
    );
  }

  /// Picks an option of a multiple-choice question (it can be changed until the answer is checked).
  void selectOption(String optionId) {
    if (state.phase != QuizPhase.answering || state.busy) return;
    if (!state.options.any((o) => o.id == optionId)) return;
    state = state.copyWith(selectedOptionId: optionId, clearError: true);
  }

  /// Checks the answer: the picked option, or [typedText] for a question answered by typing.
  Future<void> submit({String? typedText}) async {
    final question = state.question;
    if (state.phase != QuizPhase.answering || state.busy || question == null) return;

    final spent = DateTime.now().difference(_shownAt).inMilliseconds.clamp(0, 1 << 30);
    final QuizAnswer answer;
    if (question.isChoice) {
      final selected = state.selectedOptionId;
      if (selected == null) return;
      answer = QuizAnswer.choice(questionId: question.id, selectedOptionId: selected, timeSpentMs: spent);
    } else {
      final text = typedText?.trim() ?? '';
      if (text.isEmpty) return;
      answer = QuizAnswer.typed(questionId: question.id, userInputText: text, timeSpentMs: spent);
    }

    state = state.copyWith(busy: true, clearError: true);
    try {
      final result = await _repository.submitResponse(state.attemptId!, answer, baseLanguage: ref.read(baseLanguageProvider));
      if (!ref.mounted) return;
      state = state.copyWith(phase: QuizPhase.feedback, busy: false, result: result, recorded: false);
    } on ApiException catch (error) {
      if (!ref.mounted) return;
      if (error.code == 'response.already_answered') {
        // The first request got through but its reply was lost: the answer is on record, so the learner moves on.
        state = state.copyWith(phase: QuizPhase.feedback, busy: false, recorded: true, clearResult: true);
      } else if (error.code == 'attempt.already_completed') {
        state = state.copyWith(busy: false, error: 'Lượt làm bài này đã kết thúc. Hãy mở lại bài học.');
      } else {
        state = state.copyWith(busy: false, error: error.message);
      }
    }
  }

  /// Moves on after the feedback: to the next question, or completes the attempt after the last one.
  Future<void> next() async {
    if (state.phase != QuizPhase.feedback || state.busy) return;
    if (state.isLast) {
      await _complete();
    } else {
      _show(state.index + 1);
    }
  }

  Future<void> _complete() async {
    state = state.copyWith(busy: true, clearError: true);
    try {
      final outcome = await _repository.completeAttempt(state.attemptId!);
      if (!ref.mounted) return;
      state = state.copyWith(phase: QuizPhase.completed, busy: false, outcome: outcome);
      // The course list and the dashboard show progress: let them read it again.
      ref.invalidate(courseProgressProvider(state.quiz!.courseId));
    } on ApiException catch (error) {
      if (!ref.mounted) return;
      // Stay where we were: pressing the button again retries. A lesson without questions has no such button, so it is
      // reported like a quiz that did not open (its retry opens the lesson again).
      state = state.quiz!.questions.isEmpty
          ? QuizState(phase: QuizPhase.error, error: error.message)
          : state.copyWith(busy: false, error: error.message);
    }
  }
}

/// One lesson run per lesson id; forgotten when the screen is left.
final quizControllerProvider = NotifierProvider.autoDispose.family<QuizController, QuizState, String>(QuizController.new);
