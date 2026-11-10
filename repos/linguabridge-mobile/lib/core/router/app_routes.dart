abstract final class AppRoutes {
  static const splash = '/splash';
  static const welcome = '/welcome';

  /// Where the STS sends the browser after a sign-in or a sign-out (the web build only).
  static const callback = '/callback';
  static const home = '/home';
  static const courses = '/courses';
  static const srs = '/srs';
  static const aiTutor = '/tutor';

  /// A lesson run: full screen, outside the tabs.
  static const lesson = '/lesson';

  static String lessonLocation(String lessonId, {String? courseId}) =>
      '$lesson/${Uri.encodeComponent(lessonId)}${courseId == null ? '' : '?courseId=${Uri.encodeQueryComponent(courseId)}'}';
}
