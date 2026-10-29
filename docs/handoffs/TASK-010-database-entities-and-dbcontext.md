# TASK-010: Thiết Kế Domain Entities, Multi-Schema DbContext & Cấu Hình PostgreSQL (EF Core)

- **Mã Task**: TASK-010
- **Tên Task**: Thiết kế thực thể Domain, Fluent API Mapping theo 5 schemas (`content`, `learning`, `srs`, `ai`, `billing`) và cấu hình DbContext cho PostgreSQL
- **Người Giao (Tech Lead)**: Gemini
- **Người Thực Hiện (Implementer)**: Claude Code
- **Trạng Thái**: `APPROVED`
- **Ngày Tạo**: 2026-10-28
- **Ngày Hoàn Thành**: 2026-10-28
- **Phụ Thuộc**: Hoàn thành sau TASK-009
- **Tài Liệu Tham Chiếu**: [docs/architecture/language_learning_platform_design.md](../architecture/language_learning_platform_design.md) (Mục 3: Data Architecture)

---

## 1. Mục Tiêu Kỹ Thuật

Dựa trên thiết kế cơ sở dữ liệu chi tiết tại **Mục 3** trong `language_learning_platform_design.md`:
1. Định nghĩa các Domain Entities / Aggregate Roots cho từng module trong `LinguaBridge`.
2. Tổ chức thực thể theo đúng ranh giới của từng Module:
   - Module **Content** (Schema `content`): `Course`, `CourseTranslation`, `Lesson`, `LessonTranslation`, `Question`, `QuestionTranslation`, `QuestionOption`.
   - Module **Learning** (Schema `learning` & Schema `srs`):
     - `learning`: `UserCourseProgress`, `ExerciseAttempt`, `QuestionResponse`.
     - `srs`: `UserVocabularyProgress`, `ReviewLog`.
   - Module **AiTutor** (Schema `ai`): `ChatSession`, `ChatMessage`, `AiUsageLedger`.
   - Module **Billing** (Schema `billing`): `SubscriptionPlan`, `UserEntitlement`, `WebhookInbox`.
3. Triển khai DbContext và cấu hình Fluent API (sử dụng `IEntityTypeConfiguration<T>`):
   - Đảm bảo ánh xạ chuẩn xác tên bảng và tên schema (ví dụ: `builder.ToTable("Courses", "content");`).
   - Cấu hình khóa chính (Primary Key), khóa ngoại (Foreign Keys / OnDelete Cascade), chỉ mục (Indexes) và ràng buộc duy nhất (Unique Constraints) đúng như đặc tả SQL trong tài liệu thiết kế.
4. Tích hợp thư viện EF Core cho PostgreSQL (`Npgsql.EntityFrameworkCore.PostgreSQL` tương thích .NET 10).
5. Đăng ký DbContext vào DI Container trong `LinguaBridge.Infrastructure.Persistence` và cấu hình chuỗi kết nối `LinguaBridgeDb` trong `appsettings.json`.
6. Viết bộ kiểm thử Unit Test (InMemory hoặc Model Metadata Verification) để xác minh:
   - Tất cả các entity được ánh xạ vào đúng schema tương ứng.
   - Các bảng, khóa chính và ràng buộc unique tồn tại hợp lệ trong DbContext model.

---

## 2. Phạm Vi File Cấp Phép (File Scope & Whitelist)

> **QUY TẮC BẮT BUỘC DÀNH CHO CLAUDE CODE**:
> Claude Code **CHỈ ĐƯỢC PHÉP** tạo hoặc sửa các file trong danh sách dưới đây. Tuyệt đối không sửa `AGENTS.md`, `CLAUDE.md`, thư mục `repos/identity/` hoặc các file tài liệu trong `docs/architecture/`.

- [ ] `repos/linguabridge/src/Modules/LinguaBridge.Modules.Content/Entities/*` (hoặc `Domain/*`)
- [ ] `repos/linguabridge/src/Modules/LinguaBridge.Modules.Learning/Entities/*` (hoặc `Domain/*`)
- [ ] `repos/linguabridge/src/Modules/LinguaBridge.Modules.AiTutor/Entities/*` (hoặc `Domain/*`)
- [ ] `repos/linguabridge/src/Modules/LinguaBridge.Modules.Billing/Entities/*` (hoặc `Domain/*`)
- [ ] `repos/linguabridge/src/Infrastructure/LinguaBridge.Infrastructure.Persistence/*` (DbContext, Configurations, DI, `.csproj`)
- [ ] `repos/linguabridge/src/Presentation/LinguaBridge.Api/appsettings.json`
- [ ] `repos/linguabridge/src/Presentation/LinguaBridge.Api/appsettings.Development.json`
- [ ] `repos/linguabridge/tests/LinguaBridge.UnitTests/*`
- [ ] `repos/linguabridge/tests/LinguaBridge.IntegrationTests/*`

