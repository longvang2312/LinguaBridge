import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../../core/network/dio_client.dart';
import '../../auth/application/auth_controller.dart';

/// Dashboard: study progress and an overview. The numbers are placeholders until the API is wired in.
class HomeScreen extends ConsumerWidget {
  const HomeScreen({super.key});

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final theme = Theme.of(context);
    final config = ref.watch(appConfigProvider);
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
      body: ListView(padding: const EdgeInsets.all(16), children: [
        Text('Xin chào!', style: theme.textTheme.headlineSmall),
        const SizedBox(height: 4),
        Text('Đây là tiến độ học tập của bạn hôm nay.', style: theme.textTheme.bodyMedium),
        const SizedBox(height: 16),
        Card(
          child: Padding(
            padding: const EdgeInsets.all(16),
            child: Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
              Text('Tiến độ khóa học', style: theme.textTheme.titleMedium),
              const SizedBox(height: 12),
              const LinearProgressIndicator(value: 0, minHeight: 10, borderRadius: BorderRadius.all(Radius.circular(8))),
              const SizedBox(height: 8),
              Text('Chưa có dữ liệu', style: theme.textTheme.bodySmall),
            ]),
          ),
        ),
        const SizedBox(height: 12),
        const Row(children: [
          Expanded(child: _StatCard(icon: Icons.style, label: 'Thẻ cần ôn', value: '–')),
          SizedBox(width: 12),
          Expanded(child: _StatCard(icon: Icons.bolt, label: 'XP', value: '–')),
        ]),
        const SizedBox(height: 12),
        Card(
          child: ListTile(
            leading: const Icon(Icons.cloud_outlined),
            title: const Text('Máy chủ'),
            subtitle: Text('API: ${config.apiBaseUrl}\nSTS: ${config.stsBaseUrl}'),
            isThreeLine: true,
          ),
        ),
      ]),
    );
  }
}

class _StatCard extends StatelessWidget {
  const _StatCard({required this.icon, required this.label, required this.value});

  final IconData icon;
  final String label;
  final String value;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    return Card(
      child: Padding(
        padding: const EdgeInsets.all(16),
        child: Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
          Icon(icon, color: theme.colorScheme.primary),
          const SizedBox(height: 8),
          Text(value, style: theme.textTheme.headlineSmall),
          Text(label, style: theme.textTheme.bodySmall),
        ]),
      ),
    );
  }
}
