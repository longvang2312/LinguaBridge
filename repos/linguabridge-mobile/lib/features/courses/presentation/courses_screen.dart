import 'package:flutter/material.dart';

/// Course discovery. A placeholder list until the content API is wired in; the search box is real so that the tab's
/// state (its text) can be seen to survive switching tabs.
class CoursesScreen extends StatefulWidget {
  const CoursesScreen({super.key});

  @override
  State<CoursesScreen> createState() => _CoursesScreenState();
}

class _CoursesScreenState extends State<CoursesScreen> {
  final _search = TextEditingController();
  String _language = 'all';

  @override
  void dispose() {
    _search.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    return Scaffold(
      appBar: AppBar(title: const Text('Khóa học')),
      body: ListView(padding: const EdgeInsets.all(16), children: [
        TextField(
          key: const Key('courses-search'),
          controller: _search,
          decoration: const InputDecoration(prefixIcon: Icon(Icons.search), hintText: 'Tìm khóa học'),
        ),
        const SizedBox(height: 12),
        Wrap(spacing: 8, children: [
          for (final (code, label) in const [('all', 'Tất cả'), ('en', 'Tiếng Anh'), ('zh', 'Tiếng Trung')])
            ChoiceChip(label: Text(label), selected: _language == code, onSelected: (_) => setState(() => _language = code)),
        ]),
        const SizedBox(height: 16),
        Card(
          child: Padding(
            padding: const EdgeInsets.all(24),
            child: Column(children: [
              Icon(Icons.menu_book, size: 48, color: theme.colorScheme.primary),
              const SizedBox(height: 8),
              Text('Danh sách khóa học', style: theme.textTheme.titleMedium),
              const SizedBox(height: 4),
              Text(
                'Các khóa học tiếng Anh và tiếng Trung sẽ hiển thị ở đây.',
                textAlign: TextAlign.center,
                style: theme.textTheme.bodyMedium,
              ),
            ]),
          ),
        ),
      ]),
    );
  }
}
