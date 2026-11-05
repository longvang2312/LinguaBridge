import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:linguabridge_mobile/core/network/dio_client.dart';
import 'package:linguabridge_mobile/core/router/app_router.dart';
import 'package:linguabridge_mobile/core/storage/token_store.dart';
import 'package:linguabridge_mobile/core/theme/app_theme.dart';
import 'package:linguabridge_mobile/features/auth/application/auth_controller.dart';
import 'package:linguabridge_mobile/main.dart';

Future<ProviderContainer> _pump(WidgetTester tester, {String? token, Size size = const Size(400, 800)}) async {
  tester.view.physicalSize = size;
  tester.view.devicePixelRatio = 1;
  addTearDown(tester.view.reset);

  final container = ProviderContainer(overrides: [tokenStoreProvider.overrideWithValue(MemoryTokenStore(token))]);
  addTearDown(container.dispose);
  await tester.pumpWidget(UncontrolledProviderScope(container: container, child: const LinguaBridgeApp()));
  await tester.pumpAndSettle();
  return container;
}

String _location(ProviderContainer container) =>
    container.read(appRouterProvider).routerDelegate.currentConfiguration.uri.path;

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

  group('app', () {
    testWidgets('without a stored session the welcome screen is shown', (tester) async {
      final container = await _pump(tester);
      expect(_location(container), AppRoutes.welcome);
      expect(find.text('Xem trước giao diện'), findsOneWidget);
      expect(find.byType(NavigationBar), findsNothing);
    });

    testWidgets('a stored session goes straight to the home tab', (tester) async {
      final container = await _pump(tester, token: 'abc');
      expect(_location(container), AppRoutes.home);
      expect(find.byType(NavigationBar), findsOneWidget);
    });

    testWidgets('the shell has four tabs and each opens its screen', (tester) async {
      final container = await _pump(tester, token: 'abc');
      final bar = find.byType(NavigationBar);
      for (final label in ['Trang chủ', 'Khóa học', 'Ôn tập', 'Gia sư AI']) {
        expect(find.descendant(of: bar, matching: find.text(label)), findsOneWidget, reason: label);
      }

      final expectations = {
        'Khóa học': (AppRoutes.courses, 'Danh sách khóa học'),
        'Ôn tập': (AppRoutes.srs, 'Bộ thẻ ôn tập'),
        'Gia sư AI': (AppRoutes.aiTutor, 'Xin chào! Mình là gia sư AI'),
        'Trang chủ': (AppRoutes.home, 'Tiến độ khóa học'),
      };
      for (final entry in expectations.entries) {
        await tester.tap(find.descendant(of: bar, matching: find.text(entry.key)));
        await tester.pumpAndSettle();
        expect(_location(container), entry.value.$1, reason: entry.key);
        expect(find.textContaining(entry.value.$2), findsWidgets, reason: entry.key);
      }
    });

    testWidgets('a tab keeps its state while another tab is open', (tester) async {
      await _pump(tester, token: 'abc');
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
      expect(tester.widget<ChoiceChip>(find.widgetWithText(ChoiceChip, 'Tiếng Trung')).selected, isTrue);
    });

    testWidgets('wide screens get a side rail instead of the bottom bar', (tester) async {
      await _pump(tester, token: 'abc', size: const Size(1000, 700));
      expect(find.byType(NavigationRail), findsOneWidget);
      expect(find.byType(NavigationBar), findsNothing);

      await tester.tap(find.descendant(of: find.byType(NavigationRail), matching: find.text('Ôn tập')));
      await tester.pumpAndSettle();
      expect(find.textContaining('Bộ thẻ ôn tập'), findsWidgets);
    });

    testWidgets('previewing from the welcome screen enters the app, signing out comes back', (tester) async {
      final container = await _pump(tester);
      await tester.tap(find.text('Xem trước giao diện'));
      await tester.pumpAndSettle();
      expect(_location(container), AppRoutes.home);

      await tester.tap(find.byTooltip('Đăng xuất'));
      await tester.pumpAndSettle();
      expect(_location(container), AppRoutes.welcome);
    });

    testWidgets('the home screen shows the addresses the app will call', (tester) async {
      final container = await _pump(tester, token: 'abc');
      // flutter_test runs as Android, so this is the emulator address; the per-platform rules are in app_config_test.dart.
      final config = container.read(appConfigProvider);
      expect(config.apiBaseUrl, 'http://10.0.2.2:5020');
      expect(find.textContaining('API: ${config.apiBaseUrl}'), findsOneWidget);
      expect(find.textContaining('STS: ${config.stsBaseUrl}'), findsOneWidget);
    });
  });
}
