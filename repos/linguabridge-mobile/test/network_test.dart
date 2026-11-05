import 'dart:convert';
import 'dart:typed_data';

import 'package:dio/dio.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:linguabridge_mobile/core/config/app_config.dart';
import 'package:linguabridge_mobile/core/network/api_exception.dart';
import 'package:linguabridge_mobile/core/network/auth_interceptor.dart';
import 'package:linguabridge_mobile/core/network/dio_client.dart';
import 'package:linguabridge_mobile/core/storage/token_store.dart';
import 'package:linguabridge_mobile/features/auth/application/auth_controller.dart';

/// Answers every request with a canned response (or failure) and remembers what was asked.
class _FakeAdapter implements HttpClientAdapter {
  _FakeAdapter(this.respond);

  final ResponseBody Function(RequestOptions options) respond;
  final requests = <RequestOptions>[];

  @override
  Future<ResponseBody> fetch(RequestOptions options, Stream<Uint8List>? requestStream, Future<void>? cancelFuture) async {
    requests.add(options);
    return respond(options);
  }

  @override
  void close({bool force = false}) {}
}

ResponseBody _json(int status, Object body) => ResponseBody.fromString(
      jsonEncode(body),
      status,
      headers: {Headers.contentTypeHeader: ['application/json']},
    );

Dio _dio(_FakeAdapter adapter, {Future<String?> Function()? token, void Function()? onUnauthorized}) {
  final dio = Dio(BaseOptions(baseUrl: 'http://api.test'))..httpClientAdapter = adapter;
  dio.interceptors.add(AuthInterceptor(readToken: token ?? () async => null, onUnauthorized: onUnauthorized));
  return dio;
}

void main() {
  test('adds the bearer token to a request', () async {
    final adapter = _FakeAdapter((_) => _json(200, {'ok': true}));
    await _dio(adapter, token: () async => 'abc').get<dynamic>('/api/v1/me');
    expect(adapter.requests.single.headers['Authorization'], 'Bearer abc');
  });

  test('sends no Authorization header when nobody is signed in', () async {
    final adapter = _FakeAdapter((_) => _json(200, {'ok': true}));
    await _dio(adapter).get<dynamic>('/api/v1/me');
    expect(adapter.requests.single.headers.containsKey('Authorization'), isFalse);
  });

  test('keeps an Authorization header the caller set', () async {
    final adapter = _FakeAdapter((_) => _json(200, {}));
    await _dio(adapter, token: () async => 'abc').get<dynamic>('/x', options: Options(headers: {'Authorization': 'Bearer other'}));
    expect(adapter.requests.single.headers['Authorization'], 'Bearer other');
  });

  test('a problem-details error becomes an ApiException with its detail and code', () async {
    final adapter = _FakeAdapter((_) => _json(429, {
          'title': 'Too many requests',
          'detail': 'Daily limit reached.',
          'code': 'ai.quota_exhausted',
          'resetsAt': '2026-11-06T00:00:00Z',
        }));
    final error = await _dio(adapter).get<dynamic>('/x').then<ApiException?>((_) => null, onError: (Object e) => (e as DioException).error as ApiException);
    expect(error, isNotNull);
    expect(error!.statusCode, 429);
    expect(error.code, 'ai.quota_exhausted');
    expect(error.message, 'Daily limit reached.');
    expect(error.problem?['resetsAt'], '2026-11-06T00:00:00Z');
    expect(error.isNetworkError, isFalse);
  });

  test('a body that is not problem details still gives a readable message', () async {
    final adapter = _FakeAdapter((_) => ResponseBody.fromString('oops', 500));
    final error = await _dio(adapter).get<dynamic>('/x').then<ApiException?>((_) => null, onError: (Object e) => (e as DioException).error as ApiException);
    expect(error!.statusCode, 500);
    expect(error.message, contains('500'));
  });

  test('401 calls onUnauthorized once', () async {
    var calls = 0;
    final adapter = _FakeAdapter((_) => _json(401, {'title': 'Unauthorized'}));
    final error = await _dio(adapter, onUnauthorized: () => calls++)
        .get<dynamic>('/x')
        .then<ApiException?>((_) => null, onError: (Object e) => (e as DioException).error as ApiException);
    expect(calls, 1);
    expect(error!.isUnauthorized, isTrue);
  });

  test('other failures do not call onUnauthorized', () async {
    var calls = 0;
    final adapter = _FakeAdapter((_) => _json(403, {'title': 'Forbidden'}));
    await _dio(adapter, onUnauthorized: () => calls++).get<dynamic>('/x').then<void>((_) {}, onError: (Object _) {});
    expect(calls, 0);
  });

  test('no response at all is a network error', () async {
    final adapter = _FakeAdapter((options) => throw DioException.connectionError(requestOptions: options, reason: 'offline'));
    final error = await _dio(adapter).get<dynamic>('/x').then<ApiException?>((_) => null, onError: (Object e) => (e as DioException).error as ApiException);
    expect(error!.isNetworkError, isTrue);
    expect(error.message, contains('kết nối'));
  });

  test('dioProvider points at the configured API and signs the learner out on 401', () async {
    final store = MemoryTokenStore('abc');
    final container = ProviderContainer(overrides: [
      tokenStoreProvider.overrideWithValue(store),
      appConfigProvider.overrideWithValue(const AppConfig(apiBaseUrl: 'http://api.test:5020', stsBaseUrl: 'http://sts.test:5001')),
    ]);
    addTearDown(container.dispose);
    container.read(authControllerProvider);
    await Future<void>.delayed(Duration.zero);
    expect(container.read(authControllerProvider), AuthStatus.signedIn);

    final dio = container.read(dioProvider);
    expect(dio.options.baseUrl, 'http://api.test:5020');
    final adapter = _FakeAdapter((_) => _json(401, {'title': 'Unauthorized'}));
    dio.httpClientAdapter = adapter;
    await dio.get<dynamic>('/api/v1/me').then<void>((_) {}, onError: (Object _) {});

    expect(adapter.requests.single.headers['Authorization'], 'Bearer abc');
    await Future<void>.delayed(Duration.zero);
    expect(container.read(authControllerProvider), AuthStatus.signedOut);
    expect(await store.read(), isNull);
  });
}
