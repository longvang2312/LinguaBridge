import 'dart:convert';
import 'dart:math';

import 'package:crypto/crypto.dart';

/// Proof Key for Code Exchange (RFC 7636): the sign-in starts with the hash of a secret only this app instance knows,
/// and the secret itself is shown to the STS when the code is exchanged, so a stolen code is useless to anyone else.
abstract final class Pkce {
  /// A random `code_verifier` (43 characters from 32 random bytes; the RFC allows 43 to 128).
  static String generateVerifier({Random? random, int byteLength = 32}) => _randomUrlSafe(byteLength, random);

  /// A random value tying the answer to the request (`state`).
  static String generateState({Random? random}) => _randomUrlSafe(16, random);

  /// `BASE64URL(SHA-256(verifier))`, without padding.
  static String challengeFor(String verifier) => _base64UrlNoPadding(sha256.convert(ascii.encode(verifier)).bytes);

  static String _randomUrlSafe(int byteLength, Random? random) {
    final source = random ?? Random.secure();
    return _base64UrlNoPadding(List<int>.generate(byteLength, (_) => source.nextInt(256)));
  }

  static String _base64UrlNoPadding(List<int> bytes) => base64Url.encode(bytes).replaceAll('=', '');
}
