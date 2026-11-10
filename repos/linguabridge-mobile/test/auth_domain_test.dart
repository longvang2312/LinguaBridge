import 'dart:math';

import 'package:flutter_test/flutter_test.dart';
import 'package:linguabridge_mobile/features/auth/data/pkce.dart';
import 'package:linguabridge_mobile/features/auth/domain/auth_session.dart';

import 'support/fakes.dart';

void main() {
  group('Pkce', () {
    test('the challenge matches the test vector of RFC 7636 appendix B', () {
      expect(
        Pkce.challengeFor('dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk'),
        'E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM',
      );
    });

    test('a verifier is 43 URL-safe characters (32 bytes) and differs every time', () {
      final a = Pkce.generateVerifier();
      final b = Pkce.generateVerifier();
      expect(a, matches(RegExp(r'^[A-Za-z0-9_-]{43}$')));
      expect(a, isNot(b));
    });

    test('a longer verifier stays within the 43 to 128 characters the RFC allows', () {
      expect(Pkce.generateVerifier(byteLength: 64).length, inInclusiveRange(43, 128));
    });

    test('the same random source gives the same verifier (it is the source that is random)', () {
      expect(Pkce.generateVerifier(random: Random(1)), Pkce.generateVerifier(random: Random(1)));
    });

    test('state is URL-safe', () {
      expect(Pkce.generateState(), matches(RegExp(r'^[A-Za-z0-9_-]+$')));
    });
  });

  group('AuthUser', () {
    test('reads sub, email and name from an id_token', () {
      final user = AuthUser.fromIdToken(fakeJwt({'sub': 'u1', 'email': 'a@b.c', 'name': 'An'}));
      expect(user, const AuthUser(id: 'u1', email: 'a@b.c', name: 'An'));
      expect(user!.displayName, 'An');
    });

    test('falls back to preferred_username, then email, then the id for the display name', () {
      expect(AuthUser.fromIdToken(fakeJwt({'sub': 'u1', 'preferred_username': 'an'}))!.displayName, 'an');
      expect(AuthUser.fromIdToken(fakeJwt({'sub': 'u1', 'email': 'a@b.c'}))!.displayName, 'a@b.c');
      expect(AuthUser.fromIdToken(fakeJwt({'sub': 'u1'}))!.displayName, 'u1');
      expect(AuthUser.fromIdToken(fakeJwt({'sub': 'u1', 'name': '  ', 'email': 'a@b.c'}))!.displayName, 'a@b.c');
    });

    test('anything that is not a JWT with a sub gives no user', () {
      expect(AuthUser.fromIdToken(null), isNull);
      expect(AuthUser.fromIdToken(''), isNull);
      expect(AuthUser.fromIdToken('not-a-jwt'), isNull);
      expect(AuthUser.fromIdToken('a.!!!.c'), isNull);
      expect(AuthUser.fromIdToken(fakeJwt({'email': 'a@b.c'})), isNull);
    });
  });

  group('AuthSession', () {
    final now = DateTime.utc(2026, 11, 10, 12);

    test('is expiring when the token expires within the margin', () {
      AuthSession at(DateTime t) => AuthSession(accessToken: 'a', expiresAt: t);
      expect(at(now.add(const Duration(minutes: 5))).isExpiring(now), isFalse);
      expect(at(now.add(const Duration(seconds: 20))).isExpiring(now), isTrue);
      expect(at(now).isExpiring(now), isTrue);
      expect(at(now.subtract(const Duration(hours: 1))).isExpiring(now), isTrue);
    });

    test('survives a JSON round trip', () {
      final session = AuthSession(accessToken: 'a', refreshToken: 'r', idToken: testIdToken, expiresAt: now);
      final back = AuthSession.fromJson(session.toJson())!;
      expect(back.accessToken, 'a');
      expect(back.refreshToken, 'r');
      expect(back.idToken, testIdToken);
      expect(back.expiresAt, now);
      expect(back.user?.email, 'admin@appinhouse.local');
    });

    test('a damaged stored value is not a session', () {
      expect(AuthSession.fromJson(null), isNull);
      expect(AuthSession.fromJson('x'), isNull);
      expect(AuthSession.fromJson({'accessToken': '', 'expiresAt': now.toIso8601String()}), isNull);
      expect(AuthSession.fromJson({'accessToken': 'a', 'expiresAt': 'yesterday'}), isNull);
      expect(AuthSession.fromJson({'accessToken': 'a'}), isNull);
    });
  });
}
