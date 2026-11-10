import 'package:flutter/foundation.dart';

/// Where the app finds the LinguaBridge API and the STS (OpenID Connect server), and how it signs in.
///
/// The addresses depend on where the app runs: the Android emulator reaches the machine hosting it as `10.0.2.2`, while
/// Windows, macOS, Linux, the web and the iOS simulator use `localhost` for the API. All of them can be overridden at
/// build time:
///
///     flutter run --dart-define=API_BASE_URL=http://192.168.1.20:5020 --dart-define=STS_BASE_URL=http://192.168.1.20:5001
///
/// The STS address matters more than it looks: the APIs only accept tokens issued through the address configured as
/// their authority (`http://host.docker.internal:5001` in docker-compose.yml). That is why Windows, macOS, Linux and the
/// web sign in through `host.docker.internal` (Docker Desktop makes it resolve on the host); a token issued through
/// `localhost:5001` is rejected by the API with 401. The Android emulator cannot resolve that name and uses `10.0.2.2`:
/// signing in there needs the STS issuer to be configured to match (or a `--dart-define` that fits the setup).
@immutable
class AppConfig {
  const AppConfig({
    required this.apiBaseUrl,
    required this.stsBaseUrl,
    this.tokenBaseUrl,
    this.redirectUri = defaultDesktopRedirectUri,
    this.clientId = defaultClientId,
    this.scope = defaultScope,
  });

  static const _apiOverride = String.fromEnvironment('API_BASE_URL');
  static const _stsOverride = String.fromEnvironment('STS_BASE_URL');
  static const _tokenOverride = String.fromEnvironment('STS_TOKEN_BASE_URL');
  static const _redirectOverride = String.fromEnvironment('OIDC_REDIRECT_URI');

  static const apiPort = 5020;
  static const stsPort = 5001;

  /// Public client seeded in the STS (Authorization Code + PKCE, no secret).
  static const defaultClientId = 'linguabridge-app';
  static const defaultScope = 'openid profile email offline_access linguabridge_api';

  /// The redirect addresses the STS has on record for `linguabridge-app` (see DbSeeder): the web client's
  /// `http://localhost:3000/callback` is the one a browser or a desktop app can use. A phone needs a custom scheme that
  /// the STS must be told about as well.
  static const defaultDesktopRedirectUri = 'http://localhost:3000/callback';
  static const defaultMobileRedirectUri = 'com.appinhouse.linguabridge:/callback';

  /// Where the browser goes to sign in or out (`/connect/authorize`, `/connect/logout`).
  final String apiBaseUrl;
  final String stsBaseUrl;

  /// Where the app itself calls `/connect/token`; null means [stsBaseUrl]. Only a browser build behind a proxy needs
  /// to differ (the STS sends no CORS headers).
  final String? tokenBaseUrl;
  final String redirectUri;
  final String clientId;
  final String scope;

  String get tokenUrl => tokenBaseUrl ?? stsBaseUrl;

  /// The part of [redirectUri] that tells `flutter_web_auth_2` which redirect ends the sign-in: the scheme for a custom
  /// scheme, `http://localhost:port` for a loopback address (desktop).
  String get callbackScheme {
    final uri = Uri.parse(redirectUri);
    if (uri.scheme == 'http' || uri.scheme == 'https') return '${uri.scheme}://${uri.host}:${uri.port}';
    return uri.scheme;
  }

  /// The configuration for the platform the app is running on, honouring the `--dart-define` overrides.
  factory AppConfig.fromEnvironment() => AppConfig.resolve(
        isWeb: kIsWeb,
        platform: defaultTargetPlatform,
        webOrigin: kIsWeb ? Uri.base.origin : null,
        apiOverride: _apiOverride,
        stsOverride: _stsOverride,
        tokenOverride: _tokenOverride,
        redirectOverride: _redirectOverride,
      );

  /// Pure form of [AppConfig.fromEnvironment] so every platform can be tested from any machine.
  factory AppConfig.resolve({
    required bool isWeb,
    required TargetPlatform platform,
    String? webOrigin,
    String apiOverride = '',
    String stsOverride = '',
    String tokenOverride = '',
    String redirectOverride = '',
  }) {
    final onEmulator = !isWeb && platform == TargetPlatform.android;
    final apiHost = onEmulator ? '10.0.2.2' : 'localhost';
    final stsHost = onEmulator ? '10.0.2.2' : 'host.docker.internal';
    final isPhone = !isWeb && (platform == TargetPlatform.android || platform == TargetPlatform.iOS);

    final String redirect;
    if (_clean(redirectOverride) case final value?) {
      redirect = value;
    } else if (isWeb) {
      // A browser build comes back to the page that started it.
      redirect = '${webOrigin ?? 'http://localhost:3000'}/callback';
    } else {
      redirect = isPhone ? defaultMobileRedirectUri : defaultDesktopRedirectUri;
    }

    return AppConfig(
      apiBaseUrl: _clean(apiOverride) ?? 'http://$apiHost:$apiPort',
      stsBaseUrl: _clean(stsOverride) ?? 'http://$stsHost:$stsPort',
      tokenBaseUrl: _clean(tokenOverride),
      redirectUri: redirect,
    );
  }

  /// An empty override (the default of `String.fromEnvironment`) means "not set"; a trailing slash would double up in paths.
  static String? _clean(String value) {
    final trimmed = value.trim();
    if (trimmed.isEmpty) return null;
    return trimmed.endsWith('/') ? trimmed.substring(0, trimmed.length - 1) : trimmed;
  }

  @override
  String toString() => 'AppConfig(api: $apiBaseUrl, sts: $stsBaseUrl, redirect: $redirectUri)';
}
