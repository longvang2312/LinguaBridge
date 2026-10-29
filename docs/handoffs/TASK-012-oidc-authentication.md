# TASK-012: Xác Thực `LinguaBridge.Api` Qua Identity STS (OpenIddict Introspection)

- **Mã Task**: TASK-012
- **Tên Task**: Bảo vệ `LinguaBridge.Api` bằng access token do AppInHouse Identity STS phát hành, đăng ký scope/resource-server `linguabridge_api` trong STS Seeder, thêm `ICurrentUser`
- **Người Giao (Tech Lead)**: Claude (thay vai Gemini)
- **Người Thực Hiện (Implementer)**: Claude Code
- **Trạng Thái**: `PENDING`
- **Ngày Tạo**: 2026-10-28 (viết lại cùng ngày sau review TASK-011)
- **Phụ Thuộc**: TASK-011 (`APPROVED`)
- **Tài Liệu Tham Chiếu**: [docs/architecture/language_learning_platform_design.md](../architecture/language_learning_platform_design.md) (mục Bảo mật, Billing & Entitlements)

> **Vì sao task được viết lại** — bản đầu không chạy được với STS hiện tại:
> 1. **STS mã hóa access token** (`AddEncryptionCertificate`, không `DisableAccessTokenEncryption`). `AddJwtBearer` không đọc được token mã hóa → mọi request sẽ 401. Phải validate giống `AppInHouse.Identity.Admin.Api`: **OpenIddict Validation + introspection**.
> 2. **Seeder không đọc client từ JSON.** Scope và client được tạo bằng code trong `DbSeeder.cs`; thêm mục `{ "ClientId": ..., "Type": "ApiResource" }` vào appsettings không có tác dụng.
> 3. **`AppInHouse.Identity.ClientSdk` không phải thư viện xác thực** — nó cung cấp `[HasPermission]` và cần `IPermissionService` đọc DB Identity. Không dùng ở task này.
> 4. **`entitlement_tier` không nằm trong token.** Thiết kế quy định quyền lợi Free/Vip tra trong `billing.user_entitlements` (module Billing sở hữu). Đưa vào token sẽ bị cũ cho tới khi token hết hạn và tạo phụ thuộc ngược Identity → Billing. Bỏ khỏi `ICurrentUser`; `IEntitlementService` thuộc TASK-016.
> 5. Số test nền hiện tại là **141** (119 unit + 22 integration), không phải 107.

---

## 1. Mục Tiêu Kỹ Thuật

1. Mọi endpoint của `LinguaBridge.Api` **mặc định yêu cầu người gọi đã xác thực** (deny-by-default qua `FallbackPolicy`), trừ `/health` và Swagger.
2. Access token được validate bằng **OpenIddict Validation, chế độ introspection** tới STS `https://localhost:44310` — giống hệt mô hình của `Identity.Admin.Api`.
3. STS Seeder đăng ký scope `linguabridge_api` (resource `linguabridge_api`) và client resource-server `linguabridge_api` (confidential, chỉ có quyền introspection).
4. Thêm `ICurrentUser` để các module sau đọc user hiện tại mà không phụ thuộc `HttpContext`.
5. Thêm endpoint `GET /api/v1/me` (yêu cầu xác thực) làm endpoint thật đầu tiên được bảo vệ — dùng để test 401/200 và smoke test thủ công.

---

## 2. Phạm Vi File Cấp Phép (File Scope & Whitelist)

> **QUY TẮC BẮT BUỘC DÀNH CHO CLAUDE CODE**:
> Claude Code **CHỈ ĐƯỢC PHÉP** tạo hoặc sửa các file trong danh sách dưới đây. Tuyệt đối không sửa `AGENTS.md`, `CLAUDE.md`, `docs/architecture/*`. Trong `repos/identity/` **chỉ** được sửa đúng các file liệt kê.

**LinguaBridge**
- [ ] `repos/linguabridge/src/Presentation/LinguaBridge.Api/*` (`.csproj`, `Program.cs`, `appsettings*.json`, thư mục mới `Auth/` hoặc `Endpoints/`)
- [ ] `repos/linguabridge/src/Core/LinguaBridge.Application/Abstractions/ICurrentUser.cs` (tạo mới)
- [ ] `repos/linguabridge/tests/LinguaBridge.UnitTests/*`
- [ ] `repos/linguabridge/tests/LinguaBridge.IntegrationTests/*`
- [ ] `repos/linguabridge/README.md` (mục Authentication + lệnh `dotnet ef` với startup project = Persistence)

**Identity (ngoại lệ có kiểm soát)**
- [ ] `repos/identity/src/Infrastructure/AppInHouse.Identity.Infrastructure.Persistence/Seeders/DbSeeder.cs`
- [ ] `repos/identity/src/Infrastructure/AppInHouse.Identity.Infrastructure.Persistence/Seeders/DbSeederOptions.cs`
- [ ] `repos/identity/src/Presentation/AppInHouse.Identity.STS/Program.cs` (chỉ dòng `RegisterScopes`)
- [ ] `repos/identity/src/Presentation/AppInHouse.Identity.STS/appsettings.Development.json` (chỉ thêm secret)
- [ ] `repos/identity/tests/AppInHouse.Identity.IntegrationTests/*` (test cho seeder — chỉ thêm, không sửa test cũ)

