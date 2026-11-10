import 'package:flutter/foundation.dart';
import 'package:flutter/services.dart';
import 'package:flutter_web_auth_2/flutter_web_auth_2.dart';
import 'package:url_launcher/url_launcher.dart';

/// Takes the learner to the STS page and, where the platform allows, brings the answer back.
abstract class AuthLauncher {
  /// Opens [url] for the learner to sign in.
  ///
  /// On a phone or desktop this waits for the learner to finish and returns the address the STS redirected to (which
  /// carries the code), or null when they closed the page. On the web the whole page is replaced by the STS and the app
  /// starts again at the redirect address, so there is nothing to return: the result is null and the callback screen
  /// finishes the job.
  Future<Uri?> authenticate(Uri url, {required String callbackScheme});

  /// Opens the STS logout [url] so it ends its own session. Best effort: the learner is already signed out of the app.
  Future<void> endSession(Uri url, {required String callbackScheme});
}

class PlatformAuthLauncher implements AuthLauncher {
  @override
  Future<Uri?> authenticate(Uri url, {required String callbackScheme}) async {
    if (kIsWeb) {
      await launchUrl(url, webOnlyWindowName: '_self');
      return null;
    }
    try {
      final result = await FlutterWebAuth2.authenticate(url: url.toString(), callbackUrlScheme: callbackScheme);
      return Uri.parse(result);
    } on PlatformException catch (error) {
      if (error.code == 'CANCELED') return null;
      rethrow;
    }
  }

  @override
  Future<void> endSession(Uri url, {required String callbackScheme}) async {
    try {
      await authenticate(url, callbackScheme: callbackScheme);
    } on Object catch (error) {
      debugPrint('STS logout page could not be shown: $error');
    }
  }
}
