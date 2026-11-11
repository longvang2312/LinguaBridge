import 'package:dio/dio.dart';
import 'package:flutter/foundation.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:linguabridge_mobile/core/config/app_config.dart';
import 'package:linguabridge_mobile/features/auth/data/oidc_client.dart';
import 'package:linguabridge_mobile/features/auth/data/pkce.dart';

import 'support/fakes.dart';

final _config = AppConfig.resolve(isWeb: false, platform: TargetPlatform.windows);
final _now = DateTime.utc(2026, 11, 10, 12);

OidcClient _client(FakeAdapter adapter, {AppConfig? config}) =>
    OidcClient(config: config ?? _config, dio: Dio()..httpClientAdapter = adapter, clock: () => _now);

void main() {
  group('startLogin', () {
    test('builds the authorization request for the learners client with PKCE', () {
      final (url, pending) = _client(FakeAdapter((_) => jsonBody(200, {}))).startLogin();
      expect('${url.scheme}://${url.host}:${url.port}${url.path}', 'http://localhost:5001/connect/authorize');
      final q = url.queryParameters;
      expect(q['client_id'], 'linguabridge-app');
      expect(q['response_type'], 'code');
      expect(q['redirect_uri'], 'http://localhost:3000/callback');
      expect(q['scope'], contains('offline_access'));
      expect(q['code_challenge_method'], 'S256');
      expect(q['code_challenge'], Pkce.challengeFor(pending.verifier));
      expect(q['state'], pending.state);
      expect(q.containsKey('prompt'), isFalse);
      expect(url.toString(), isNot(contains(pending.verifier)), reason: 'the verifier must never travel in the first request');
    });

    test('every sign-in has its own secret and state', () {
      final client = _client(FakeAdapter((_) => jsonBody(200, {})));
      final a = client.startLogin().$2;
      final b = client.startLogin().$2;
      expect(a.verifier, isNot(b.verifier));
      expect(a.state, isNot(b.state));
    });

    test('forceLogin asks the STS for the password again', () {
      final (url, _) = _client(FakeAdapter((_) => jsonBody(200, {}))).startLogin(forceLogin: true);
      expect(url.queryParameters['prompt'], 'login');
    });
  });

  group('completeLogin', () {
    const pending = PendingLogin(verifier: 'v', state: 's', redirectUri: 'http://localhost:3000/callback');

    test('exchanges the code, sending the verifier, as a form', () async {
      final adapter = FakeAdapter((_) => jsonBody(200, tokenResponse(idToken: testIdToken)));
      final session = await _client(adapter).completeLogin(Uri.parse('http://localhost:3000/callback?code=abc&state=s'), pending);

      final request = adapter.requests.single;
      expect(request.method, 'POST');
      expect(request.uri.toString(), 'http://localhost:5001/connect/token');
      expect(request.contentType, contains('application/x-www-form-urlencoded'));
      expect(request.data, {
        'grant_type': 'authorization_code',
        'client_id': 'linguabridge-app',
        'code': 'abc',
        'redirect_uri': 'http://localhost:3000/callback',
        'code_verifier': 'v',
      });
      expect(session.accessToken, 'access-1');
      expect(session.refreshToken, 'refresh-1');
      expect(session.expiresAt, _now.add(const Duration(seconds: 3600)));
      expect(session.user?.email, 'admin@appinhouse.local');
    });

    test('uses the token address when it differs from the STS address (a web build behind a proxy)', () async {
      final adapter = FakeAdapter((_) => jsonBody(200, tokenResponse()));
      final config = AppConfig(apiBaseUrl: '/lb', stsBaseUrl: 'http://localhost:5001', tokenBaseUrl: '/sts');
      await _client(adapter, config: config).completeLogin(Uri.parse('http://x/callback?code=abc&state=s'), pending);
      expect(adapter.requests.single.uri.toString(), '/sts/connect/token');
    });

    test('refuses an answer whose state is not the one that was sent, without calling the STS', () async {
      final adapter = FakeAdapter((_) => jsonBody(200, tokenResponse()));
      await expectLater(
        _client(adapter).completeLogin(Uri.parse('http://x/callback?code=abc&state=other'), pending),
        throwsA(isA<AuthException>()),
      );
      await expectLater(_client(adapter).completeLogin(Uri.parse('http://x/callback?code=abc'), pending), throwsA(isA<AuthException>()));
      expect(adapter.requests, isEmpty);
    });

    test('an error from the STS is reported with its description', () async {
      final adapter = FakeAdapter((_) => jsonBody(200, {}));
      await expectLater(
        _client(adapter).completeLogin(Uri.parse('http://x/callback?error=access_denied&error_description=Bi%20tu%20choi&state=s'), pending),
        throwsA(isA<AuthException>().having((e) => e.message, 'message', 'Bi tu choi')),
      );
      expect(adapter.requests, isEmpty);
    });

    test('an answer without a code is refused', () async {
      await expectLater(
        _client(FakeAdapter((_) => jsonBody(200, {}))).completeLogin(Uri.parse('http://x/callback?state=s'), pending),
        throwsA(isA<AuthException>()),
      );
    });

    test('the STS refusing the code gives its message and is an invalid grant', () async {
      final adapter = FakeAdapter((_) => jsonBody(400, {'error': 'invalid_grant', 'error_description': 'The code is no longer valid.'}));
      await expectLater(
        _client(adapter).completeLogin(Uri.parse('http://x/callback?code=abc&state=s'), pending),
        throwsA(isA<AuthException>().having((e) => e.invalidGrant, 'invalidGrant', isTrue).having((e) => e.message, 'message', contains('no longer valid'))),
      );
    });

    test('an unreachable STS is a readable failure, not an invalid grant', () async {
      final adapter = FakeAdapter((o) => throw DioException.connectionError(requestOptions: o, reason: 'offline'));
      await expectLater(
        _client(adapter).completeLogin(Uri.parse('http://x/callback?code=abc&state=s'), pending),
        throwsA(isA<AuthException>().having((e) => e.invalidGrant, 'invalidGrant', isFalse).having((e) => e.message, 'message', contains('kết nối'))),
      );
    });

    test('an answer without an access token is refused', () async {
      await expectLater(
        _client(FakeAdapter((_) => jsonBody(200, {'token_type': 'Bearer'}))).completeLogin(Uri.parse('http://x/callback?code=abc&state=s'), pending),
        throwsA(isA<AuthException>()),
      );
    });
  });

  group('refresh', () {
    test('sends the refresh token and keeps the old refresh and id tokens when none are rotated', () async {
      final adapter = FakeAdapter((_) => jsonBody(200, tokenResponse(access: 'access-2', refresh: null)));
      final session = await _client(adapter).refresh(makeSession(refresh: 'old-refresh'));

      expect(adapter.requests.single.data, {'grant_type': 'refresh_token', 'client_id': 'linguabridge-app', 'refresh_token': 'old-refresh'});
      expect(session.accessToken, 'access-2');
      expect(session.refreshToken, 'old-refresh');
      expect(session.idToken, testIdToken);
    });

    test('takes the rotated refresh token when the STS sends one', () async {
      final adapter = FakeAdapter((_) => jsonBody(200, tokenResponse(access: 'access-2', refresh: 'new-refresh')));
      expect((await _client(adapter).refresh(makeSession())).refreshToken, 'new-refresh');
    });

    test('without a refresh token there is nothing to try', () async {
      final adapter = FakeAdapter((_) => jsonBody(200, tokenResponse()));
      await expectLater(
        _client(adapter).refresh(makeSession(refresh: null)),
        throwsA(isA<AuthException>().having((e) => e.invalidGrant, 'invalidGrant', isTrue)),
      );
      expect(adapter.requests, isEmpty);
    });
  });

  group('logoutUri', () {
    test('names the id_token, where to come back to and a state', () {
      final url = _client(FakeAdapter((_) => jsonBody(200, {}))).logoutUri(makeSession());
      expect(url.path, '/connect/logout');
      expect(url.queryParameters['id_token_hint'], testIdToken);
      expect(url.queryParameters['post_logout_redirect_uri'], 'http://localhost:3000/callback');
      expect(url.queryParameters['state'], 'lb-logout');
    });

    test('without a session there is no hint', () {
      final url = _client(FakeAdapter((_) => jsonBody(200, {}))).logoutUri(null);
      expect(url.queryParameters.containsKey('id_token_hint'), isFalse);
    });
  });
}
