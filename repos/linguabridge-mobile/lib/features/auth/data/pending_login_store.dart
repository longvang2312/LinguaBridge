import 'dart:convert';

import 'package:shared_preferences/shared_preferences.dart';

import 'oidc_client.dart';

/// Keeps the PKCE secret and `state` of a sign-in that is in progress. Needed because the web build leaves the page for the
/// STS and starts again at the callback address. Single use: whoever reads it takes it.
abstract class PendingLoginStore {
  Future<void> save(PendingLogin pending);

  /// The pending sign-in, which is forgotten at the same time; null when there is none.
  Future<PendingLogin?> take();
}

class SharedPreferencesPendingLoginStore implements PendingLoginStore {
  static const _key = 'lb.pendingLogin';

  @override
  Future<void> save(PendingLogin pending) async {
    final prefs = await SharedPreferences.getInstance();
    await prefs.setString(_key, jsonEncode(pending.toJson()));
  }

  @override
  Future<PendingLogin?> take() async {
    final prefs = await SharedPreferences.getInstance();
    final text = prefs.getString(_key);
    await prefs.remove(_key);
    if (text == null) return null;
    try {
      return PendingLogin.fromJson(jsonDecode(text));
    } on FormatException {
      return null;
    }
  }
}

class MemoryPendingLoginStore implements PendingLoginStore {
  PendingLogin? _pending;

  @override
  Future<void> save(PendingLogin pending) async => _pending = pending;

  @override
  Future<PendingLogin?> take() async {
    final pending = _pending;
    _pending = null;
    return pending;
  }
}
