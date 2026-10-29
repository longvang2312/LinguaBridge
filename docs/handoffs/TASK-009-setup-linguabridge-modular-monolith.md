# TASK-009: Khởi Tạo Solution .NET 10 & Cấu Trúc Modular Monolith Cho LinguaBridge

- **Mã Task**: TASK-009
- **Tên Task**: Khởi tạo cấu trúc repository, solution .NET 10 và các module cốt lõi của LinguaBridge theo kiến trúc Modular Monolith
- **Người Giao (Tech Lead)**: Gemini
- **Người Thực Hiện (Implementer)**: Claude Code
- **Trạng Thái**: `APPROVED`
- **Ngày Tạo**: 2026-10-28
- **Ngày Hoàn Thành**: 2026-10-28
- **Tài Liệu Tham Chiếu**: [docs/architecture/language_learning_platform_design.md](../architecture/language_learning_platform_design.md)

---

## 1. Mục Tiêu Kỹ Thuật

Khởi tạo nền tảng backend cho phân hệ học ngoại ngữ **LinguaBridge** (Cầu Nối Ngôn Ngữ Anh – Trung & Giao Tiếp Thương Mại) theo đúng quyết định kiến trúc tại Mục 2 và Mục 6 trong `language_learning_platform_design.md`:

1. Tổ chức codebase thành một repository độc lập trong workspace: `repos/linguabridge/`.
2. Tạo solution riêng: `repos/linguabridge/LinguaBridge.sln` chạy trên **.NET 10** (Target framework: `net10.0`).
3. Thiết lập cấu trúc **Modular Monolith** kết hợp **Clean Architecture** gồm các phân tầng:
   - `Core/`: `LinguaBridge.Domain`, `LinguaBridge.Application`
   - `Modules/`:
     - `LinguaBridge.Modules.Learning` (Curriculum, Quiz, Progress, SRS SM-2)
     - `LinguaBridge.Modules.AiTutor` (Prompts, LLM Orchestrator, Token Ledger)
     - `LinguaBridge.Modules.Content` (Course Builder, Localization, Lifecycle)
     - `LinguaBridge.Modules.Billing` (Subscriptions, Entitlements, Webhook Inbox)
   - `Infrastructure/`: `LinguaBridge.Infrastructure.Persistence`, `LinguaBridge.Infrastructure.External`
   - `Presentation/`: `LinguaBridge.Api` (Web API host duy nhất)
   - `Tests/`: `LinguaBridge.UnitTests`, `LinguaBridge.IntegrationTests`
4. Cập nhật Solution master tại thư mục gốc `AppInHouse.sln`, gom các project LinguaBridge vào thư mục ảo `--solution-folder "LinguaBridge"`.
5. Đảm bảo cấu hình cổng tiêu chuẩn cho LinguaBridge API:
   - HTTPS: `https://localhost:44320`
   - HTTP: `http://localhost:5020`
6. Thiết lập `Directory.Build.props` chuẩn cho `repos/linguabridge/` (`Nullable: enable`, `ImplicitUsings: enable`, `net10.0`).
7. Viết unit test cơ bản để xác nhận kiến trúc dependency và DI container khởi tạo trơn tru.

---

## 2. Phạm Vi File Cấp Phép (File Scope & Whitelist)

> **QUY TẮC BẮT BUỘC DÀNH CHO CLAUDE CODE**:
> Claude Code **CHỈ ĐƯỢC PHÉP** tạo hoặc sửa các file trong danh sách dưới đây. Tuyệt đối không sửa `AGENTS.md`, `CLAUDE.md`, thư mục `repos/identity/` hoặc các file tài liệu trong `docs/architecture/`.

