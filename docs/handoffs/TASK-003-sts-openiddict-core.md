# TASK-003: Cấu Hình OpenIddict STS, Account UI & Database Seeder

- **Người giao (Tech Lead)**: Gemini Orchestrator
- **Người thực hiện (Implementer)**: Claude Code
- **Trạng thái**: `APPROVED`
- **Ngày hoàn thành**: 2026-10-27
- **Tài liệu tham chiếu**: [docs/architecture/system_design.md](../architecture/system_design.md)

---

## 1. Mục Tiêu Kỹ Thuật

Triển khai lõi máy chủ xác thực **Identity Provider (STS - Security Token Service)** sử dụng **OpenIddict Core 5.x** trên nền ASP.NET Core 8 và ASP.NET Core Identity:
1. Tích hợp OpenIddict Models & Stores vào EF Core (quản lý Applications, Authorizations, Scopes, Tokens).
2. Cấu hình OpenIddict Server trong `AppInHouse.Identity.STS` hỗ trợ đầy đủ các flow bắt buộc:
   - **Authorization Code Flow + PKCE** (cho Web App, SPA, Mobile).
   - **Client Credentials Flow** (cho Service-to-Service nội bộ).
   - **Refresh Token Flow** (với cơ chế xoay vòng token - token rotation).
3. Triển khai các Endpoint OIDC chuẩn:
   - `/.well-known/openid-configuration` (Discovery)
   - `/connect/authorize` (Ủy quyền)
   - `/connect/token` (Cấp phát Token)
   - `/connect/userinfo` (Thông tin người dùng)
   - `/connect/logout` (Đăng xuất)
   - `/connect/introspect` (Kiểm tra token trạng thái reference token)
4. Xây dựng giao diện đăng nhập tối giản chuẩn MVC/Razor trong STS (`/Account/Login`, `/Account/Logout`, `/Account/AccessDenied`).
5. Xây dựng `DatabaseSeeder` khởi tạo idempotent:
   - Tạo role `GlobalAdmin`.
   - Tạo user `admin@appinhouse.local` mặc định (mật khẩu chuẩn).
   - Đăng ký các OpenIddict Scopes chuẩn: `openid`, `profile`, `email`, `offline_access`, `identity_admin_api`.
   - Đăng ký default clients: `admin-portal` (PKCE) và `internal-service` (Client Credentials).
6. Chuyển đổi ràng buộc `ProjectCode` sang **Filtered Unique Index** (`.HasFilter("[IsDeleted] = 0")`) theo quyết định tại Task 002.
7. Viết Integration Tests kiểm tra Discovery document và cấp phát token Client Credentials.

---

## 2. Phạm Vi File Được Phép Tạo / Sửa (File Scope & Whitelist)

> **LƯU Ý DÀNH CHO CLAUDE CODE**:
> Chỉ được phép tạo và chỉnh sửa các file thuộc phạm vi dưới đây.

- [ ] `repos/identity/src/Infrastructure/AppInHouse.Identity.Infrastructure.Identity/**/*`
- [ ] `repos/identity/src/Infrastructure/AppInHouse.Identity.Infrastructure.Persistence/**/*`
- [ ] `repos/identity/src/Presentation/AppInHouse.Identity.STS/**/*`
- [ ] `repos/identity/tests/AppInHouse.Identity.IntegrationTests/**/*`
- [ ] `repos/identity/tests/AppInHouse.Identity.UnitTests/**/*`

---

## 3. Hướng Dẫn Kỹ Thuật Chi Tiết

### 3.1. Tích Hợp OpenIddict vào EF Core
- Trong `AppIdentityDbContext` (hoặc tạo thêm `AppOpenIddictDbContext` kế thừa từ `DbContext` với cấu hình `.UseOpenIddict()`):
  ```csharp
  builder.UseOpenIddict();
  ```
- Thêm package `OpenIddict.EntityFrameworkCore` (phiên bản 5.*) vào `Infrastructure.Persistence` và `Infrastructure.Identity` nếu cần.

