# Hướng Dẫn Cài Đặt & Vận Hành Hệ Thống Identity Platform
## AppInHouse Centralized Identity Server & Admin Portal (.NET 8 LTS)

Tài liệu này hướng dẫn chi tiết cách chuẩn bị môi trường, cấu hình cơ sở dữ liệu (SQL Server), chạy migrations, khởi chạy ứng dụng và tích hợp các dịch vụ con với nền tảng **AppInHouse Identity**.

---

## 1. Yêu Cầu Môi Trường (Prerequisites)

Trước khi bắt đầu, hãy đảm bảo máy tính hoặc máy chủ của bạn đã cài đặt:

1. **.NET 8 SDK**:
   * Phiên bản: `.NET SDK 8.0.100` trở lên (kiểm tra bằng lệnh `dotnet --version`).
   * Tải về từ: [https://dotnet.microsoft.com/download/dotnet/8.0](https://dotnet.microsoft.com/download/dotnet/8.0).
2. **Cơ sở dữ liệu (Database)**:
   * Hỗ trợ **SQL Server 2019 / 2022**, **SQL Server Express**, **SQL Server LocalDB**, hoặc **SQL Server trên Docker**.
3. **Công cụ dòng lệnh EF Core CLI (Tùy chọn)**:
   * Cài đặt công cụ toàn cục để quản lý migrations:
     ```bash
     dotnet tool install --global dotnet-ef
     ```

---

## 2. Cấu Trúc Solution & Các Dự Án

Toàn bộ mã nguồn của Identity Platform được đặt trong thư mục `repos/identity/`:

```text
repos/identity/
├── AppInHouse.Identity.sln                # Solution riêng của Identity
├── Directory.Build.props                  # Cấu hình build chung (.NET 8, Nullable, ImplicitUsings)
├── src/
│   ├── Core/
│   │   ├── AppInHouse.Identity.Domain/              # Thực thể nghiệp vụ (Project, Permission, AuditLog...)
│   │   ├── AppInHouse.Identity.Application/         # DTOs, CQRS Interfaces, Permission Service, Cache Invalidation
│   │   └── AppInHouse.Identity.SharedKernel/        # Constants, Helpers, Guard Clauses
│   ├── Infrastructure/
│   │   ├── AppInHouse.Identity.Infrastructure.Identity/     # ASP.NET Core Identity Models (ApplicationUser, Role)
│   │   ├── AppInHouse.Identity.Infrastructure.Persistence/  # EF Core DbContexts, Fluent Configurations, Migrations
│   │   ├── AppInHouse.Identity.Infrastructure.Caching/      # Versioned In-Memory/Redis Permission Cache
│   │   └── AppInHouse.Identity.Infrastructure.Logging/      # Serilog Structured Logging
│   ├── Presentation/
│   │   ├── AppInHouse.Identity.STS/                 # Identity Provider (OpenIddict OIDC Engine, Account Login UI)
│   │   ├── AppInHouse.Identity.Admin.Api/           # Admin REST API (27 Endpoints v1, Swagger OpenAPI)
│   │   └── AppInHouse.Identity.Admin.Web/           # Admin Portal Web UI (MVC + Bootstrap 5 Skoruba-style)
│   └── Sdk/
│       └── AppInHouse.Identity.ClientSdk/           # Thư viện phân quyền động [HasPermission] dùng cho API con
└── tests/
    ├── AppInHouse.Identity.UnitTests/               # 170 Unit Tests (Domain, Security, Invalidation)
    └── AppInHouse.Identity.IntegrationTests/        # 282 Integration Tests (OIDC Flows, Admin API, Web Portal)
```

---

## 3. Cấu Hình Cơ Sở Dữ Liệu (Database Setup)

Hệ thống sử dụng **3 DbContext riêng biệt** dùng chung một Database nhờ cơ chế phân tách bảng lịch sử di trú (`__EFMigrationsHistory_{Context}`):
1. `AppIdentityDbContext`: Chứa bảng người dùng ASP.NET Identity (`AspNet*`) và OpenIddict (`OpenIddict*`).
2. `AppAuthDbContext`: Chứa bảng quản lý dự án và phân quyền động (`Projects`, `Permissions`, `RolePermissions`, `ProjectUserAssignments`, `UserRoleProjectAssignments`, `UserPermissionAssignments`, `PermissionCacheVersions`).
3. `AppAuditDbContext`: Chứa bảng nhật ký kiểm toán (`AuditLogs`, `ConfigurationVersions`).

### 3.1. Thiết Lập Chuỗi Kết Nối (Connection String)

Mở các file cấu hình sau để cập nhật connection string phù hợp với máy của bạn:
* `src/Presentation/AppInHouse.Identity.STS/appsettings.json`
* `src/Presentation/AppInHouse.Identity.Admin.Api/appsettings.json`
* `src/Presentation/AppInHouse.Identity.Admin.Web/appsettings.json`

**Ví dụ cấu hình mẫu**:
```json
{
  "ConnectionStrings": {
    "IdentityDb": "Server=(localdb)\\mssqllocaldb;Database=AppInHouse_IdentityDb;Trusted_Connection=True;MultipleActiveResultSets=true;TrustServerCertificate=True"
  }
}
```

* **Nếu dùng SQL Server LocalDB**:
  `Server=(localdb)\\mssqllocaldb;Database=AppInHouse_IdentityDb;Trusted_Connection=True;TrustServerCertificate=True`
* **Nếu dùng SQL Server Express**:
  `Server=localhost\\SQLEXPRESS;Database=AppInHouse_IdentityDb;Trusted_Connection=True;TrustServerCertificate=True`
* **Nếu dùng SQL Server Docker / Server có mật khẩu**:
  `Server=localhost,1433;Database=AppInHouse_IdentityDb;User Id=sa;Password=YourStrongPassword123!;TrustServerCertificate=True`

---

### 3.2. Tạo Bảng Vật Lý Trên SQL Server (Tự Động Khi Khởi Động)

Khi chạy `AppInHouse.Identity.STS` hoặc `AppInHouse.Identity.Admin.Web` lần đầu với `"Database": { "MigrateOnStartup": true }` (đã bật sẵn trong `appsettings.Development.json` của cả hai), hệ thống **tự động áp dụng toàn bộ migration** theo thứ tự `AppIdentityDbContext` → `AppAuthDbContext` → `AppAuditDbContext` (23 bảng + 3 bảng `__EFMigrationsHistory_*`). Riêng STS còn **tự seed** tài khoản `admin@appinhouse.local`, vai trò `GlobalAdmin`, scope và các OpenIddict client mặc định ngay sau khi migrate (`Seeding:Enabled`). Với cơ sở dữ liệu không quan hệ (InMemory dùng trong test) bước migrate được bỏ qua.

Nên chạy STS trước; nếu chạy đồng thời nhiều ứng dụng trên một DB trống, hãy khởi động lần lượt để tránh hai tiến trình cùng migrate.

#### Phương án thay thế: chạy migration thủ công bằng CLI (tùy chọn)

Nếu không muốn tự động (ví dụ Production, `MigrateOnStartup=false`), từ thư mục gốc `AppInHouse` chạy lần lượt 3 lệnh migration để áp dụng toàn bộ 23 bảng vào database:

```bash
# 1. Migrate bảng Identity & OpenIddict
dotnet ef database update --project repos/identity/src/Infrastructure/AppInHouse.Identity.Infrastructure.Persistence --startup-project repos/identity/src/Presentation/AppInHouse.Identity.STS --context AppIdentityDbContext

# 2. Migrate bảng Phân quyền động & Multi-Project
dotnet ef database update --project repos/identity/src/Infrastructure/AppInHouse.Identity.Infrastructure.Persistence --startup-project repos/identity/src/Presentation/AppInHouse.Identity.STS --context AppAuthDbContext

# 3. Migrate bảng Nhật ký kiểm toán Audit Trail
dotnet ef database update --project repos/identity/src/Infrastructure/AppInHouse.Identity.Infrastructure.Persistence --startup-project repos/identity/src/Presentation/AppInHouse.Identity.STS --context AppAuditDbContext
```

> **Lưu ý**: `Database:MigrateOnStartup` mặc định là `false` ở `appsettings.json` (Production nên migrate bằng CLI hoặc script có kiểm soát) và `true` ở `appsettings.Development.json`. `Database:EnsureCreated` chỉ còn là phương án cũ cho test in-memory.

---

## 4. Dữ Liệu Khởi Tạo Mặc Định (Default Seed Data)

Khi khởi động lần đầu, hệ thống sẽ tự động khởi tạo dữ liệu mẫu (Idempotent Database Seeder):

* **Tài khoản Quản trị Toàn cục (Global Administrator)**:
  * **Email**: `admin@appinhouse.local`
  * **Mật khẩu**: `Admin@AppInHouse123!`
  * **Role**: `GlobalAdmin` (toàn quyền quản trị hệ thống)
* **Các OIDC Scopes mặc định**:
  * `openid`, `profile`, `email`, `offline_access`, `identity_admin_api`
* **Các OIDC Clients mặc định**:
  1. `admin-portal`:
     * Dành cho: Giao diện Web Quản trị (Admin Portal UI).
     * Loại flow: **Authorization Code Flow + PKCE**.
     * Redirect URI: `https://localhost:44303/signin-oidc`
  2. `internal-service`:
     * Dành cho: Microservices, Machine-to-Machine.
     * Loại flow: **Client Credentials Flow**.
     * Secret: `internal-secret-for-dev-only-32chars!`
  3. `identity_admin_api`:
     * Dành cho: Resource Server bảo vệ Admin REST API qua Introspection.

---

## 5. Hướng Dẫn Khởi Chạy Ứng Dụng (Run & Launch)

Hệ thống bao gồm 3 ứng dụng web độc lập phối hợp với nhau:

### 5.1. Khởi chạy bằng .NET CLI (3 Cửa Sổ Terminal Riêng Biệt)

**Terminal 1 — Security Token Service (STS) (Port: 44310 HTTPS / 5001 HTTP)**:
```bash
dotnet run --project repos/identity/src/Presentation/AppInHouse.Identity.STS
```
* **Discovery Document**: [https://localhost:44310/.well-known/openid-configuration](https://localhost:44310/.well-known/openid-configuration)
* **Trang Đăng Nhập**: [https://localhost:44310/Account/Login](https://localhost:44310/Account/Login)

---

**Terminal 2 — Admin REST API (Port: 44302 HTTPS / 5002 HTTP)**:
```bash
dotnet run --project repos/identity/src/Presentation/AppInHouse.Identity.Admin.Api
```
* **Swagger OpenAPI UI**: [https://localhost:44302/swagger](https://localhost:44302/swagger)

---

**Terminal 3 — Admin Web Portal UI (Port: 44303 HTTPS / 5000 HTTP)**:
```bash
dotnet run --project repos/identity/src/Presentation/AppInHouse.Identity.Admin.Web
```
* **Giao Diện Quản Trị**: [https://localhost:44303](https://localhost:44303)

---

### 5.2. Khởi chạy bằng Visual Studio
1. Mở file [AppInHouse.sln](../../AppInHouse.sln) tại thư mục gốc bằng Visual Studio 2022.
2. Click chuột phải vào Solution -> Chọn **Configure Startup Projects...**.
3. Chọn **Multiple startup projects** và đặt `Start` cho:
   * `AppInHouse.Identity.STS`
   * `AppInHouse.Identity.Admin.Api`
   * `AppInHouse.Identity.Admin.Web`
4. Bấm **F5** để khởi chạy toàn bộ hệ thống.

---

## 6. Hướng Dẫn Sử Dụng Giao Diện Admin Portal (Skoruba-Style)

Truy cập [https://localhost:44303](https://localhost:44303) và đăng nhập bằng tài khoản `admin@appinhouse.local` / `Admin@AppInHouse123!`:

1. **Dashboard Tổng Quan**:
   * Xem thống kê nhanh số lượng Dự án, Clients, Người dùng, Vai trò, Quyền hạn và các sự kiện bảo mật gần nhất.
2. **Quản Lý Dự Án (Projects)**:
   * Bấm `Create Project` để đăng ký dự án mới (ví dụ `ERP`, `AIChatBot`, `MobileApp`).
   * Dự án mới có hiệu lực ngay lập tức, không cần sửa code hay restart server.
   * Khóa / Mở khóa dự án khi cần bảo trì.
3. **Quản Lý Clients (Client Studio)**:
   * Tạo client OIDC theo từng loại ứng dụng: `Web Application`, `Single Page Application (SPA)`, `Mobile App`, hoặc `Machine-to-Machine`.
   * Cấu hình Redirect URIs và gán Allowed Scopes.
   * Tạo / Xoay vòng Client Secret: Secret chỉ hiển thị **đúng 1 lần** kèm khuyến nghị lưu trữ an toàn (hệ thống chỉ lưu hash).
4. **Quản Lý Người Dùng & Phân Bổ Dự Án (Users & Assignments)**:
   * Thêm người dùng mới, khóa tài khoản khi có sự cố, đổi mật khẩu.
   * Phân bổ người dùng vào từng dự án cụ thể (`ProjectUserAssignment`), chỉ định vai trò Quản trị viên dự án (`IsProjectAdmin`).
5. **Ma Trận Phân Quyền (RBAC Studio)**:
   * Truy cập `Roles` -> Chọn `Permission Matrix`.
   * Giao diện ma trận checkbox phân chia theo từng module (`Users`, `Roles`, `Clients`, `Projects`...) cho phép gán quyền nhanh cho từng Role.
   * Thao tác lưu ma trận sẽ tự động kích hoạt **Cache Invalidation** tức thì (< 1 giây).
6. **Nhật Ký Kiểm Toán (Audit Trail)**:
   * Tra cứu lịch sử thao tác theo Người thực hiện (Actor), Dự án, Loại đối tượng, Hành động (CREATE, UPDATE, REVOKE...), và Thời gian.
   * Bấm nút `View Details` trên từng dòng để mở modal xem diff chi tiết `Old Values` vs `New Values` dưới dạng JSON định dạng đẹp.

---

## 7. Hướng Dẫn Tích Hợp Client SDK Cho Các API Con

Bất kỳ ứng dụng API nội bộ nào (như `ERP.Api`, `ChatBot.Api`) muốn được bảo vệ bởi hệ thống phân quyền động đều có thể tích hợp thư viện `AppInHouse.Identity.ClientSdk`:

### 7.1. Cài đặt Project Reference
Thêm tham chiếu tới project `AppInHouse.Identity.ClientSdk.csproj`.

### 7.2. Đăng ký trong `Program.cs` của API con
```csharp
using AppInHouse.Identity.ClientSdk;

var builder = WebApplication.CreateBuilder(args);

// 1. Cấu hình xác thực Bearer Token OIDC
builder.Services.AddAuthentication("Bearer")
    .AddJwtBearer("Bearer", options =>
    {
        options.Authority = "https://localhost:44310"; // Địa chỉ STS
        options.Audience = "identity_admin_api";
        options.RequireHttpsMetadata = false; // bật true trên production
    });

// 2. Đăng ký Client SDK phân quyền động
builder.Services.AddAppInHouseAuthorization();

builder.Services.AddControllers();

var app = builder.Build();

app.UseRouting();

// 3. Kích hoạt Middleware nhận diện ngữ cảnh dự án (từ Header X-Project-Code hoặc Route)
app.UseAppInHouseProjectContext();

app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();
app.Run();
```

### 7.3. Bảo vệ Controller / Action bằng Attribute
```csharp
[ApiController]
[Route("api/[controller]")]
public class OrdersController : ControllerBase
{
    // Yêu cầu quyền "orders.view" trong đúng dự án được gửi qua header X-Project-Code
    [HttpGet]
    [HasPermission("orders.view")]
    public IActionResult GetOrders()
    {
        return Ok(new[] { "Order #1", "Order #2" });
    }

    // Yêu cầu quyền "orders.create"
    [HttpPost]
    [HasPermission("orders.create")]
    public IActionResult CreateOrder()
    {
        return Ok("Created order successfully");
    }
}
```

---

## 8. Chạy Bộ Kiểm Thử Tự Động (Automated Testing)

Solution đi kèm bộ test toàn diện 452 bài test kiểm tra mọi ngóc ngách từ unit test đến end-to-end integration test:

```bash
# Chạy toàn bộ test trong solution
dotnet test repos/identity/AppInHouse.Identity.sln

# Hoặc chạy từ solution master ở thư mục gốc:
dotnet test AppInHouse.sln
```

**Bao gồm**:
* **170 Unit Tests**: Kiểm tra tính bất biến của Domain Entities (Guard clauses), Thuật toán Invalidation theo Composite Version (`Global + User + Project`), Phân quyền chống leo quyền và kiểm tra Model Snapshot.
* **282 Integration Tests**: Kiểm tra toàn bộ luồng Authorization Code + PKCE, Token Rotation, Introspection, Chống IDOR chéo dự án, Chống CSRF trên Portal và Thu hồi quyền tức thì (< 1 giây).