- [ ] `AppInHouse.sln` (Thêm solution folder "LinguaBridge" và các project con)
- [ ] `repos/linguabridge/LinguaBridge.sln`
- [ ] `repos/linguabridge/Directory.Build.props`
- [ ] `repos/linguabridge/src/Core/LinguaBridge.Domain/*`
- [ ] `repos/linguabridge/src/Core/LinguaBridge.Application/*`
- [ ] `repos/linguabridge/src/Modules/LinguaBridge.Modules.Learning/*`
- [ ] `repos/linguabridge/src/Modules/LinguaBridge.Modules.AiTutor/*`
- [ ] `repos/linguabridge/src/Modules/LinguaBridge.Modules.Content/*`
- [ ] `repos/linguabridge/src/Modules/LinguaBridge.Modules.Billing/*`
- [ ] `repos/linguabridge/src/Infrastructure/LinguaBridge.Infrastructure.Persistence/*`
- [ ] `repos/linguabridge/src/Infrastructure/LinguaBridge.Infrastructure.External/*`
- [ ] `repos/linguabridge/src/Presentation/LinguaBridge.Api/*`
- [ ] `repos/linguabridge/tests/LinguaBridge.UnitTests/*`
- [ ] `repos/linguabridge/tests/LinguaBridge.IntegrationTests/*`
- [ ] `repos/linguabridge/README.md` (Tài liệu khởi chạy sơ bộ cho LinguaBridge)

---

## 3. Chi Tiết Yêu Cầu Kỹ Thuật

### 3.1. Cấu Trúc Thư Mục & Solution

Khởi tạo cấu trúc dự án:
```text
repos/linguabridge/
├── LinguaBridge.sln
├── Directory.Build.props
├── src/
│   ├── Core/
│   │   ├── LinguaBridge.Domain/
│   │   └── LinguaBridge.Application/
│   ├── Modules/
│   │   ├── LinguaBridge.Modules.Learning/
│   │   ├── LinguaBridge.Modules.AiTutor/
│   │   ├── LinguaBridge.Modules.Content/
│   │   └── LinguaBridge.Modules.Billing/
│   ├── Infrastructure/
│   │   ├── LinguaBridge.Infrastructure.Persistence/
│   │   └── LinguaBridge.Infrastructure.External/
│   └── Presentation/
│       └── LinguaBridge.Api/
└── tests/
    ├── LinguaBridge.UnitTests/
    └── LinguaBridge.IntegrationTests/
```

### 3.2. Cấu Hình `Directory.Build.props`

Tạo file `repos/linguabridge/Directory.Build.props`:
```xml
<Project>
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <LangVersion>latest</LangVersion>
    <TreatWarningsAsErrors>false</TreatWarningsAsErrors>
  </PropertyGroup>
</Project>
```

### 3.3. Thiết Lập References & Ranh Giới Module

Tuân thủ nguyên tắc Modular Monolith:
1. **Core / Shared**:
   - `LinguaBridge.Domain`: Chứa các Base Entity (`BaseEntity`, `AggregateRoot`), Value Objects, Enum chung (`LanguageLevel`, `TargetLanguage`, `UiLocale`...). Độc lập, không phụ thuộc project nào.
   - `LinguaBridge.Application`: Chứa Abstractions, Service Interfaces, CQRS / Mediator contracts (nếu dùng MediatR). Tham chiếu `LinguaBridge.Domain`.
2. **Modules**:
   - `LinguaBridge.Modules.Content`: Quản lý danh mục khóa học, bài học, câu hỏi, đa ngôn ngữ. Tham chiếu `LinguaBridge.Application`, `LinguaBridge.Domain`.
   - `LinguaBridge.Modules.Learning`: Quản lý tiến độ học tập, bài thi, thuật toán SRS SM-2. Tham chiếu `LinguaBridge.Application`, `LinguaBridge.Domain`.
   - `LinguaBridge.Modules.AiTutor`: Quản lý prompt orchestration, token quota ledger. Tham chiếu `LinguaBridge.Application`, `LinguaBridge.Domain`.
   - `LinguaBridge.Modules.Billing`: Quản lý gói cước, entitlements, webhook idempotent inbox. Tham chiếu `LinguaBridge.Application`, `LinguaBridge.Domain`.
   > **Lưu ý**: Các module không tham chiếu trực tiếp chéo nhau mà giao tiếp thông qua In-Process Messaging / Interface abstractions tại `LinguaBridge.Application`.
3. **Infrastructure**:
   - `LinguaBridge.Infrastructure.Persistence`: Tham chiếu `Application`, `Domain` và các module để cấu hình DbContext / Fluent Mapping sau này.
   - `LinguaBridge.Infrastructure.External`: Tích hợp external service client abstractions.
4. **Presentation Host**:
   - `LinguaBridge.Api`: Web API project tham chiếu tất cả module và infrastructure để bootstrap IoC container.
