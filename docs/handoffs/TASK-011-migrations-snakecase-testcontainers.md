# TASK-011: EF Core Migrations, snake_case Convention & PostgreSQL Integration Test

- **Mã Task**: TASK-011
- **Tên Task**: Cấu hình `snake_case` naming convention, tạo EF Core Migration ban đầu, `IDesignTimeDbContextFactory`, di chuyển `Guard` vào Domain và Testcontainers integration test
- **Người Giao (Tech Lead)**: Gemini
- **Người Thực Hiện (Implementer)**: Claude Code
- **Trạng Thái**: `APPROVED`
- **Ngày Tạo**: 2026-10-28
- **Phụ Thuộc**: Hoàn thành sau TASK-010
- **Tài Liệu Tham Chiếu**: [docs/architecture/language_learning_platform_design.md](../architecture/language_learning_platform_design.md)

---

## 1. Mục Tiêu Kỹ Thuật

Hoàn thiện tầng Persistence để sẵn sàng cho việc chạy trên PostgreSQL thật, giải quyết 4 vấn đề kỹ thuật đã được chốt trong nghiệm thu TASK-010:

1. **`snake_case` naming convention**: Chuyển toàn bộ tên bảng/cột từ PascalCase (được quote bởi Npgsql) sang `snake_case` tự nhiên của PostgreSQL (`content.courses`, `user_id`, ...) bằng package `EFCore.NamingConventions`. **Phải làm trước khi sinh migration.**
2. **`Guard` helper vào Domain**: Di chuyển `Guard.cs` từ 4 module vào `LinguaBridge.Domain.Common`, bổ sung method `Range` (kiểm tra int trong khoảng), xóa các bản copy trong module.
3. **`IDesignTimeDbContextFactory`**: Thêm factory để `dotnet ef migrations` có thể chạy mà không cần `appsettings.json` hay host.
4. **Initial Migration**: Tạo migration đầu tiên (tên: `InitialCreate`) bằng `dotnet ef migrations add InitialCreate`.
5. **Testcontainers integration test**: Thêm test chạy migration thật trên PostgreSQL container (xUnit + `Testcontainers.PostgreSql`) để xác minh toàn bộ DDL (schema, tên bảng/cột `snake_case`, ràng buộc, index) thực sự được tạo đúng trên Postgres thật.

---

## 2. Phạm Vi File Cấp Phép (File Scope & Whitelist)

> **QUY TẮC BẮT BUỘC DÀNH CHO CLAUDE CODE**:
> Claude Code **CHỈ ĐƯỢC PHÉP** tạo hoặc sửa các file trong danh sách dưới đây. Tuyệt đối không sửa `AGENTS.md`, `CLAUDE.md`, thư mục `repos/identity/` hoặc `docs/architecture/`.

- [x] `repos/linguabridge/src/Core/LinguaBridge.Domain/*` (thêm `Common/Guard.cs`)
- [x] `repos/linguabridge/src/Modules/LinguaBridge.Modules.Content/Entities/Guard.cs` (XÓA)
- [x] `repos/linguabridge/src/Modules/LinguaBridge.Modules.Learning/Entities/Guard.cs` (XÓA)
- [x] `repos/linguabridge/src/Modules/LinguaBridge.Modules.AiTutor/Entities/Guard.cs` (XÓA)
- [x] `repos/linguabridge/src/Modules/LinguaBridge.Modules.Billing/Entities/Guard.cs` (XÓA)
- [x] `repos/linguabridge/src/Modules/LinguaBridge.Modules.Content/*.csproj` (thêm ref Domain nếu chưa có)
- [x] `repos/linguabridge/src/Modules/LinguaBridge.Modules.Learning/*.csproj`
- [x] `repos/linguabridge/src/Modules/LinguaBridge.Modules.AiTutor/*.csproj`
- [x] `repos/linguabridge/src/Modules/LinguaBridge.Modules.Billing/*.csproj`
- [x] `repos/linguabridge/src/Modules/LinguaBridge.Modules.*/Entities/*.cs` (cập nhật `using` trỏ sang Domain.Common)
- [x] `repos/linguabridge/src/Infrastructure/LinguaBridge.Infrastructure.Persistence/*` (thêm convention, factory, csproj, Configurations)
- [x] `repos/linguabridge/src/Infrastructure/LinguaBridge.Infrastructure.Persistence/Migrations/*` (file migration được sinh tự động)
- [x] `repos/linguabridge/tests/LinguaBridge.UnitTests/*` (cập nhật DbContextModelTests theo snake_case)
- [x] `repos/linguabridge/tests/LinguaBridge.IntegrationTests/*` (thêm Testcontainers test)
- [x] `repos/linguabridge/tests/LinguaBridge.IntegrationTests/*.csproj` (thêm Testcontainers package)

