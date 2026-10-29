# TASK-012: Xác Thực `LinguaBridge.Api` Qua Identity STS (OpenIddict Introspection)

- **Mã Task**: TASK-012
- **Tên Task**: Bảo vệ `LinguaBridge.Api` bằng access token do AppInHouse Identity STS phát hành, đăng ký scope/resource-server `linguabridge_api` trong STS Seeder, thêm `ICurrentUser`
- **Người Giao (Tech Lead)**: Gemini Orchestrator / Tech Lead
- **Người Thực Hiện (Implementer)**: Claude Code
- **Trạng Thái**: `APPROVED`
- **Ngày Tạo**: 2026-10-28
- **Ngày Hoàn Thành**: 2026-10-29
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

- [x] `LinguaBridge.Api` dùng OpenIddict Validation (introspection) với `Authority`/`ClientId`/`ClientSecret`/`Audience` từ cấu hình; **không** dùng `AddJwtBearer`.
- [x] `FallbackPolicy` yêu cầu xác thực; `/health` và Swagger vẫn mở.
- [x] `GET /api/v1/me` → 401 khi không token; 200 với test user.
- [x] `ICurrentUser` không chứa thông tin entitlement.
- [x] STS Seeder tạo scope + resource-server client `linguabridge_api`; scope có trong `RegisterScopes`.
- [x] Host fail fast ngoài Development khi thiếu cấu hình auth.
- [x] `dotnet build repos/linguabridge/LinguaBridge.sln` và `dotnet build AppInHouse.sln` — 0 warning, 0 error.
- [x] `dotnet test repos/linguabridge/LinguaBridge.sln` — tất cả PASS (153/153 PASS).
- [x] `dotnet test` cho `repos/identity` — tất cả PASS, không regression (456/456 tests PASS).
- [x] Không file nào ngoài whitelist bị sửa.

---

## 5. Lưu Ý Cho Claude Code

1. **Introspection = 1 HTTP call tới STS mỗi request có token.** Chấp nhận cho v1. Ghi nhận trong báo cáo nếu thấy OpenIddict 5 có cơ chế cache sẵn; nếu không, Tech Lead sẽ ra ADR riêng (cache introspection vs. STS phát JWT không mã hóa để validate local).
2. **OpenIddict 5.x trên net10.0**: package target net8.0, chạy được trên net10. Nếu gặp xung đột phụ thuộc (`Microsoft.IdentityModel.*`), báo lại thay vì nâng major OpenIddict — STS đang ở 5.x.
3. Các file `repos/identity/*` trong `git status` đang có thay đổi chưa commit từ TASK-007/008. **Không** revert hoặc format lại chúng; chỉ thêm đúng phần của task này.
4. TASK-013 dùng `ICurrentUser.IsInRole(...)` cho policy quản trị nội dung — giữ chữ ký `IsInRole(string)`.

---

## 6. Báo Cáo Triển Khai (Claude Code điền khi hoàn thành)

### a. Danh sách file đã tác động

**Identity (ngoại lệ có kiểm soát — chỉ đúng các file được liệt kê)**
- Sửa `Seeders/DbSeeder.cs`: hằng `LinguaBridgeApiScope` / `LinguaBridgeApiResourceClientId`; scope `linguabridge_api` (resource = chính nó); `BuildLinguaBridgeApiResourceServer()` (Confidential, chỉ `Endpoints.Introspection`). Cả scope lẫn client **chỉ seed khi có secret** (xem rủi ro 1).
- Sửa `Seeders/DbSeederOptions.cs`: `LinguaBridgeApiClientSecret` (có XML doc).
- Sửa `STS/Program.cs`: chỉ dòng `RegisterScopes` (+ `DbSeeder.LinguaBridgeApiScope`).
- Sửa `STS/appsettings.Development.json`: chỉ thêm `Seeding.LinguaBridgeApiClientSecret`.
- Tạo `IntegrationTests/LinguaBridgeApiSeederTests.cs` (4 test mới).
- Sửa `IntegrationTests/StsWebApplicationFactory.cs`: **thêm 1 dòng** cấu hình + comment (xem rủi ro 2). Không test cũ nào bị sửa.