---

## 3. Chi Tiết Yêu Cầu Kỹ Thuật

### 3.1. STS: Scope + Resource-Server Client

Theo đúng mẫu đang có của `identity_admin_api` trong `DbSeeder.cs` (scope name = resource = client id, vì OpenIddict chỉ cho client introspect token có audience chứa client id của nó):

- `DbSeeder`: hằng số `LinguaBridgeApiScope = "linguabridge_api"`, `LinguaBridgeApiResourceClientId = LinguaBridgeApiScope`.
- Thêm `OpenIddictScopeDescriptor { Name = "linguabridge_api", DisplayName = "LinguaBridge API", Resources = { "linguabridge_api" } }`.
- Thêm client `BuildLinguaBridgeApiResourceServer()`: `Confidential`, `Permissions = { Endpoints.Introspection }`, secret từ `DbSeederOptions.LinguaBridgeApiClientSecret`. **Chỉ seed khi secret có giá trị** (giống `AdminApiClientSecret`).
- `DbSeederOptions`: thêm `LinguaBridgeApiClientSecret` (có XML doc như property bên cạnh).
- STS `Program.cs`: thêm `DbSeeder.LinguaBridgeApiScope` vào `RegisterScopes(...)` để xuất hiện trong discovery document.
- STS `appsettings.Development.json` → `Seeding.LinguaBridgeApiClientSecret = "dev-linguabridge-api-secret-change-me"`.
- **Không** cấp scope này cho client hiện có (`admin-portal`, `internal-service`): seeder bỏ qua client đã tồn tại nên sửa permission sẽ không áp dụng lên DB dev, và client học viên (mobile/web, PKCE) sẽ là task riêng.

### 3.2. LinguaBridge.Api: OpenIddict Validation

Packages (cùng major với STS là **5.x**):
```xml
<PackageReference Include="OpenIddict.Validation.AspNetCore" Version="5.*" />
<PackageReference Include="OpenIddict.Validation.SystemNetHttp" Version="5.*" />
```

Cấu hình — tham chiếu trực tiếp `repos/identity/src/Presentation/AppInHouse.Identity.Admin.Api/Program.cs` (dòng ~39-68):
```csharp
builder.Services.AddOpenIddict()
    .AddValidation(options =>
    {
        options.UseIntrospection();
        options.UseSystemNetHttp();
        options.UseAspNetCore();
    });
// Đọc Authentication:Authority / ClientId / ClientSecret / Audience lazily qua
// AddOptions<OpenIddictValidationOptions>().Configure<IConfiguration>(...), như Admin.Api.

builder.Services.AddAuthentication(OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme);
builder.Services.AddAuthorization(options =>
    options.FallbackPolicy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());
```

`appsettings.Development.json`:
```json
"Authentication": {
  "Authority": "https://localhost:44310",
  "ClientId": "linguabridge_api",
  "ClientSecret": "dev-linguabridge-api-secret-change-me",
  "Audience": "linguabridge_api"
}
```
`appsettings.json`: cùng khối với giá trị rỗng (secret production lấy từ env / user-secrets).

**Fail fast**: ngoài môi trường Development, nếu `Authority` hoặc `ClientSecret` rỗng → ném `InvalidOperationException` lúc khởi động (cùng kiểu thông báo STS dùng cho certificate).

Pipeline: `UseSwagger/UseSwaggerUI` → `UseHttpsRedirection` → `UseAuthentication` → `UseAuthorization` → map endpoint. Swagger là middleware đứng trước auth nên không bị `FallbackPolicy` chặn — giữ thứ tự này và có test chứng minh.

- `/health` → `.AllowAnonymous()`.
- Swagger: thêm `SecurityDefinition` kiểu Bearer + `SecurityRequirement` để dán token thử.

### 3.3. `ICurrentUser`

`repos/linguabridge/src/Core/LinguaBridge.Application/Abstractions/ICurrentUser.cs`:
```csharp
public interface ICurrentUser
{
    bool IsAuthenticated { get; }
    Guid? UserId { get; }          // claim "sub"; null khi không có hoặc không phải Guid (vd. token client_credentials)
    string? ClientId { get; }      // claim "client_id" / "azp"
    bool IsInRole(string role);    // claim "role" (STS phát role vào access token)
}
```
- Implementation `HttpContextCurrentUser` đặt trong **`LinguaBridge.Api`** (thư mục `Auth/`), không đặt ở Infrastructure.External: `HttpContext` là mối quan tâm của tầng Presentation, tránh kéo `Microsoft.AspNetCore.App` vào Infrastructure.
- Đăng ký `AddHttpContextAccessor()` + `AddScoped<ICurrentUser, HttpContextCurrentUser>()` trong `Program.cs`.
- `sub` của STS là `IdentityUser.Id` (chuỗi Guid) → parse bằng `Guid.TryParse`.
- **Không** có `EntitlementTier`/`IsPremium` (lý do ở đầu task).

