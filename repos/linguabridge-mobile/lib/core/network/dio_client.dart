import 'package:dio/dio.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../features/auth/application/auth_controller.dart';
import '../config/config_providers.dart';
import 'auth_interceptor.dart';

export '../config/config_providers.dart' show appConfigProvider;

/// The HTTP client for LinguaBridge.Api. It signs requests with the learner's token and keeps the token fresh.
final dioProvider = Provider<Dio>((ref) {
  final config = ref.watch(appConfigProvider);
  final dio = Dio(BaseOptions(
    baseUrl: config.apiBaseUrl,
    connectTimeout: const Duration(seconds: 10),
    receiveTimeout: const Duration(seconds: 20),
    headers: {'Accept': 'application/json'},
  ));
  dio.interceptors.add(AuthInterceptor(
    dio: dio,
    readToken: ({bool forceRefresh = false}) => ref.read(authServiceProvider).accessToken(forceRefresh: forceRefresh),
    onUnauthorized: () => ref.read(authControllerProvider.notifier).sessionExpired(),
  ));
  return dio;
});
