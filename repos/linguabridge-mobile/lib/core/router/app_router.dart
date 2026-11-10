import 'package:flutter/foundation.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:go_router/go_router.dart';

import '../../features/ai_tutor/presentation/ai_tutor_screen.dart';
import '../../features/auth/application/auth_controller.dart';
import '../../features/auth/presentation/callback_screen.dart';
import '../../features/auth/presentation/splash_screen.dart';
import '../../features/auth/presentation/welcome_screen.dart';
import '../../features/courses/presentation/courses_screen.dart';
import '../../features/home/presentation/home_screen.dart';
import '../../features/srs/presentation/srs_screen.dart';
import 'app_routes.dart';
import 'app_shell.dart';

export 'app_routes.dart';

/// Decides where the learner may be. Pure, so it can be tested without a widget tree.
String? redirectFor(AuthStatus status, String location) {
  // The callback carries the sign-in answer in its address, so it must not be bounced to the splash screen while the
  // stored session is still being read; it finishes by itself and a signed-in learner leaves it for the home tab.
  if (location == AppRoutes.callback) return status == AuthStatus.signedIn ? AppRoutes.home : null;

  const entry = {AppRoutes.splash, AppRoutes.welcome};
  switch (status) {
    case AuthStatus.unknown:
      return location == AppRoutes.splash ? null : AppRoutes.splash;
    case AuthStatus.signedOut:
      return location == AppRoutes.welcome ? null : AppRoutes.welcome;
    case AuthStatus.signedIn:
      return entry.contains(location) ? AppRoutes.home : null;
  }
}

final appRouterProvider = Provider<GoRouter>((ref) {
  // GoRouter re-runs `redirect` when this notifier changes, i.e. whenever the learner signs in or out.
  final status = ValueNotifier<AuthStatus>(ref.read(authStatusProvider));
  ref.listen(authStatusProvider, (_, next) => status.value = next);
  ref.onDispose(status.dispose);

  final router = GoRouter(
    initialLocation: AppRoutes.splash,
    refreshListenable: status,
    redirect: (context, state) => redirectFor(status.value, state.uri.path),
    routes: [
      GoRoute(path: AppRoutes.splash, builder: (context, state) => const SplashScreen()),
      GoRoute(path: AppRoutes.welcome, builder: (context, state) => const WelcomeScreen()),
      GoRoute(path: AppRoutes.callback, builder: (context, state) => CallbackScreen(uri: state.uri)),
      // One navigator per tab, kept alive by an IndexedStack: leaving a tab does not lose its scroll position or input.
      StatefulShellRoute.indexedStack(
        builder: (context, state, shell) => AppShell(shell: shell),
        branches: [
          StatefulShellBranch(routes: [GoRoute(path: AppRoutes.home, builder: (context, state) => const HomeScreen())]),
          StatefulShellBranch(routes: [GoRoute(path: AppRoutes.courses, builder: (context, state) => const CoursesScreen())]),
          StatefulShellBranch(routes: [GoRoute(path: AppRoutes.srs, builder: (context, state) => const SrsScreen())]),
          StatefulShellBranch(routes: [GoRoute(path: AppRoutes.aiTutor, builder: (context, state) => const AiTutorScreen())]),
        ],
      ),
    ],
  );
  ref.onDispose(router.dispose);
  return router;
});
