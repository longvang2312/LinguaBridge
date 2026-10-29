# TASK-013: Module Content & CMS — Khóa Học, Bài Học, Câu Hỏi & Đa Ngôn Ngữ

- **Mã Task**: TASK-013
- **Người Giao (Tech Lead)**: Gemini
- **Người Thực Hiện (Implementer)**: Claude Code
- **Trạng Thái**: `APPROVED`
- **Ngày Tạo**: 2026-10-28
- **Ngày Hoàn Thành**: 2026-10-29
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

- [x] CRUD Course, Lesson, Question qua API hoạt động đúng.
- [x] Content Lifecycle: Learner chỉ thấy Published, Admin thấy tất cả.
- [x] `PublishCourseCommand` bị từ chối nếu không có Lesson Published.
- [x] Unit tests cho tất cả Commands và Queries đạt PASS (213 unit tests PASS).
- [x] Integration tests: gọi API với token giả → kiểm tra response code và body (63 integration tests PASS).
- [x] `dotnet test repos/linguabridge/LinguaBridge.sln` — toàn bộ PASS, không regression (276/276 tests PASS).

---

## 5. Báo Cáo Triển Khai (Claude Code điền khi hoàn thành)

### a. Danh sách file đã tác động

**Application (`LinguaBridge.Application/Abstractions/`)** — tạo `Result.cs` (`Result`, `Result<T>`, `Error`, `ErrorType`: Validation/NotFound/Conflict), `UniqueConstraintViolationException.cs`.

**Module Content (`LinguaBridge.Modules.Content/`)**
- Sửa `Entities/Course.cs` (+ `UpdateDetails`, `SubmitForReview`, `Publish`, `Archive`, `IsArchived`), `Entities/Lesson.cs` (+ `Publish`, `Archive`), `ContentModule.cs` (đăng ký handler + validator), `.csproj` (+ `FluentValidation` 12.*, `FluentValidation.DependencyInjectionExtensions` 12.*).
- Tạo `Abstractions/` (`IContentRepository`, `IContentReadModel`, `Dtos`), `Commands/` (`CourseCommands`, `LessonCommands`, `QuestionCommands`, `Validators`, `CommandSupport`), `Queries/ContentQueries.cs`, `ContentParsing.cs`, `TranslationResolver.cs`.
- Module vẫn chỉ phụ thuộc `Domain` + `Application` (`ArchitectureTests` PASS); không biết gì về EF Core.

**Persistence** — sửa `DependencyInjection.cs` (đăng ký repository/read model; `PersistenceOptions` đọc cấu hình lười — xem rủi ro 7); tạo `Content/ContentRepository.cs`, `Content/ContentReadModel.cs`.

**Api** — sửa `Program.cs` (+ `MapContentEndpoints`), `Auth/AuthenticationExtensions.cs` (+ policy `Admin`); tạo `Auth/AdminPolicy.cs`, `Endpoints/ContentEndpoints.cs`, `Endpoints/ResultHttpExtensions.cs`.

**Tests** — UnitTests: tạo `Content/{FakeContent, CourseCommandTests, LessonAndQuestionCommandTests, ContentQueryAndSupportTests}.cs`; sửa `ModuleRegistrationTests.cs` (xem rủi ro 8). IntegrationTests: tạo `ContentApiFixture.cs`, `ContentApiTests.cs`.

Không file nào ngoài whitelist bị sửa (đối chiếu `git status`).

### b. Kiểm thử thực tế
```bash
dotnet build repos/linguabridge/LinguaBridge.sln --no-incremental   # 0 Warning(s)  0 Error(s)
dotnet build AppInHouse.sln                                         # 0 Warning(s)  0 Error(s)

dotnet test repos/linguabridge/LinguaBridge.sln          # Docker Desktop 29.9.2 đang chạy
#   Passed!  - Failed: 0, Passed: 213, Total: 213 - LinguaBridge.UnitTests.dll        (119 cũ + 94 mới)
#   Passed!  - Failed: 0, Passed:  63, Total:  63 - LinguaBridge.IntegrationTests.dll (34 cũ + 29 mới)
```
Tổng LinguaBridge: **276** (153 nền + 123 mới).

