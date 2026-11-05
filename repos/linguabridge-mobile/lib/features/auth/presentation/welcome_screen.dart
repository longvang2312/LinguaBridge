import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../application/auth_controller.dart';

/// First screen for a learner who is not signed in.
class WelcomeScreen extends ConsumerWidget {
  const WelcomeScreen({super.key});

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final theme = Theme.of(context);
    return Scaffold(
      body: SafeArea(
        child: Center(
          child: SingleChildScrollView(
            padding: const EdgeInsets.all(24),
            child: ConstrainedBox(
              constraints: const BoxConstraints(maxWidth: 420),
              child: Column(mainAxisSize: MainAxisSize.min, crossAxisAlignment: CrossAxisAlignment.stretch, children: [
                Icon(Icons.translate, size: 64, color: theme.colorScheme.primary),
                const SizedBox(height: 12),
                Text('LinguaBridge', textAlign: TextAlign.center, style: theme.textTheme.headlineMedium?.copyWith(fontWeight: FontWeight.w800)),
                const SizedBox(height: 8),
                Text(
                  'Học tiếng Anh và tiếng Trung giao tiếp công việc: bài học ngắn, thẻ ghi nhớ thông minh và gia sư AI.',
                  textAlign: TextAlign.center,
                  style: theme.textTheme.bodyLarge,
                ),
                const SizedBox(height: 32),
                // The OpenID Connect sign-in (authorization code + PKCE against the STS) is the next task.
                FilledButton.icon(
                  onPressed: null,
                  icon: const Icon(Icons.login),
                  label: const Text('Đăng nhập (sắp có)'),
                ),
                const SizedBox(height: 12),
                OutlinedButton(
                  onPressed: () => ref.read(authControllerProvider.notifier).startPreview(),
                  style: OutlinedButton.styleFrom(minimumSize: const Size.fromHeight(48)),
                  child: const Text('Xem trước giao diện'),
                ),
              ]),
            ),
          ),
        ),
      ),
    );
  }
}
