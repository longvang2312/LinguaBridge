import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../../core/network/dio_client.dart';
import '../../../core/widgets/error_view.dart';
import '../../auth/application/auth_controller.dart';
import '../../courses/application/courses_providers.dart';
import '../../courses/data/course_models.dart';
import '../../courses/presentation/course_card.dart';
import '../../courses/presentation/lessons_sheet.dart';

/// All published courses of both languages.
final _allCoursesProvider = FutureProvider<List<CourseModel>>(
  (ref) async {
    final both = await Future.wait([ref.watch(coursesProvider('en').future), ref.watch(coursesProvider('zh').future)]);
    return [...both[0], ...both[1]];
  },
  retry: (_, _) => null,
);

/// How many courses the dashboard suggests.
const _suggestionCount = 3;

/// Dashboard: a greeting and the courses to carry on with (the ones already started first, then the others).
class HomeScreen extends ConsumerWidget {
  const HomeScreen({super.key});

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final theme = Theme.of(context);
    final config = ref.watch(appConfigProvider);
    final user = ref.watch(authControllerProvider.select((s) => s.user));
    final courses = ref.watch(_allCoursesProvider);

    return Scaffold(
      appBar: AppBar(
        title: const Text('Trang chủ'),
        actions: [
          IconButton(
            tooltip: 'Đăng xuất',
            icon: const Icon(Icons.logout),
            onPressed: () => ref.read(authControllerProvider.notifier).signOut(),
          ),
        ],
      ),
      body: RefreshIndicator(
        onRefresh: () async {
          ref.invalidate(coursesProvider('en'));
          ref.invalidate(coursesProvider('zh'));
          await ref.read(_allCoursesProvider.future).then<void>((_) {}, onError: (Object _) {});
        },
        child: ListView(padding: const EdgeInsets.all(16), children: [
          Text(user == null ? 'Xin chào!' : 'Xin chào, ${user.displayName}!', key: const Key('greeting'), style: theme.textTheme.headlineSmall),
          const SizedBox(height: 4),
          Text('Đây là tiến độ học tập của bạn hôm nay.', style: theme.textTheme.bodyMedium),
          const SizedBox(height: 20),
          courses.when(
            loading: () => const Padding(padding: EdgeInsets.all(32), child: Center(child: CircularProgressIndicator())),
            error: (error, _) => SizedBox(
              height: 280,
              child: ErrorView(
                error: error,
                onRetry: () {
                  ref.invalidate(coursesProvider('en'));
                  ref.invalidate(coursesProvider('zh'));
                },
              ),
            ),
            data: (items) => _Suggestions(courses: items),
          ),
          const SizedBox(height: 16),
          Card(
            child: ListTile(
              leading: const Icon(Icons.cloud_outlined),
              title: const Text('Máy chủ'),
              subtitle: Text('API: ${config.apiBaseUrl}\nSTS: ${config.stsBaseUrl}'),
              isThreeLine: true,
            ),
          ),
        ]),
      ),
    );
  }
}

class _Suggestions extends ConsumerWidget {
  const _Suggestions({required this.courses});

  final List<CourseModel> courses;

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final theme = Theme.of(context);
    if (courses.isEmpty) {
      return Card(child: Padding(padding: const EdgeInsets.all(24), child: Text('Chưa có khóa học nào.', style: theme.textTheme.bodyMedium)));
    }

    final progress = {for (final c in courses) c.id: ref.watch(courseProgressProvider(c.id)).value};
    // Started and unfinished first, then untouched ones, then finished ones; stable within each group.
    int rank(CourseModel c) {
      final p = progress[c.id];
      if (p == null || p.completedLessons == 0) return 1;
      return p.isCompleted ? 2 : 0;
    }

    final ordered = [...courses]..sort((a, b) => rank(a).compareTo(rank(b)));
    final shown = ordered.take(_suggestionCount).toList();
    final started = shown.any((c) => rank(c) == 0);

    return Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
      Text(started ? 'Tiếp tục học' : 'Gợi ý cho bạn', style: theme.textTheme.titleMedium),
      const SizedBox(height: 8),
      for (final course in shown) ...[
        CourseCard(course: course, progress: progress[course.id], onOpen: () => showLessonsSheet(context, course)),
        const SizedBox(height: 12),
      ],
    ]);
  }
}
