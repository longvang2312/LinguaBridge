import 'package:dio/dio.dart';

import '../../../core/network/api_exception.dart';
import 'course_models.dart';

/// Reads courses, lessons and progress from LinguaBridge.Api. Every failure is an [ApiException].
class CoursesRepository {
  CoursesRepository(this._dio);

  final Dio _dio;

  /// `GET /api/content/courses`: the published courses learning [targetLanguage] (`en` or `zh`), titled in
  /// [baseLanguage] (`vi` or `en`).
  Future<List<CourseModel>> courses({required String targetLanguage, required String baseLanguage}) async {
    final body = await _get('/api/content/courses', {'targetLanguage': targetLanguage, 'baseLanguage': baseLanguage});
    return _list(body).map(CourseModel.fromJson).toList();
  }

  /// `GET /api/content/courses/{id}/lessons`, in lesson order.
  Future<List<LessonModel>> lessons(String courseId, {required String baseLanguage}) async {
    final body = await _get('/api/content/courses/$courseId/lessons', {'baseLanguage': baseLanguage});
    return _list(body).map(LessonModel.fromJson).toList()..sort((a, b) => a.orderIndex.compareTo(b.orderIndex));
  }

  /// `GET /api/v1/learning/progress/{id}`.
  Future<CourseProgress> progress(String courseId) async =>
      CourseProgress.fromJson(_object(await _get('/api/v1/learning/progress/$courseId')));

  Future<Object?> _get(String path, [Map<String, dynamic>? query]) async {
    try {
      return (await _dio.get<Object?>(path, queryParameters: query)).data;
    } on DioException catch (error) {
      throw ApiException.fromDio(error);
    }
  }

  List<Map<String, dynamic>> _list(Object? body) {
    if (body is! List) throw const ApiException(message: 'Máy chủ trả về dữ liệu không đúng định dạng.');
    return body.whereType<Map<String, dynamic>>().toList();
  }

  Map<String, dynamic> _object(Object? body) {
    if (body is! Map<String, dynamic>) throw const ApiException(message: 'Máy chủ trả về dữ liệu không đúng định dạng.');
    return body;
  }
}
