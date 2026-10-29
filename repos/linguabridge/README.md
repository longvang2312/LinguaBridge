# LinguaBridge Backend
## Modular Monolith API cho nền tảng học Tiếng Anh – Tiếng Trung & Giao Tiếp Thương Mại (.NET 10)

Tài liệu khởi chạy sơ bộ cho backend LinguaBridge. 
- Hướng dẫn cấu hình & khởi chạy chi tiết toàn bộ hệ sinh thái: [docs/guides/RUN_AND_SETUP_GUIDE.md](../../docs/guides/RUN_AND_SETUP_GUIDE.md)
- Thiết kế kiến trúc đầy đủ: [docs/architecture/language_learning_platform_design.md](../../docs/architecture/language_learning_platform_design.md).

---

## 1. Yêu Cầu Môi Trường

- **.NET 10 SDK** (`dotnet --list-sdks` phải có `10.0.x`).
- **PostgreSQL 16/17** (Local hoặc Docker `localhost:5432`) với database `LinguaBridge`.
- **Docker Desktop** (khuyến nghị để chạy Testcontainers khi test).

---

## 2. Cấu Trúc Solution

```text
repos/linguabridge/
├── LinguaBridge.sln
├── Directory.Build.props                  # net10.0, Nullable, ImplicitUsings cho mọi project
├── src/
│   ├── Core/
│   │   ├── LinguaBridge.Domain/           # BaseEntity, AggregateRoot, ValueObject, enum ngôn ngữ
│   │   └── LinguaBridge.Application/      # Abstractions, In-process Event Bus, IModuleDescriptor
│   ├── Modules/
│   │   ├── LinguaBridge.Modules.Content/  # schema: content
│   │   ├── LinguaBridge.Modules.Learning/ # schema: learning, srs
│   │   ├── LinguaBridge.Modules.AiTutor/  # schema: ai
│   │   └── LinguaBridge.Modules.Billing/  # schema: billing
│   ├── Infrastructure/
│   │   ├── LinguaBridge.Infrastructure.Persistence/
│   │   └── LinguaBridge.Infrastructure.External/
│   └── Presentation/
│       └── LinguaBridge.Api/              # Web API host duy nhất
└── tests/
    ├── LinguaBridge.UnitTests/            # Kiểm tra ranh giới module, DI, Domain
    └── LinguaBridge.IntegrationTests/     # Chạy host thật qua WebApplicationFactory
```

### Quy tắc phụ thuộc

| Project | Được tham chiếu |
|---|---|
| `Domain` | Không tham chiếu project nào |
| `Application` | `Domain` |
| `Modules.*` | `Domain`, `Application` — **không** tham chiếu module khác |
| `Infrastructure.Persistence` | `Domain`, `Application`, các `Modules.*` |
| `Infrastructure.External` | `Application` |
| `Api` | Tất cả module + infrastructure |

Các module giao tiếp với nhau qua `IEventBus` / `IIntegrationEvent` khai báo tại `LinguaBridge.Application.Messaging`. Quy tắc này được kiểm tra tự động bởi `ArchitectureTests` trong `LinguaBridge.UnitTests`.

### Thêm module mới

1. Tạo project `src/Modules/LinguaBridge.Modules.<Ten>` tham chiếu `Domain` và `Application`.
2. Viết `Add<Ten>Module(this IServiceCollection, IConfiguration)` gọi `services.AddModule("<Ten>", "<schema>")`.
3. Gọi hàm này trong `LinguaBridge.Api/Program.cs` và thêm assembly vào `ArchitectureTests.ModuleAssemblies`.

---

## 3. Build & Test

Chạy từ thư mục gốc workspace:

```bash
dotnet build repos/linguabridge/LinguaBridge.sln
dotnet test repos/linguabridge/LinguaBridge.sln
```

Các project LinguaBridge cũng nằm trong thư mục ảo `LinguaBridge` của `AppInHouse.sln`.

Các test `PostgresMigrationTests` cần Docker (Testcontainers). Khi không có Docker, bỏ qua bằng:

```bash
dotnet test repos/linguabridge/LinguaBridge.sln --filter "Category!=Integration"
```

### Migration (EF Core)