---

## 3. Chi Tiết Yêu Cầu Kỹ Thuật

### 3.1. snake_case Naming Convention

Cài `EFCore.NamingConventions` (chọn phiên bản compatible với EF Core 10) vào `LinguaBridge.Infrastructure.Persistence.csproj`.

Trong `DependencyInjection.cs` (hoặc trong `AddInfrastructurePersistence`), bật convention:
```csharp
options.UseNpgsql(connectionString)
       .UseSnakeCaseNamingConvention();
```

Sau đó **rà soát lại toàn bộ Fluent Configuration** để:
- Đổi string literal tên cột / tên bảng trong `HasColumnName`, `HasDatabaseName`, `HasIndex` sang `snake_case` hoặc bỏ đi nếu convention đã tự sinh đúng.
- Tên index thủ công giữ nguyên theo đặc tả: `uq_user_vocab`, `uq_provider_event`, `idx_srs_user_due`, `idx_ai_usage_user_daily`.
- Kiểm tra lại test `DbContextModelTests` và cập nhật các chuỗi assert theo `snake_case`:
  - `"CREATE TABLE content.\"courses\""` (hoặc không quote nếu convention xử lý)
  - `"\"payload\" jsonb NOT NULL"` (lowercase)
  - v.v.

**Quy tắc đặt tên mong đợi sau khi bật convention**:
- Bảng: `content.courses`, `content.course_translations`, `learning.user_course_progress`, `srs.user_vocabulary_progress`, `ai.chat_sessions`, `ai.ai_usage_ledger`, `billing.webhook_inbox`, ...
- Cột: `user_id`, `course_id`, `target_language`, `created_at`, `score_percentage`, `ease_factor`, `next_review_date`, `estimated_cost_usd`, `pinyin_or_ipa`, ...

### 3.2. Di Chuyển `Guard` vào `LinguaBridge.Domain.Common`

Tạo `repos/linguabridge/src/Core/LinguaBridge.Domain/Common/Guard.cs`:
```csharp
namespace LinguaBridge.Domain.Common;

public static class Guard
{
    public static string Required(string? value, int maxLength, string paramName) { ... }
    public static string? Optional(string? value, int maxLength, string paramName) { ... }
    public static Guid RequiredId(Guid value, string paramName) { ... }
    public static int NonNegative(int value, string paramName) { ... }
    public static int Range(int value, int min, int max, string paramName) { ... } // <-- BỔ SUNG MỚI
}
```

- Di chuyển với access modifier `public` (để Domain có thể reuse và test).
- Xóa 4 file `Guard.cs` internal trong các module.
- Cập nhật `using` trong tất cả entity file sang `LinguaBridge.Domain.Common`.
- Thêm `<ProjectReference>` từ các module csproj sang `LinguaBridge.Domain.csproj` nếu chưa có.
- **Kiểm tra**: `ArchitectureTests.Module_depends_only_on_Core` vẫn phải PASS (Domain là Core nên được phép).

### 3.3. `IDesignTimeDbContextFactory`

Tạo `LinguaBridgeDbContextFactory.cs` trong `LinguaBridge.Infrastructure.Persistence`:
```csharp
public class LinguaBridgeDbContextFactory : IDesignTimeDbContextFactory<LinguaBridgeDbContext>
{
    public LinguaBridgeDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<LinguaBridgeDbContext>()
            .UseNpgsql("Host=localhost;Port=5432;Database=LinguaBridge_Design;Username=postgres;Password=postgres")
            .UseSnakeCaseNamingConvention()
            .Options;
        return new LinguaBridgeDbContext(options);
    }
}
```

### 3.4. Initial Migration

Sau khi cài convention và factory, chạy:
```bash
cd repos/linguabridge
dotnet ef migrations add InitialCreate \
  --project src/Infrastructure/LinguaBridge.Infrastructure.Persistence \
  --startup-project src/Presentation/LinguaBridge.Api \
  --output-dir Migrations
```