### 3.2. Cấu Hình OpenIddict Server trong `STS/Program.cs`
- **Core & EF Core Store**:
  ```csharp
  services.AddOpenIddict()
      .AddCore(options => options.UseEntityFrameworkCore().UseDbContext<AppIdentityDbContext>())
      .AddServer(options =>
      {
          options.SetAuthorizationEndpointUris("/connect/authorize")
                 .SetTokenEndpointUris("/connect/token")
                 .SetUserinfoEndpointUris("/connect/userinfo")
                 .SetEndSessionEndpointUris("/connect/logout")
                 .SetIntrospectionEndpointUris("/connect/introspect");

          options.AllowAuthorizationCodeFlow().RequireProofKeyForCodeExchange()
                 .AllowClientCredentialsFlow()
                 .AllowRefreshTokenFlow();

          // Dùng development encryption/signing certificates cho môi trường Dev/Test
          options.AddDevelopmentEncryptionCertificate()
                 .AddDevelopmentSigningCertificate();

          options.UseAspNetCore()
                 .EnableAuthorizationEndpointPassthrough()
                 .EnableTokenEndpointPassthrough()
                 .EnableUserinfoEndpointPassthrough()
                 .EnableEndSessionEndpointPassthrough()
                 .EnableStatusCodePagesIntegration();
      })
      .AddValidation(options =>
      {
          options.UseLocalServer();
          options.UseAspNetCore();
      });
  ```

### 3.3. Xây Dựng AuthorizationController & AccountController trong `STS`
- `AuthorizationController`: Xử lý endpoint `/connect/token` cho `client_credentials` và `authorization_code`. Trả về `ClaimsPrincipal` kèm claims chuẩn (`sub`, `name`, `email`, `role`).
- `AccountController`: Hành động `Login` (GET/POST), `Logout` (GET/POST) sử dụng `SignInManager<ApplicationUser>`.

### 3.4. Database Seeder Idempotent
- Tạo `DbSeeder.cs` trong `Persistence/Seeders/`:
  - Kiểm tra nếu `admin@appinhouse.local` chưa tồn tại thì tạo mới với mật khẩu mạnh (ví dụ `Admin@AppInHouse123!`).
  - Gán vào role `GlobalAdmin`.
  - Khởi tạo các Scope và Application của OpenIddict nếu chưa có.

### 3.5. Integration Tests
- Sử dụng `WebApplicationFactory<Program>` từ `Microsoft.AspNetCore.Mvc.Testing` (net8.0) trong `IntegrationTests`.
- Test 1: `GET /.well-known/openid-configuration` trả về status 200 và chứa `token_endpoint`.
- Test 2: `POST /connect/token` với grant_type `client_credentials`, `client_id` và `client_secret` của `internal-service` trả về status 200 và access_token hợp lệ.

---

## 4. Tiêu Chí Nghiệm Thu (Acceptance Criteria)

- [ ] Lệnh `dotnet build AppInHouse.sln` chạy thành công `0 Error(s), 0 Warning(s)`.
- [ ] Lệnh `dotnet test AppInHouse.sln` chạy pass 100% bao gồm cả integration tests mới.
- [ ] Endpoint `/.well-known/openid-configuration` hoạt động chuẩn xác.
- [ ] Cấp phát token Client Credentials thành công qua integration test.
- [ ] Filtered Index cho `ProjectCode` (`[IsDeleted] = 0`) được cấu hình chính xác.
- [ ] Điền báo cáo đầy đủ vào Mục 5 và đổi trạng thái sang `READY_FOR_REVIEW`.

---

## 5. Báo Cáo Triển Khai (Claude Code điền khi hoàn thành)

