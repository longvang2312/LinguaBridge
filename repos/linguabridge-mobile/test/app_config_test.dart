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
      test('$platform uses localhost', () {
        final config = AppConfig.resolve(isWeb: false, platform: platform);
        expect(config.apiBaseUrl, 'http://localhost:5020');
        expect(config.stsBaseUrl, 'http://localhost:5001');
      });
    }

    test('the web uses localhost even when the browser runs on Android', () {
      final config = AppConfig.resolve(isWeb: true, platform: TargetPlatform.android);
      expect(config.apiBaseUrl, 'http://localhost:5020');
    });

    test('overrides win, and a trailing slash is dropped', () {
      final config = AppConfig.resolve(
        isWeb: false,
        platform: TargetPlatform.android,
        apiOverride: 'http://192.168.1.20:5020/',
        stsOverride: ' http://sts.example.com ',
      );
      expect(config.apiBaseUrl, 'http://192.168.1.20:5020');
      expect(config.stsBaseUrl, 'http://sts.example.com');
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
  });
}
