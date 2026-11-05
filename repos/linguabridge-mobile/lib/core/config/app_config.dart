import 'package:flutter/foundation.dart';

/// Where the app finds the LinguaBridge API and the STS (OpenID Connect server).
///
/// The address depends on where the app runs: the Android emulator reaches the machine hosting it as `10.0.2.2`, while
/// Windows, macOS, Linux, the web and iOS simulator use `localhost`. Both can be overridden at build time:
///
///     flutter run --dart-define=API_BASE_URL=http://192.168.1.20:5020 --dart-define=STS_BASE_URL=http://192.168.1.20:5001
///
/// Note for the sign-in task: the APIs only accept tokens issued through the address configured as the STS issuer
/// (`http://host.docker.internal:5001` in docker-compose.yml), whatever address the app uses to reach it.
@immutable
class AppConfig {
  const AppConfig({required this.apiBaseUrl, required this.stsBaseUrl});

  static const _apiOverride = String.fromEnvironment('API_BASE_URL');
  static const _stsOverride = String.fromEnvironment('STS_BASE_URL');

  static const apiPort = 5020;
  static const stsPort = 5001;

  final String apiBaseUrl;
  final String stsBaseUrl;

  /// The configuration for the platform the app is running on, honouring the `--dart-define` overrides.
  factory AppConfig.fromEnvironment() => AppConfig.resolve(
        isWeb: kIsWeb,
        platform: defaultTargetPlatform,
        apiOverride: _apiOverride,
        stsOverride: _stsOverride,
      );

  /// Pure form of [AppConfig.fromEnvironment] so every platform can be tested from any machine.
  factory AppConfig.resolve({
    required bool isWeb,
    required TargetPlatform platform,
    String apiOverride = '',
    String stsOverride = '',
  }) {
    final host = !isWeb && platform == TargetPlatform.android ? '10.0.2.2' : 'localhost';
    return AppConfig(
      apiBaseUrl: _clean(apiOverride) ?? 'http://$host:$apiPort',
      stsBaseUrl: _clean(stsOverride) ?? 'http://$host:$stsPort',
    );
  }

  /// An empty override (the default of `String.fromEnvironment`) means "not set"; a trailing slash would double up in paths.
  static String? _clean(String value) {
    final trimmed = value.trim();
    if (trimmed.isEmpty) return null;
    return trimmed.endsWith('/') ? trimmed.substring(0, trimmed.length - 1) : trimmed;
  }

  @override
  String toString() => 'AppConfig(api: $apiBaseUrl, sts: $stsBaseUrl)';
}
