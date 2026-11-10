import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:go_router/go_router.dart';

import '../../../core/router/app_routes.dart';
import '../../../core/widgets/error_view.dart';
import '../application/courses_providers.dart';
import '../data/course_models.dart';
import 'course_card.dart';

/// Opens the lessons of [course] in a bottom sheet.
Future<void> showLessonsSheet(BuildContext context, CourseModel course) {
  return showModalBottomSheet<void>(
    context: context,
    isScrollControlled: true,
    showDragHandle: true,
    constraints: const BoxConstraints(maxWidth: 640),
    builder: (context) => DraggableScrollableSheet(
      expand: false,
      initialChildSize: 0.65,
      minChildSize: 0.35,
      maxChildSize: 0.95,
      builder: (context, controller) => LessonsSheet(course: course, scrollController: controller),
    ),
  );
}

class LessonsSheet extends ConsumerWidget {
  const LessonsSheet({super.key, required this.course, this.scrollController});

  final CourseModel course;
  final ScrollController? scrollController;

  /// Closes the sheet and opens the lesson on top of the page the sheet was opened from.
  void _start(BuildContext context, LessonModel lesson) {
    final router = GoRouter.of(context);
    Navigator.of(context).pop();
    router.push(AppRoutes.lessonLocation(lesson.id, courseId: course.id));
  }

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final theme = Theme.of(context);
    final lessons = ref.watch(lessonsProvider(course.id));
    return Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
      Padding(
        padding: const EdgeInsets.fromLTRB(20, 0, 20, 8),
        child: Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
          Text(course.displayTitle, style: theme.textTheme.titleLarge),
          Text('${languageName(course.targetLanguage)} · ${course.level} · ${course.code}', style: theme.textTheme.bodySmall),
        ]),
      ),
      const Divider(height: 1),
      Expanded(
        child: lessons.when(
          loading: () => const Center(child: CircularProgressIndicator()),
          error: (error, _) => ErrorView(error: error, onRetry: () => ref.invalidate(lessonsProvider(course.id))),
          data: (items) => items.isEmpty
              ? const Center(child: Text('Khóa học này chưa có bài học.'))
              : ListView.separated(
                  controller: scrollController,
                  padding: const EdgeInsets.symmetric(vertical: 8),
                  itemCount: items.length,
                  separatorBuilder: (_, _) => const Divider(height: 1, indent: 72),
                  itemBuilder: (context, index) => _LessonTile(lesson: items[index], onStart: () => _start(context, items[index])),
                ),
        ),
      ),
    ]);
  }
}

class _LessonTile extends StatelessWidget {
  const _LessonTile({required this.lesson, required this.onStart});

  final LessonModel lesson;
  final VoidCallback onStart;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    return InkWell(
      onTap: onStart,
      child: Padding(
        padding: const EdgeInsets.symmetric(horizontal: 16, vertical: 10),
        child: Row(children: [
          CircleAvatar(
            backgroundColor: theme.colorScheme.primaryContainer,
            foregroundColor: theme.colorScheme.onPrimaryContainer,
            child: Text('${lesson.orderIndex}'),
          ),
          const SizedBox(width: 16),
          Expanded(
            child: Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
              Text(lesson.displayTitle, style: theme.textTheme.bodyLarge),
              if (lesson.guideText?.trim().isNotEmpty ?? false)
                Text(lesson.guideText!, maxLines: 2, overflow: TextOverflow.ellipsis, style: theme.textTheme.bodySmall),
              Row(mainAxisSize: MainAxisSize.min, children: [
                Icon(Icons.bolt, size: 16, color: theme.colorScheme.tertiary),
                Text('${lesson.xpReward} XP', style: theme.textTheme.bodySmall),
              ]),
            ]),
          ),
          const SizedBox(width: 12),
          FilledButton.tonal(
            key: Key('start-lesson-${lesson.id}'),
            onPressed: onStart,
            style: FilledButton.styleFrom(minimumSize: const Size(0, 40), padding: const EdgeInsets.symmetric(horizontal: 16)),
            child: const Text('Học ngay'),
          ),
        ]),
      ),
    );
  }
}