**Unit (94 mới, dùng repository/read model giả trong bộ nhớ):** mọi Command và Query — tạo course (Draft, v1, trim/không phân biệt hoa thường, mọi trường hợp sai: code rỗng/quá dài, `fr`, level sai/khác ngôn ngữ, code trùng, thua race ở unique index); sửa course; publish (không có lesson → từ chối; chỉ lesson Draft hoặc lesson của course khác → từ chối; có lesson Published → OK; từ Review → OK; từ Published/Archived → từ chối); archive; translation add/update (trùng, thiếu, course Archived); lesson (thêm cuối đúng theo từng course, vị trí tường minh, course Archived, publish 1 lần); question (translation kèm theo, trùng ngôn ngữ, loại sai); option (thứ tự, cờ đúng); Query chuyển đúng tham số và đổi "không thấy" thành NotFound; parser mã ngôn ngữ/level; `TranslationResolver`; chuyển trạng thái của `Course`/`Lesson`.

**Integration (29 mới) — host thật + PostgreSQL 17 trong Testcontainers đã `MigrateAsync()` + token giả `X-Test-User`/`X-Test-Roles`:**
- Ẩn danh → 401 ở mọi nơi. Learner → **403 ở cả 13 endpoint admin**. Role `Vip`, `ContentAdmin`, `globaladmin` (sai hoa/thường) → 403: chỉ đúng `GlobalAdmin` mới quản trị.
- Tạo course → 201 + `Location` + `id`; input sai → 400 `application/problem+json` có `errors.Code`/`errors.TargetLanguage` và `code`; trùng code → 409; **8 request song song cùng code → đúng 1×201 và 7×409, DB chỉ có 1 course** (unique index, không 500).
- Vòng đời: publish course khi chưa có lesson / chỉ lesson Draft → 409 `course.no_published_lesson`; publish lesson → publish course 204; publish lại → 409; id không tồn tại → 404 problem.
- Hiển thị: Learner chỉ thấy Published (lọc theo `targetLanguage`), Admin thấy tất cả; course Draft với Learner là **404 chứ không phải 403** (không lộ sự tồn tại); lesson lọc theo status và sắp theo `orderIndex`; `?includeUnpublished=true` do Learner gửi **không có tác dụng** (quyền xem lấy từ role, không từ request); lesson thêm sau khi publish course vẫn ẩn tới khi publish; Archive → course biến mất với Learner và bị khóa với Admin.
- Preview (Admin): lesson Draft kèm question, option, đáp án đúng, thứ tự. Câu hỏi nối đuôi đúng thứ tự.
- Đa ngôn ngữ: yêu cầu `vi` → fallback `en` khi thiếu → trả `translationLanguage` thực tế; add trùng → 409; update ngôn ngữ chưa có → 404; ngôn ngữ lạ → 400.
- **Kiểm chứng đột biến:** (1) cho endpoint Learner luôn `IncludeUnpublished: true` → 5 test hiển thị FAIL (cùng `A_learner_cannot_widen_the_view…`); (2) bỏ ánh xạ unique-violation → `Concurrent_creates_of_one_code_produce_exactly_one_course` FAIL. Đã hoàn tác cả hai.

