# LinguaBridge Backend
## Modular Monolith API cho nền tảng học Tiếng Anh – Tiếng Trung & Giao Tiếp Thương Mại (.NET 10)

Tài liệu khởi chạy sơ bộ cho backend LinguaBridge. Thiết kế đầy đủ xem tại [docs/architecture/language_learning_platform_design.md](../../docs/architecture/language_learning_platform_design.md).

---

## 1. Yêu Cầu Môi Trường

- **.NET 10 SDK** (`dotnet --list-sdks` phải có `10.0.x`).
- **PostgreSQL** — chưa bắt buộc ở giai đoạn này: chưa có DbContext nào, API khởi chạy được khi chưa có database.

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
