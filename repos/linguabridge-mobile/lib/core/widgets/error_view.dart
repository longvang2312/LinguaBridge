import 'package:flutter/material.dart';

import '../network/api_exception.dart';

/// The words to show a learner for whatever went wrong while loading.
String describeError(Object error) {
  if (error is ApiException) {
    if (error.statusCode == 403) return 'Tài khoản này không có quyền thực hiện thao tác đó.';
    return error.message;
  }
  return 'Đã xảy ra lỗi. Hãy thử lại.';
}

/// A load that failed: what happened and a way to try again.
class ErrorView extends StatelessWidget {
  const ErrorView({super.key, required this.error, required this.onRetry});

  final Object error;
  final VoidCallback onRetry;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    return Center(
      child: Padding(
        padding: const EdgeInsets.all(24),
        child: Column(mainAxisSize: MainAxisSize.min, children: [
          Icon(Icons.cloud_off, size: 48, color: theme.colorScheme.error),
          const SizedBox(height: 12),
          Text(describeError(error), textAlign: TextAlign.center, style: theme.textTheme.bodyLarge),
          const SizedBox(height: 16),
          FilledButton.icon(
            onPressed: onRetry,
            icon: const Icon(Icons.refresh),
            label: const Text('Thử lại'),
            style: FilledButton.styleFrom(minimumSize: const Size(160, 44)),
          ),
        ]),
      ),
    );
  }
}
