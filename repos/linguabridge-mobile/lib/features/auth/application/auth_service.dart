import 'dart:async';

import '../../../core/config/app_config.dart';
import '../../../core/storage/token_store.dart';
import '../data/auth_launcher.dart';
import '../data/oidc_client.dart';
import '../data/pending_login_store.dart';
import '../domain/auth_session.dart';

/// The learner's session: sign-in, tokens that stay fresh, sign-out. No UI and no Riverpod in here, so it is easy to test.
class AuthService {
  AuthService({
    required this.config,
    required this.store,
    required this.oidc,
    required this.pending,
    required this.launcher,
    DateTime Function()? clock,
  }) : _clock = clock ?? DateTime.now;

  final AppConfig config;
  final TokenStore store;
  final OidcClient oidc;
  final PendingLoginStore pending;
  final AuthLauncher launcher;
  final DateTime Function() _clock;

  AuthSession? _session;
  bool _loaded = false;
  Future<AuthSession?>? _refreshing;

  /// Counts the times the session was ended or replaced (sign-out, expiry, a new sign-in). A refresh remembers the number
  /// it started under and is thrown away if the number has changed when the STS answers: otherwise a refresh that was
  /// still running would write fresh tokens back after a sign-out and bring the session back to life.
  int _generation = 0;

  /// The stored session, or null. A session that has expired and cannot be refreshed is dropped here.
  Future<AuthSession?> restore() async {
    if (!_loaded) {
      _session = await store.read();
      _loaded = true;
    }
    final session = _session;
    if (session != null && session.isExpiring(_clock()) && session.refreshToken == null) {
      await _forget();
      return null;
    }
    return _session;
  }

  /// An access token the API will accept, refreshed first when it is about to expire (or [forceRefresh], after the API
  /// refused it). Null when nobody is signed in or the session can no longer be refreshed. Concurrent callers share one
  /// refresh: refresh tokens may be single-use.
  Future<String?> accessToken({bool forceRefresh = false}) async {
    final session = await restore();
    if (session == null) return null;
    if (!forceRefresh && !session.isExpiring(_clock())) return session.accessToken;
    return (await _refresh(session))?.accessToken;
  }

  Future<AuthSession?> _refresh(AuthSession stale) {
    // A refresh that is already running answers every caller.
    final running = _refreshing;
    if (running != null) return running;
    late final Future<AuthSession?> refresh;
    refresh = _doRefresh(stale).whenComplete(() {
      if (identical(_refreshing, refresh)) _refreshing = null;
    });
    return _refreshing = refresh;
  }

  Future<AuthSession?> _doRefresh(AuthSession stale) async {
    final generation = _generation;
    try {
      final fresh = await oidc.refresh(stale);
      // Signed out (or signed in again) while the STS was answering: these tokens belong to a session that is over.
      if (generation != _generation) return null;
      await _remember(fresh, newSession: false);
      return fresh;
    } on AuthException catch (error) {
      // The STS said no (the refresh token was revoked or expired): the session is over. A network failure only means
      // "not now": the session is kept so the next request can try again.
      if (error.invalidGrant && generation == _generation) await _forget();
      return null;
    }
  }

  /// Starts the sign-in. Returns the session when it finished here (phone, desktop), or null when it did not: the learner
  /// closed the page, or on the web the page is being replaced and [completeLogin] runs at the callback address.
  Future<AuthSession?> beginLogin({bool forceLogin = false}) async {
    final (url, flow) = oidc.startLogin(forceLogin: forceLogin);
    await pending.save(flow);
    final callback = await launcher.authenticate(url, callbackScheme: config.callbackScheme);
    if (callback == null) return null;
    return completeLogin(callback);
  }

  /// Finishes a sign-in from the address the STS redirected to. Fails when this app instance did not start one.
  Future<AuthSession> completeLogin(Uri callback) async {
    final flow = await pending.take();
    if (flow == null) throw const AuthException('Không có phiên đăng nhập nào đang chờ. Hãy bắt đầu đăng nhập lại.');
    final session = await oidc.completeLogin(callback, flow);
    await _remember(session, newSession: true);
    return session;
  }

  /// Signs out: the tokens go first, so the app is signed out whatever happens next; then the STS is asked to end its own
  /// session (on the web this leaves the page).
  Future<void> logout() async {
    final session = await restore();
    await _forget();
    await launcher.endSession(oidc.logoutUri(session), callbackScheme: config.callbackScheme);
  }

  /// Forgets the session without involving the STS (the API refused it for good).
  Future<void> expire() => _forget();

  Future<void> _remember(AuthSession session, {required bool newSession}) async {
    if (newSession) {
      _generation++; // a refresh still running belongs to the previous session
      _refreshing = null;
    }
    _session = session;
    _loaded = true;
    await store.write(session);
  }

  Future<void> _forget() async {
    _generation++;
    _refreshing = null;
    _session = null;
    _loaded = true;
    await store.clear();
  }
}