---

## 3. Chi Tiết Yêu Cầu Kỹ Thuật

### 3.1. Danh Sách Thực Thể & Schema

#### A. Schema `content` (`LinguaBridge.Modules.Content`)
- **`Course`**: `Id` (Guid), `Code` (string 50, unique), `TargetLanguage` (enum/string), `Level` (enum/string), `Status` (enum/string: Draft, Review, Published, Archived), `Version` (int), `ThumbnailUrl` (string nullable), `CreatedAt`, `UpdatedAt`.
- **`CourseTranslation`**: PK composite `(CourseId, BaseLanguage)`. `CourseId`, `BaseLanguage`, `Title` (string 255), `Description` (text).
- **`Lesson`**: `Id` (Guid), `CourseId` (Guid, FK), `OrderIndex` (int), `LessonType` (string 30), `Status` (string 20), `XpReward` (int), `Version` (int), `CreatedAt`.
- **`LessonTranslation`**: PK composite `(LessonId, BaseLanguage)`. `Title` (string 255), `GuideText` (text).
- **`Question`**: `Id` (Guid), `LessonId` (Guid, FK), `OrderIndex` (int), `QuestionType` (string 30), `AudioPromptUrl` (string nullable), `TargetText` (text), `PinyinOrIpa` (string 255 nullable), `GrammarNotes` (text nullable).
- **`QuestionTranslation`**: PK composite `(QuestionId, BaseLanguage)`. `PromptTranslation` (text), `Explanation` (text nullable).
- **`QuestionOption`**: `Id` (Guid), `QuestionId` (Guid, FK), `OptionText` (text), `PinyinOrIpa` (string 255 nullable), `IsCorrect` (bool), `OrderIndex` (int).

#### B. Schema `learning` & `srs` (`LinguaBridge.Modules.Learning`)
- **`UserCourseProgress`** (schema `learning`): PK composite `(UserId, CourseId)`. `CompletedLessonsCount`, `TotalLessonsCount`, `IsCompleted`, `LastAccessedAt`.
- **`ExerciseAttempt`** (schema `learning`): `Id` (Guid), `UserId` (Guid), `LessonId` (Guid), `ScorePercentage` (int 0-100), `XpEarned` (int), `DurationSeconds` (int), `StartedAt`, `CompletedAt` (nullable).
- **`QuestionResponse`** (schema `learning`): `Id` (Guid), `AttemptId` (Guid, FK), `QuestionId` (Guid), `SelectedOptionId` (Guid nullable), `UserInputText` (text nullable), `IsCorrect` (bool), `TimeSpentMs` (int).
- **`UserVocabularyProgress`** (schema `srs`): `Id` (Guid), `UserId` (Guid), `VocabularyId` (Guid), `TargetLanguage` (string 10), `Repetitions` (int), `EaseFactor` (double/float default 2.5), `IntervalDays` (int), `NextReviewDate` (DateOnly), `LastReviewDate` (DateTimeOffset nullable), `State` (string 20).
  - Ràng buộc: Unique `(UserId, VocabularyId)`.
  - Chỉ mục: Index `(UserId, NextReviewDate)`.
- **`ReviewLog`** (schema `srs`): `Id` (Guid), `UserVocabularyId` (Guid, FK), `Rating` (int: 1-Again, 2-Hard, 3-Good, 4-Easy), `OldEaseFactor` (float), `NewEaseFactor` (float), `OldIntervalDays` (int), `NewIntervalDays` (int), `ReviewedAt`.

#### C. Schema `ai` (`LinguaBridge.Modules.AiTutor`)
- **`ChatSession`**: `Id` (Guid), `UserId` (Guid), `TargetLanguage` (string 10), `ScenarioCode` (string 50), `Title` (string 255), `StartedAt`, `EndedAt` (nullable), `TotalMessagesCount` (int).
- **`ChatMessage`**: `Id` (Guid), `SessionId` (Guid, FK), `SenderType` (string 10: 'User' / 'Assistant'), `Content` (text), `PinyinOrIpa` (text nullable), `Translation` (text nullable), `GrammarCorrection` (text nullable), `CreatedAt`.
- **`AiUsageLedger`**: `Id` (Guid), `UserId` (Guid), `TaskType` (string 30), `ModelProvider` (string 50), `PromptTokens` (int), `CompletionTokens` (int), `TotalTokens` (int), `EstimatedCostUsd` (decimal 10,6), `CreatedAt`.
  - Chỉ mục: Index `(UserId, CreatedAt)`.

