import 'package:flutter/foundation.dart';

/// A published course, as `GET /api/content/courses` describes it (`CourseSummaryDto`).
@immutable
class CourseModel {
  const CourseModel({
    required this.id,
    required this.code,
    required this.targetLanguage,
    required this.level,
    this.title,
    this.description,
    this.thumbnailUrl,
  });

  final String id;
  final String code;

  /// The language being learned: `en` or `zh`.
  final String targetLanguage;

  /// `A1`, `A2`, `HSK1`...
  final String level;

  /// In the base language that was asked for; null when the course has no translation at all.
  final String? title;
  final String? description;
  final String? thumbnailUrl;

  /// The title to show: the translation, or the code when there is none.
  String get displayTitle => (title?.trim().isNotEmpty ?? false) ? title! : code;

  factory CourseModel.fromJson(Map<String, dynamic> json) => CourseModel(
        id: json['id'] as String,
        code: json['code'] as String,
        targetLanguage: json['targetLanguage'] as String,
        level: json['level'] as String,
        title: json['title'] as String?,
        description: json['description'] as String?,
        thumbnailUrl: json['thumbnailUrl'] as String?,
      );
}

/// A lesson of a course (`LessonDto`).
@immutable
class LessonModel {
  const LessonModel({
    required this.id,
    required this.courseId,
    required this.orderIndex,
    required this.lessonType,
    required this.xpReward,
    this.title,
    this.guideText,
  });

  final String id;
  final String courseId;
  final int orderIndex;
  final String lessonType;
  final int xpReward;
  final String? title;
  final String? guideText;

  String get displayTitle => (title?.trim().isNotEmpty ?? false) ? title! : 'Bài $orderIndex';

  factory LessonModel.fromJson(Map<String, dynamic> json) => LessonModel(
        id: json['id'] as String,
        courseId: json['courseId'] as String,
        orderIndex: (json['orderIndex'] as num).toInt(),
        lessonType: json['lessonType'] as String,
        xpReward: (json['xpReward'] as num?)?.toInt() ?? 0,
        title: json['title'] as String?,
        guideText: json['guideText'] as String?,
      );
}

/// How far the learner is in a course (`CourseProgressDto`).
@immutable
class CourseProgress {
  const CourseProgress({required this.completedLessons, required this.totalLessons, required this.isCompleted});

  final int completedLessons;
  final int totalLessons;
  final bool isCompleted;

  double get fraction => totalLessons == 0 ? 0 : (completedLessons / totalLessons).clamp(0, 1).toDouble();
  bool get isStarted => completedLessons > 0 && !isCompleted;

  factory CourseProgress.fromJson(Map<String, dynamic> json) => CourseProgress(
        completedLessons: (json['completedLessonsCount'] as num).toInt(),
        totalLessons: (json['totalLessonsCount'] as num).toInt(),
        isCompleted: json['isCompleted'] as bool? ?? false,
      );
}
