import 'dart:convert';
import 'dart:typed_data';

import 'package:dio/dio.dart';
import 'package:linguabridge_mobile/core/network/api_exception.dart';
import 'package:linguabridge_mobile/features/auth/data/auth_launcher.dart';
import 'package:linguabridge_mobile/features/auth/domain/auth_session.dart';
import 'package:linguabridge_mobile/features/courses/data/course_models.dart';
import 'package:linguabridge_mobile/features/courses/data/courses_repository.dart';

/// Answers every request with whatever [respond] says and remembers what was asked.
class FakeAdapter implements HttpClientAdapter {
  FakeAdapter(this.respond);

  final ResponseBody Function(RequestOptions options) respond;
  final requests = <RequestOptions>[];

  @override
  Future<ResponseBody> fetch(RequestOptions options, Stream<Uint8List>? requestStream, Future<void>? cancelFuture) async {
    requests.add(options);
    return respond(options);
  }

  @override
  void close({bool force = false}) {}
}

ResponseBody jsonBody(int status, Object body) => ResponseBody.fromString(
      jsonEncode(body),
      status,
      headers: {Headers.contentTypeHeader: ['application/json']},
    );

/// A JWT whose payload is [claims] (the signature is not checked by the app).
String fakeJwt(Map<String, Object?> claims) {
  String part(Object value) => base64Url.encode(utf8.encode(jsonEncode(value))).replaceAll('=', '');
  return '${part({'alg': 'none'})}.${part(claims)}.sig';
}

/// A token endpoint answer.
Map<String, Object?> tokenResponse({
  String access = 'access-1',
  String? refresh = 'refresh-1',
  String? idToken,
  int expiresIn = 3600,
}) =>
    {
      'access_token': access,
      'token_type': 'Bearer',
      'expires_in': expiresIn,
      'refresh_token': ?refresh,
      'id_token': ?idToken,
    };

final testIdToken = fakeJwt({'sub': 'user-1', 'email': 'admin@appinhouse.local', 'name': 'Admin'});

AuthSession makeSession({
  String access = 'access-1',
  String? refresh = 'refresh-1',
  DateTime? expiresAt,
  String? idToken,
}) =>
    AuthSession(
      accessToken: access,
      refreshToken: refresh,
      idToken: idToken ?? testIdToken,
      expiresAt: expiresAt ?? DateTime.now().add(const Duration(hours: 1)),
    );

/// Plays the browser: the sign-in "succeeds" by returning the redirect address with a code and the request's own state.
class FakeAuthLauncher implements AuthLauncher {
  /// When set, decides the answer instead; null means the learner closed the page (or the page is being replaced).
  Uri? Function(Uri url)? onAuthenticate;
  final authenticated = <Uri>[];
  final ended = <Uri>[];

  @override
  Future<Uri?> authenticate(Uri url, {required String callbackScheme}) async {
    authenticated.add(url);
    return onAuthenticate?.call(url);
  }

  @override
  Future<void> endSession(Uri url, {required String callbackScheme}) async => ended.add(url);

  /// The redirect the STS would send for a successful sign-in.
  static Uri success(Uri url, {String code = 'the-code'}) => Uri.parse(url.queryParameters['redirect_uri']!)
      .replace(queryParameters: {'code': code, 'state': url.queryParameters['state']!});
}

class FakeCoursesRepository implements CoursesRepository {
  FakeCoursesRepository({this.courseList = const {}, this.lessonList = const {}, this.progressList = const {}});

  final Map<String, List<CourseModel>> courseList;
  final Map<String, List<LessonModel>> lessonList;
  final Map<String, CourseProgress> progressList;

  /// Failures to throw before succeeding, per call kind.
  int courseFailures = 0;
  int lessonFailures = 0;
  final courseCalls = <String>[];
  final lessonCalls = <String>[];

  @override
  Future<List<CourseModel>> courses({required String targetLanguage, required String baseLanguage}) async {
    courseCalls.add('$targetLanguage/$baseLanguage');
    if (courseFailures > 0) {
      courseFailures--;
      throw const ApiException(message: 'Không kết nối được máy chủ. Hãy kiểm tra mạng và API đang chạy.');
    }
    return courseList[targetLanguage] ?? const [];
  }

  @override
  Future<List<LessonModel>> lessons(String courseId, {required String baseLanguage}) async {
    lessonCalls.add(courseId);
    if (lessonFailures > 0) {
      lessonFailures--;
      throw const ApiException(message: 'Lỗi tải bài học.');
    }
    // The real repository returns lessons in lesson order.
    return [...?lessonList[courseId]]..sort((a, b) => a.orderIndex.compareTo(b.orderIndex));
  }

  @override
  Future<CourseProgress> progress(String courseId) async {
    final value = progressList[courseId];
    if (value == null) throw const ApiException(message: 'no progress', statusCode: 404);
    return value;
  }
}

const englishCourse = CourseModel(
  id: '11111111-1111-1111-1111-111111111111',
  code: 'ENG-A1-COMM',
  targetLanguage: 'en',
  level: 'A1',
  title: 'Tiếng Anh giao tiếp nhập môn',
  description: 'Những mẫu câu đầu tiên để chào hỏi.',
);
const businessCourse = CourseModel(
  id: '22222222-2222-2222-2222-222222222222',
  code: 'ENG-BIZ-A2',
  targetLanguage: 'en',
  level: 'A2',
  title: 'Tiếng Anh công sở',
  description: 'Viết email chuyên nghiệp.',
);
const chineseCourse = CourseModel(
  id: '33333333-3333-3333-3333-333333333333',
  code: 'ZH-HSK1-COMM',
  targetLanguage: 'zh',
  level: 'HSK1',
  title: 'Tiếng Trung giao tiếp HSK 1',
  description: 'Từ vựng và mẫu câu HSK 1.',
);

const englishLessons = [
  LessonModel(id: 'l2', courseId: '11111111-1111-1111-1111-111111111111', orderIndex: 2, lessonType: 'Vocabulary', xpReward: 10, title: 'Nhờ giúp đỡ'),
  LessonModel(id: 'l1', courseId: '11111111-1111-1111-1111-111111111111', orderIndex: 1, lessonType: 'Vocabulary', xpReward: 15, title: 'Chào hỏi', guideText: 'Học cách chào.'),
];