### c. Rủi ro / lưu ý cho Tech Lead
1. **Learner chưa thể đọc câu hỏi.** Task chỉ có `GET .../lessons` (danh sách) và `preview` (Admin). Không có endpoint nào trả question/option của lesson Published cho học viên — việc đó thuộc module Learning (làm bài) và cần một query/endpoint riêng (và **không** được trả `isCorrect` cho Learner). Chưa làm vì ngoài phạm vi; cần task tiếp theo.
2. **Endpoint nhiều hơn bảng ở mục 3.2.** Bảng chỉ có 7 endpoint nhưng mục 3.1 liệt kê 11 Command và nghiệm thu yêu cầu "CRUD qua API", nên tôi map đủ mọi Command + `GET /courses/{id}` (GetCourseDetailQuery): thêm `PUT /courses/{id}`, `PUT .../archive`, `POST|PUT .../translations`, `POST .../lessons`, `POST /lessons/{id}/translations`, `POST /lessons/{id}/questions`, `POST /questions/{id}/options`. Giữ nguyên động từ của bảng (publish course = `PUT`, publish lesson = `POST`). Prefix là `/api/content` đúng spec — **không có `v1`** như `/api/v1/me`; cần chốt một quy ước versioning.
3. **Không có xóa (DELETE).** Mục 1 nói "tạo/sửa/xoá" nhưng 3.1 không có command xóa, chỉ có `ArchiveCourseCommand`. Tôi coi Archive là cách "xóa mềm" (Archived là trạng thái cuối); xóa cứng course đã có tiến độ học sẽ làm vỡ dữ liệu module Learning.
4. **Trạng thái `Review` chỉ có ở domain.** `Course.SubmitForReview()` có và `PublishCourse` chấp nhận Draft/Review, nhưng spec không có command/endpoint nào chuyển sang Review nên hiện không với tới được qua API. Lesson cũng chưa có Archive command (domain có `Archive()`).
5. **Điều kiện publish course chặt hơn mục 3.4.** 3.4 ghi "phải có ít nhất 1 Lesson", mục 3.1 và nghiệm thu ghi "ít nhất 1 Lesson **Published**". Tôi theo bản chặt hơn (lesson Draft không đủ) vì course công khai mà học viên không thấy lesson nào là vô nghĩa. Trả 409 `course.no_published_lesson`.
6. **Các quy tắc spec không nêu, tôi chưa tự đặt:** publish lesson **không** yêu cầu có câu hỏi; không bắt MultipleChoice phải có đáp án đúng; sửa/thêm question vào lesson đã Published có hiệu lực ngay với học viên; không có ràng buộc duy nhất cho `OrderIndex` (thêm cuối = max+1, hai request song song có thể trùng số thứ tự — vô hại về dữ liệu nhưng thứ tự giữa hai mục đó không xác định); `Course.Version`/`Lesson.Version` không bao giờ tăng. Cần Tech Lead/PO quyết định.
7. **`PersistenceOptions` giờ đọc chuỗi kết nối lười** (`DependencyInjection.cs`). Trước đây đọc ngay lúc đăng ký nên cấu hình thêm bởi host/test (`ConfigureAppConfiguration`) không có tác dụng — cùng nguyên nhân với lỗi fail-fast ở TASK-012. Cần để fixture trỏ host tới container Postgres. Hành vi production không đổi.
8. **Sửa 1 test cũ:** `ModuleRegistrationTests.All_modules_register_and_container_validates` giờ cũng gọi `AddInfrastructurePersistence`, vì module Content không còn tự đủ — repository/read model do Persistence cung cấp (`ValidateOnBuild` nếu không sẽ fail). Đây là hệ quả trực tiếp của thiết kế (module không biết EF), không phải nới lỏng test.
9. **Lỗi tìm thấy và đã sửa trong lúc làm:** `Enum.TryParse` chấp nhận chuỗi kiểu `"A1,B1"` (cộng giá trị số), nên parser level đầu tiên của tôi nhận `A1,B1` là level hợp lệ. Đã chuyển sang so khớp đúng tên member (`ContentParsing`), có test. **Lỗi tương tự còn ở** `Persistence/Converters/LanguageConverters.cs` (`LanguageCodeParser.ParseLevel`, code TASK-010) — chỉ đọc giá trị từ DB do chính app ghi nên rủi ro thấp, tôi **không sửa** vì ngoài phạm vi task này.
10. **Test API cần Docker** (`[Trait("Category", "Integration")]`, như `PostgresMigrationTests`); khi không có Docker, thêm `--filter "Category!=Integration"`. Auth ở test là `TestAuthHandler` (header giả), **chưa chạy với token thật từ STS** — claim `role = GlobalAdmin` do STS phát vào access token chưa được kiểm chứng end-to-end (như rủi ro 6 của TASK-012), và scope `linguabridge_api` vẫn chưa cấp cho client nào nên chưa lấy được token thật để thử.
11. **Phân trang chưa có:** `GET /courses` và `/courses/admin` trả toàn bộ danh sách. Chấp nhận cho v1 (số course nhỏ); cần phân trang khi catalog lớn.


---

## 6. Kết Quả Code Review (Claude Code — Reviewer)

- **Kết luận**: `REVIEW_PASSED` — không có 🔴 Blocker. 1 🟡 Major cần Gemini chọn: sửa ngay hay đưa vào backlog (lý do bên dưới). 4 🔵 Minor, Coder có thể sửa trong vòng sau.
- **Ngày review**: 2026-10-29 — **Lần review**: 1

### Xác minh độc lập
```bash
dotnet build repos/linguabridge/LinguaBridge.sln --no-incremental   # 0 Warning(s), 0 Error(s)
dotnet test  repos/linguabridge/LinguaBridge.sln --no-build          # Unit 213/213, Integration 63/63 PASS (Docker 29.9.2)
dotnet build AppInHouse.sln                                          # 0 Warning(s), 0 Error(s)

# Probe trong scratchpad (ngoài repo): AddQuestionCommandValidator với Translations = [null]
#   THROWS NullReferenceException  → endpoint trả 500 (finding #2)
```
- **Phạm vi file**: đúng whitelist (`git status`). Sửa test cũ `ModuleRegistrationTests` nằm trong `UnitTests/*`, có lý do (5.c.8) → chấp nhận.
- **Kiến trúc**: module Content không tham chiếu EF Core (`ArchitectureTests` PASS); repository/read model đặt ở Persistence; `UniqueConstraintViolationException` giữ module độc lập với Npgsql.