### a. Danh sách file đã tạo / chỉnh sửa:
- **Persistence**: sửa `Contexts/AppIdentityDbContext.cs` (`builder.UseOpenIddict()`), `Configurations/ProjectConfiguration.cs` (filtered unique index `[IsDeleted] = 0`), `.csproj` (+ `OpenIddict.EntityFrameworkCore 5.*`); tạo `Seeders/DbSeeder.cs`, `Seeders/DbSeederOptions.cs`.
- **STS**: viết lại `Program.cs`; tạo `Controllers/AuthorizationController.cs` (authorize, token, userinfo, logout), `Controllers/AccountController.cs` (Login, Logout, AccessDenied), `Models/LoginViewModel.cs`, `Services/StartupInitializer.cs`, `Views/` (`_ViewImports`, `_ViewStart`, `Shared/_Layout`, `Account/Login`, `Account/Logout`, `Account/AccessDenied`); sửa `appsettings.json`, `appsettings.Development.json`.
- **IntegrationTests**: `StsWebApplicationFactory.cs`, `DiscoveryTests.cs`, `ClientCredentialsTests.cs`, `AuthorizationCodeFlowTests.cs`, `DbSeederTests.cs`; xóa `UnitTest1.cs`; `.csproj` (+ `Microsoft.AspNetCore.Mvc.Testing 8.*`, `Microsoft.EntityFrameworkCore.InMemory 8.*`).
- **UnitTests**: thêm 2 test vào `Persistence/DbContextModelTests.cs` (filtered index, bảng OpenIddict).
- Không sửa `Infrastructure.Identity` (không cần thay đổi).

### b. Kết quả kiểm thử thực tế (Output terminal):
```text
# Lệnh: dotnet build repos/identity/AppInHouse.Identity.sln --no-incremental
Build succeeded.  0 Warning(s)  0 Error(s)

# Lệnh: dotnet build AppInHouse.sln --no-incremental
Build succeeded.  0 Warning(s)  0 Error(s)

# Lệnh: dotnet test AppInHouse.sln
Passed!  - Failed: 0, Passed: 38, Skipped: 0, Total: 38 - AppInHouse.Identity.UnitTests.dll (net8.0)
Passed!  - Failed: 0, Passed: 18, Skipped: 0, Total: 18 - AppInHouse.Identity.IntegrationTests.dll (net8.0)
```
Integration tests gồm: discovery (200 + `token_endpoint`, các endpoint, grant types, S256, scopes), JWKS, `client_credentials` (200 + access_token / sai secret 401 `invalid_client` / scope lạ `invalid_scope` / scope không được phép), userinfo không token = 401, toàn bộ luồng Authorization Code + PKCE qua trang Login thật (login -> code -> token -> userinfo -> code chỉ dùng 1 lần -> refresh có xoay refresh token), sai `code_verifier` bị từ chối, thiếu PKCE bị từ chối, chống open redirect ở Login, seeder đầy đủ và idempotent.

