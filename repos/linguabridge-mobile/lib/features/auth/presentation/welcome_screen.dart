import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../application/auth_controller.dart';

/// First screen for a learner who is not signed in.
class WelcomeScreen extends ConsumerWidget {
  const WelcomeScreen({super.key});

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final theme = Theme.of(context);
    final auth = ref.watch(authControllerProvider);
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
                if (auth.error != null) ...[
                  Container(
                    key: const Key('login-error'),
                    padding: const EdgeInsets.all(12),
                    decoration: BoxDecoration(color: theme.colorScheme.errorContainer, borderRadius: BorderRadius.circular(12)),
                    child: Row(crossAxisAlignment: CrossAxisAlignment.start, children: [
                      Icon(Icons.error_outline, color: theme.colorScheme.onErrorContainer),
                      const SizedBox(width: 8),
                      Expanded(child: Text(auth.error!, style: TextStyle(color: theme.colorScheme.onErrorContainer))),
                    ]),
                  ),
                  const SizedBox(height: 16),
                ],
                FilledButton.icon(
                  onPressed: auth.busy ? null : () => ref.read(authControllerProvider.notifier).signIn(),
                  icon: auth.busy
                      ? const SizedBox(width: 18, height: 18, child: CircularProgressIndicator(strokeWidth: 2))
                      : const Icon(Icons.login),
                  label: const Text('Đăng nhập với AppInHouse ID'),
                ),
              ]),
            ),
          ),
        ),
      ),
    );
  }
}