### Điểm tốt
- Quyền xem (`IncludeUnpublished`) **lấy từ role, không từ request**; có mutation test chứng minh learner không mở rộng được phạm vi xem.
- Nội dung chưa publish trả **404 chứ không phải 403** (không lộ việc nó tồn tại).
- Race tạo course trùng code được xử lý đúng ở unique index: 8 request song song → đúng 1×201 và 7×409 (mutation test).
- `ContentParsing` tránh bẫy `Enum.TryParse("A1,B1")`, và Coder còn chỉ ra lỗi tương tự trong code cũ.
- Sửa `PersistenceOptions` thành đọc lười: đúng nguyên nhân gốc, cùng loại lỗi với fail-fast ở TASK-012.

### Findings
| # | Mức độ | File:dòng | Vấn đề | Đề xuất |
|---|---|---|---|---|
| 1 | 🟡 Major → Gemini quyết | `Commands/CourseCommands.cs` (Publish/Archive/Update handler), `Commands/LessonCommands.cs:51` | **Lost update trên trạng thái**: không có optimistic concurrency. Ví dụ: `PublishCourse` đọc course `Draft` → `ArchiveCourse` commit `Archived` → Publish ghi `Status = Published`. Course đã Archived ("trạng thái cuối") **hiện lại cho học viên**. Tương tự: publish lesson sau khi course vừa bị archive; `UpdateCourse` ghi đè course đã archive. | Không chặn vì đặc tả không có yêu cầu concurrency, endpoint chỉ dành cho admin và ít tranh chấp. Đề xuất: concurrency token (`xmin` của Postgres qua `UseXminAsConcurrencyToken`, hoặc dùng `Course.Version` đang có sẵn nhưng chưa dùng) → map `DbUpdateConcurrencyException` thành 409. Cần migration. |
| 2 | 🔵 Minor | `Commands/Validators.cs:111-114` | `"translations": [null]` → `NullReferenceException` trong rule kiểm trùng (`t.BaseLanguage` với `t == null`) → **500** thay vì 400. Đã tái hiện. Handler (`QuestionCommands.cs:47`) cũng sẽ NRE nếu đi qua được validator. | Thêm `RuleForEach(c => c.Translations).NotNull()` và dùng `t?.BaseLanguage` trong rule kiểm trùng; thêm test. |
| 3 | 🔵 Minor | `Commands/LessonCommands.cs:67`, `Commands/QuestionCommands.cs:33,63` | Không nhất quán: `CreateLesson`/`PublishLesson` chặn course Archived, nhưng `AddLessonTranslation`, `AddQuestion`, `AddQuestionOption` vẫn sửa được nội dung của course Archived. | Cùng kiểm tra `course.IsArchived` → 409 `course.archived`, hoặc Gemini chốt rằng nội dung con của course Archived vẫn được sửa. |
| 4 | 🔵 Minor | `Endpoints/ContentEndpoints.cs:203` | `Location` của `POST /questions/{id}/options` trỏ tới `/api/content/questions/{qid}/options/{id}`, endpoint này không tồn tại. | Trỏ về `/lessons/{lessonId}/preview` như `AddQuestion`, hoặc không trả `Location` (`200` + id). |
| 5 | 🔵 Minor | `Content/ContentRepository.cs:11` + unique index `ix_courses_code` | Mã course phân biệt hoa thường: `EN-A1` và `en-a1` cùng tồn tại được. | Gemini chốt quy ước. Nếu không phân biệt: chuẩn hóa `ToUpperInvariant()` trong `Course` hoặc unique index trên `lower(code)`. |

### Cần Gemini quyết định
1. **Finding #1**: sửa ngay trong TASK-013 (vòng 2) hay mở task "optimistic concurrency cho Content". Khuyến nghị: làm **trước khi** có admin thật dùng CMS, và quyết luôn vai trò của `Course.Version`/`Lesson.Version` (5.c.6): concurrency token hay version nội dung.
2. **Các khoảng trống đặc tả Coder nêu ở 5.c** — Reviewer đồng ý đây là quyết định của Tech Lead/PO, không phải lỗi:
   - (5.c.1) Chưa có endpoint cho learner đọc question của lesson Published, và endpoint đó **không được** trả `isCorrect`. Lưu ý: `QuestionOptionDto` hiện có `IsCorrect`, nên endpoint learner phải dùng DTO riêng chứ không tái sử dụng.
   - (5.c.2) Quy ước versioning URL: `/api/content/...` không có `v1`, còn `/api/v1/me` thì có.
   - (5.c.3, 5.c.4) Không có DELETE (Archive thay cho xóa mềm); trạng thái `Review` chưa với tới được qua API.
   - (5.c.6) Các quy tắc nghiệp vụ chưa có: publish lesson không cần câu hỏi, MultipleChoice không bắt buộc có đáp án đúng, sửa lesson đã Published có hiệu lực ngay.
   - (5.c.11) Chưa phân trang.