#### D. Schema `billing` (`LinguaBridge.Modules.Billing`)
- **`SubscriptionPlan`**: `Id` (string 50, PK), `Name` (string 100), `PriceVnd` (decimal 18,0), `DurationDays` (int), `AiDailyTokenQuota` (int).
- **`UserEntitlement`**: `UserId` (Guid, PK), `Tier` (string 20: 'Free', 'Vip'), `PlanId` (string 50 nullable, FK), `ExpiresAt` (DateTimeOffset nullable), `UpdatedAt`.
- **`WebhookInbox`**: `Id` (Guid), `Provider` (string 50), `EventId` (string 255), `Payload` (string/json), `ProcessedAt` (DateTimeOffset nullable), `Status` (string 20: 'Pending', 'Processed', 'Failed'), `ErrorMessage` (text nullable).
  - Ràng buộc: Unique `(Provider, EventId)`.

### 3.2. Cấu Trúc & Tách Biệt DbContext trong Modular Monolith

- Tại `LinguaBridge.Infrastructure.Persistence`, tạo `LinguaBridgeDbContext : DbContext`:
  - Trong `OnModelCreating`, áp dụng cấu hình của tất cả các entity (có thể dùng `modelBuilder.ApplyConfigurationsFromAssembly(...)`).
  - Định nghĩa rõ tên schema của từng entity tương ứng với 5 schema: `content`, `learning`, `srs`, `ai`, `billing`.
- Cài đặt package `Npgsql.EntityFrameworkCore.PostgreSQL` (.NET 10 compatible) vào `LinguaBridge.Infrastructure.Persistence.csproj`.
- Cập nhật `AddInfrastructurePersistence` để đăng ký `DbContextPool<LinguaBridgeDbContext>` hoặc `AddDbContext<LinguaBridgeDbContext>`.
- Chuỗi kết nối mẫu trong `appsettings.Development.json`:
  ```json
  "ConnectionStrings": {
    "LinguaBridgeDb": "Host=localhost;Port=5432;Database=LinguaBridge;Username=postgres;Password=postgres"
  }
  ```

### 3.3. Unit Test Kiểm Thử Model Metadata

Tạo `DbContextModelTests` trong `LinguaBridge.UnitTests`:
- Kiểm tra toàn bộ 16 thực thể đều có mặt trong `LinguaBridgeDbContext.Model`.
- Kiểm tra các thực thể thuộc đúng schema dự kiến:
  - `content`: Course, CourseTranslation, Lesson, LessonTranslation, Question, QuestionTranslation, QuestionOption.
  - `learning`: UserCourseProgress, ExerciseAttempt, QuestionResponse.
  - `srs`: UserVocabularyProgress, ReviewLog.
  - `ai`: ChatSession, ChatMessage, AiUsageLedger.
  - `billing`: SubscriptionPlan, UserEntitlement, WebhookInbox.
- Kiểm tra các composite keys và unique constraints:
  - `CourseTranslation` có PK `(CourseId, BaseLanguage)`.
  - `UserVocabularyProgress` có Unique `(UserId, VocabularyId)`.
  - `WebhookInbox` có Unique `(Provider, EventId)`.

---

## 4. Tiêu Chí Nghiệm Thu (Acceptance Criteria)

- [x] Toàn bộ 18 Domain Entities được định nghĩa đầy đủ (task ghi 16 nhưng SQL thiết kế thực tế có 7+3+2+3+3=18 — Claude Code đúng khi triển khai đủ 18).
- [x] Cấu hình Fluent API (`IEntityTypeConfiguration`) tách biệt, gán đúng tên bảng và schema (`content`, `learning`, `srs`, `ai`, `billing`).
- [x] `LinguaBridgeDbContext` được cấu hình hoàn chỉnh trong `LinguaBridge.Infrastructure.Persistence`.
- [x] Package `Npgsql.EntityFrameworkCore.PostgreSQL` 10.0.3 / EF Core 10.0.4 tích hợp không phát sinh cảnh báo.
- [x] Bộ unit test `DbContextModelTests` chạy PASS 100%, xác nhận schema, keys, FK và column types.
- [x] Cả 2 solutions build thành công 0 warning, 0 error (đã xác minh trực tiếp: 107/107 tests PASS).
- [x] Không vi phạm File Scope: 57 file thay đổi, tất cả trong whitelist.

