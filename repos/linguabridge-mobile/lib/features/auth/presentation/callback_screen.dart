import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:go_router/go_router.dart';

import '../../../core/router/app_routes.dart';
import '../application/auth_controller.dart';

/// Where the web build lands when the STS sends the learner back: with a code (finish signing in) or after a sign-out
/// (nothing to finish). Phones and desktops never come here; their redirect is caught by the sign-in call itself.
class CallbackScreen extends ConsumerStatefulWidget {
  const CallbackScreen({super.key, required this.uri});

  /// The address the app was opened with, query included.
  final Uri uri;

  @override
  ConsumerState<CallbackScreen> createState() => _CallbackScreenState();
}

class _CallbackScreenState extends ConsumerState<CallbackScreen> {
  bool _failed = false;

  @override
  void initState() {
    super.initState();
    WidgetsBinding.instance.addPostFrameCallback((_) => _finish());
  }

  Future<void> _finish() async {
    final query = widget.uri.queryParameters;
    final controller = ref.read(authControllerProvider.notifier);
    // After a sign-out the STS comes back here with only a state; a code or an error means a sign-in answer.
    if (!query.containsKey('code') && !query.containsKey('error')) {
      if (mounted) context.go(AppRoutes.welcome);
      return;
    }
    await controller.completeCallback(widget.uri);
    if (!mounted) return;
    // On success the router moves on by itself (signed in leaves this address); on failure the reason is on the welcome screen.
    if (ref.read(authControllerProvider).status != AuthStatus.signedIn) {
      setState(() => _failed = true);
      context.go(AppRoutes.welcome);
    }
  }

  @override
  Widget build(BuildContext context) {
    final scheme = Theme.of(context).colorScheme;
    return Scaffold(
      body: Center(
        child: _failed
            ? const SizedBox.shrink()
            : Column(mainAxisSize: MainAxisSize.min, children: [
                CircularProgressIndicator(color: scheme.primary),
                const SizedBox(height: 16),
                const Text('Đang hoàn tất đăng nhập...'),
              ]),
      ),
    );
  }
}
