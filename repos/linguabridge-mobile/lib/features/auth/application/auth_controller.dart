import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../../core/storage/token_store.dart';

enum AuthStatus {
  /// The stored token has not been looked at yet (app start).
  unknown,
  signedOut,
  signedIn,
}

/// Whether a learner is signed in. The OpenID Connect sign-in itself is a later task: until then [startPreview] lets a
/// developer into the app shell without a token, and every API call goes out unauthenticated.
class AuthController extends Notifier<AuthStatus> {
  @override
  AuthStatus build() {
    _restore();
    return AuthStatus.unknown;
  }

  Future<void> _restore() async {
    final token = await ref.read(tokenStoreProvider).read();
    // A sign-in or preview that happened while the store was being read wins over the stored answer.
    if (state == AuthStatus.unknown) state = token == null ? AuthStatus.signedOut : AuthStatus.signedIn;
  }

  Future<void> signIn(String accessToken) async {
    await ref.read(tokenStoreProvider).write(accessToken);
    state = AuthStatus.signedIn;
  }

  /// Enters the app without a token (development only).
  void startPreview() => state = AuthStatus.signedIn;

  Future<void> signOut() async {
    await ref.read(tokenStoreProvider).clear();
    state = AuthStatus.signedOut;
  }
}

final authControllerProvider = NotifierProvider<AuthController, AuthStatus>(AuthController.new);