---

## 5. Báo Cáo Triển Khai (Claude Code điền khi hoàn thành)

### a. Danh sách file đã tác động

**Entities (tạo mới, trong `repos/linguabridge/src/Modules/`)** — không module nào tham chiếu EF Core:
- `LinguaBridge.Modules.Content/Entities/`: `Course`, `CourseTranslation`, `Lesson`, `LessonTranslation`, `Question`, `QuestionTranslation`, `QuestionOption`, `ContentEnums` (`ContentStatus`, `LessonType`, `QuestionType`), `Guard`
- `LinguaBridge.Modules.Learning/Entities/`: `UserCourseProgress`, `ExerciseAttempt`, `QuestionResponse`, `UserVocabularyProgress`, `ReviewLog`, `LearningEnums` (`SrsState`, `ReviewRating`), `Guard`
- `LinguaBridge.Modules.AiTutor/Entities/`: `ChatSession`, `ChatMessage`, `AiUsageLedger`, `SenderType`, `Guard`
- `LinguaBridge.Modules.Billing/Entities/`: `SubscriptionPlan`, `UserEntitlement`, `WebhookInbox`, `BillingEnums` (`EntitlementTier`, `WebhookStatus`), `Guard`

**Persistence (`repos/linguabridge/src/Infrastructure/LinguaBridge.Infrastructure.Persistence/`)**
- Sửa: `LinguaBridge.Infrastructure.Persistence.csproj` (+ `Npgsql.EntityFrameworkCore.PostgreSQL` 10.0.* → resolve 10.0.3 / EF Core 10.0.4), `DependencyInjection.cs` (`AddDbContext<LinguaBridgeDbContext>` + `UseNpgsql`), `PersistenceOptions.cs` (chỉ sửa comment)
- Tạo: `LinguaBridgeDbContext.cs`, `Schemas.cs`, `Converters/LanguageConverters.cs`
- Tạo: `Configurations/{Content,Learning,AiTutor,Billing}/*Configuration.cs` — 18 lớp `IEntityTypeConfiguration<T>`

**Api**
- Sửa: `LinguaBridge.Api/appsettings.Development.json` (`Database=LinguaBridge` theo mục 3.2). `appsettings.json` giữ nguyên `LinguaBridgeDb: ""` (không commit mật khẩu cho môi trường khác).

**Tests**
- `LinguaBridge.UnitTests`: sửa `.csproj` (+ tham chiếu Persistence); tạo `DbContextModelTests.cs`, `EntityTests.cs`
- `LinguaBridge.IntegrationTests`: sửa `.csproj` (+ `Microsoft.EntityFrameworkCore.Sqlite` 10.0.*), `ApiHostTests.cs` (+ kiểm tra resolve DbContext); tạo `PersistenceRoundTripTests.cs`

Không file nào ngoài whitelist bị sửa (đã kiểm tra: 57 file thay đổi kể từ TASK-009, tất cả thuộc whitelist).

### b. Kiểm thử thực tế (Commands & Results)
```bash
dotnet build repos/linguabridge/LinguaBridge.sln --no-incremental
#   0 Warning(s)  0 Error(s)

dotnet build AppInHouse.sln
#   Build succeeded. 0 Warning(s)  0 Error(s)

dotnet test repos/linguabridge/LinguaBridge.sln
#   Passed!  - Failed: 0, Passed: 98, Total: 98 - LinguaBridge.UnitTests.dll (net10.0)
#   Passed!  - Failed: 0, Passed:  9, Total:  9 - LinguaBridge.IntegrationTests.dll (net10.0)
```

