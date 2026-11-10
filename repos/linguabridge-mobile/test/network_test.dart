import 'package:dio/dio.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:linguabridge_mobile/core/config/app_config.dart';
import 'package:linguabridge_mobile/core/network/api_exception.dart';
import 'package:linguabridge_mobile/core/network/auth_interceptor.dart';
import 'package:linguabridge_mobile/core/network/dio_client.dart';
import 'package:linguabridge_mobile/core/storage/token_store.dart';
import 'package:linguabridge_mobile/features/auth/application/auth_controller.dart';
import 'package:linguabridge_mobile/features/auth/data/pending_login_store.dart';

import 'support/fakes.dart';

Dio _dio(
  FakeAdapter adapter, {
  Future<String?> Function({bool forceRefresh})? token,
  void Function()? onUnauthorized,
  bool retry = true,
}) {
  final dio = Dio(BaseOptions(baseUrl: 'http://api.test'))..httpClientAdapter = adapter;
  dio.interceptors.add(AuthInterceptor(
    readToken: token ?? ({bool forceRefresh = false}) async => null,
    onUnauthorized: onUnauthorized,
    dio: retry ? dio : null,
  ));
  return dio;
}

Future<ApiException?> _failure(Future<Object?> request) =>
    request.then<ApiException?>((_) => null, onError: (Object e) => (e as DioException).error as ApiException);