Kiểm tra file migration được sinh ra:
- Phải có `CREATE SCHEMA content;`, `CREATE SCHEMA learning;`, `CREATE SCHEMA srs;`, `CREATE SCHEMA ai;`, `CREATE SCHEMA billing;`.
- Tên bảng/cột đúng `snake_case`.
- Các index và unique constraint phải có mặt.

### 3.5. Testcontainers Integration Test

Thêm package vào `LinguaBridge.IntegrationTests.csproj`:
```xml
<PackageReference Include="Testcontainers.PostgreSql" Version="4.*" />
```

Tạo `PostgresMigrationTests.cs`:
- Khởi PostgreSQL container (`PostgreSqlBuilder`).
- Chạy `context.Database.MigrateAsync()`.
- Kiểm tra các schema tồn tại: query `information_schema.schemata`.
- Kiểm tra một số bảng quan trọng tồn tại: `content.courses`, `billing.webhook_inbox`, `srs.user_vocabulary_progress`.
- Kiểm tra index `uq_provider_event` tồn tại: query `pg_indexes`.
- Test xóa cascade: insert `course` → insert `lesson` → xóa `course` → `lesson` không còn.
- Test idempotency webhook: insert hai bản ghi `WebhookInbox` cùng `(Provider, EventId)` → lần thứ hai phải bị lỗi unique constraint.

---

## 4. Tiêu Chí Nghiệm Thu (Acceptance Criteria)

- [x] Bật `UseSnakeCaseNamingConvention()` — DDL sinh ra dùng `snake_case` cho bảng và cột.
- [x] `Guard` được định nghĩa duy nhất tại `LinguaBridge.Domain.Common.Guard`, không còn bản copy trong module.
- [x] `ArchitectureTests` (26 unit tests từ TASK-009) vẫn PASS toàn bộ.
- [x] `dotnet ef migrations add InitialCreate` thành công, file migration không chứa tên PascalCase.
- [x] `dotnet build repos/linguabridge/LinguaBridge.sln` — 0 warning, 0 error.
- [x] `dotnet test repos/linguabridge/LinguaBridge.sln` — **tất cả tests PASS**, bao gồm Testcontainers test.
- [x] `PostgresMigrationTests` chạy PASS, xác nhận schema, bảng, index tồn tại trên PostgreSQL container thật.

---

## 5. Lưu Ý Cho Claude Code

1. **Testcontainers cần Docker đang chạy** trên máy. Nếu Docker không có, đánh dấu test `[Trait("Category", "Integration")]` và báo lại.
2. **Thứ tự làm việc**: (1) Cài convention → (2) Cập nhật Configurations → (3) Cập nhật tests → (4) Build pass → (5) Tạo migration → (6) Testcontainers test. Không tạo migration trước khi convention ổn định.
3. **Kiểm tra `EFCore.NamingConventions` version**: Chọn đúng major version tương thích với EF Core 10. Nếu package chưa có bản stable cho EF10, báo lại Tech Lead để xem xét phương án thay thế (configure thủ công `snake_case` qua `ModelConfigurationBuilder`).

---

## 6. Báo Cáo Triển Khai (Claude Code điền khi hoàn thành)

### a. Danh sách file đã tác động

**Domain**
- Tạo: `src/Core/LinguaBridge.Domain/Common/Guard.cs` — `public static class Guard` với `Required`, `Optional`, `RequiredId`, `NonNegative` và **`Range`** mới (khoảng đóng `[min, max]`, ném `ArgumentOutOfRangeException`; ném `ArgumentException` nếu `min > max`).

**Modules**
- Xóa: `Entities/Guard.cs` ở cả 4 module (Content, Learning, AiTutor, Billing).
- Sửa: 18 entity file — thêm `using LinguaBridge.Domain.Common;` ở những file chưa có. `ExerciseAttempt.Complete` dùng `Guard.Range(score, 0, 100, …)` thay cho 2 lệnh `ThrowIf…`.
- `.csproj` của 4 module **không cần sửa** — đã tham chiếu `LinguaBridge.Domain` từ TASK-009.