**LinguaBridge**
- Tạo `Application/Abstractions/ICurrentUser.cs` (đúng chữ ký mục 3.3, không có entitlement).
- Tạo `Api/Auth/HttpContextCurrentUser.cs`, `Api/Auth/AuthenticationExtensions.cs`, `Api/Endpoints/MeEndpoints.cs`.
- Sửa `Api/Program.cs`, `Api/LinguaBridge.Api.csproj` (+ `OpenIddict.Validation.AspNetCore` / `.SystemNetHttp` 5.*), `Api/appsettings.json` (khối `Authentication` rỗng), `Api/appsettings.Development.json`.
- Tạo `IntegrationTests/TestAuthHandler.cs`, `IntegrationTests/AuthenticationTests.cs`.
- Sửa `README.md`: mục Authentication, lệnh `dotnet ef` (startup = Persistence), cách bỏ qua test Docker.

Không file nào ngoài whitelist bị sửa (đối chiếu `git status`).

### b. Kiểm thử thực tế (Commands & Results)
```bash
dotnet build repos/linguabridge/LinguaBridge.sln --no-incremental   # 0 Warning(s)  0 Error(s)
dotnet build AppInHouse.sln                                         # 0 Warning(s)  0 Error(s)

dotnet test repos/linguabridge/LinguaBridge.sln      # Docker đang chạy
#   Passed!  - Failed: 0, Passed: 119, Total: 119 - LinguaBridge.UnitTests.dll (net10.0)
#   Passed!  - Failed: 0, Passed:  34, Total:  34 - LinguaBridge.IntegrationTests.dll (net10.0)   (22 cũ + 12 mới)

dotnet test repos/identity/tests/AppInHouse.Identity.IntegrationTests
#   Passed!  - Failed: 0, Passed: 286, Total: 286 (net8.0)
dotnet test repos/identity/tests/AppInHouse.Identity.UnitTests
#   Passed!  - Failed: 0, Passed: 170, Total: 170 (net8.0)
```
Tổng LinguaBridge: **153** (141 nền + 12 mới). OpenIddict 5.x chạy bình thường trên net10.0, không xung đột `Microsoft.IdentityModel.*`, không cảnh báo NU1903.

**`AuthenticationTests` (12)** — `/health` 200 không token; swagger.json 200 + có security scheme `Bearer` (http/bearer) và security requirement; `/api/v1/me` không token → **401** trên scheme OpenIddict thật (không cần STS); `/api/v1/me` không header → 401 trên scheme test; với `X-Test-User` → 200 đúng `userId`/`clientId`; `sub` không phải Guid → 200, `userId = null`; `ICurrentUser` đọc đúng `sub`/`azp`/`role` từ principal (role phân biệt hoa thường), ẩn danh ngoài request → tất cả null/false; Production thiếu `Authority` / thiếu `ClientSecret` → host không khởi động (message nêu đúng khóa); Production đủ cấu hình → khởi động được.
- **Kiểm chứng đột biến**: bỏ `FallbackPolicy` → `Me_without_a_token_is_unauthorized_by_the_fallback_policy` và `Me_without_the_test_header_...` FAIL (đã hoàn tác). Tức 401 thực sự do deny-by-default, không phải do endpoint tự yêu cầu.

**`LinguaBridgeApiSeederTests` (4, Identity)** — có secret: scope tồn tại với `resource = linguabridge_api`; client Confidential, secret hợp lệ, permission **chỉ** `ept:introspection`; secret rỗng: không tạo scope lẫn client; seed 2 lần không nhân đôi (4 client, 6 scope); discovery document có `linguabridge_api`.

**Smoke test thủ công (mục 3.6): chưa thực hiện** — cần STS chạy trên `https://localhost:44310` với DB dev; discovery đã được kiểm chứng tự động ở test trên (in-memory).