Dùng `LinguaBridge.Infrastructure.Persistence` làm **cả** project lẫn startup project (đã có `IDesignTimeDbContextFactory`, không cần chạy host):

```bash
dotnet ef migrations add <TenMigration> \
  --project repos/linguabridge/src/Infrastructure/LinguaBridge.Infrastructure.Persistence \
  --startup-project repos/linguabridge/src/Infrastructure/LinguaBridge.Infrastructure.Persistence \
  --output-dir Migrations
```

Cần `dotnet-ef` 10.x (`dotnet tool install --global dotnet-ef --version 10.*`). Unit test `Model_has_no_changes_missing_from_the_migrations` sẽ fail nếu sửa model mà quên tạo migration.

---

## 4. Khởi Chạy API

```bash
dotnet run --project repos/linguabridge/src/Presentation/LinguaBridge.Api --launch-profile https
```

| Profile | URL |
|---|---|
| `https` | `https://localhost:44320` và `http://localhost:5020` |
| `http` | `http://localhost:5020` |

Hoặc trong Visual Studio: chọn `LinguaBridge.Api` làm Startup Project, profile `https`, nhấn F5.

### Endpoint kiểm tra

- `GET /health` →
  ```json
  { "status": "Healthy", "module": "LinguaBridge.Api", "timestamp": "2026-10-28T14:52:59.8067455+00:00" }
  ```
- Swagger UI: `https://localhost:44320/swagger` — chỉ bật ở môi trường `Development`, hoặc khi đặt `Swagger:Enabled = true`.

### Cấu hình

| Khóa | Ý nghĩa |
|---|---|
| `ConnectionStrings:LinguaBridgeDb` | Chuỗi kết nối PostgreSQL (mỗi module dùng schema riêng). `appsettings.Development.json` trỏ tới `localhost:5432`. |
| `Swagger:Enabled` | Bật Swagger ngoài môi trường Development. |
| `Authentication:Authority` | URL gốc của Identity STS (dev: `https://localhost:44310`). **Bắt buộc** ngoài Development. |
| `Authentication:ClientId` / `Audience` | `linguabridge_api` — client resource-server và audience của scope cùng tên. |
| `Authentication:ClientSecret` | Secret introspection. **Bắt buộc** ngoài Development; lấy từ biến môi trường / user-secrets, không commit. |

---

## 5. Authentication

`LinguaBridge.Api` không tự phát token. Access token do **AppInHouse Identity STS** phát hành (và mã hóa), nên API xác thực bằng **OpenIddict Validation ở chế độ introspection** — mỗi request có token sẽ gọi STS một lần để hỏi token còn hiệu lực không — giống `AppInHouse.Identity.Admin.Api`. (`AddJwtBearer` không đọc được token mã hóa.)

- **Deny-by-default**: mọi endpoint yêu cầu đăng nhập (`FallbackPolicy`), trừ `GET /health` (`AllowAnonymous`) và Swagger (middleware, đứng trước auth).
- **Fail fast**: ngoài Development, thiếu `Authentication:Authority` hoặc `Authentication:ClientSecret` thì host không khởi động.
- **`ICurrentUser`** (`LinguaBridge.Application.Abstractions`): `IsAuthenticated`, `UserId` (claim `sub`, `null` nếu không phải Guid, vd. token `client_credentials`), `ClientId`, `IsInRole(string)`. Chỉ chứa danh tính; quyền lợi Free/Vip tra trong module Billing, không đọc từ token.
- **`GET /api/v1/me`** → `{ userId, clientId, isAuthenticated }`; 401 khi không có token.

### Chạy thử với STS thật

1. Chạy STS (`https://localhost:44310`). Seeder tạo scope + client `linguabridge_api` khi `Seeding:LinguaBridgeApiClientSecret` có giá trị (đã đặt sẵn trong `appsettings.Development.json` của STS). Seeder bỏ qua dữ liệu đã tồn tại, nên DB dev cũ cần seed lại (xóa DB hoặc thêm thủ công).
2. Chạy `LinguaBridge.Api`, lấy token từ STS cho scope `linguabridge_api`, rồi bấm **Authorize** trong Swagger và dán token.

> Scope `linguabridge_api` chưa được cấp cho client nào (`admin-portal`, `internal-service`); client học viên (mobile/web, PKCE) sẽ làm ở task riêng.
