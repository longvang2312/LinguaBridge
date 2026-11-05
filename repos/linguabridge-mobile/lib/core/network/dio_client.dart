import 'package:dio/dio.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../features/auth/application/auth_controller.dart';
import '../config/app_config.dart';
import '../storage/token_store.dart';
import 'auth_interceptor.dart';

final appConfigProvider = Provider<AppConfig>((ref) => AppConfig.fromEnvironment());

/// The HTTP client for LinguaBridge.Api, ready for the feature repositories to use.
final dioProvider = Provider<Dio>((ref) {
  final config = ref.watch(appConfigProvider);
  final dio = Dio(BaseOptions(
    baseUrl: config.apiBaseUrl,
    connectTimeout: const Duration(seconds: 10),
    receiveTimeout: const Duration(seconds: 20),
    headers: {'Accept': 'application/json'},
  ));
  dio.interceptors.add(AuthInterceptor(
    readToken: () => ref.read(tokenStoreProvider).read(),
    onUnauthorized: () => ref.read(authControllerProvider.notifier).signOut(),
  ));
  return dio;
});
