import 'package:flutter/material.dart';

import '../data/course_models.dart';

String languageName(String code) => switch (code) {
      'en' => 'English',
      'zh' => 'Chinese',
      _ => code,
    };

/// One course: language and level badges, code, title, description and (when known) the learner's progress.
class CourseCard extends StatelessWidget {
  const CourseCard({super.key, required this.course, required this.onOpen, this.progress});

  final CourseModel course;
  final CourseProgress? progress;
  final VoidCallback onOpen;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final scheme = theme.colorScheme;
    final progress = this.progress;
    return Card(
      clipBehavior: Clip.antiAlias,
      child: InkWell(
        onTap: onOpen,
        child: Padding(
          padding: const EdgeInsets.all(16),
          child: Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
            Wrap(spacing: 8, runSpacing: 4, crossAxisAlignment: WrapCrossAlignment.center, children: [
              _Badge(label: languageName(course.targetLanguage), color: scheme.primary, onColor: scheme.onPrimary),
              _Badge(label: course.level, color: scheme.tertiaryContainer, onColor: scheme.onTertiaryContainer),
              Text(course.code, style: theme.textTheme.labelMedium?.copyWith(color: scheme.outline)),
            ]),
            const SizedBox(height: 10),
            Text(course.displayTitle, style: theme.textTheme.titleMedium),
            if (course.description?.trim().isNotEmpty ?? false) ...[
              const SizedBox(height: 4),
              Text(course.description!, style: theme.textTheme.bodyMedium, maxLines: 3, overflow: TextOverflow.ellipsis),
            ],
            if (progress != null && progress.totalLessons > 0) ...[
              const SizedBox(height: 12),
              LinearProgressIndicator(value: progress.fraction, minHeight: 8, borderRadius: BorderRadius.circular(8)),
              const SizedBox(height: 4),
              Text('Đã hoàn thành ${progress.completedLessons}/${progress.totalLessons} bài học', style: theme.textTheme.bodySmall),
            ],
            const SizedBox(height: 8),
            Align(
              alignment: Alignment.centerRight,
              child: TextButton.icon(
                onPressed: onOpen,
                icon: const Icon(Icons.list_alt),
                label: const Text('Xem bài học'),
              ),
            ),
          ]),
        ),
      ),
    );
  }
}

class _Badge extends StatelessWidget {
  const _Badge({required this.label, required this.color, required this.onColor});

  final String label;
  final Color color;
  final Color onColor;

  @override
  Widget build(BuildContext context) {
    return Container(
      padding: const EdgeInsets.symmetric(horizontal: 10, vertical: 3),
      decoration: BoxDecoration(color: color, borderRadius: BorderRadius.circular(999)),
      child: Text(label, style: Theme.of(context).textTheme.labelSmall?.copyWith(color: onColor, fontWeight: FontWeight.w700)),
    );
  }
}