**Persistence** (`src/Infrastructure/LinguaBridge.Infrastructure.Persistence/`)
- Sửa `.csproj`: + `EFCore.NamingConventions` 10.0.* (→ 10.0.1), + `Microsoft.EntityFrameworkCore.Design` 10.0.* (private), + `Microsoft.EntityFrameworkCore.Relational` 10.0.* (tham chiếu trực tiếp — xem rủi ro 3). EF Core thống nhất **10.0.14** trên mọi project.
- Sửa `LinguaBridgeDbContext.cs`: `OnConfiguring` gọi `UseSnakeCaseNamingConvention()` (xem rủi ro 1).
- Tạo `LinguaBridgeDbContextFactory.cs` (`IDesignTimeDbContextFactory`, đúng như mục 3.3).
- Sửa 18 file `Configurations/**`: tên bảng `snake_case` (`courses`, `ai_usage_ledger`, `webhook_inbox`…); check constraint → `ck_exercise_attempts_score_percentage` / `score_percentage BETWEEN 0 AND 100`. 4 tên index thủ công giữ nguyên.
- Tạo (sinh tự động): `Migrations/20261028152917_InitialCreate.cs`, `…InitialCreate.Designer.cs`, `LinguaBridgeDbContextModelSnapshot.cs`.

**Tests**
- `LinguaBridge.UnitTests`: sửa `DbContextModelTests.cs` (tên bảng/DDL theo snake_case + 5 test mới); tạo `GuardTests.cs`.
- `LinguaBridge.IntegrationTests`: sửa `.csproj` (+ `Testcontainers.PostgreSql` 4.* → 4.16.0); tạo `PostgresMigrationTests.cs` (gồm `PostgresFixture`).

Không file nào ngoài whitelist bị sửa (đã kiểm tra bằng `find -newer` trên toàn `repos/linguabridge`).

### b. Kiểm thử thực tế (Commands & Results)
```bash
dotnet build repos/linguabridge/LinguaBridge.sln --no-incremental
#   0 Warning(s)  0 Error(s)
dotnet build AppInHouse.sln
#   0 Warning(s)  0 Error(s)

# Migration (dotnet-ef 10.0.14 cài vào thư mục tạm bằng --tool-path, không cài global):
dotnet ef migrations add InitialCreate \
  --project src/Infrastructure/LinguaBridge.Infrastructure.Persistence \
  --startup-project src/Infrastructure/LinguaBridge.Infrastructure.Persistence \
  --output-dir Migrations
#   Build succeeded. Done.

dotnet test repos/linguabridge/LinguaBridge.sln          # Docker Desktop 29.9.2 đang chạy
#   Passed!  - Failed: 0, Passed: 119, Total: 119 - LinguaBridge.UnitTests.dll (net10.0)
#   Passed!  - Failed: 0, Passed:  22, Total:  22 - LinguaBridge.IntegrationTests.dll (net10.0)

dotnet test repos/linguabridge/tests/LinguaBridge.IntegrationTests --filter "Category!=Integration"   # khi không có Docker
#   Passed!  - Failed: 0, Passed: 9, Total: 9
```

**Kiểm tra script migration** (`dotnet ef migrations script --idempotent`):
- Có đủ `CREATE SCHEMA content|learning|srs|ai|billing;` và 18 `CREATE TABLE <schema>.<snake_case>`.
- Có `uq_provider_event`, `uq_user_vocab`, `idx_srs_user_due`, `idx_ai_usage_user_daily`, `ix_courses_code` (unique), `ck_exercise_attempts_score_percentage`, `payload jsonb NOT NULL`.
- **Không tên DB nào chứa chữ hoa**. Identifier duy nhất bị quote là bảng lịch sử nội bộ của EF `"__EFMigrationsHistory"` (+ cột `"migration_id"`).

**Unit test mới** (`DbContextModelTests`): `Ddl_contains_no_identifier_that_needs_quoting` (DDL không có dấu `"` nào), `Columns_are_snake_case`, `Generated_constraint_names_are_snake_case` (`pk_courses`, `ix_courses_code`, `fk_lessons_courses_course_id`), **`Model_has_no_changes_missing_from_the_migrations`** (`HasPendingModelChanges()` — ai sửa entity/config mà quên `migrations add` sẽ fail ngay ở unit test, không cần DB), `Initial_migration_is_present`. `GuardTests`: 9 test cho Guard (gồm biên của `Range`).

