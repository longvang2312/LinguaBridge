import 'package:flutter/foundation.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:linguabridge_mobile/core/config/app_config.dart';

void main() {
  group('AppConfig.resolve', () {
    test('Android emulator reaches the host machine as 10.0.2.2', () {
      final config = AppConfig.resolve(isWeb: false, platform: TargetPlatform.android);
      expect(config.apiBaseUrl, 'http://10.0.2.2:5020');
      expect(config.stsBaseUrl, 'http://10.0.2.2:5001');
    });

    for (final platform in [TargetPlatform.windows, TargetPlatform.macOS, TargetPlatform.linux, TargetPlatform.iOS]) {
      test('$platform calls the API on localhost and signs in through localhost', () {
        final config = AppConfig.resolve(isWeb: false, platform: platform);
        expect(config.apiBaseUrl, 'http://localhost:5020');
        // The API only accepts tokens issued through the STS authority it was configured with.
        expect(config.stsBaseUrl, 'http://localhost:5001');
      });
    }

    test('the web behaves like a desktop even when the browser runs on Android', () {
      final config = AppConfig.resolve(isWeb: true, platform: TargetPlatform.android);
      expect(config.apiBaseUrl, 'http://localhost:5020');
      expect(config.stsBaseUrl, 'http://localhost:5001');
    });

    test('overrides win, and a trailing slash is dropped', () {
      final config = AppConfig.resolve(
        isWeb: false,
        platform: TargetPlatform.android,
        apiOverride: 'http://192.168.1.20:5020/',
        stsOverride: ' http://sts.example.com ',
        tokenOverride: '/sts/',
      );
      expect(config.apiBaseUrl, 'http://192.168.1.20:5020');
      expect(config.stsBaseUrl, 'http://sts.example.com');
      expect(config.tokenUrl, '/sts');
    });

    test('an empty override (the default of --dart-define) means not set', () {
      final config = AppConfig.resolve(isWeb: false, platform: TargetPlatform.windows, apiOverride: '', stsOverride: '  ');
      expect(config.apiBaseUrl, 'http://localhost:5020');
      expect(config.stsBaseUrl, 'http://localhost:5001');
    });

    test('one override does not change the other address', () {
      final config = AppConfig.resolve(isWeb: false, platform: TargetPlatform.android, apiOverride: 'http://x:1');
      expect(config.apiBaseUrl, 'http://x:1');
      expect(config.stsBaseUrl, 'http://10.0.2.2:5001');
    });

    test('the token endpoint is on the STS unless told otherwise', () {
      final config = AppConfig.resolve(isWeb: false, platform: TargetPlatform.windows);
      expect(config.tokenUrl, config.stsBaseUrl);
    });
  });

  group('sign-in settings', () {
    test('the client and scopes are the ones the STS has for the learners app', () {
      final config = AppConfig.resolve(isWeb: false, platform: TargetPlatform.windows);
      expect(config.clientId, 'linguabridge-app');
      expect(config.scope.split(' '), containsAll(['openid', 'profile', 'email', 'offline_access', 'linguabridge_api']));
    });

    test('desktop uses the loopback redirect the STS already knows, and its scheme is host and port', () {
      final config = AppConfig.resolve(isWeb: false, platform: TargetPlatform.windows);
      expect(config.redirectUri, 'http://localhost:3000/callback');
      expect(config.callbackScheme, 'http://localhost:3000');
    });

    test('a phone uses a custom scheme', () {
      for (final platform in [TargetPlatform.android, TargetPlatform.iOS]) {
        final config = AppConfig.resolve(isWeb: false, platform: platform);
        expect(config.redirectUri, 'com.appinhouse.linguabridge:/callback');
        expect(config.callbackScheme, 'com.appinhouse.linguabridge');
      }
    });

    test('the web comes back to the page that started the sign-in', () {
      final config = AppConfig.resolve(isWeb: true, platform: TargetPlatform.windows, webOrigin: 'http://localhost:3000');
      expect(config.redirectUri, 'http://localhost:3000/callback');
    });

    test('the redirect can be overridden', () {
      final config = AppConfig.resolve(isWeb: false, platform: TargetPlatform.windows, redirectOverride: 'http://localhost:5173/callback');
      expect(config.redirectUri, 'http://localhost:5173/callback');
      expect(config.callbackScheme, 'http://localhost:5173');
    });
  });
}
