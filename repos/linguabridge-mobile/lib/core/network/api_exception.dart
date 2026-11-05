import 'package:dio/dio.dart';

/// A failed call to the LinguaBridge API in terms the screens can show.
///
/// The API answers errors as RFC 7807 problem details: `title`, `detail`, `status` and a machine-readable `code`
/// (for example `ai.quota_exhausted`).
class ApiException implements Exception {
  const ApiException({required this.message, this.statusCode, this.code, this.problem});

  /// Something the learner can read.
  final String message;

  /// The HTTP status, or null when no response arrived (offline, timeout, DNS).
  final int? statusCode;
  final String? code;

  /// The whole problem-details body, for extension members such as `resetsAt`.
  final Map<String, dynamic>? problem;

  bool get isNetworkError => statusCode == null;
  bool get isUnauthorized => statusCode == 401;

  factory ApiException.fromDio(DioException error) {
    final existing = error.error;
    if (existing is ApiException) return existing;

    final response = error.response;
    if (response == null) {
      final timedOut = error.type == DioExceptionType.connectionTimeout ||
          error.type == DioExceptionType.sendTimeout ||
          error.type == DioExceptionType.receiveTimeout;
      return ApiException(
        message: timedOut
            ? 'Máy chủ phản hồi quá chậm. Hãy thử lại.'
            : 'Không kết nối được máy chủ. Hãy kiểm tra mạng và API đang chạy.',
      );
    }

    final data = response.data;
    final problem = data is Map ? Map<String, dynamic>.from(data) : null;
    final detail = problem?['detail'] ?? problem?['title'];
    return ApiException(
      message: detail is String && detail.isNotEmpty ? detail : 'Yêu cầu thất bại (${response.statusCode}).',
      statusCode: response.statusCode,
      code: problem?['code'] as String?,
      problem: problem,
    );
  }

  @override
  String toString() => 'ApiException($statusCode${code == null ? '' : ' $code'}: $message)';
}
