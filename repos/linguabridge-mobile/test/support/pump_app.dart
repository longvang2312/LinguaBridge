import 'package:dio/dio.dart';
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:linguabridge_mobile/core/router/app_router.dart';
import 'package:linguabridge_mobile/core/storage/token_store.dart';
import 'package:linguabridge_mobile/features/auth/application/auth_controller.dart';
import 'package:linguabridge_mobile/features/auth/data/pending_login_store.dart';
import 'package:linguabridge_mobile/features/auth/domain/auth_session.dart';
import 'package:linguabridge_mobile/features/courses/application/courses_providers.dart';
import 'package:linguabridge_mobile/main.dart';

import 'fakes.dart';

/// Everything a test may want to look at or change after the app is up.
class TestApp {
  TestApp({
    required this.container,
    required this.store,
    required this.launcher,
    required this.repository,
    required this.tokenEndpoint,
    required this.pending,
  });

  final ProviderContainer container;
  final MemoryTokenStore store;
  final FakeAuthLauncher launcher;
  final FakeCoursesRepository repository;
  final FakeAdapter tokenEndpoint;
  final MemoryPendingLoginStore pending;

  String get location => container.read(appRouterProvider).routerDelegate.currentConfiguration.uri.path;
}

FakeCoursesRepository sampleRepository() => FakeCoursesRepository(
      courseList: {
        'en': [englishCourse, businessCourse],
        'zh': [chineseCourse],
      },
      lessonList: {englishCourse.id: englishLessons},
    );

/// Starts the app on fakes: no network, no secure storage, no browser.
Future<TestApp> pumpApp(
  WidgetTester tester, {
  AuthSession? session,
  MemoryTokenStore? store,
  FakeCoursesRepository? repository,
  FakeAuthLauncher? launcher,
  FakeAdapter? tokenEndpoint,
  MemoryPendingLoginStore? pending,
  Size size = const Size(400, 800),
}) async {
  tester.view.physicalSize = size;
  tester.view.devicePixelRatio = 1;
  addTearDown(tester.view.reset);

  final theStore = store ?? MemoryTokenStore(session);
  final theLauncher = launcher ?? FakeAuthLauncher();
  final theRepository = repository ?? sampleRepository();
  final theTokenEndpoint = tokenEndpoint ?? FakeAdapter((_) => jsonBody(200, tokenResponse(idToken: testIdToken)));
  final thePending = pending ?? MemoryPendingLoginStore();

  final app = TestApp(
    container: ProviderContainer(overrides: [
      tokenStoreProvider.overrideWithValue(theStore),
      pendingLoginStoreProvider.overrideWithValue(thePending),
      authLauncherProvider.overrideWithValue(theLauncher),
      tokenDioProvider.overrideWithValue(Dio()..httpClientAdapter = theTokenEndpoint),
      coursesRepositoryProvider.overrideWithValue(theRepository),
    ]),
    store: theStore,
    launcher: theLauncher,
    repository: theRepository,
    tokenEndpoint: theTokenEndpoint,
    pending: thePending,
  );
  addTearDown(app.container.dispose);
  await tester.pumpWidget(UncontrolledProviderScope(container: app.container, child: const LinguaBridgeApp()));
  await tester.pumpAndSettle();
  return app;
}
