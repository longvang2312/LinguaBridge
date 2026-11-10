import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../../core/widgets/error_view.dart';
import '../application/courses_providers.dart';
import '../data/course_models.dart';
import 'course_card.dart';
import 'lessons_sheet.dart';

/// Course discovery: the published courses of the language being learned, from the API.
class CoursesScreen extends ConsumerStatefulWidget {
  const CoursesScreen({super.key});

  @override
  ConsumerState<CoursesScreen> createState() => _CoursesScreenState();
}

class _CoursesScreenState extends ConsumerState<CoursesScreen> {
  final _search = TextEditingController();
  String _query = '';

  @override
  void dispose() {
    _search.dispose();
    super.dispose();
  }

  bool _matches(CourseModel course) {
    final query = _query.trim().toLowerCase();
    if (query.isEmpty) return true;
    return course.displayTitle.toLowerCase().contains(query) ||
        course.code.toLowerCase().contains(query) ||
        (course.description?.toLowerCase().contains(query) ?? false);
  }

  @override
  Widget build(BuildContext context) {
    final language = ref.watch(targetLanguageProvider);
    final courses = ref.watch(coursesProvider(language));

    return Scaffold(
      appBar: AppBar(title: const Text('Khóa học')),
      body: Column(children: [
        Padding(
          padding: const EdgeInsets.fromLTRB(16, 16, 16, 8),
          child: Column(children: [
            SegmentedButton<String>(
              key: const Key('language-tabs'),
              showSelectedIcon: false,
              segments: const [
                ButtonSegment(value: 'en', label: Text('Tiếng Anh'), icon: Icon(Icons.language)),
                ButtonSegment(value: 'zh', label: Text('Tiếng Trung'), icon: Icon(Icons.translate)),
              ],
              selected: {language},
              onSelectionChanged: (selection) => ref.read(targetLanguageProvider.notifier).select(selection.first),
            ),
            const SizedBox(height: 12),
            TextField(
              key: const Key('courses-search'),
              controller: _search,
              onChanged: (value) => setState(() => _query = value),
              decoration: const InputDecoration(prefixIcon: Icon(Icons.search), hintText: 'Tìm khóa học', isDense: true),
            ),
          ]),
        ),
        Expanded(
          child: courses.when(
            loading: () => const Center(child: CircularProgressIndicator()),
            error: (error, _) => ErrorView(error: error, onRetry: () => ref.invalidate(coursesProvider(language))),
            data: (items) {
              final shown = items.where(_matches).toList();
              return RefreshIndicator(
                onRefresh: () async {
                  ref.invalidate(coursesProvider(language));
                  await ref.read(coursesProvider(language).future);
                },
                child: shown.isEmpty
                    ? ListView(children: [
                        Padding(
                          padding: const EdgeInsets.all(48),
                          child: Text(
                            items.isEmpty ? 'Chưa có khóa học nào cho ngôn ngữ này.' : 'Không có khóa học nào khớp với tìm kiếm.',
                            textAlign: TextAlign.center,
                          ),
                        ),
                      ])
                    : ListView.separated(
                        padding: const EdgeInsets.fromLTRB(16, 8, 16, 16),
                        itemCount: shown.length,
                        separatorBuilder: (_, _) => const SizedBox(height: 12),
                        itemBuilder: (context, index) {
                          final course = shown[index];
                          return CourseCard(
                            course: course,
                            progress: ref.watch(courseProgressProvider(course.id)).value,
                            onOpen: () => showLessonsSheet(context, course),
                          );
                        },
                      ),
              );
            },
          ),
        ),
      ]),
    );
  }
}