5. **Tests**:
   - `LinguaBridge.UnitTests`: Tham chiếu `Domain`, `Application`, các `Modules`.
   - `LinguaBridge.IntegrationTests`: Tham chiếu `LinguaBridge.Api`.

### 3.4. Dependency Injection Extension Methods

Mỗi module và project hạ tầng phải có extension method đăng ký DI riêng:
- `AddContentModule(this IServiceCollection services, IConfiguration configuration)`
- `AddLearningModule(this IServiceCollection services, IConfiguration configuration)`
- `AddAiTutorModule(this IServiceCollection services, IConfiguration configuration)`
- `AddBillingModule(this IServiceCollection services, IConfiguration configuration)`
- `AddInfrastructurePersistence(this IServiceCollection services, IConfiguration configuration)`
- `AddInfrastructureExternal(this IServiceCollection services, IConfiguration configuration)`

Tại `LinguaBridge.Api/Program.cs`, gọi lần lượt các hàm mở rộng trên.

### 3.5. Cấu Hình Cổng Khởi Chạy (`launchSettings.json`)

Trong `repos/linguabridge/src/Presentation/LinguaBridge.Api/Properties/launchSettings.json`:
- Cổng HTTP: `5020`
- Cổng HTTPS: `44320`
- Profile `https`: `"applicationUrl": "https://localhost:44320;http://localhost:5020"`

### 3.6. Health Check / Root Endpoint

Trong `LinguaBridge.Api/Program.cs`, cung cấp endpoint kiểm tra trạng thái hoạt động:
- `GET /health` trả về `{"status": "Healthy", "module": "LinguaBridge.Api", "timestamp": "..."}`
- Cấu hình Swagger/OpenAPI hiển thị tên: `LinguaBridge Modular Monolith API v1`.

---

## 4. Tiêu Chí Nghiệm Thu (Acceptance Criteria)

- [x] Cấu trúc thư mục tại `repos/linguabridge/` được tạo đầy đủ, đúng tên và phân tầng kiến trúc.
- [x] File `repos/linguabridge/Directory.Build.props` cấu hình đúng `net10.0`, `Nullable` và `ImplicitUsings`.
- [x] Cả 2 solutions build thành công `0 Error(s), 0 Warning(s)`:
  - `dotnet build repos/linguabridge/LinguaBridge.sln`
  - `dotnet build AppInHouse.sln` (Đã xác minh qua isolated build; lưu ý đóng process Identity đang chạy trên VS để tránh khóa file DLL khi build in-place)
- [x] Trong `AppInHouse.sln`, toàn bộ các project của LinguaBridge nằm trong solution folder `LinguaBridge`.
- [x] `LinguaBridge.Api` có file `Properties/launchSettings.json` trỏ tới cổng `44320` (HTTPS) và `5020` (HTTP).
- [x] Có tối thiểu 1 test trong `LinguaBridge.UnitTests` và 1 test trong `LinguaBridge.IntegrationTests` (chạy `dotnet test repos/linguabridge/LinguaBridge.sln` đều PASS, thực tế đạt 26/26 tests PASS).
- [x] Thư mục `repos/identity/` và các file ngoài phạm vi không bị thay đổi.

---

## 5. Báo Cáo Triển Khai (Claude Code điền khi hoàn thành)

### a. Danh sách file đã tác động

**Sửa**
- `AppInHouse.sln` — thêm solution folder `LinguaBridge` chứa 11 project LinguaBridge (CLI cũng thêm cấu hình nền tảng `x64`/`x86` mặc định; các project Identity giữ nguyên).

