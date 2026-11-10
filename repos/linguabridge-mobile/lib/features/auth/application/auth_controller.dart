import 'package:dio/dio.dart';
import 'package:flutter/foundation.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../../core/config/config_providers.dart';
import '../../../core/storage/token_store.dart';
import '../data/auth_launcher.dart';
import '../data/oidc_client.dart';
import '../data/pending_login_store.dart';
import '../domain/auth_session.dart';
import 'auth_service.dart';

enum AuthStatus {
  /// The stored session has not been looked at yet (app start).
  unknown,
  signedOut,
  signedIn,
}

@immutable
class AuthState {
  const AuthState({required this.status, this.user, this.busy = false, this.error});

  const AuthState.unknown() : this(status: AuthStatus.unknown);

  final AuthStatus status;

  /// The signed-in learner (from the id_token), when there is one.
  final AuthUser? user;

  /// A sign-in is in progress.
  final bool busy;

  /// Why the last sign-in did not work, in words for the learner.
  final String? error;
}

final authLauncherProvider = Provider<AuthLauncher>((ref) => PlatformAuthLauncher());
final pendingLoginStoreProvider = Provider<PendingLoginStore>((ref) => SharedPreferencesPendingLoginStore());

/// The HTTP client of the token endpoint: no bearer interceptor (the endpoint takes no token, and refreshing must not recurse).
final tokenDioProvider = Provider<Dio>((ref) => Dio(BaseOptions(
      connectTimeout: const Duration(seconds: 10),
      receiveTimeout: const Duration(seconds: 20),
    )));

final oidcClientProvider = Provider<OidcClient>(
  (ref) => OidcClient(config: ref.watch(appConfigProvider), dio: ref.watch(tokenDioProvider)),
);

final authServiceProvider = Provider<AuthService>((ref) => AuthService(
      config: ref.watch(appConfigProvider),
      store: ref.watch(tokenStoreProvider),
      oidc: ref.watch(oidcClientProvider),
      pending: ref.watch(pendingLoginStoreProvider),
      launcher: ref.watch(authLauncherProvider),
    ));

/// Whether a learner is signed in, and who. The screens read this; the work is done by [AuthService].
class AuthController extends Notifier<AuthState> {
  AuthService get _service => ref.read(authServiceProvider);

  @override
  AuthState build() {
    _restore();
    return const AuthState.unknown();
  }

  Future<void> _restore() async {
    final session = await _service.restore();
    // A sign-in that finished while the store was being read wins over the stored answer.
    if (state.status == AuthStatus.unknown) _apply(session);
  }

  void _apply(AuthSession? session, {String? error}) {
    state = session == null
        ? AuthState(status: AuthStatus.signedOut, error: error)
        : AuthState(status: AuthStatus.signedIn, user: session.user);
  }

  /// Opens the STS sign-in page. On a phone or desktop this completes here; on the web the page is replaced and
  /// [completeCallback] finishes the job when the STS sends the learner back.
  Future<void> signIn({bool forceLogin = false}) async {
    if (state.busy) return;
    state = AuthState(status: state.status, busy: true);
    try {
      final session = await _service.beginLogin(forceLogin: forceLogin);
      if (session != null) {
        _apply(session);
      } else {
        state = AuthState(status: state.status);
      }
    } on AuthException catch (error) {
      state = AuthState(status: AuthStatus.signedOut, error: error.message);
    } on Object {
      state = const AuthState(status: AuthStatus.signedOut, error: 'Không mở được trang đăng nhập. Hãy thử lại.');
    }
  }

  /// Finishes a sign-in from the address the STS redirected to.
  Future<void> completeCallback(Uri callback) async {
    try {
      _apply(await _service.completeLogin(callback));
    } on AuthException catch (error) {
      state = AuthState(status: AuthStatus.signedOut, error: error.message);
    }
  }

  /// Signs out of the app, then of the STS (on the web this leaves the page).
  Future<void> signOut() async {
    state = const AuthState(status: AuthStatus.signedOut);
    await _service.logout();
  }

  /// The API refused the session for good: back to the welcome screen without a trip to the STS.
  Future<void> sessionExpired() async {
    if (state.status != AuthStatus.signedIn) return;
    await _service.expire();
    state = const AuthState(status: AuthStatus.signedOut, error: 'Phiên đăng nhập đã hết hạn. Hãy đăng nhập lại.');
  }
}

final authControllerProvider = NotifierProvider<AuthController, AuthState>(AuthController.new);

/// Just the status, for the router: it must not re-run its redirect when only the user or an error changes.
final authStatusProvider = Provider<AuthStatus>((ref) => ref.watch(authControllerProvider.select((s) => s.status)));
