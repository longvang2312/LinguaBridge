# TASK-014: Module Learning & SRS — Tiến Độ Học Tập & Thuật Toán SM-2

- **Mã Task**: TASK-014
- **Người Giao (Tech Lead)**: Gemini
- **Người Thực Hiện (Implementer)**: Claude Code
- **Trạng Thái**: `PENDING`
- **Ngày Tạo**: 2026-10-28
- **Phụ Thuộc**: TASK-013 (Content module đã hoàn thành)
- **Tài Liệu Tham Chiếu**: [docs/architecture/language_learning_platform_design.md](../architecture/language_learning_platform_design.md) (Mục 3.2, 3.3, Definition of Done)

---

## 1. Mục Tiêu Kỹ Thuật

Xây dựng lớp **Application + API** cho Module Learning, gồm:
1. **Exercise Session**: Bắt đầu bài học (`ExerciseAttempt`), ghi nhận từng câu trả lời (`QuestionResponse`), tính điểm và XP khi kết thúc.
2. **User Progress Tracker**: Cập nhật `UserCourseProgress` sau mỗi lần học bài hoàn thành.
3. **SRS Engine (SM-2)**: Sau mỗi câu trả lời, cập nhật `UserVocabularyProgress` theo thuật toán SM-2 chuẩn và ghi `ReviewLog`. Chỉ thẻ có `NextReviewDate <= today` xuất hiện trong session ôn tập.
4. **Review Session API**: Lấy danh sách thẻ cần ôn hôm nay, submit đánh giá (1-Again, 2-Hard, 3-Good, 4-Easy), nhận lịch ôn tiếp theo.

---

## 2. Phạm Vi File Cấp Phép (File Scope & Whitelist)

- [ ] `repos/linguabridge/src/Modules/LinguaBridge.Modules.Learning/*` (Use Cases, SM-2 Engine, DTOs)
- [ ] `repos/linguabridge/src/Infrastructure/LinguaBridge.Infrastructure.Persistence/*`
- [ ] `repos/linguabridge/src/Presentation/LinguaBridge.Api/*` (Endpoint mapping)
- [ ] `repos/linguabridge/tests/LinguaBridge.UnitTests/*`
- [ ] `repos/linguabridge/tests/LinguaBridge.IntegrationTests/*`

---

## 3. Chi Tiết Yêu Cầu Kỹ Thuật

### 3.1. SM-2 Algorithm (Pure Domain Logic)

Triển khai thuật toán SM-2 tiêu chuẩn dưới dạng **pure function** (không phụ thuộc infrastructure) trong `LinguaBridge.Modules.Learning`:

```
Input: (EaseFactor, IntervalDays, Repetitions, Rating 1-4)
Output: (NewEaseFactor, NewIntervalDays, NewRepetitions, NextReviewDate)

- Rating 1 (Again): Repetitions = 0, IntervalDays = 1, EaseFactor giữ nguyên hoặc giảm 0.2 (min 1.3)
- Rating 2 (Hard):  EaseFactor -= 0.15 (min 1.3), IntervalDays *= 1.2
- Rating 3 (Good):  EaseFactor giữ nguyên, IntervalDays = prev * EaseFactor (lần đầu: 1, 6)
- Rating 4 (Easy):  EaseFactor += 0.15, IntervalDays = prev * EaseFactor * 1.3
```

**Yêu cầu test**: SM-2 Engine phải có ít nhất **20 unit tests** bao gồm: rating lần đầu, sau nhiều lần đúng, rating "Again" reset, giới hạn EaseFactor min 1.3, chuyển State từ `Learning → Review → Graduated`.

### 3.2. API Endpoints (Quy ước thống nhất tiền tố `/api/v1/learning/...`)

| Method | Path | Mô tả |
|---|---|---|
| `GET` | `/api/v1/learning/lessons/{lessonId}/quiz` | Lấy danh sách câu hỏi làm bài (DTO **tuyệt đối không chứa `isCorrect`**) |
| `POST` | `/api/v1/learning/attempts` | Bắt đầu exercise attempt cho một Lesson (Lesson phải `Published`) |
| `POST` | `/api/v1/learning/attempts/{id}/responses` | Submit câu trả lời (server tự đối chiếu đáp án đúng/sai, trả kết quả tức thì) |
| `PUT` | `/api/v1/learning/attempts/{id}/complete` | Kết thúc bài học, tính điểm, ghi nhận XP và cập nhật `UserCourseProgress` |
| `GET` | `/api/v1/learning/srs/due` | Lấy danh sách thẻ từ vựng cần ôn hôm nay (`NextReviewDate <= today`) |
| `POST` | `/api/v1/learning/srs/review` | Submit đánh giá thẻ SRS (Rating 1-4) |
| `GET` | `/api/v1/learning/progress/{courseId}` | Tiến độ học khóa học của học viên hiện tại |

> **Lưu ý bảo mật (Security note)**: Endpoint lấy câu hỏi cho học viên (`/quiz`) phải dùng DTO riêng (`LearnerQuestionDto`), giấu toàn bộ trường `isCorrect` của các options để ngăn chặn học viên xem trước đáp án qua DevTools. Việc chấm điểm đúng/sai được thực hiện nghiêm ngặt ở server khi nhận `POST /responses`.

### 3.3. Acceptance Criteria Từ Tài Liệu Thiết Kế

- Chỉ các thẻ có `NextReviewDate <= CURRENT_DATE` mới xuất hiện trong review session.
- Sau khi submit đánh giá, `NextReviewDate` được cập nhật chính xác theo SM-2.
- `ScorePercentage` trong `ExerciseAttempt` tính đúng tỉ lệ câu đúng.
- `UserCourseProgress.CompletedLessonsCount` tăng khi hoàn thành bài học lần đầu.

---

## 4. Tiêu Chí Nghiệm Thu (Acceptance Criteria)

- [ ] SM-2 Engine có ≥ 20 unit tests PASS với mọi nhánh logic.
- [ ] SRS chỉ trả thẻ có `NextReviewDate <= today`.
- [ ] Submit rating → `UserVocabularyProgress` và `ReviewLog` được cập nhật chính xác.
- [ ] `UserCourseProgress` được cập nhật sau khi hoàn thành bài.
- [ ] Tất cả tests PASS, 0 regression.

---

## 5. Báo Cáo Triển Khai (Claude Code điền khi hoàn thành)

### a. Danh sách file đã tác động
### b. Kiểm thử thực tế
### c. Rủi ro / lưu ý cho Tech Lead

