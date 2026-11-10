import 'dart:async';

import 'package:dio/dio.dart';
import 'package:flutter/foundation.dart';
import 'package:flutter_secure_storage/flutter_secure_storage.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:linguabridge_mobile/core/config/app_config.dart';
import 'package:linguabridge_mobile/core/storage/token_store.dart';
import 'package:linguabridge_mobile/features/auth/application/auth_service.dart';
import 'package:linguabridge_mobile/features/auth/data/oidc_client.dart';
import 'package:linguabridge_mobile/features/auth/data/pending_login_store.dart';
import 'package:linguabridge_mobile/features/auth/domain/auth_session.dart';

import 'support/fakes.dart';

final _config = AppConfig.resolve(isWeb: false, platform: TargetPlatform.windows);

AuthService _service(MemoryTokenStore store, FakeAdapter adapter, {FakeAuthLauncher? launcher}) => AuthService(
      config: _config,
      store: store,
      oidc: OidcClient(config: _config, dio: Dio()..httpClientAdapter = adapter),
      pending: MemoryPendingLoginStore(),
      launcher: launcher ?? FakeAuthLauncher(),
    );

/// A token endpoint that answers only when the test says so, to hold a refresh in flight.
class _GatedAdapter extends FakeAdapter {
  _GatedAdapter(super.respond);

  final gate = Completer<void>();
  final started = Completer<void>();

  @override
  Future<ResponseBody> fetch(RequestOptions options, Stream<Uint8List>? requestStream, Future<void>? cancelFuture) async {
    if (!started.isCompleted) started.complete();
    await gate.future;
    return super.fetch(options, requestStream, cancelFuture);
  }
}

class _Harness {
  _Harness({AuthSession? stored, ResponseBody Function(RequestOptions)? tokenEndpoint, DateTime Function()? clock})
      : store = MemoryTokenStore(stored),
        adapter = FakeAdapter(tokenEndpoint ?? (_) => jsonBody(200, tokenResponse(access: 'access-2', refresh: 'refresh-2', idToken: testIdToken))) {
    final now = clock ?? DateTime.now;
    service = AuthService(
      config: _config,
      store: store,
      oidc: OidcClient(config: _config, dio: Dio()..httpClientAdapter = adapter, clock: now),
      pending: pending,
      launcher: launcher,
      clock: now,
    );
  }

  final MemoryTokenStore store;
  final FakeAdapter adapter;
  final pending = MemoryPendingLoginStore();
  final launcher = FakeAuthLauncher();
  late final AuthService service;
}