### 3.4. Endpoint `GET /api/v1/me`

Trả về `{ userId, clientId, isAuthenticated }` từ `ICurrentUser`. Không cần `.RequireAuthorization()` vì `FallbackPolicy` đã áp — test phải chứng minh điều đó.

### 3.5. Tests

**Integration (`LinguaBridge.IntegrationTests`)**
- `TestAuthHandler` chỉ xác thực **khi request có header** `X-Test-User: <guid>` (có thể kèm `X-Test-Roles`); không có header → `AuthenticateResult.NoResult()`. Như vậy cùng một factory test được cả 401 lẫn 200. Đăng ký trong một factory riêng qua `ConfigureTestServices` với default scheme = test scheme.
- Test bắt buộc:
  - `/health` không token → 200 (factory gốc, scheme OpenIddict thật).
  - `/swagger/v1/swagger.json` không token → 200, và document có security scheme Bearer.
  - `/api/v1/me` không token → **401** trên factory gốc (scheme OpenIddict thật, không cần STS chạy vì không có token để introspect).
  - `/api/v1/me` với `X-Test-User` → 200, `userId` khớp header.
  - `ICurrentUser` resolve được trong request scope; `sub` không phải Guid → `UserId == null`.
- Factory gốc hiện chạy `Development`, nên cấu hình auth có sẵn. Thêm test: môi trường `Production` thiếu `Authentication:Authority` → host khởi động thất bại.

**Identity (`AppInHouse.Identity.IntegrationTests`)**
- Seeder tạo scope `linguabridge_api` và client `linguabridge_api` khi có secret; **không** tạo client khi secret rỗng. Theo đúng mẫu test seeder hiện có của `identity_admin_api` (nếu có) — chỉ thêm test, không sửa test cũ.

### 3.6. Smoke test thủ công (khuyến nghị, không bắt buộc cho nghiệm thu)

Chạy STS (`https://localhost:44310`) với DB dev đã seed lại, kiểm tra `/.well-known/openid-configuration` có `linguabridge_api` trong `scopes_supported`. Ghi kết quả vào báo cáo nếu thực hiện được.

---

## 4. Tiêu Chí Nghiệm Thu (Acceptance Criteria)

- [ ] `LinguaBridge.Api` dùng OpenIddict Validation (introspection) với `Authority`/`ClientId`/`ClientSecret`/`Audience` từ cấu hình; **không** dùng `AddJwtBearer`.
- [ ] `FallbackPolicy` yêu cầu xác thực; `/health` và Swagger vẫn mở.
- [ ] `GET /api/v1/me` → 401 khi không token; 200 với test user.
- [ ] `ICurrentUser` không chứa thông tin entitlement.
- [ ] STS Seeder tạo scope + resource-server client `linguabridge_api`; scope có trong `RegisterScopes`.
- [ ] Host fail fast ngoài Development khi thiếu cấu hình auth.
- [ ] `dotnet build repos/linguabridge/LinguaBridge.sln` và `dotnet build AppInHouse.sln` — 0 warning, 0 error.
- [ ] `dotnet test repos/linguabridge/LinguaBridge.sln` — tất cả PASS (≥ 141 + test mới).
- [ ] `dotnet test` cho `repos/identity` — tất cả PASS, không regression.
- [ ] Không file nào ngoài whitelist bị sửa.

---

## 5. Lưu Ý Cho Claude Code

1. **Introspection = 1 HTTP call tới STS mỗi request có token.** Chấp nhận cho v1. Ghi nhận trong báo cáo nếu thấy OpenIddict 5 có cơ chế cache sẵn; nếu không, Tech Lead sẽ ra ADR riêng (cache introspection vs. STS phát JWT không mã hóa để validate local).
2. **OpenIddict 5.x trên net10.0**: package target net8.0, chạy được trên net10. Nếu gặp xung đột phụ thuộc (`Microsoft.IdentityModel.*`), báo lại thay vì nâng major OpenIddict — STS đang ở 5.x.
3. Các file `repos/identity/*` trong `git status` đang có thay đổi chưa commit từ TASK-007/008. **Không** revert hoặc format lại chúng; chỉ thêm đúng phần của task này.
4. TASK-013 dùng `ICurrentUser.IsInRole(...)` cho policy quản trị nội dung — giữ chữ ký `IsInRole(string)`.

---

## 6. Báo Cáo Triển Khai (Claude Code điền khi hoàn thành)

### a. Danh sách file đã tác động
- `...`

### b. Kiểm thử thực tế (Commands & Results)
```bash
dotnet build repos/linguabridge/LinguaBridge.sln
dotnet test repos/linguabridge/LinguaBridge.sln
dotnet build AppInHouse.sln
dotnet test repos/identity/tests/AppInHouse.Identity.IntegrationTests
```

### c. Rủi ro, đánh đổi kỹ thuật hoặc lưu ý cho Tech Lead
-
