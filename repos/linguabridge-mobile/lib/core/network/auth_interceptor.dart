import 'package:dio/dio.dart';

import 'api_exception.dart';

/// Adds the learner's bearer token to every request and turns every failure into an [ApiException].
class AuthInterceptor extends Interceptor {
  AuthInterceptor({required this.readToken, this.onUnauthorized});

  /// The current access token, or null when nobody is signed in.
  final Future<String?> Function() readToken;

  /// Called when the API refuses the token (401), so the app can sign the learner out.
  final void Function()? onUnauthorized;

  @override
  Future<void> onRequest(RequestOptions options, RequestInterceptorHandler handler) async {
    final token = await readToken();
    if (token != null && token.isNotEmpty && !options.headers.containsKey('Authorization')) {
      options.headers['Authorization'] = 'Bearer $token';
    }
    handler.next(options);
  }

  @override
  void onError(DioException err, ErrorInterceptorHandler handler) {
    final exception = ApiException.fromDio(err);
    if (exception.isUnauthorized) onUnauthorized?.call();
    handler.next(err.copyWith(error: exception));
  }
}