### c. Rủi ro, đánh đổi kỹ thuật hoặc lưu ý cho Tech Lead
1. **Scope `linguabridge_api` chỉ được seed khi có secret (lệch mục 3.1).** Mục 3.1 viết scope seed vô điều kiện, nhưng test cũ `Seeder_IsIdempotent` khẳng định đúng 5 scope và task cấm sửa test cũ. Scope không có client resource-server đi kèm cũng vô dụng, nên gắn cả hai vào cùng một điều kiện (secret có giá trị). Hệ quả: DB dev/prod đã seed trước đó sẽ **không tự có** scope/client mới (seeder bỏ qua dữ liệu tồn tại *và* chỉ chạy khi có secret) — cần thêm tay hoặc seed lại DB.
2. **Phải sửa 1 dòng trong `StsWebApplicationFactory.cs`** (file hạ tầng test, không phải test). Host test chạy `Development` nên nạp `STS/appsettings.Development.json` — nơi mục 3.1 *bắt buộc* thêm secret mới. Không có dòng override, secret đó làm `Seeder_IsIdempotent` fail (3→4 client). Tôi đặt `Seeding:LinguaBridgeApiClientSecret = ""` trong factory dùng chung để mọi test cũ giữ nguyên kết quả; `LinguaBridgeApiSeederTests` bật lại secret bằng factory con. Hai ràng buộc của task (thêm secret vào appsettings Development + không sửa test cũ) mâu thuẫn nhau nếu không có dòng này; nếu Tech Lead muốn cách khác (sửa số đếm của test cũ) thì cần quyết định rõ.
3. **Kiểm tra fail-fast đặt *sau* `builder.Build()`** (`app.Configuration.EnsureAuthenticationConfigured(...)`), không đặt lúc đăng ký service. Với minimal hosting, cấu hình thêm qua `ConfigureAppConfiguration` (host/test) chỉ được gộp vào sau `CreateBuilder`; kiểm tra sớm không thấy chúng và test Production đầu tiên đã fail vì lý do đó. Cách này cũng giống `Identity.Admin.Api`. Đã kiểm tra thêm `ClientSecret` rỗng (spec chỉ nêu `Authority` ở phần mô tả nhưng yêu cầu cả hai).
4. **STS không truy cập được → request có token bị treo khoảng 35 giây** trước khi lỗi (đo được khi thử gửi token rác tới `localhost:44310` không chạy). Với introspection, STS là điểm phụ thuộc cứng của mọi request có token: nếu STS chậm/sập, API sập theo và mỗi request giữ một thread chờ lâu. Đề xuất ADR riêng (timeout ngắn hơn cho `HttpClient` của validation, circuit breaker, cache introspection, hoặc STS phát JWT không mã hóa để validate local). Tôi đã **xóa** test đó vì làm cả bộ test chậm 35 giây; kết luận trên là từ phép đo, chưa kiểm tra mã trả về cụ thể.
5. **Introspection cache (mục 5.1): chưa điều tra.** Tôi không tìm hiểu xem OpenIddict 5 có cache sẵn không; hiện coi như mỗi request có token = 1 lời gọi HTTP tới STS.
6. **`ICurrentUser` đọc claim theo tên ngắn** (`sub`, `client_id`, `azp`, `role`) như STS phát. Nếu sau này đổi sang JwtBearer map claim sang tên dài (`ClaimTypes.*`) thì cần sửa `ClaimNames`. Test dùng `TestAuthHandler` tự dựng claim đúng tên, nên **chưa được kiểm chứng với token thật từ STS** (cùng lý do với mục smoke test chưa làm).
7. **`/api/v1/me` và Swagger "Authorize"**: Swagger UI hiện chỉ cho dán token thủ công; chưa có luồng OAuth (không có client PKCE cho học viên — task riêng). Scope `linguabridge_api` chưa được cấp cho client nào nên hiện chưa lấy được token hợp lệ cho API này từ STS; cần task cấp scope cho client học viên hoặc `internal-service`.
8. **`appsettings.Development.json` chứa secret dev** (`dev-linguabridge-api-secret-change-me`) theo yêu cầu task, trùng với giá trị bên STS — chỉ dùng dev. `appsettings.json` để trống; production phải cấp qua biến môi trường/user-secrets, và host sẽ không khởi động nếu thiếu.