3. **Phụ thuộc chưa đóng từ TASK-012**: claim `role = GlobalAdmin` vẫn chưa được kiểm với token thật từ STS (chưa client nào có scope `linguabridge_api`). Toàn bộ phân quyền admin của TASK-013 dựa vào claim này.
4. **Lỗi cũ** ở `Persistence/Converters/LanguageConverters.cs` (`ParseLevel` dùng `Enum.TryParse`): đưa vào backlog.


---

## 7. Quyết Định Kiến Trúc & Nghiệm Thu Cuối Cùng (Gemini Tech Lead)

- **Kết luận**: **`APPROVED`**
- **Đánh giá tổng quan**:
  - Triển khai xuất sắc, tuân thủ nghiêm ngặt ranh giới kiến trúc (Module Content không dính líu EF Core).
  - Bảo mật tốt: quyền xem draft dựa hoàn toàn vào Principal/Role ở server, không tin cậy request param; draft trả 404 chống rò rỉ metadata.
  - Xử lý race condition tạo trùng mã course hoàn hảo (8 requests song song → đúng 1 thành công).
  - Bộ test rất chất lượng (213 unit + 63 integration tests chạy trên PostgreSQL container thật).

### Quyết định chính thức của Tech Lead về các phát hiện của Reviewer:

1. **Về Finding 🟡 Major (Lost Update / Concurrency trên Course Status)**:
   - **Quyết định**: **Chuyển vào Backlog** cho task Tối ưu hóa / Concurrency Control. Không chặn TASK-013 vì hiện tại chỉ có admin thao tác nội bộ với tần suất tranh chấp cực thấp.
   - **Định vị kiến trúc của `Course.Version`**:
     - `Course.Version` (và `Lesson.Version`) là **Content Release Version** (nghiệp vụ phiên bản khóa học/bài học: v1, v2 khi có thay đổi lớn nội dung để bảo vệ tiến độ học tập của học viên cũ).
     - Đối với **Concurrency Control (chống Lost Update)**: Sử dụng cơ chế tối ưu của PostgreSQL là `xmin` (`UseXminAsConcurrencyToken()`) trong EF Core, tách bạch rõ ràng giữa phiên bản nghiệp vụ và kỹ thuật khóa dữ liệu.
2. **Về các Finding 🔵 Minor**:
   - **Minor 1 (`translations: [null]`)**: Ghi nhận backlog để bổ sung rule `NotNull()` cho validator.
   - **Minor 2 (Thao tác trên Course đã Archived)**: **Quy tắc nghiệp vụ**: Khi Course đã `Archived`, toàn bộ nội dung con (Lessons, Questions, Translations) bị **đóng băng (Frozen / Read-Only)**. Sẽ bổ sung kiểm tra `course.IsArchived` đồng bộ ở tất cả các command con.
   - **Minor 3 (Location header)**: Sẽ chuẩn hóa trả về URL preview hoặc chỉ trả `201 Created` kèm `id`.
   - **Minor 4 (Mã Course phân biệt hoa thường)**: **Quy tắc nghiệp vụ**: Mã Course (`Code`) là **Case-Insensitive** (ví dụ `EN_CEFR_A1`). Sẽ chuẩn hóa `.ToUpperInvariant()` khi lưu và tìm kiếm.
3. **Về các khoảng trống đặc tả**:
   - **Endpoint lấy câu hỏi cho học viên**: Thuộc phạm vi của **Module Learning (TASK-014)**. Khi học viên bắt đầu bài học, Module Learning sẽ query câu hỏi và trả về `LearnerQuestionDto` (**tuyệt đối giấu `isCorrect`** để chống gian lận).
   - **URL Versioning**: Thống nhất tiền tố `/api/v1/...` trên toàn bộ hệ thống API LinguaBridge (sẽ refactor `/api/content` → `/api/v1/content`).
   - **Không xóa cứng (No hard delete)**: Giữ nguyên quyết định thiết kế — chỉ dùng `Archive` (xóa mềm) để bảo toàn tính toàn vẹn dữ liệu cho tiến độ học viên.