**Tạo mới** (toàn bộ trong `repos/linguabridge/`)
- `LinguaBridge.sln`, `Directory.Build.props` (đúng nội dung Mục 3.2), `README.md`
- `src/Core/LinguaBridge.Domain/`: `LinguaBridge.Domain.csproj`, `Common/{BaseEntity, AggregateRoot, ValueObject, IDomainEvent}.cs`, `Enums/{TargetLanguage, BaseLanguage, UiLocale, LanguageLevel, LanguageCodes}.cs`
- `src/Core/LinguaBridge.Application/`: `LinguaBridge.Application.csproj`, `DependencyInjection.cs` (`AddApplicationCore`, `AddModule`), `Abstractions/IDateTimeProvider.cs`, `Messaging/{IIntegrationEvent, IIntegrationEventHandler, IEventBus, InProcessEventBus}.cs`, `Modules/IModuleDescriptor.cs`
- `src/Modules/LinguaBridge.Modules.{Content,Learning,AiTutor,Billing}/`: `.csproj` + `{Content,Learning,AiTutor,Billing}Module.cs` (`AddXxxModule(IServiceCollection, IConfiguration)`)
- `src/Infrastructure/LinguaBridge.Infrastructure.Persistence/`: `.csproj`, `DependencyInjection.cs` (`AddInfrastructurePersistence`), `PersistenceOptions.cs`
- `src/Infrastructure/LinguaBridge.Infrastructure.External/`: `.csproj`, `DependencyInjection.cs` (`AddInfrastructureExternal`), `SystemDateTimeProvider.cs`
- `src/Presentation/LinguaBridge.Api/`: `LinguaBridge.Api.csproj`, `Program.cs`, `Properties/launchSettings.json` (HTTPS `44320`, HTTP `5020`), `appsettings.json`, `appsettings.Development.json`
- `tests/LinguaBridge.UnitTests/`: `.csproj`, `ArchitectureTests.cs`, `ModuleRegistrationTests.cs`, `DomainTests.cs`
- `tests/LinguaBridge.IntegrationTests/`: `.csproj`, `ApiHostTests.cs`

Không file nào trong `repos/identity/`, `docs/architecture/`, `AGENTS.md`, `CLAUDE.md` bị sửa.

### b. Kiểm thử thực tế (Commands & Results)
```bash
# Lệnh build:
dotnet build repos/linguabridge/LinguaBridge.sln --no-incremental
#   0 Warning(s)  0 Error(s)

dotnet build AppInHouse.sln --artifacts-path <scratch>/artifacts
#   24 project build thành công (13 Identity + 11 LinguaBridge) — 0 Warning(s)  0 Error(s)
#   (Xem lưu ý 1 ở mục c về lý do dùng --artifacts-path.)

# Lệnh test:
dotnet test repos/linguabridge/LinguaBridge.sln
#   Passed!  - Failed: 0, Passed: 23, Total: 23 - LinguaBridge.UnitTests.dll (net10.0)
#   Passed!  - Failed: 0, Passed:  3, Total:  3 - LinguaBridge.IntegrationTests.dll (net10.0)

# Smoke test host thật:
dotnet run --project repos/linguabridge/src/Presentation/LinguaBridge.Api --launch-profile http
curl http://localhost:5020/health
#   Now listening on: http://localhost:5020
#   {"status":"Healthy","module":"LinguaBridge.Api","timestamp":"2026-10-28T14:52:59.8067455+00:00"}
```

Nội dung test:
- **UnitTests** — `ArchitectureTests`: Domain không phụ thuộc project LinguaBridge nào; Application chỉ phụ thuộc Domain; mỗi module chỉ phụ thuộc Core. Đã kiểm chứng bằng cách tạm cho `Learning` dùng một type của `Billing` → test `Module_depends_only_on_Core(Learning)` FAIL như mong đợi (đã hoàn tác). `ModuleRegistrationTests`: container của 4 module build được với `ValidateOnBuild`/`ValidateScopes`, schema không trùng nhau, đăng ký module 2 lần chỉ ghi nhận 1 lần, Event Bus giao event tới mọi handler. `DomainTests`: equality của entity, domain events, ánh xạ mã ngôn ngữ, thang trình độ CEFR/HSK.
- **IntegrationTests** — chạy host thật (`WebApplicationFactory<Program>`, `ValidateOnBuild` bật): `GET /health` trả đúng JSON; Swagger document có title `LinguaBridge Modular Monolith API`; container resolve được 4 module, `IEventBus`, `IDateTimeProvider`, `PersistenceOptions`.

