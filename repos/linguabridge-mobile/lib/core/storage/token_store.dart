import 'dart:convert';

import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_secure_storage/flutter_secure_storage.dart';

import '../../features/auth/domain/auth_session.dart';

/// Where the learner's tokens (access, refresh and id token) are kept between launches.
abstract class TokenStore {
  Future<AuthSession?> read();
  Future<void> write(AuthSession session);
  Future<void> clear();
}

/// Keychain (iOS), Keystore (Android), Credential Locker (Windows) or WebCrypto-encrypted storage (web).
class SecureTokenStore implements TokenStore {
  SecureTokenStore([FlutterSecureStorage? storage]) : _storage = storage ?? const FlutterSecureStorage();

  static const _key = 'lb.session';
  final FlutterSecureStorage _storage;

  @override
  Future<AuthSession?> read() async {
    try {
      final text = await _storage.read(key: _key);
      return text == null ? null : AuthSession.fromJson(jsonDecode(text));
    } on Object {
      // A store that cannot be read (corrupt keystore, blocked storage, damaged value) is the same as no session: the
      // learner signs in again.
      return null;
    }
  }

  @override
  Future<void> write(AuthSession session) => _storage.write(key: _key, value: jsonEncode(session.toJson()));

  @override
  Future<void> clear() => _storage.delete(key: _key);
}

/// Kept in memory only; for tests.
class MemoryTokenStore implements TokenStore {
  MemoryTokenStore([this._session]);
  AuthSession? _session;

  @override
  Future<AuthSession?> read() async => _session;

  @override
  Future<void> write(AuthSession session) async => _session = session;

  @override
  Future<void> clear() async => _session = null;
}

final tokenStoreProvider = Provider<TokenStore>((ref) => SecureTokenStore());
