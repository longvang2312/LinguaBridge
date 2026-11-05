import 'package:flutter/foundation.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:go_router/go_router.dart';

import '../../features/ai_tutor/presentation/ai_tutor_screen.dart';
import '../../features/auth/application/auth_controller.dart';
import '../../features/auth/presentation/splash_screen.dart';
import '../../features/auth/presentation/welcome_screen.dart';
import '../../features/courses/presentation/courses_screen.dart';
import '../../features/home/presentation/home_screen.dart';
import '../../features/srs/presentation/srs_screen.dart';
import 'app_shell.dart';

abstract final class AppRoutes {
  static const splash = '/splash';
  static const welcome = '/welcome';
  static const home = '/home';
  static const courses = '/courses';
  static const srs = '/srs';
  static const aiTutor = '/tutor';
}

/// Decides where the learner may be. Pure, so it can be tested without a widget tree.
String? redirectFor(AuthStatus status, String location) {
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
  final status = ValueNotifier<AuthStatus>(ref.read(authControllerProvider));
  ref.listen(authControllerProvider, (_, next) => status.value = next);
  ref.onDispose(status.dispose);

  final router = GoRouter(
    initialLocation: AppRoutes.splash,
    refreshListenable: status,
    redirect: (context, state) => redirectFor(status.value, state.uri.path),
    routes: [
      GoRoute(path: AppRoutes.splash, builder: (context, state) => const SplashScreen()),
      GoRoute(path: AppRoutes.welcome, builder: (context, state) => const WelcomeScreen()),
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
