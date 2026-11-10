import 'package:dio/dio.dart';
import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:linguabridge_mobile/core/router/app_router.dart';
import 'package:linguabridge_mobile/core/storage/token_store.dart';
import 'package:linguabridge_mobile/core/theme/app_theme.dart';
import 'package:linguabridge_mobile/features/auth/application/auth_controller.dart';
import 'package:linguabridge_mobile/features/auth/data/oidc_client.dart';

import 'support/fakes.dart';
import 'support/pump_app.dart';

const _signIn = 'Đăng nhập với AppInHouse ID';

void main() {
  group('redirectFor', () {
    test('while the session is unknown everything waits on the splash screen', () {
      expect(redirectFor(AuthStatus.unknown, AppRoutes.home), AppRoutes.splash);
      expect(redirectFor(AuthStatus.unknown, AppRoutes.splash), isNull);
    });

    test('signed out learners only see the welcome screen', () {
      expect(redirectFor(AuthStatus.signedOut, AppRoutes.courses), AppRoutes.welcome);
      expect(redirectFor(AuthStatus.signedOut, AppRoutes.splash), AppRoutes.welcome);
      expect(redirectFor(AuthStatus.signedOut, AppRoutes.welcome), isNull);
    });

    test('signed in learners skip the entry screens', () {
      expect(redirectFor(AuthStatus.signedIn, AppRoutes.splash), AppRoutes.home);
      expect(redirectFor(AuthStatus.signedIn, AppRoutes.welcome), AppRoutes.home);
      expect(redirectFor(AuthStatus.signedIn, AppRoutes.srs), isNull);
    });

    test('the callback is never bounced while a sign-in answer is waiting in its address', () {
      expect(redirectFor(AuthStatus.unknown, AppRoutes.callback), isNull);
      expect(redirectFor(AuthStatus.signedOut, AppRoutes.callback), isNull);
      expect(redirectFor(AuthStatus.signedIn, AppRoutes.callback), AppRoutes.home);
    });
  });

  group('theme', () {
    test('the brand blue is the primary colour of the light theme and the app bar', () {
      expect(AppTheme.primary, const Color(0xFF2563EB));
      expect(AppTheme.light.colorScheme.primary, const Color(0xFF2563EB));
      expect(AppTheme.light.appBarTheme.backgroundColor, const Color(0xFF2563EB));
    });

    test('the dark theme is dark and has a readable primary', () {
      expect(AppTheme.dark.brightness, Brightness.dark);
      expect(AppTheme.dark.colorScheme.primary, isNot(AppTheme.dark.colorScheme.surface));
    });
  });

  group('session', () {
    testWidgets('without a stored session the welcome screen offers the sign-in', (tester) async {
      final app = await pumpApp(tester);
      expect(app.location, AppRoutes.welcome);
      expect(find.text(_signIn), findsOneWidget);
      expect(find.text('Xem trước giao diện'), findsNothing);
      expect(find.byType(NavigationBar), findsNothing);
    });

    testWidgets('a stored session goes straight to the home tab and greets the learner', (tester) async {
      final app = await pumpApp(tester, session: makeSession());
      expect(app.location, AppRoutes.home);
      expect(find.byType(NavigationBar), findsOneWidget);
      expect(find.text('Xin chào, Admin!'), findsOneWidget);
    });

    testWidgets('signing in sends the learner to the STS, exchanges the code and opens the home tab', (tester) async {
      final launcher = FakeAuthLauncher()..onAuthenticate = FakeAuthLauncher.success;
      final app = await pumpApp(tester, launcher: launcher);

      await tester.tap(find.text(_signIn));
      await tester.pumpAndSettle();

      expect(launcher.authenticated.single.path, '/connect/authorize');
      expect((app.tokenEndpoint.requests.single.data as Map)['grant_type'], 'authorization_code');
      expect(app.location, AppRoutes.home);
      expect(find.text('Xin chào, Admin!'), findsOneWidget);
      final stored = await app.store.read();
      expect(stored?.accessToken, 'access-1');
      expect(stored?.refreshToken, 'refresh-1');
    });

    testWidgets('closing the sign-in page leaves the learner on the welcome screen, able to try again', (tester) async {
      final app = await pumpApp(tester, launcher: FakeAuthLauncher());
      await tester.tap(find.text(_signIn));
      await tester.pumpAndSettle();

      expect(app.location, AppRoutes.welcome);
      expect(find.byKey(const Key('login-error')), findsNothing);
      expect(tester.widget<FilledButton>(find.byType(FilledButton)).onPressed, isNotNull);
    });

    testWidgets('a sign-in the STS refuses shows why and stays on the welcome screen', (tester) async {
      final launcher = FakeAuthLauncher()..onAuthenticate = FakeAuthLauncher.success;
      final app = await pumpApp(
        tester,
        launcher: launcher,
        tokenEndpoint: FakeAdapter((_) => jsonBody(400, {'error': 'invalid_grant', 'error_description': 'Mã đăng nhập không còn hiệu lực.'})),
      );

      await tester.tap(find.text(_signIn));
      await tester.pumpAndSettle();

      expect(app.location, AppRoutes.welcome);
      expect(find.byKey(const Key('login-error')), findsOneWidget);
      expect(find.text('Mã đăng nhập không còn hiệu lực.'), findsOneWidget);
      expect(await app.store.read(), isNull);
    });

    testWidgets('an unreachable STS says so', (tester) async {
      final launcher = FakeAuthLauncher()..onAuthenticate = FakeAuthLauncher.success;
      await pumpApp(
        tester,
        launcher: launcher,
        tokenEndpoint: FakeAdapter((o) => throw DioException.connectionError(requestOptions: o, reason: 'offline')),
      );
      await tester.tap(find.text(_signIn));
      await tester.pumpAndSettle();
      expect(find.textContaining('Không kết nối được máy chủ đăng nhập'), findsOneWidget);
    });

    testWidgets('relaunching the app restores the session without signing in again', (tester) async {
      final store = MemoryTokenStore();
      final launcher = FakeAuthLauncher()..onAuthenticate = FakeAuthLauncher.success;
      await pumpApp(tester, store: store, launcher: launcher);
      await tester.tap(find.text(_signIn));
      await tester.pumpAndSettle();
      expect(find.text('Xin chào, Admin!'), findsOneWidget);

      // A new process: nothing in memory, only what the token store kept.
      await tester.pumpWidget(const SizedBox());
      final relaunched = await pumpApp(tester, store: store);

      expect(relaunched.location, AppRoutes.home);
      expect(find.text('Xin chào, Admin!'), findsOneWidget);
      expect(relaunched.launcher.authenticated, isEmpty);
    });

    testWidgets('signing out clears the tokens, ends the STS session and returns to the welcome screen', (tester) async {
      final app = await pumpApp(tester, session: makeSession());

      await tester.tap(find.byTooltip('Đăng xuất'));
      await tester.pumpAndSettle();

      expect(app.location, AppRoutes.welcome);
      expect(await app.store.read(), isNull);
      final logout = app.launcher.ended.single;
      expect(logout.path, '/connect/logout');
      expect(logout.queryParameters['id_token_hint'], testIdToken);
    });

    testWidgets('after signing out the next sign-in can start again', (tester) async {
      final launcher = FakeAuthLauncher()..onAuthenticate = FakeAuthLauncher.success;
      final app = await pumpApp(tester, session: makeSession(), launcher: launcher);
      await tester.tap(find.byTooltip('Đăng xuất'));
      await tester.pumpAndSettle();

      await tester.tap(find.text(_signIn));
      await tester.pumpAndSettle();
      expect(app.location, AppRoutes.home);
    });
  });

  group('callback (web)', () {
    const pending = PendingLogin(verifier: 'v', state: 's', redirectUri: 'http://localhost:3000/callback');

    testWidgets('an answer with a code finishes the sign-in and opens the home tab', (tester) async {
      final app = await pumpApp(tester);
      await app.pending.save(pending);

      app.container.read(appRouterProvider).go('${AppRoutes.callback}?code=abc&state=s');
      await tester.pumpAndSettle();

      expect(app.location, AppRoutes.home);
      expect(find.text('Xin chào, Admin!'), findsOneWidget);
      final form = app.tokenEndpoint.requests.single.data as Map;
      expect(form['code'], 'abc');
      expect(form['code_verifier'], 'v');
    });

    testWidgets('an answer for a sign-in this app did not start is refused', (tester) async {
      final app = await pumpApp(tester);

      app.container.read(appRouterProvider).go('${AppRoutes.callback}?code=abc&state=s');
      await tester.pumpAndSettle();

      expect(app.location, AppRoutes.welcome);
      expect(find.byKey(const Key('login-error')), findsOneWidget);
      expect(await app.store.read(), isNull);
    });

    testWidgets('an error from the STS is shown on the welcome screen', (tester) async {
      final app = await pumpApp(tester);
      await app.pending.save(pending);

      app.container.read(appRouterProvider).go('${AppRoutes.callback}?error=access_denied&error_description=Bi%20tu%20choi&state=s');
      await tester.pumpAndSettle();

      expect(app.location, AppRoutes.welcome);
      expect(find.text('Bi tu choi'), findsOneWidget);
    });

    testWidgets('the return from a sign-out (no code) lands on the welcome screen', (tester) async {
      final app = await pumpApp(tester);

      app.container.read(appRouterProvider).go('${AppRoutes.callback}?state=lb-logout');
      await tester.pumpAndSettle();

      expect(app.location, AppRoutes.welcome);
      expect(find.byKey(const Key('login-error')), findsNothing);
    });
  });

  group('shell', () {
    testWidgets('has four tabs and each opens its screen', (tester) async {
      final app = await pumpApp(tester, session: makeSession());
      final bar = find.byType(NavigationBar);
      for (final label in ['Trang chủ', 'Khóa học', 'Ôn tập', 'Gia sư AI']) {
        expect(find.descendant(of: bar, matching: find.text(label)), findsOneWidget, reason: label);
      }

      final expectations = {
        'Khóa học': (AppRoutes.courses, 'Tiếng Anh giao tiếp nhập môn'),
        'Ôn tập': (AppRoutes.srs, 'Bộ thẻ ôn tập'),
        'Gia sư AI': (AppRoutes.aiTutor, 'Xin chào! Mình là gia sư AI'),
        'Trang chủ': (AppRoutes.home, 'Xin chào, Admin!'),
      };
      for (final entry in expectations.entries) {
        await tester.tap(find.descendant(of: bar, matching: find.text(entry.key)));
        await tester.pumpAndSettle();
        expect(app.location, entry.value.$1, reason: entry.key);
        expect(find.textContaining(entry.value.$2), findsWidgets, reason: entry.key);
      }
    });

    testWidgets('a tab keeps its state while another tab is open', (tester) async {
      await pumpApp(tester, session: makeSession());
      final bar = find.byType(NavigationBar);

      await tester.tap(find.descendant(of: bar, matching: find.text('Khóa học')));
      await tester.pumpAndSettle();
      await tester.enterText(find.byKey(const Key('courses-search')), 'business');
      await tester.tap(find.text('Tiếng Trung'));
      await tester.pumpAndSettle();

      await tester.tap(find.descendant(of: bar, matching: find.text('Ôn tập')));
      await tester.pumpAndSettle();
      await tester.tap(find.descendant(of: bar, matching: find.text('Khóa học')));
      await tester.pumpAndSettle();

      expect(find.widgetWithText(TextField, 'business'), findsOneWidget);
      final tabs = tester.widget<SegmentedButton<String>>(find.byKey(const Key('language-tabs')));
      expect(tabs.selected, {'zh'});
    });

    testWidgets('wide screens get a side rail instead of the bottom bar', (tester) async {
      await pumpApp(tester, session: makeSession(), size: const Size(1000, 700));
      expect(find.byType(NavigationRail), findsOneWidget);
      expect(find.byType(NavigationBar), findsNothing);

      await tester.tap(find.descendant(of: find.byType(NavigationRail), matching: find.text('Ôn tập')));
      await tester.pumpAndSettle();
      expect(find.textContaining('Bộ thẻ ôn tập'), findsWidgets);
    });
  });
}
