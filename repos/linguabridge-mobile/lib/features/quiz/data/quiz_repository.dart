import 'package:dio/dio.dart';

import '../../../core/network/api_exception.dart';
import 'quiz_models.dart';

/// The Learning endpoints of LinguaBridge.Api that run a lesson. Every failure is an [ApiException].
class QuizRepository {
  QuizRepository(this._dio);

  final Dio _dio;

  /// `GET /api/v1/learning/lessons/{id}/quiz`: the questions, without their answers.
  Future<Quiz> quiz(String lessonId, {required String baseLanguage}) async =>
      Quiz.fromJson(_object(await _send('GET', '/api/v1/learning/lessons/$lessonId/quiz', query: {'baseLanguage': baseLanguage})));

  /// `POST /api/v1/learning/attempts`: opens a new attempt (every call is a fresh one); returns its id.
  Future<String> startAttempt(String lessonId) async {
    final body = _object(await _send('POST', '/api/v1/learning/attempts', data: {'lessonId': lessonId}));
    final id = body['attemptId'];
    if (id is! String) throw const ApiException(message: 'Máy chủ trả về dữ liệu không đúng định dạng.');
    return id;
  }

  /// `POST /api/v1/learning/attempts/{id}/responses`: grades one answer. One answer per question per attempt: answering
  /// again fails with the code `response.already_answered`.
  Future<AnswerResult> submitResponse(String attemptId, QuizAnswer answer, {required String baseLanguage}) async => AnswerResult.fromJson(
        _object(await _send('POST', '/api/v1/learning/attempts/$attemptId/responses', query: {'baseLanguage': baseLanguage}, data: answer.toJson())),
      );

  /// `PUT /api/v1/learning/attempts/{id}/complete`: closes the attempt and scores it (every question must be answered).
  Future<AttemptResult> completeAttempt(String attemptId) async =>
      AttemptResult.fromJson(_object(await _send('PUT', '/api/v1/learning/attempts/$attemptId/complete')));

  Future<Object?> _send(String method, String path, {Map<String, dynamic>? query, Object? data}) async {
    try {
      final response = await _dio.request<Object?>(path, data: data, queryParameters: query, options: Options(method: method));
      return response.data;
    } on DioException catch (error) {
      throw ApiException.fromDio(error);
    }
  }

  Map<String, dynamic> _object(Object? body) {
    if (body is! Map<String, dynamic>) throw const ApiException(message: 'Máy chủ trả về dữ liệu không đúng định dạng.');
    return body;
  }
}