### c. Đánh giá rủi ro hoặc ghi chú kỹ thuật cho Tech Lead:
- **Khác biệt với snippet trong task**: task dùng tên API của OpenIddict 6 (`SetEndSessionEndpointUris`, `EnableEndSessionEndpointPassthrough`, `Permissions.Endpoints.EndSession`). Gói 5.* dùng `SetLogoutEndpointUris`, `EnableLogoutEndpointPassthrough`, `Permissions.Endpoints.Logout`; cùng endpoint `/connect/logout`.
- **Chưa bật reference token**: task nêu `/connect/introspect` cho reference token nhưng mục 3.2 không có `UseReferenceAccessTokens()`. Tôi giữ access token mặc định (JWE); endpoint introspect đã bật và được quảng bá trong discovery nhưng **chưa có client nào được cấp quyền gọi** và **chưa có test** cho nó. Cần quyết định ở task sau (kèm client cho Admin API).
- **Thông tin đăng nhập**: không hard-code trong code. Mật khẩu admin `Admin@AppInHouse123!` và secret `internal-service` chỉ nằm trong `appsettings.Development.json` (credential dev, đã commit-able); ngoài Development seeder chỉ chạy khi `Seeding:Enabled=true` và các giá trị được cấu hình, nếu thiếu sẽ ném lỗi.
- **Chứng chỉ**: Development/Test dùng `AddDevelopmentEncryptionCertificate/SigningCertificate` (sinh cert trong CurrentUser certificate store của máy chạy test - có tác dụng phụ lên máy). Ngoài Development bắt buộc cấu hình `OpenIddict:EncryptionCertificateThumbprint` / `SigningCertificateThumbprint`, nếu không app ném lỗi khi khởi động. Nhánh thumbprint **chưa được test**.
- **`DisableTransportSecurityRequirement`** chỉ bật bằng cấu hình (true ở Development và test) để chạy HTTP; mặc định false.
- **Schema DB**: chưa có migration. `Database:EnsureCreated=true` (chỉ bật ở Development) tạo schema của `AppIdentityDbContext` (Identity + OpenIddict). Nó **không** tạo bảng của `AppAuthDbContext`/`AppAuditDbContext`; cần task migration riêng. Chưa chạy thử với SQL Server thật (test dùng EF InMemory, không enforce filtered index/unique/FK).
- **Seeder** chạy qua `IHostedService` (`StartupInitializer`) thay vì code sau `Build()` để hoạt động ổn định với `WebApplicationFactory`. Seeder chỉ tạo nếu chưa có (không cập nhật client/secret đã tồn tại).
- **Client `admin-portal`** là public client + PKCE bắt buộc, consent `Implicit` (không có màn hình đồng ý); redirect URI mặc định `https://localhost:7111/signin-oidc` (cổng https của Admin.Web) cấu hình qua `Seeding:AdminPortalRedirectUris`. Claim `role` chỉ vào access token (scope `roles` chưa được đăng ký theo danh sách scope của task).
- **Refresh token rotation** dùng mặc định của OpenIddict (xoay mỗi lần dùng, có leeway mặc định 30 giây cho request song song); test chỉ kiểm tra token mới khác token cũ, không kiểm tra phát hiện tái sử dụng sau leeway.
- Mỗi lần đổi code/refresh, STS kiểm tra lại user (tồn tại, `IsActive`, không bị khóa); đăng nhập sai trả cùng một thông báo chung để tránh dò tài khoản.
- Logging project (Serilog) chưa được nối vào STS (ngoài phạm vi task).

---

## 6. Đánh Giá & Nghiệm Thu Kỹ Thuật (Tech Lead Review)

- **Kết luận**: `APPROVED`
- **Người nghiệm thu**: Gemini Orchestrator (Tech Lead)
- **Đánh giá chi tiết & Phản hồi các ghi chú kỹ thuật**:
  1. **Chất lượng kiểm thử xuất sắc**: Bộ 18 integration tests dùng `WebApplicationFactory` mô phỏng đầy đủ luồng thực tế (PKCE challenge, Single-use Auth code, Rotational Refresh token, Token Userinfo, Anti-Open Redirect). Tổng số test toàn solution đạt 56/56 test pass.
  2. **Về tên API OpenIddict 5.x**: Hoàn toàn nhất trí với việc sử dụng `SetLogoutEndpointUris` / `EnableLogoutEndpointPassthrough` để phù hợp với OpenIddict 5.8 LTS.
  3. **Về Quản lý Credential & Chứng chỉ**:
     - Tách biệt credential vào `appsettings.Development.json` và yêu cầu Thumbprint trên môi trường Production là quyết định bảo mật rất chuẩn xác.
     - Sử dụng `StartupInitializer` qua `IHostedService` giúp việc kiểm thử tự động hóa với `WebApplicationFactory` đạt độ tin cậy tuyệt đối.
  4. **Về Introspection & Reference Tokens**:
     - Sẽ được cấp quyền và cấu hình chi tiết trong **Phase 5** khi kết nối với `Admin.Api` và Client SDK.
  5. **Hành động tiếp theo**:
     - Chuyển sang **Phase 4**: Xây dựng **Dynamic Authorization Engine**, Client SDK (`[HasPermission]`), và cơ chế **Cache Invalidation Pipeline** (< 1s revocation latency).

