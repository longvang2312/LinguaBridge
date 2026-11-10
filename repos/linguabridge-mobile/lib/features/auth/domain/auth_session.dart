import 'dart:convert';

import 'package:flutter/foundation.dart';

/// The learner as the `id_token` describes them.
@immutable
class AuthUser {
  const AuthUser({required this.id, this.email, this.name});

  final String id;
  final String? email;
  final String? name;

  /// What to call the learner on screen.
  String get displayName {
    final value = (name?.trim().isNotEmpty ?? false) ? name! : email;
    return (value?.trim().isNotEmpty ?? false) ? value! : id;
  }

  /// Reads `sub`, `email` and `name` from the (unverified) payload of a JWT. The token came straight from the STS over the
  /// token endpoint, so this is for display only: the API validates tokens itself.
  static AuthUser? fromIdToken(String? idToken) {
    final claims = decodeJwtPayload(idToken);
    final sub = claims?['sub'];
    if (sub is! String || sub.isEmpty) return null;
    final email = claims?['email'];
    final name = claims?['name'] ?? claims?['preferred_username'];
    return AuthUser(id: sub, email: email is String ? email : null, name: name is String ? name : null);
  }

  @override
  bool operator ==(Object other) => other is AuthUser && other.id == id && other.email == email && other.name == name;

  @override
  int get hashCode => Object.hash(id, email, name);
}

/// The payload of a JWT, or null when [jwt] is not one.
Map<String, dynamic>? decodeJwtPayload(String? jwt) {
  final parts = jwt?.split('.');
  if (parts == null || parts.length < 2) return null;
  try {
    final decoded = jsonDecode(utf8.decode(base64Url.decode(base64Url.normalize(parts[1]))));
    return decoded is Map<String, dynamic> ? decoded : null;
  } on FormatException {
    return null;
  }
}

/// The tokens of a signed-in learner.
@immutable
class AuthSession {
  const AuthSession({required this.accessToken, required this.expiresAt, this.refreshToken, this.idToken});

  final String accessToken;
  final String? refreshToken;
  final String? idToken;

  /// When [accessToken] stops being accepted.
  final DateTime expiresAt;

  AuthUser? get user => AuthUser.fromIdToken(idToken);

  /// True when the access token has expired or will within [margin].
  bool isExpiring(DateTime now, {Duration margin = const Duration(seconds: 30)}) => !now.add(margin).isBefore(expiresAt);

  Map<String, dynamic> toJson() => {
        'accessToken': accessToken,
        'refreshToken': refreshToken,
        'idToken': idToken,
        'expiresAt': expiresAt.toUtc().toIso8601String(),
      };

  /// Null when [json] is not a stored session (an older or damaged value).
  static AuthSession? fromJson(Object? json) {
    if (json is! Map) return null;
    final access = json['accessToken'];
    final expires = json['expiresAt'] is String ? DateTime.tryParse(json['expiresAt'] as String) : null;
    if (access is! String || access.isEmpty || expires == null) return null;
    return AuthSession(
      accessToken: access,
      refreshToken: json['refreshToken'] as String?,
      idToken: json['idToken'] as String?,
      expiresAt: expires,
    );
  }

  AuthSession copyWith({String? accessToken, String? refreshToken, String? idToken, DateTime? expiresAt}) => AuthSession(
        accessToken: accessToken ?? this.accessToken,
        refreshToken: refreshToken ?? this.refreshToken,
        idToken: idToken ?? this.idToken,
        expiresAt: expiresAt ?? this.expiresAt,
      );
}