**`DbContextModelTests`** (dựng model Npgsql thật, không cần kết nối DB):
- Model chứa đúng 18 entity (không thừa, không thiếu); mỗi entity đúng schema + tên bảng theo SQL thiết kế.
- Khóa chính: composite `(CourseId, BaseLanguage)`, `(LessonId, BaseLanguage)`, `(QuestionId, BaseLanguage)`, `(UserId, CourseId)`; `UserEntitlement.UserId`; `SubscriptionPlan.Id` (string).
- Unique: `Course.Code`, `uq_user_vocab (UserId, VocabularyId)`, `uq_provider_event (Provider, EventId)`. Index: `idx_srs_user_due`, `idx_ai_usage_user_daily`.
- 10 FK đúng bảng cha + `ON DELETE` (9 Cascade, `UserEntitlements.PlanId` NO ACTION); không có FK nào khác; **không FK nào đi xuyên schema**.
- Kiểu cột: `jsonb`, `numeric(10,6)`, `numeric(18,0)`, `date`, `double precision`, `timestamp with time zone`, `varchar(n)`, `text`, `integer`.
- Converter ngôn ngữ lưu mã (`zh`, `vi`, `HSK3`, `B2`), giá trị lạ (`fr`, `HSK9`, `3`) ném lỗi thay vì đọc ra giá trị mặc định.
- `GenerateCreateScript()` sinh DDL PostgreSQL hợp lệ cho cả 5 schema.

**`PersistenceRoundTripTests`** (SQLite in-memory, chạy model thật): lưu được cả 18 loại entity, đọc lại và kiểm tra giá trị cho các aggregate chính (materialize qua constructor private & backing field); con thêm vào aggregate đã load được INSERT (không UPDATE nhầm); xóa Course cascade xuống Lesson/Question/Option; webhook trùng `(Provider, EventId)` bị chặn; không xóa được plan đang được dùng.
- Kiểm chứng đột biến: bỏ `ValueGeneratedNever()` khỏi `QuestionResponse` → test `Children_added_to_a_loaded_aggregate_are_inserted` FAIL (EF UPDATE dòng không tồn tại). Đã hoàn tác.

### c. Rủi ro, đánh đổi kỹ thuật hoặc lưu ý cho Tech Lead
1. **Số lượng entity là 18, không phải 16.** Mục 3.3 và Tiêu chí nghiệm thu ghi "16 thực thể", nhưng danh sách trong chính task (và SQL thiết kế) có 7 + 3 + 2 + 3 + 3 = **18**. Tôi triển khai đủ 18; test khẳng định đúng 18.
2. **Chưa có migration và chưa chạy trên PostgreSQL thật.** Task không yêu cầu migration (cần thêm `Microsoft.EntityFrameworkCore.Design` + `IDesignTimeDbContextFactory`). DDL Postgres được sinh và kiểm tra bằng `GenerateCreateScript()`, hành vi lưu/đọc kiểm tra trên SQLite — SQLite bỏ qua schema/kiểu cột nên chưa thay thế được test trên Postgres thật (đề xuất Testcontainers ở task migration).
3. **Tên bảng/cột PascalCase sẽ được quote trong PostgreSQL** (`content."Courses"`, `"UserId"`). SQL trong tài liệu thiết kế không quote, nên nếu chạy tay thì Postgres hạ về chữ thường (`content.courses`). Hai cách không tương thích — cần chốt một quy ước (giữ PascalCase như hiện tại, hoặc dùng `EFCore.NamingConventions` → snake_case) **trước** migration đầu tiên.
4. **Bỏ các DEFAULT phía DB** (`gen_random_uuid()`, `NOW()`, `'Draft'`, `Version 1`, `XpReward 20`, `EaseFactor 2.5`...). Giá trị mặc định nằm trong constructor của entity; Id sinh phía ứng dụng (`ValueGeneratedNever`). Lý do: `HasDefaultValue` trong EF bỏ qua giá trị CLR mặc định (0/false) khi INSERT, dễ gây sai lệch ngầm, và Id phía client là cần thiết để thêm con vào aggregate (xem kiểm chứng đột biến ở mục b).
5. **Thêm ràng buộc ngoài SQL thiết kế:** `CK_ExerciseAttempts_ScorePercentage` (0–100, theo mô tả "int 0-100" của task). Các FK cùng module trong SQL thiết kế cho phép NULL (`CourseId UUID REFERENCES ...` không có `NOT NULL`), tôi để **NOT NULL** vì bản dịch/bài học/câu hỏi mồ côi không có nghĩa.
6. **Ranh giới aggregate:** `Course`, `Lesson`, `Question` là 3 aggregate riêng (tham chiếu nhau bằng Id + FK, không navigation) để không phải load cả giáo trình khi sửa một câu hỏi. `ChatMessage` và `ReviewLog` cũng không nằm trong collection của cha vì tăng không giới hạn; `ChatSession.AddMessage` trả về message để caller lưu và tự tăng `TotalMessagesCount`.
7. **Kiểu enum thay cho chuỗi tự do** với các tập đóng (status, type, tier, sender, state) — lưu bằng tên (`Draft`, `MultipleChoice`...). `ReviewRating` lưu số 1–4. Các mã mở (`ScenarioCode`, `TaskType`, `ModelProvider`, `Provider`) giữ là string. Thêm giá trị enum mới = thay đổi code.
8. **`Guard` bị lặp ở 4 module** (internal, giống `AppInHouse.Identity.Domain.Common.Guard`) vì `LinguaBridge.Domain` không nằm trong whitelist task này. Đề xuất chuyển vào `LinguaBridge.Domain.Common` ở task sau.
9. **Npgsql yêu cầu `DateTimeOffset` có offset 0 (UTC).** Mọi entity gán `DateTimeOffset.UtcNow`; khi sau này nhận thời gian từ client phải `.ToUniversalTime()` trước khi lưu, nếu không Npgsql ném lỗi lúc ghi.
10. **Host vẫn khởi động khi chưa có DB** — DbContext mở kết nối lười; `appsettings.json` để trống chuỗi kết nối nên ngoài Development truy vấn đầu tiên sẽ lỗi. Nên thêm kiểm tra fail-fast/health check DB khi có endpoint dùng DB.