void main() {
  group('restore', () {
    test('nothing stored means signed out', () async {
      expect(await _Harness().service.restore(), isNull);
    });

    test('a stored session comes back', () async {
      final h = _Harness(stored: makeSession());
      expect((await h.service.restore())?.accessToken, 'access-1');
    });

    test('an expired session that cannot be refreshed is dropped from the store', () async {
      final h = _Harness(stored: makeSession(refresh: null, expiresAt: DateTime.now().subtract(const Duration(hours: 1))));
      expect(await h.service.restore(), isNull);
      expect(await h.store.read(), isNull);
    });

    test('an expired session that can be refreshed is kept (it is refreshed on first use)', () async {
      final h = _Harness(stored: makeSession(expiresAt: DateTime.now().subtract(const Duration(hours: 1))));
      expect(await h.service.restore(), isNotNull);
      expect(h.adapter.requests, isEmpty);
    });
  });

  group('accessToken', () {
    test('a fresh token is returned without calling the STS', () async {
      final h = _Harness(stored: makeSession());
      expect(await h.service.accessToken(), 'access-1');
      expect(h.adapter.requests, isEmpty);
    });

    test('nobody signed in gives null', () async {
      expect(await _Harness().service.accessToken(), isNull);
    });

    test('a token that is about to expire is refreshed and the new tokens are stored', () async {
      final h = _Harness(stored: makeSession(expiresAt: DateTime.now().add(const Duration(seconds: 10))));
      expect(await h.service.accessToken(), 'access-2');
      final stored = await h.store.read();
      expect(stored?.accessToken, 'access-2');
      expect(stored?.refreshToken, 'refresh-2');
    });

    test('forceRefresh refreshes a token that still looks fine', () async {
      final h = _Harness(stored: makeSession());
      expect(await h.service.accessToken(forceRefresh: true), 'access-2');
    });

    test('callers at the same time share one refresh', () async {
      final h = _Harness(stored: makeSession(expiresAt: DateTime.now().subtract(const Duration(minutes: 1))));
      final tokens = await Future.wait([h.service.accessToken(), h.service.accessToken(), h.service.accessToken()]);
      expect(tokens, ['access-2', 'access-2', 'access-2']);
      expect(h.adapter.requests, hasLength(1), reason: 'refresh tokens may be single-use');
    });

    test('the STS refusing the refresh token ends the session', () async {
      final h = _Harness(
        stored: makeSession(expiresAt: DateTime.now().subtract(const Duration(minutes: 1))),
        tokenEndpoint: (_) => jsonBody(400, {'error': 'invalid_grant'}),
      );
      expect(await h.service.accessToken(), isNull);
      expect(await h.store.read(), isNull);
      expect(await h.service.restore(), isNull);
    });

    test('an unreachable STS keeps the session for the next try', () async {
      final h = _Harness(
        stored: makeSession(expiresAt: DateTime.now().subtract(const Duration(minutes: 1))),
        tokenEndpoint: (o) => throw DioException.connectionError(requestOptions: o, reason: 'offline'),
      );
      expect(await h.service.accessToken(), isNull);
      expect(await h.store.read(), isNotNull);
    });
  });

  group('sign-in', () {
    test('on a phone or desktop the redirect comes back to the sign-in call and the tokens are stored', () async {
      final h = _Harness();
      h.launcher.onAuthenticate = FakeAuthLauncher.success;
      final session = await h.service.beginLogin();

      expect(session?.accessToken, 'access-2');
      expect((await h.store.read())?.refreshToken, 'refresh-2');
      expect(h.launcher.authenticated.single.path, '/connect/authorize');
      final form = h.adapter.requests.single.data as Map;
      expect(form['code'], 'the-code');
      expect(form['code_verifier'], isNotEmpty);
    });

    test('closing the page signs nobody in', () async {
      final h = _Harness();
      expect(await h.service.beginLogin(), isNull);
      expect(await h.store.read(), isNull);
      expect(h.adapter.requests, isEmpty);
    });

    test('on the web the sign-in finishes later with completeLogin, from the stored secret', () async {
      final h = _Harness();
      expect(await h.service.beginLogin(), isNull); // the page is being replaced by the STS
      final url = h.launcher.authenticated.single;

      final session = await h.service.completeLogin(FakeAuthLauncher.success(url));
      expect(session.accessToken, 'access-2');
      expect((h.adapter.requests.single.data as Map)['code_verifier'], isNotEmpty);
    });

    test('an answer that nobody asked for is refused', () async {
      final h = _Harness();
      await expectLater(
        h.service.completeLogin(Uri.parse('http://localhost:3000/callback?code=x&state=y')),
        throwsA(isA<AuthException>()),
      );
      expect(h.adapter.requests, isEmpty);
    });

    test('the pending secret is single use', () async {
      final h = _Harness();
      await h.service.beginLogin();
      final answer = FakeAuthLauncher.success(h.launcher.authenticated.single);
      await h.service.completeLogin(answer);
      await expectLater(h.service.completeLogin(answer), throwsA(isA<AuthException>()));
    });

    test('a failed exchange stores nothing', () async {
      final h = _Harness(tokenEndpoint: (_) => jsonBody(400, {'error': 'invalid_grant'}));
      h.launcher.onAuthenticate = FakeAuthLauncher.success;
      await expectLater(h.service.beginLogin(), throwsA(isA<AuthException>()));
      expect(await h.store.read(), isNull);
    });
  });

  group('logout', () {
    test('forgets the tokens and sends the learner to the STS logout with the id_token hint', () async {
      final h = _Harness(stored: makeSession());
      await h.service.logout();

      expect(await h.store.read(), isNull);
      expect(await h.service.accessToken(), isNull);
      final url = h.launcher.ended.single;
      expect(url.path, '/connect/logout');
      expect(url.queryParameters['id_token_hint'], testIdToken);
    });

    test('expire forgets the session without involving the STS', () async {
      final h = _Harness(stored: makeSession());
      await h.service.expire();
      expect(await h.store.read(), isNull);
      expect(h.launcher.ended, isEmpty);
    });
  });

  group('a refresh that is still running when the session ends', () {
    ResponseBody newTokens(RequestOptions _) =>
        jsonBody(200, tokenResponse(access: 'access-NEW', refresh: 'refresh-NEW', idToken: testIdToken));
    AuthSession expired() => makeSession(expiresAt: DateTime.now().subtract(const Duration(minutes: 1)));

    test('does not bring the session back after a sign-out', () async {
      final gated = _GatedAdapter(newTokens);
      final store = MemoryTokenStore(expired());
      final service = _service(store, gated);

      final token = service.accessToken(); // starts a refresh and waits for the STS
      await gated.started.future;
      await service.logout(); // the learner signs out meanwhile
      gated.gate.complete(); // ... and only now does the STS answer

      expect(await token, isNull, reason: 'the refreshed token belongs to a session that has ended');
      expect(await store.read(), isNull, reason: 'nothing may be written back');
      expect(await service.restore(), isNull);
      expect(await service.accessToken(), isNull);
      // The next launch (a new service over the same store) starts signed out too.
      expect(await _service(store, FakeAdapter((_) => jsonBody(200, {}))).restore(), isNull);
    });

    test('does not bring the session back after the API ended it either', () async {
      final gated = _GatedAdapter(newTokens);
      final store = MemoryTokenStore(expired());
      final service = _service(store, gated);

      final token = service.accessToken();
      await gated.started.future;
      await service.expire();
      gated.gate.complete();

      expect(await token, isNull);
      expect(await store.read(), isNull);
    });

    test('does not overwrite a newer sign-in', () async {
      final gated = _GatedAdapter((_) => jsonBody(200, tokenResponse(access: 'access-FROM-TOKEN-CALL', refresh: 'refresh-X', idToken: testIdToken)));
      final store = MemoryTokenStore(expired());
      final launcher = FakeAuthLauncher()..onAuthenticate = FakeAuthLauncher.success;
      final service = _service(store, gated, launcher: launcher);

      final stale = service.accessToken();
      await gated.started.future;
      final signIn = service.beginLogin(); // the sign-in's own token call waits at the same gate
      gated.gate.complete();
      final session = await signIn;
      await stale;

      expect(session, isNotNull);
      expect((await store.read())?.accessToken, session!.accessToken, reason: 'the sign-in is what remains');
      expect(await service.accessToken(), session.accessToken);
    });

    test('a later refresh for another session works as usual', () async {
      final gated = _GatedAdapter(newTokens);
      final store = MemoryTokenStore(expired());
      final service = _service(store, gated);
      final token = service.accessToken();
      await gated.started.future;
      await service.logout();
      gated.gate.complete();
      await token;

      await store.write(makeSession(access: 'second', expiresAt: DateTime.now().subtract(const Duration(minutes: 1))));
      final again = _service(store, FakeAdapter((_) => jsonBody(200, tokenResponse(access: 'access-3', refresh: 'refresh-3', idToken: testIdToken))));
      expect(await again.accessToken(), 'access-3');
    });
  });

  group('SecureTokenStore', () {
    setUp(() => FlutterSecureStorage.setMockInitialValues({}));

    test('keeps the access, refresh and id tokens between launches', () async {
      final session = makeSession();
      await SecureTokenStore().write(session);

      final back = await SecureTokenStore().read(); // a new instance: the next launch
      expect(back?.accessToken, session.accessToken);
      expect(back?.refreshToken, session.refreshToken);
      expect(back?.idToken, session.idToken);
      expect(back?.expiresAt, session.expiresAt.toUtc());
    });

    test('clear removes it', () async {
      final store = SecureTokenStore();
      await store.write(makeSession());
      await store.clear();
      expect(await store.read(), isNull);
    });

    test('a damaged stored value is the same as no session', () async {
      FlutterSecureStorage.setMockInitialValues({'lb.session': '{not json'});
      expect(await SecureTokenStore().read(), isNull);
      FlutterSecureStorage.setMockInitialValues({'lb.session': '{"accessToken":""}'});
      expect(await SecureTokenStore().read(), isNull);
    });
  });
}
