import 'package:flutter/material.dart';

/// Shown for the moment it takes to look for a stored session at start-up.
class SplashScreen extends StatelessWidget {
  const SplashScreen({super.key});

  @override
  Widget build(BuildContext context) {
    final scheme = Theme.of(context).colorScheme;
    return Scaffold(
      backgroundColor: scheme.primary,
      body: Center(
        child: Column(mainAxisSize: MainAxisSize.min, children: [
          Icon(Icons.translate, size: 72, color: scheme.onPrimary),
          const SizedBox(height: 12),
          Text('LinguaBridge',
              style: Theme.of(context).textTheme.headlineMedium?.copyWith(color: scheme.onPrimary, fontWeight: FontWeight.w800)),
          const SizedBox(height: 24),
          SizedBox(width: 28, height: 28, child: CircularProgressIndicator(strokeWidth: 3, color: scheme.onPrimary)),
        ]),
      ),
    );
  }
}
