# TASK-013: Module Content & CMS — Khóa Học, Bài Học, Câu Hỏi & Đa Ngôn Ngữ

- **Mã Task**: TASK-013
- **Người Giao (Tech Lead)**: Gemini
- **Người Thực Hiện (Implementer)**: Claude Code
- **Trạng Thái**: `PENDING`
- **Ngày Tạo**: 2026-10-28
- **Phụ Thuộc**: TASK-012 (Auth đã ổn định)
- **Tài Liệu Tham Chiếu**: [docs/architecture/language_learning_platform_design.md](../architecture/language_learning_platform_design.md) (Mục 3.1, Mục 5 Content Lifecycle)

---

## 1. Mục Tiêu Kỹ Thuật

Xây dựng lớp **Application + API** cho Module Content, phục vụ:
1. **CRUD Khóa học (Course)**: Admin tạo/sửa/xoá course với vòng đời `Draft → Review → Published → Archived`.
2. **CRUD Bài học (Lesson)** và **Câu hỏi (Question/Options)** kèm dịch thuật đa ngôn ngữ (`CourseTranslation`, `LessonTranslation`, `QuestionTranslation`).
3. **Content Lifecycle**: Chỉ bài học `Published` mới hiển thị cho học viên; Admin thấy tất cả trạng thái.
4. **API Endpoints** RESTful tại `/api/content/` (bảo vệ bởi `[Authorize]`, phân quyền Admin/Learner).
5. **Preview mode**: Endpoint riêng để Admin xem trước bài học `Draft` trước khi Publish.

---

## 2. Phạm Vi File Cấp Phép (File Scope & Whitelist)

> Claude Code **CHỈ ĐƯỢC PHÉP** tạo hoặc sửa các file trong phạm vi sau:

- [ ] `repos/linguabridge/src/Modules/LinguaBridge.Modules.Content/*` (Use Cases, DTOs, Commands, Queries, Validators)
- [ ] `repos/linguabridge/src/Core/LinguaBridge.Application/*` (thêm abstractions nếu cần)
- [ ] `repos/linguabridge/src/Infrastructure/LinguaBridge.Infrastructure.Persistence/*` (Repositories, Query services)
- [ ] `repos/linguabridge/src/Presentation/LinguaBridge.Api/*` (Endpoint mapping cho Content module)
- [ ] `repos/linguabridge/tests/LinguaBridge.UnitTests/*`
- [ ] `repos/linguabridge/tests/LinguaBridge.IntegrationTests/*`

---

## 3. Chi Tiết Yêu Cầu Kỹ Thuật

### 3.1. Use Cases (Application Layer — trong Module.Content)

**Commands (Write)**:
- `CreateCourseCommand` → tạo Course mới với trạng thái `Draft`.
- `UpdateCourseCommand` → sửa metadata của Course.
- `PublishCourseCommand` → chuyển trạng thái `Draft/Review → Published`. Guard: phải có ít nhất 1 Lesson Published.
- `ArchiveCourseCommand` → chuyển sang `Archived`.
- `AddCourseTranslationCommand` / `UpdateCourseTranslationCommand`.
- `CreateLessonCommand`, `PublishLessonCommand`, `AddLessonTranslationCommand`.
- `AddQuestionCommand`, `AddQuestionOptionCommand`.

**Queries (Read)**:
- `GetPublishedCoursesQuery(BaseLanguage, TargetLanguage)` → danh sách khóa học Published (cho Learner).
- `GetAllCoursesQuery(TargetLanguage?)` → tất cả trạng thái (cho Admin).
- `GetCourseDetailQuery(CourseId, BaseLanguage)` → chi tiết kèm bản dịch.
- `GetLessonsQuery(CourseId, BaseLanguage, includeUnpublished: bool)`.
- `PreviewLessonQuery(LessonId, BaseLanguage)` → preview Draft (Admin only).

### 3.2. API Endpoints

Tại `LinguaBridge.Api`, map các endpoint dưới prefix `/api/content`:

| Method | Path | Auth | Mô tả |
|---|---|---|---|
| `GET` | `/api/content/courses` | Bearer | Danh sách courses Published |
| `GET` | `/api/content/courses/admin` | Bearer + `Admin` policy | Tất cả courses |
| `POST` | `/api/content/courses` | Bearer + `Admin` | Tạo course mới |
| `PUT` | `/api/content/courses/{id}/publish` | Bearer + `Admin` | Publish course |
| `GET` | `/api/content/courses/{id}/lessons` | Bearer | Lessons của course |
| `POST` | `/api/content/lessons/{id}/publish` | Bearer + `Admin` | Publish lesson |
| `GET` | `/api/content/lessons/{id}/preview` | Bearer + `Admin` | Preview lesson Draft |

### 3.3. Phân Quyền

- Policy `Admin`: `ICurrentUser.IsInRole("GlobalAdmin")` (role claim do STS phát trong access token). **Không** dùng entitlement để cấp quyền quản trị — Vip là quyền lợi học viên, do Billing sở hữu (`billing.user_entitlements`), không nằm trong token. _(Tech Lead sửa sau review TASK-011, TASK-012.)_
- Learner chỉ thấy `Published` content.
- Dùng `ICurrentUser` (từ TASK-012) để phân biệt Admin vs Learner trong query.

### 3.4. Validation

Dùng `FluentValidation` cho các Command:
- `CreateCourseCommand`: `Code` không rỗng, unique; `TargetLanguage` phải là `en` hoặc `zh`.
- `PublishCourseCommand`: Course phải có ít nhất 1 Lesson.

---

## 4. Tiêu Chí Nghiệm Thu (Acceptance Criteria)

- [ ] CRUD Course, Lesson, Question qua API hoạt động đúng.
- [ ] Content Lifecycle: Learner chỉ thấy Published, Admin thấy tất cả.
- [ ] `PublishCourseCommand` bị từ chối nếu không có Lesson Published.
- [ ] Unit tests cho tất cả Commands và Queries đạt PASS.
- [ ] Integration tests: gọi API với token giả → kiểm tra response code và body.
- [ ] `dotnet test repos/linguabridge/LinguaBridge.sln` — toàn bộ PASS, không regression.

---

## 5. Báo Cáo Triển Khai (Claude Code điền khi hoàn thành)

### a. Danh sách file đã tác động
### b. Kiểm thử thực tế
### c. Rủi ro / lưu ý cho Tech Lead