---

## 6. Đánh Giá & Kết Luận Của Tech Lead (Gemini)

- **Kết quả đánh giá**: **`APPROVED`**
- **Nhận xét chuyên môn**:
  1. **18 entity, không phải 16**: Sai sót nhỏ trong task handoff (ghi 16 nhưng danh sách thực tế có 18). Claude Code đếm đúng, triển khai đủ 18 và test khẳng định đúng 18. Không làm tắt theo con số sai trong đặc tả — đây là hành vi đúng.
  2. **Kiến trúc DB đúng chuẩn thiết kế**: Tất cả FK nằm trong phạm vi schema riêng, không FK nào xuyên module (test enforce). Tên index (`uq_user_vocab`, `uq_provider_event`, `idx_srs_user_due`, `idx_ai_usage_user_daily`) đúng với đặc tả SQL.
  3. **Bỏ DB-side DEFAULT là đúng**: Gán default trong constructor entity thay vì `HasDefaultValue()` tránh lỗ hổng kinh điển của EF Core với giá trị 0/false/string rỗng bị bỏ qua khi INSERT.
  4. **`WebhookInbox.Payload` dùng `jsonb`**: Chuẩn xác — kiểu tốt nhất để lưu raw JSON webhook, hỗ trợ GIN index nếu cần query nội dung payload sau này.
  5. **`ValueGeneratedNever()` + client-side Guid**: Đúng pattern cho Modular Monolith, cho phép thêm child entity vào aggregate đã load được INSERT chứ không bị EF nhầm thành UPDATE (đã kiểm chứng bằng mutation test).
  6. **Ranh giới aggregate rõ ràng**: `Course`, `Lesson`, `Question` là 3 aggregate riêng — đúng, không cần load cả curriculum khi sửa 1 câu hỏi.

- **Quyết định kỹ thuật cần chốt TRƯỚC migration đầu tiên (TASK-011)**:

  | # | Vấn đề | Quyết định của Tech Lead |
  |---|---|---|
  | **1** | **Naming convention** — EF quote PascalCase (`content."Courses"`), Postgres convention thực tế là `content.courses`. | ✅ **Chuyển sang `snake_case`** dùng package `EFCore.NamingConventions`. Làm trong TASK-011 trước khi sinh migration đầu tiên. |
  | **2** | **`Guard` bị nhân bản ở 4 module** do Domain không trong whitelist TASK-010. | ✅ **Chuyển vào `LinguaBridge.Domain.Common`** trong TASK-011. |
  | **3** | **Chưa có test trên PostgreSQL thật** (SQLite bỏ qua schema/column type). | ✅ **Thêm Testcontainers** PostgreSQL test trong TASK-011. |
  | **4** | **DateTimeOffset phải UTC** khi lưu qua Npgsql. | ⚠️ Ghi chú cho Application Layer: tất cả timestamp nhận từ client phải `.ToUniversalTime()` trước khi gán cho entity. |

- **Bước tiếp theo**: **TASK-011** — `snake_case` naming conventions, `IDesignTimeDbContextFactory`, EF Core Migrations (Initial), di chuyển `Guard` vào Domain, Testcontainers integration test trên PostgreSQL thật.
