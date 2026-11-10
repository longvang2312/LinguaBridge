import 'dart:math';

import 'package:dio/dio.dart';
import 'package:flutter/foundation.dart';

import '../../../core/config/app_config.dart';
import '../domain/auth_session.dart';
import 'pkce.dart';

/// A sign-in or sign-out step that failed in terms the learner can read.
class AuthException implements Exception {
  const AuthException(this.message, {this.invalidGrant = false});

  final String message;

  /// The STS refused the code or refresh token itself (`invalid_grant`): trying again with the same one cannot work, as
  /// opposed to a network failure, which can.
  final bool invalidGrant;

  @override
  String toString() => 'AuthException($message)';
}

/// What has to be remembered between sending the learner to the STS and getting them back: on the web the page is
/// replaced in between, so this goes to storage.
@immutable
class PendingLogin {
  const PendingLogin({required this.verifier, required this.state, required this.redirectUri});

  final String verifier;
  final String state;
  final String redirectUri;

  Map<String, dynamic> toJson() => {'verifier': verifier, 'state': state, 'redirectUri': redirectUri};

  static PendingLogin? fromJson(Object? json) {
    if (json is! Map) return null;
    final verifier = json['verifier'];
    final state = json['state'];
    final redirectUri = json['redirectUri'];
    if (verifier is! String || state is! String || redirectUri is! String) return null;
    return PendingLogin(verifier: verifier, state: state, redirectUri: redirectUri);
  }
}

/// OpenID Connect against the STS: Authorization Code with PKCE (the app is a public client with no secret), refresh
/// tokens and RP-initiated logout.
class OidcClient {
  OidcClient({required this.config, required this.dio, DateTime Function()? clock, this.random})
      : _clock = clock ?? DateTime.now;

  final AppConfig config;

  /// A client without the bearer interceptor: the token endpoint takes no token and must not recurse into refreshing.
  final Dio dio;
  final DateTime Function() _clock;
  final Random? random;

  /// The address to send the learner to, and what to keep for when they come back.
  ///
  /// [forceLogin] makes the STS ask for the password even when it still has a session for someone.
  (Uri, PendingLogin) startLogin({bool forceLogin = false}) {
    final pending = PendingLogin(
      verifier: Pkce.generateVerifier(random: random),
      state: Pkce.generateState(random: random),
      redirectUri: config.redirectUri,
    );
    final url = Uri.parse('${config.stsBaseUrl}/connect/authorize').replace(queryParameters: {
      'client_id': config.clientId,
      'response_type': 'code',
      'redirect_uri': pending.redirectUri,
      'scope': config.scope,
      'state': pending.state,
      'code_challenge': Pkce.challengeFor(pending.verifier),
      'code_challenge_method': 'S256',
      if (forceLogin) 'prompt': 'login',
    });
    return (url, pending);
  }

  /// Turns the address the STS sent the learner back to into tokens.
  Future<AuthSession> completeLogin(Uri callback, PendingLogin pending) async {
    final query = callback.queryParameters;
    if (query['error'] != null) {
      throw AuthException(query['error_description'] ?? 'Đăng nhập không thành công (${query['error']}).');
    }
    if (query['state'] != pending.state) {
      // The answer does not belong to the request this app instance made.
      throw const AuthException('Phản hồi đăng nhập không hợp lệ. Hãy thử đăng nhập lại.');
    }
    final code = query['code'];
    if (code == null || code.isEmpty) throw const AuthException('Không nhận được mã đăng nhập từ máy chủ.');

    return _token({
      'grant_type': 'authorization_code',
      'client_id': config.clientId,
      'code': code,
      'redirect_uri': pending.redirectUri,
      'code_verifier': pending.verifier,
    });
  }

  /// Exchanges the refresh token for a new access token. The STS may or may not rotate the refresh token (and send a new
  /// id token); what it does not send is kept from [session].
  Future<AuthSession> refresh(AuthSession session) async {
    final refreshToken = session.refreshToken;
    if (refreshToken == null) throw const AuthException('Phiên đăng nhập đã hết hạn.', invalidGrant: true);
    final fresh = await _token({
      'grant_type': 'refresh_token',
      'client_id': config.clientId,
      'refresh_token': refreshToken,
    });
    return fresh.copyWith(refreshToken: fresh.refreshToken ?? refreshToken, idToken: fresh.idToken ?? session.idToken);
  }

  /// Where to send the learner so the STS ends its own session as well; it then returns to the redirect address.
  Uri logoutUri(AuthSession? session) {
    final idToken = session?.idToken;
    return Uri.parse('${config.stsBaseUrl}/connect/logout').replace(queryParameters: {
      'id_token_hint': ?idToken,
      'post_logout_redirect_uri': config.redirectUri,
      'state': 'lb-logout',
    });
  }

  Future<AuthSession> _token(Map<String, String> form) async {
    final Response<dynamic> response;
    try {
      response = await dio.post<dynamic>(
        '${config.tokenUrl}/connect/token',
        data: form,
        options: Options(contentType: Headers.formUrlEncodedContentType, headers: {'Accept': 'application/json'}),
      );
    } on DioException catch (error) {
      final body = error.response?.data;
      if (body is Map && body['error'] is String) {
        final code = body['error'] as String;
        throw AuthException(
          (body['error_description'] as String?) ?? 'Máy chủ từ chối đăng nhập ($code).',
          invalidGrant: code == 'invalid_grant',
        );
      }
      throw const AuthException('Không kết nối được máy chủ đăng nhập. Hãy kiểm tra mạng và STS đang chạy.');
    }

    final body = response.data;
    final access = body is Map ? body['access_token'] : null;
    if (access is! String || access.isEmpty) throw const AuthException('Máy chủ đăng nhập trả về dữ liệu không hợp lệ.');
    final expiresIn = body['expires_in'];
    return AuthSession(
      accessToken: access,
      refreshToken: body['refresh_token'] as String?,
      idToken: body['id_token'] as String?,
      expiresAt: _clock().add(Duration(seconds: expiresIn is num ? expiresIn.toInt() : 300)),
    );
  }
}