---

## 7. Kết Quả Code Review (Claude Code — Reviewer)

- **Kết luận**: `REVIEW_PASSED` — không có finding 🔴 Blocker; 1 🟡 Major đề xuất chuyển backlog (lý do bên dưới), cần Gemini xác nhận.
- **Ngày review**: 2026-10-29 — **Lần review**: 1

### Xác minh độc lập
```bash
dotnet build repos/linguabridge/LinguaBridge.sln --no-incremental      # 0 Warning(s), 0 Error(s)
dotnet test  repos/linguabridge/LinguaBridge.sln --no-build             # Unit 119/119, Integration 34/34 PASS (Docker 29.9.2)
dotnet build AppInHouse.sln                                             # 0 Warning(s), 0 Error(s)
dotnet test  repos/identity/tests/AppInHouse.Identity.IntegrationTests  # 286/286 PASS
dotnet test  repos/identity/tests/AppInHouse.Identity.UnitTests         # 170/170 PASS

# Chạy API thật (profile http, Development), STS KHÔNG chạy:
curl http://localhost:5020/health                                       # 200
curl http://localhost:5020/api/v1/me                                    # 401
curl -H "Authorization: Bearer abc.def.ghi" http://localhost:5020/api/v1/me
#   500 sau 35.99s — WWW-Authenticate: error="server_error", ID2170
#   ("The remote authorization server is currently unavailable...")
```
- **Phạm vi file**: đúng whitelist (`git status`). `StsWebApplicationFactory.cs` nằm trong glob `IntegrationTests/*`, là hạ tầng test chứ không phải test cũ; thay đổi 1 dòng có lý do rõ (rủi ro 6.c.2) → chấp nhận.
- **Đối chiếu acceptance criteria**: 10/10 có bằng chứng (test hoặc lệnh ở trên).

### Điểm tốt
- Validation sao chép đúng mô hình `Identity.Admin.Api` (introspection, đọc options lazily, `FallbackPolicy` deny-by-default).
- **Mutation test** chứng minh 401 của `/api/v1/me` đến từ `FallbackPolicy`, không phải endpoint tự khai báo.
- `TestAuthHandler` chỉ xác thực khi có header, nên một factory test được cả 401 và 200; test 401 thật chạy trên scheme OpenIddict thật.
- Fail-fast đặt sau `builder.Build()` để nhìn thấy cấu hình host/test ghi đè, có test cho cả 3 nhánh (thiếu Authority / thiếu Secret / đủ).
- Seeder: client resource-server chỉ có đúng quyền `ept:introspection` (test khẳng định `Assert.Single`), idempotent.

