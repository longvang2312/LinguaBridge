import 'package:flutter/foundation.dart';

import '../../courses/data/course_models.dart';

/// An answer choice as the learner sees it: there is deliberately no correctness flag (`LearnerOptionDto`).
@immutable
class QuizOption {
  const QuizOption({required this.id, required this.orderIndex, required this.text, this.pinyinOrIpa});

  final String id;
  final int orderIndex;
  final String text;
  final String? pinyinOrIpa;

  factory QuizOption.fromJson(Map<String, dynamic> json) => QuizOption(
        id: json['id'] as String,
        orderIndex: (json['orderIndex'] as num).toInt(),
        text: json['text'] as String,
        pinyinOrIpa: json['pinyinOrIpa'] as String?,
      );
}

/// A question of a lesson (`LearnerQuestionDto`). For questions answered by typing, [targetText] and [pinyinOrIpa] are
/// null: they are the answer.
@immutable
class QuizQuestion {
  const QuizQuestion({
    required this.id,
    required this.orderIndex,
    required this.questionType,
    this.audioPromptUrl,
    this.targetText,
    this.pinyinOrIpa,
    this.promptTranslation,
    this.options = const [],
  });

  final String id;
  final int orderIndex;

  /// `MultipleChoice`, `FillBlank`, `Translate` or `Pronounce`.
  final String questionType;
  final String? audioPromptUrl;
  final String? targetText;
  final String? pinyinOrIpa;

  /// The prompt in the learner's language.
  final String? promptTranslation;
  final List<QuizOption> options;

  /// Answered by picking an option; every other kind is answered by typing.
  bool get isChoice => options.isNotEmpty;

  factory QuizQuestion.fromJson(Map<String, dynamic> json) => QuizQuestion(
        id: json['id'] as String,
        orderIndex: (json['orderIndex'] as num).toInt(),
        questionType: json['questionType'] as String,
        audioPromptUrl: json['audioPromptUrl'] as String?,
        targetText: json['targetText'] as String?,
        pinyinOrIpa: json['pinyinOrIpa'] as String?,
        promptTranslation: json['promptTranslation'] as String?,
        options: [
          for (final option in (json['options'] as List<dynamic>? ?? const []))
            QuizOption.fromJson(option as Map<String, dynamic>),
        ],
      );
}

/// The learner's quiz of a lesson (`LearnerQuizDto`), questions in order.
@immutable
class Quiz {
  const Quiz({required this.lessonId, required this.courseId, required this.xpReward, required this.questions});

  final String lessonId;
  final String courseId;
  final int xpReward;
  final List<QuizQuestion> questions;

  factory Quiz.fromJson(Map<String, dynamic> json) => Quiz(
        lessonId: json['lessonId'] as String,
        courseId: json['courseId'] as String,
        xpReward: (json['xpReward'] as num?)?.toInt() ?? 0,
        questions: [
          for (final question in (json['questions'] as List<dynamic>? ?? const []))
            QuizQuestion.fromJson(question as Map<String, dynamic>),
        ]..sort((a, b) => a.orderIndex.compareTo(b.orderIndex)),
      );
}

/// What the learner answered. Exactly one of [selectedOptionId] and [userInputText] is set.
@immutable
class QuizAnswer {
  const QuizAnswer.choice({required this.questionId, required String this.selectedOptionId, required this.timeSpentMs}) : userInputText = null;

  const QuizAnswer.typed({required this.questionId, required String this.userInputText, required this.timeSpentMs}) : selectedOptionId = null;

  final String questionId;
  final String? selectedOptionId;
  final String? userInputText;
  final int timeSpentMs;

  Map<String, dynamic> toJson() => {
        'questionId': questionId,
        'selectedOptionId': ?selectedOptionId,
        'userInputText': ?userInputText,
        'timeSpentMs': timeSpentMs,
      };
}

/// The instant feedback of one answer (`ResponseResultDto`): the right answer is revealed only now.
@immutable
class AnswerResult {
  const AnswerResult({
    required this.questionId,
    required this.isCorrect,
    this.correctOptionId,
    this.correctAnswer,
    this.explanation,
    this.grammarNotes,
  });

  final String questionId;
  final bool isCorrect;
  final String? correctOptionId;
  final String? correctAnswer;
  final String? explanation;
  final String? grammarNotes;

  factory AnswerResult.fromJson(Map<String, dynamic> json) => AnswerResult(
        questionId: json['questionId'] as String,
        isCorrect: json['isCorrect'] as bool,
        correctOptionId: json['correctOptionId'] as String?,
        correctAnswer: json['correctAnswer'] as String?,
        explanation: json['explanation'] as String?,
        grammarNotes: json['grammarNotes'] as String?,
      );
}

/// The outcome of a completed attempt (`AttemptCompletedDto`).
@immutable
class AttemptResult {
  const AttemptResult({
    required this.scorePercentage,
    required this.correctCount,
    required this.totalQuestions,
    required this.xpEarned,
    required this.isFirstCompletion,
    this.progress,
  });

  final int scorePercentage;
  final int correctCount;
  final int totalQuestions;

  /// Only the first completion of a lesson earns XP.
  final int xpEarned;
  final bool isFirstCompletion;
  final CourseProgress? progress;

  factory AttemptResult.fromJson(Map<String, dynamic> json) => AttemptResult(
        scorePercentage: (json['scorePercentage'] as num).toInt(),
        correctCount: (json['correctCount'] as num).toInt(),
        totalQuestions: (json['totalQuestions'] as num).toInt(),
        xpEarned: (json['xpEarned'] as num).toInt(),
        isFirstCompletion: json['isFirstCompletion'] as bool? ?? false,
        progress: json['progress'] is Map<String, dynamic> ? CourseProgress.fromJson(json['progress'] as Map<String, dynamic>) : null,
      );
}