void main() {
  test('adds the bearer token to a request', () async {
    final adapter = FakeAdapter((_) => jsonBody(200, {'ok': true}));
    await _dio(adapter, token: ({bool forceRefresh = false}) async => 'abc').get<dynamic>('/api/v1/me');
    expect(adapter.requests.single.headers['Authorization'], 'Bearer abc');
  });

  test('sends no Authorization header when nobody is signed in', () async {
    final adapter = FakeAdapter((_) => jsonBody(200, {'ok': true}));
    await _dio(adapter).get<dynamic>('/api/v1/me');
    expect(adapter.requests.single.headers.containsKey('Authorization'), isFalse);
  });

  test('keeps an Authorization header the caller set', () async {
    final adapter = FakeAdapter((_) => jsonBody(200, {}));
    await _dio(adapter, token: ({bool forceRefresh = false}) async => 'abc')
        .get<dynamic>('/x', options: Options(headers: {'Authorization': 'Bearer other'}));
    expect(adapter.requests.single.headers['Authorization'], 'Bearer other');
  });

  test('a problem-details error becomes an ApiException with its detail and code', () async {
    final adapter = FakeAdapter((_) => jsonBody(429, {
          'title': 'Too many requests',
          'detail': 'Daily limit reached.',
          'code': 'ai.quota_exhausted',
          'resetsAt': '2026-11-06T00:00:00Z',
        }));
    final error = await _failure(_dio(adapter).get<dynamic>('/x'));
    expect(error, isNotNull);
    expect(error!.statusCode, 429);
    expect(error.code, 'ai.quota_exhausted');
    expect(error.message, 'Daily limit reached.');
    expect(error.problem?['resetsAt'], '2026-11-06T00:00:00Z');
    expect(error.isNetworkError, isFalse);
  });

  test('a body that is not problem details still gives a readable message', () async {
    final adapter = FakeAdapter((_) => ResponseBody.fromString('oops', 500));
    final error = await _failure(_dio(adapter).get<dynamic>('/x'));
    expect(error!.statusCode, 500);
    expect(error.message, contains('500'));
  });

  test('no response at all is a network error', () async {
    final adapter = FakeAdapter((options) => throw DioException.connectionError(requestOptions: options, reason: 'offline'));
    final error = await _failure(_dio(adapter).get<dynamic>('/x'));
    expect(error!.isNetworkError, isTrue);
    expect(error.message, contains('kết nối'));
  });

  test('other failures do not sign the learner out', () async {
    var calls = 0;
    final adapter = FakeAdapter((_) => jsonBody(403, {'title': 'Forbidden'}));
    await _failure(_dio(adapter, onUnauthorized: () => calls++).get<dynamic>('/x'));
    expect(calls, 0);
  });

  group('401', () {
    // Accepts only the token "good"; anything else is refused.
    ResponseBody onlyGood(RequestOptions o) =>
        o.headers['Authorization'] == 'Bearer good' ? jsonBody(200, {'ok': true}) : jsonBody(401, {'title': 'Unauthorized'});

    test('refreshes the token once and repeats the request, which then works', () async {
      final refreshes = <bool>[];
      final sent = <String?>[]; // the options object is reused for the repeat, so the header is noted when it is sent
      final adapter = FakeAdapter((o) {
        sent.add(o.headers['Authorization'] as String?);
        return onlyGood(o);
      });
      final dio = _dio(adapter, token: ({bool forceRefresh = false}) async {
        refreshes.add(forceRefresh);
        return forceRefresh ? 'good' : 'stale';
      });

      final response = await dio.get<dynamic>('/api/v1/me');

      expect(response.statusCode, 200);
      expect(refreshes, [false, true]);
      expect(sent, ['Bearer stale', 'Bearer good']);
    });

    test('signs the learner out when the refreshed token is refused too, and stops after one retry', () async {
      var signedOut = 0;
      final adapter = FakeAdapter((_) => jsonBody(401, {'title': 'Unauthorized'}));
      final dio = _dio(adapter, token: ({bool forceRefresh = false}) async => 'stale', onUnauthorized: () => signedOut++);

      final error = await _failure(dio.get<dynamic>('/x'));

      expect(error!.isUnauthorized, isTrue);
      expect(adapter.requests, hasLength(2));
      expect(signedOut, 1);
    });

    test('signs the learner out when the session cannot be refreshed', () async {
      var signedOut = 0;
      final adapter = FakeAdapter((_) => jsonBody(401, {'title': 'Unauthorized'}));
      final dio = _dio(adapter, token: ({bool forceRefresh = false}) async => forceRefresh ? null : 'stale', onUnauthorized: () => signedOut++);

      await _failure(dio.get<dynamic>('/x'));

      expect(adapter.requests, hasLength(1));
      expect(signedOut, 1);
    });

    test('a request that carried no token is not retried', () async {
      final adapter = FakeAdapter((_) => jsonBody(401, {'title': 'Unauthorized'}));
      var asked = 0;
      final dio = _dio(adapter, token: ({bool forceRefresh = false}) async {
        asked++;
        return null;
      });
      await _failure(dio.get<dynamic>('/x'));
      expect(adapter.requests, hasLength(1));
      expect(asked, 1);
    });
  });

  group('dioProvider', () {
    ProviderContainer container(MemoryTokenStore store, FakeAdapter tokenEndpoint) {
      final c = ProviderContainer(overrides: [
        tokenStoreProvider.overrideWithValue(store),
        pendingLoginStoreProvider.overrideWithValue(MemoryPendingLoginStore()),
        appConfigProvider.overrideWithValue(const AppConfig(apiBaseUrl: 'http://api.test:5020', stsBaseUrl: 'http://sts.test:5001')),
        tokenDioProvider.overrideWithValue(Dio()..httpClientAdapter = tokenEndpoint),
      ]);
      addTearDown(c.dispose);
      return c;
    }

    test('points at the configured API and signs requests with the stored token', () async {
      final c = container(MemoryTokenStore(makeSession(access: 'abc')), FakeAdapter((_) => jsonBody(200, tokenResponse())));
      final dio = c.read(dioProvider);
      final adapter = FakeAdapter((_) => jsonBody(200, {}));
      dio.httpClientAdapter = adapter;

      await dio.get<dynamic>('/api/v1/me');

      expect(dio.options.baseUrl, 'http://api.test:5020');
      expect(adapter.requests.single.headers['Authorization'], 'Bearer abc');
    });

    test('refreshes the session through the STS when the API says 401, then carries on', () async {
      final store = MemoryTokenStore(makeSession(access: 'stale'));
      final sts = FakeAdapter((_) => jsonBody(200, tokenResponse(access: 'fresh', refresh: 'refresh-2', idToken: testIdToken)));
      final c = container(store, sts);
      final dio = c.read(dioProvider);
      dio.httpClientAdapter = FakeAdapter(
        (o) => o.headers['Authorization'] == 'Bearer fresh' ? jsonBody(200, {'ok': true}) : jsonBody(401, {'title': 'Unauthorized'}),
      );

      final response = await dio.get<dynamic>('/api/v1/me');

      expect(response.statusCode, 200);
      expect((sts.requests.single.data as Map)['grant_type'], 'refresh_token');
      expect((await store.read())?.accessToken, 'fresh');
    });

    test('signs the learner out for good when the STS refuses the refresh', () async {
      final store = MemoryTokenStore(makeSession());
      final c = container(store, FakeAdapter((_) => jsonBody(400, {'error': 'invalid_grant'})));
      c.read(authControllerProvider);
      await Future<void>.delayed(Duration.zero);
      expect(c.read(authControllerProvider).status, AuthStatus.signedIn);

      final dio = c.read(dioProvider);
      dio.httpClientAdapter = FakeAdapter((_) => jsonBody(401, {'title': 'Unauthorized'}));
      await _failure(dio.get<dynamic>('/api/v1/me'));
      await Future<void>.delayed(Duration.zero);

      expect(c.read(authControllerProvider).status, AuthStatus.signedOut);
      expect(c.read(authControllerProvider).error, contains('hết hạn'));
      expect(await store.read(), isNull);
    });
  });
}
