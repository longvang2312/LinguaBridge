import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_secure_storage/flutter_secure_storage.dart';

/// Where the learner's access token is kept between launches.
abstract class TokenStore {
  Future<String?> read();
  Future<void> write(String accessToken);
  Future<void> clear();
}

/// Keychain (iOS), Keystore (Android), Credential Locker (Windows) or WebCrypto-encrypted storage (web).
class SecureTokenStore implements TokenStore {
  SecureTokenStore([FlutterSecureStorage? storage]) : _storage = storage ?? const FlutterSecureStorage();

  static const _key = 'lb.accessToken';
  final FlutterSecureStorage _storage;

  @override
  Future<String?> read() async {
    try {
      return await _storage.read(key: _key);
    } on Object {
      // A store that cannot be read (corrupt keystore, blocked storage) is the same as no token: the learner signs in again.
      return null;
    }
  }

  @override
  Future<void> write(String accessToken) => _storage.write(key: _key, value: accessToken);

  @override
  Future<void> clear() => _storage.delete(key: _key);
}

/// Kept in memory only; for tests and previews.
class MemoryTokenStore implements TokenStore {
  MemoryTokenStore([this._token]);
  String? _token;

  @override
  Future<String?> read() async => _token;

  @override
  Future<void> write(String accessToken) async => _token = accessToken;

  @override
  Future<void> clear() async => _token = null;
}

final tokenStoreProvider = Provider<TokenStore>((ref) => SecureTokenStore());
