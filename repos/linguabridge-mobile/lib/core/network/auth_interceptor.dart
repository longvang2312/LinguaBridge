import 'package:dio/dio.dart';

import 'api_exception.dart';

/// Adds the learner's bearer token to every request and turns every failure into an [ApiException].
///
/// When the API answers 401 the token is refreshed once and the request is sent again; only when that does not help is
/// the learner signed out.
class AuthInterceptor extends Interceptor {
  AuthInterceptor({required this.readToken, this.onUnauthorized, this.dio});

  /// The current access token (refreshed first when it is about to expire, or when [forceRefresh]); null when nobody is
  /// signed in or the session cannot be refreshed.
  final Future<String?> Function({bool forceRefresh}) readToken;

  /// Called when the API refuses the token for good (401 even after a refresh), so the app can sign the learner out.
  final void Function()? onUnauthorized;

  /// The client to repeat a refused request with. Without one a 401 is final.
  final Dio? dio;

  static const _retriedKey = 'lb.retried';

  @override
  Future<void> onRequest(RequestOptions options, RequestInterceptorHandler handler) async {
    // A caller-set header (or the one a retry carries) is used as it is.
    if (!options.headers.containsKey('Authorization')) {
      final token = await readToken();
      if (token != null && token.isNotEmpty) options.headers['Authorization'] = 'Bearer $token';
    }
    handler.next(options);
  }

  @override
  Future<void> onError(DioException err, ErrorInterceptorHandler handler) async {
    final retryable = err.response?.statusCode == 401 &&
        dio != null &&
        err.requestOptions.extra[_retriedKey] != true &&
        err.requestOptions.headers.containsKey('Authorization');
    if (retryable) {
      final token = await readToken(forceRefresh: true);
      if (token != null) {
        final options = err.requestOptions
          ..headers['Authorization'] = 'Bearer $token'
          ..extra[_retriedKey] = true;
        try {
          handler.resolve(await dio!.fetch<dynamic>(options));
          return;
        } on DioException catch (second) {
          // The repeated request went through this interceptor too: its error is already an ApiException and the
          // learner has already been signed out if that was the cause.
          handler.next(second);
          return;
        }
      }
    }

    final exception = ApiException.fromDio(err);
    if (exception.isUnauthorized) onUnauthorized?.call();
    handler.next(err.copyWith(error: exception));
  }
}