**`PostgresMigrationTests`** (13 test, `postgres:17-alpine`, `MigrateAsync()` thật, `[Trait("Category", "Integration")]`):
- Migration đã apply, không còn migration pending.
- 5 schema tồn tại (`information_schema.schemata`); **đủ và đúng 18 bảng** trong 5 schema (`information_schema.tables`).
- Cột snake_case với kiểu đúng (`date`, `double precision`, `numeric`, `jsonb`, `timestamp with time zone`), không cột nào có chữ hoa.
- 5 index có tên đúng, trên đúng bảng, đúng unique/không unique (`pg_indexes`).
- Xóa `course` → `lesson` và `course_translations` bị xóa theo (cascade).
- Webhook trùng `(provider, event_id)` → `PostgresException` SQLSTATE `23505`, `ConstraintName = uq_provider_event`.
- Check constraint: INSERT trực tiếp `score_percentage = 150` (bỏ qua Guard) → SQLSTATE `23514`, `ck_exercise_attempts_score_percentage`.
- Giá trị lưu đúng thiết kế: `zh|HSK2|Draft`; `payload->>'amount'` truy vấn được (jsonb thật); `DateTimeOffset` đọc lại có offset 0.

### c. Rủi ro, đánh đổi kỹ thuật hoặc lưu ý cho Tech Lead
1. **`UseSnakeCaseNamingConvention()` đặt trong `LinguaBridgeDbContext.OnConfiguring`, không đặt ở `DependencyInjection.cs`** (mục 3.1 cho phép "hoặc"). Lý do: quy ước đặt tên là một phần của *model*; nếu để caller tự bật, bất kỳ nơi nào tạo context mà quên gọi (test, tool, script) sẽ có model khác migration và query sai tên cột lúc runtime. Đặt trong context thì DI, factory, SQLite test và Testcontainers test đều chắc chắn dùng cùng model. Factory vẫn gọi lại đúng như snippet mục 3.3 (gọi 2 lần vô hại).
2. **Migration chạy với `--startup-project` = Persistence, không phải `LinguaBridge.Api`** như lệnh mẫu ở mục 3.4. `dotnet ef` yêu cầu startup project tham chiếu `Microsoft.EntityFrameworkCore.Design`; `LinguaBridge.Api` không nằm trong whitelist nên tôi không sửa. Vì đã có `IDesignTimeDbContextFactory`, dùng Persistence làm startup là đủ và không cần host. Nếu muốn dùng Api làm startup, cần thêm Design package vào `LinguaBridge.Api.csproj` ở task khác.
3. **Xung đột phiên bản EF Core khi thêm Design package** (đã xử lý): Design 10.0.* kéo EF Core lên 10.0.14 trong Persistence nhưng là `PrivateAssets=all`, nên các project tham chiếu Persistence vẫn nhận EF 10.0.4 (từ Npgsql) → `CS1705` + 6 cảnh báo `MSB3277`. Sửa bằng tham chiếu trực tiếp `Microsoft.EntityFrameworkCore.Relational` 10.0.* trong Persistence để phiên bản lan ra consumer. Hiện mọi project thống nhất 10.0.14. **Lưu ý**: Design và Relational cùng dùng `10.0.*` nên luôn trôi cùng nhau; nếu sau này pin phiên bản thì phải pin cả hai giống nhau.
4. **`dotnet-ef` không được cài sẵn trên máy.** Tôi cài `dotnet-ef 10.0.14` vào thư mục tạm bằng `--tool-path` (không cài global, không tạo `.config/dotnet-tools.json` vì ngoài whitelist). Đề xuất task sau thêm tool manifest vào repo (`dotnet new tool-manifest` + `dotnet tool install dotnet-ef`) để cả team dùng chung phiên bản.
5. **Testcontainers cần Docker.** Docker Desktop đã cài nhưng chưa chạy; tôi khởi động nó. Ngay sau đó Docker Desktop tự cập nhật (29.4.1 → 29.9.2) và tắt engine giữa chừng — lần chạy test đầu tiên vì vậy timeout ở named pipe; sau khi cập nhật xong mọi test PASS. Container Postgres được dọn tự động sau test. Trên máy không có Docker, 13 test Postgres sẽ **FAIL** (không skip); chạy `--filter "Category!=Integration"` để bỏ qua. Nếu muốn tự skip, cần package `Xunit.SkippableFact` hoặc nâng lên xUnit v3 (`Assert.Skip`).
6. **Bảng `__EFMigrationsHistory` nằm ở schema `public`** và là identifier duy nhất còn bị quote. Có thể chuyển sang schema riêng bằng `UseNpgsql(cs, o => o.MigrationsHistoryTable("__ef_migrations_history", "public"))` — cần quyết định **trước khi** migration được apply lên môi trường thật, vì đổi sau sẽ khiến EF tưởng chưa có migration nào.
7. **Đổi tên bảng/cột sau này phải qua migration**: unit test `Model_has_no_changes_missing_from_the_migrations` sẽ fail nếu model thay đổi mà chưa có migration tương ứng — đây là chủ đích.


