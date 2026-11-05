import 'package:flutter/material.dart';

/// Flashcard review. A placeholder deck until the SRS API is wired in.
class SrsScreen extends StatelessWidget {
  const SrsScreen({super.key});

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    return Scaffold(
      appBar: AppBar(title: const Text('Ôn tập')),
      body: Center(
        child: Padding(
          padding: const EdgeInsets.all(24),
          child: ConstrainedBox(
            constraints: const BoxConstraints(maxWidth: 420),
            child: Card(
              color: theme.colorScheme.primaryContainer,
              child: Padding(
                padding: const EdgeInsets.symmetric(horizontal: 24, vertical: 48),
                child: Column(mainAxisSize: MainAxisSize.min, children: [
                  Icon(Icons.style, size: 56, color: theme.colorScheme.primary),
                  const SizedBox(height: 12),
                  Text('Bộ thẻ ôn tập', style: theme.textTheme.titleLarge),
                  const SizedBox(height: 4),
                  Text(
                    'Thẻ lật và thuật toán lặp lại ngắt quãng sẽ có ở đây.',
                    textAlign: TextAlign.center,
                    style: theme.textTheme.bodyMedium,
                  ),
                ]),
              ),
            ),
          ),
        ),
      ),
    );
  }
}
