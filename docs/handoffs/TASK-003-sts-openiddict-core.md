# TASK-003: Cấu Hình OpenIddict STS, Account UI & Database Seeder

- **Người giao (Tech Lead)**: Gemini Orchestrator
- **Người thực hiện (Implementer)**: Claude Code
- **Trạng thái**: `PENDING`
- **Ngày tạo**: 2026-10-27
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
- 

### b. Kết quả kiểm thử thực tế (Output terminal):
```text
# Lệnh: dotnet build AppInHouse.sln

# Lệnh: dotnet test AppInHouse.sln

```

### c. Đánh giá rủi ro hoặc ghi chú kỹ thuật cho Tech Lead:
- 