---

## 7. Đánh Giá & Kết Luận Của Tech Lead (Claude — thay vai Gemini)

- **Kết quả đánh giá**: **`APPROVED`**
- **Xác minh độc lập** (2026-10-28, Docker 29.9.2):
  ```bash
  dotnet build repos/linguabridge/LinguaBridge.sln --no-incremental   # 0 Warning(s), 0 Error(s)
  dotnet test  repos/linguabridge/LinguaBridge.sln --no-build          # Unit 119/119, Integration 22/22 PASS
  ```
  - Không còn `Guard.cs` nào trong `src/Modules`; 18 entity dùng `LinguaBridge.Domain.Common.Guard`.
  - Migration `InitialCreate` không chứa identifier PascalCase nào (grep `name:/table:/schema:/column:`).
- **Nhận xét chuyên môn**:
  1. **`UseSnakeCaseNamingConvention()` trong `OnConfiguring`** — chấp nhận, tốt hơn đặc tả: convention là một phần của model, đặt ở context đảm bảo DI / factory / test luôn ra cùng model với migration.
  2. **`Model_has_no_changes_missing_from_the_migrations`** (`HasPendingModelChanges()`) — điểm cộng lớn: bắt lỗi quên `migrations add` ngay ở unit test, không cần DB.
  3. **`--startup-project` = Persistence** — chấp nhận. Có `IDesignTimeDbContextFactory` nên không cần host; lệnh mẫu trong mục 3.4 của task là sai sót của đặc tả.
  4. **Test Postgres đủ sâu**: kiểm tra SQLSTATE `23505`/`23514` và `ConstraintName` thay vì chỉ "có exception" — đúng cách.
  5. **Xử lý xung đột EF Core 10.0.4 ↔ 10.0.14** bằng tham chiếu trực tiếp `Relational` — đúng, có ghi chú rõ.

- **Quyết định cho các rủi ro mục 6.c**:

  | # | Vấn đề | Quyết định |
  |---|---|---|
  | 1 | Snake_case trong `OnConfiguring` | ✅ Giữ nguyên. |
  | 2 | Startup project cho `dotnet ef` | ✅ Dùng Persistence. Cập nhật README ở task có README trong whitelist. |
  | 3 | Version trôi `10.0.*` | ⚠️ Chấp nhận tạm. Backlog: chuyển sang Central Package Management (`Directory.Packages.props`) và pin version trước khi lên staging. |
  | 4 | Chưa có `dotnet-ef` tool manifest | ⚠️ Backlog: thêm `.config/dotnet-tools.json` (dotnet-ef 10.0.x) ở task tooling kế tiếp. |
  | 5 | Test Postgres FAIL khi không có Docker | ✅ Chấp nhận; CI phải có Docker. Máy dev không Docker dùng `--filter "Category!=Integration"`. |
  | 6 | `__EFMigrationsHistory` ở schema `public` | ✅ **Giữ mặc định của EF.** Đổi tên phải cấu hình ở 3 nơi (DI, factory, fixture) mà lợi ích chỉ là thẩm mỹ. Quyết định này là **cuối cùng** — không đổi sau khi đã apply lên môi trường thật. |
  | 7 | Đổi schema phải qua migration | ✅ Đúng chủ đích. |

- **Bước tiếp theo**: **TASK-012** — xác thực cho `LinguaBridge.Api` qua STS. Đặc tả TASK-012 đã được **viết lại** sau khi đối chiếu với STS thực tế (xem ghi chú đầu task).