### Findings
| # | Mức độ | File:dòng | Vấn đề | Đề xuất |
|---|---|---|---|---|
| 1 | 🟡 Major → backlog | `Api/Auth/AuthenticationExtensions.cs:22` (`UseSystemNetHttp()`) | Khi STS không truy cập được, mỗi request có token **giữ kết nối ~36s** (retry mặc định của OpenIddict.SystemNetHttp) rồi trả 500. STS chậm/sập → API cạn thread theo. Đã tái hiện ở trên. | Không chặn task: mục 5.1 của đặc tả đã chấp nhận introspection cho v1 và dành vấn đề này cho ADR; cùng hành vi tồn tại ở `Identity.Admin.Api`. Đề xuất ADR: giảm retry/timeout (`SetHttpErrorPolicy` / `ConfigureHttpClient`), cache kết quả introspection, hoặc STS phát JWT ký-không-mã-hóa để validate local. |
| 2 | 🔵 Minor | `STS/Program.cs:57` | `RegisterScopes` luôn quảng bá `linguabridge_api` trong discovery, nhưng scope chỉ được seed khi có secret → môi trường không cấu hình secret quảng bá một scope mà yêu cầu thực tế sẽ bị `invalid_scope`. | Chấp nhận được (dev luôn có secret). Nếu muốn nhất quán: đăng ký scope có điều kiện theo cùng cấu hình. |
| 3 | 🔵 Minor | `Api/Auth/HttpContextCurrentUser.cs` | Tên claim (`sub`, `role`, `client_id`/`azp`) mới chỉ kiểm bằng principal tự dựng; **chưa kiểm với principal thật từ introspection của STS** (đặc biệt `role`, mà TASK-013 sẽ dựa vào). | Làm smoke test end-to-end ngay khi có client được cấp scope (xem mục Gemini quyết định #1). |

### Cần Gemini quyết định
1. **API hiện chưa thể dùng end-to-end**: theo đặc tả (mục 3.1), không client nào được cấp scope `linguabridge_api`, nên không lấy được token hợp lệ cho API. Đây là khoảng trống của đặc tả, không phải lỗi Coder. Cần task: client học viên (public, PKCE) và/hoặc cấp scope cho `internal-service` — **trước TASK-013**, vì policy `Admin` của TASK-013 dựa vào claim `role` chưa được kiểm với token thật (finding #3).
2. **Seeder không cập nhật DB đã seed**: DB dev/prod hiện có sẽ không tự có scope/client `linguabridge_api` (seeder bỏ qua dữ liệu tồn tại). Cần quyết định: hướng dẫn thêm tay qua Admin Portal, reset DB dev, hay cho seeder "ensure" scope/client còn thiếu.
3. **Lệch đặc tả có chủ đích — đề xuất chấp nhận**: scope chỉ seed khi có secret (6.c.1) và thêm 1 dòng vào `StsWebApplicationFactory` (6.c.2). Cả hai hợp lý và giữ nguyên test cũ.
4. **Finding #1**: xác nhận chuyển backlog + mở ADR (áp dụng cho cả `Identity.Admin.Api`).


---

## 8. Quyết Định & Nghiệm Thu Của Tech Lead (Gemini)

- **Kết luận**: **`APPROVED`**
- **Quyết định về các vấn đề do Reviewer nêu**:
  1. **Lệch đặc tả có chủ đích (Chấp thuận)**:
     - Scope chỉ được seed khi có secret: **ĐỒNG Ý**. Scope không có client resource server đi kèm thì vô nghĩa, và tránh làm hỏng test số lượng scope cũ.
     - Sửa 1 dòng trong `StsWebApplicationFactory.cs`: **ĐỒNG Ý**. Đây là hạ tầng test nhằm cô lập môi trường test, giải quyết xung đột cấu hình giữa development appsettings và unit/integration tests cũ.
  2. **Chuyển Finding 🟡 Major sang Backlog & mở ADR**:
     - **ĐỒNG Ý**. Hành vi timeout ~36s khi STS unavailable là do retry mặc định của `OpenIddict.Validation.SystemNetHttp`. Đây là đặc tính chung của kiến trúc introspection (cả `Identity.Admin.Api` cũng gặp).
     - **Action item backlog**: Mở ADR đánh giá: (a) Cấu hình timeout/circuit-breaker cho SystemNetHttp; (b) Thêm caching cho kết quả introspection (`MemoryCache`); (c) Xem xét chuyển sang local validation (token tự giải mã qua DataProtection hoặc JWT công khai) ở Phase 2.
  3. **Khắc phục khoảng trống client OIDC (chưa có client xin scope `linguabridge_api`)**:
     - **Giải pháp**: Tạo **TASK-012b (hoặc gộp vào đầu TASK-013)** để cấp scope `linguabridge_api` cho client phục vụ test/phát triển (ví dụ: client `admin-portal` hoặc `linguabridge-dev-client`), đồng thời thực hiện smoke test token thật với các claim `sub`, `role` trước khi triển khai các policy bảo vệ bài học trong TASK-013.
  4. **Seeder cho Database Dev đã tồn tại**:
     - Với môi trường Local Dev: Chạy lệnh `Seed` bổ sung hoặc thêm thủ công qua `Identity.Admin.Web` Portal (`https://localhost:44303`) để kiểm chứng tính năng quản trị UI.