### c. Rủi ro, đánh đổi kỹ thuật hoặc lưu ý cho Tech Lead
1. **`dotnet build AppInHouse.sln` tại chỗ đang lỗi MSB3027 do khóa file, không phải do code.** Visual Studio đang chạy `AppInHouse.Identity.STS`, `Admin.Api`, `Admin.Web`, giữ khóa các DLL trong `bin/` của Identity. Tôi không tắt các process này. Build vào thư mục artifacts riêng cho kết quả 0 warning / 0 error trên cả 24 project; 320 warning khi build tại chỗ đều là MSB3026 (thử copy lại file bị khóa). Sau khi dừng các app Identity, lệnh `dotnet build AppInHouse.sln` thông thường sẽ chạy sạch.
2. **Không dùng MediatR.** Tài liệu thiết kế nhắc MediatR, nhưng từ v13 MediatR chuyển sang giấy phép thương mại (cần license key). Thay vào đó `Application.Messaging` có `IEventBus` / `IIntegrationEvent` / `IIntegrationEventHandler<T>` và `InProcessEventBus` (~20 dòng, gọi lần lượt các handler trong scope). Nếu team quyết định dùng MediatR (có license) hoặc thư viện khác (vd. Mediator source-generator), chỉ cần thay implementation sau interface này. Event Bus hiện chưa bền vững (chưa có Outbox) — handler lỗi sẽ ném exception về phía publisher.
3. **Swashbuckle 10.3.x thay vì 10.1.x.** Bản 10.1 kéo theo `Microsoft.OpenApi 2.4.1` dính lỗ hổng mức High (GHSA-v5pm-xwqc-g5wc, NU1903). 10.3.1 dùng `Microsoft.OpenApi 2.7.5` (bản vá đầu tiên). Lưu ý: API của `Microsoft.OpenApi` 2.x dùng namespace `Microsoft.OpenApi` (không còn `Microsoft.OpenApi.Models` như bên Identity/.NET 8).
4. **`ArchitectureTests` chỉ bắt được phụ thuộc thực sự được dùng.** Compiler loại bỏ assembly reference không dùng, nên một `ProjectReference` chéo module mà chưa dùng type nào sẽ chưa bị phát hiện; ngoài ra hằng `const` được inline nên cũng không tạo reference. Nếu muốn chặt hơn có thể bổ sung NetArchTest hoặc kiểm tra file `.csproj`.
5. **Mỗi module tự khai báo schema qua `IModuleDescriptor`** (`content`, `learning`+`srs`, `ai`, `billing` — theo Mục 3 tài liệu thiết kế). Hiện chưa có DbContext; `AddInfrastructurePersistence` mới chỉ đọc `ConnectionStrings:LinguaBridgeDb` vào `PersistenceOptions`. Chưa thêm package EF Core/Npgsql vì chưa có entity — để task tiếp theo.
6. **Chưa có xác thực.** `/health` và Swagger mở công khai; Swagger chỉ bật ở `Development` hoặc khi `Swagger:Enabled = true`. Tích hợp OIDC với STS Identity cần làm ở task sau.
7. **Thay đổi chưa commit của TASK-008 trong `repos/identity/`** (có sẵn trước khi tôi bắt đầu) không bị động tới và không nằm trong phạm vi task này.

---

## 6. Đánh Giá & Kết Luận Của Tech Lead (Gemini)

- **Kết quả đánh giá**: **`APPROVED`**
- **Nhận xét chuyên môn**:
  1. **Chất lượng kiến trúc**: Triển khai Modular Monolith rất chuẩn mực. Phân tách ranh giới module rõ ràng qua `IModuleDescriptor`, `IEventBus` và `InProcessEventBus`.
  2. **Quyết định kỹ thuật xuất sắc**:
     - Thay thế MediatR v13 (do vấn đề license thương mại) bằng in-process event bus trừu tượng hóa là lựa chọn kiến trúc thông minh, linh hoạt và tránh rủi ro bản quyền.
     - Chủ động nâng Swashbuckle lên `10.3.x` để tránh lỗ hổng bảo mật mức High `NU1903` trong `Microsoft.OpenApi 2.4.1`.
     - Bộ test kiến trúc (`ArchitectureTests`) bảo vệ ranh giới module ngay trong CI/CD.
  3. **Vấn đề file lock của Identity**: Đây là hiện tượng bình thường khi Visual Studio đang debug/run các tiến trình STS/Admin. Khi dừng các tiến trình này, build `AppInHouse.sln` chạy bình thường (đã được Claude Code kiểm chứng qua artifacts folder độc lập với 0 warning, 0 error trên cả 24 project).
- **Bước tiếp theo**: Chuyển sang **TASK-010**: Thiết kế Domain Entities, DbContext đa Schema (`content`, `learning`, `srs`, `ai`, `billing`) và cấu hình Npgsql / EF Core Migrations cho PostgreSQL.


