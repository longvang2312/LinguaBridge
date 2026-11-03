# TASK-020: Kết Nối LinguaBridge API Với Identity STS Trên Docker Desktop

- **Mã Task**: TASK-020
- **Tên Task**: Tích Hợp Toàn Trình LinguaBridge API Với Cụm Identity STS (OpenIddict Introspection) Trên Docker Desktop
- **Người Giao (Tech Lead)**: Gemini
- **Người Thực Hiện (Implementer)**: Claude Code
- **Trạng Thái**: `APPROVED`
- **Ngày Tạo**: 2026-11-03
- **Tài Liệu Tham Chiếu**: [docs/handoffs/TASK-012-oidc-authentication.md](TASK-012-oidc-authentication.md), [docs/handoffs/TASK-018-docker-compose-orchestration.md](TASK-018-docker-compose-orchestration.md), [docs/handoffs/TASK-019-dev-certs-and-e2e-verification.md](TASK-019-dev-certs-and-e2e-verification.md)

---

## 1. Mục Tiêu Kỹ Thuật

Thiết lập luồng xác thực và ủy quyền hoàn chỉnh giữa backend **LinguaBridge API** và máy chủ cấp token **Identity STS** đang chạy trên Docker Desktop:
1. **Cập nhật Identity STS Seeder**:
   - Cấp thêm quyền scope `linguabridge_api` cho client `internal-service` (để hỗ trợ service-to-service calls & automated integration test).
   - Đăng ký mới client OIDC cho ứng dụng người dùng học viên: `linguabridge-app` (Public Client, Authorization Code Flow + PKCE, hỗ trợ scope `openid`, `profile`, `email`, `offline_access`, `linguabridge_api`).
   - Xử lý đồng bộ permission cho client nếu client đã tồn tại trong DB, hoặc hướng dẫn reset DB volume (`.\scripts\docker-identity-down.ps1 -RemoveData`).
2. **Cấu hình LinguaBridge.Api kết nối STS Docker**:
   - Trong `repos/linguabridge/src/Presentation/LinguaBridge.Api/appsettings.Development.json`:
     - Cập nhật `Authentication:Authority` trỏ tới `http://host.docker.internal:5001` (hoặc biến môi trường tương đương).
   - Trong `repos/linguabridge/src/Presentation/LinguaBridge.Api/Auth/AuthenticationExtensions.cs`:
     - Khi chạy trong môi trường Development với URL HTTP, tắt yêu cầu bắt buộc HTTPS nếu OpenIddict Validation yêu cầu (`DisableTransportSecurityRequirement = true` nếu cần).
3. **Bổ sung Container PostgreSQL cho LinguaBridge**:
   - Thêm service `linguabridge-db` (`postgres:16-alpine`, port `5432:5432`, user/pass: `postgres/postgres`, database: `LinguaBridge`) vào `docker-compose.identity.yml` (hoặc compose tổng hợp) kèm volume persistence `linguabridge-db-data`.
   - Đảm bảo LinguaBridge API có thể kết nối CSDL và chạy migrations thành công (`dotnet ef database update` hoặc tự động áp dụng).
4. **Kiểm thử liên thông toàn trình (End-to-End Integration Verification)**:
   - Viết script `scripts/verify-linguabridge-identity.ps1`:
     - Lấy Access Token từ Identity STS container (`http://host.docker.internal:5001/connect/token`) cho scope `linguabridge_api`.
     - Gửi request `GET /api/v1/me` tới `LinguaBridge.Api` kèm Bearer token.
     - Xác nhận `LinguaBridge.Api` gọi introspection sang STS container thành công và trả về `200 OK` với thông tin User hợp lệ (`isAuthenticated = true`).
     - Xác nhận gửi token không hợp lệ hoặc thiếu token trả về `401 Unauthorized`.

---

## 2. Phạm Vi File Cấp Phép (File Scope & Whitelist)

> **QUY TẮC BẮT BUỘC DÀNH CHO CLAUDE CODE**:
> Claude Code **CHỈ ĐƯỢC PHÉP** tạo hoặc sửa các file trong danh sách dưới đây. Tuyệt đối không sửa `AGENTS.md`, `CLAUDE.md`, hoặc các file ngoài phạm vi.

- [ ] `repos/identity/src/Infrastructure/AppInHouse.Identity.Infrastructure.Persistence/Seeders/DbSeeder.cs`
- [ ] `repos/identity/src/Infrastructure/AppInHouse.Identity.Infrastructure.Persistence/Seeders/DbSeederOptions.cs`
- [ ] `repos/linguabridge/src/Presentation/LinguaBridge.Api/appsettings.Development.json`
- [ ] `repos/linguabridge/src/Presentation/LinguaBridge.Api/Auth/AuthenticationExtensions.cs`
- [ ] `docker-compose.identity.yml`
- [ ] `scripts/verify-linguabridge-identity.ps1` (Script kiểm thử tích hợp E2E)
- [ ] `docs/handoffs/TASK-020-connect-linguabridge-identity.md` (Điền báo cáo triển khai)

---

## 3. Chi Tiết Kỹ Thuật Cần Lưu Ý

### 3.1. STS Client Seeding
Trong `DbSeeder.cs`:
- Client `internal-service`: bổ sung `Permissions.Prefixes.Scope + LinguaBridgeApiScope`.
- Client `linguabridge-app`:
  ```csharp
  new OpenIddictApplicationDescriptor
  {
      ClientId = "linguabridge-app",
      DisplayName = "LinguaBridge Student Client",
      ClientType = ClientTypes.Public,
      ConsentType = ConsentTypes.Implicit,
      Permissions =
      {
          Permissions.Endpoints.Authorization,
          Permissions.Endpoints.Token,
          Permissions.Endpoints.Logout,
          Permissions.GrantTypes.AuthorizationCode,
          Permissions.GrantTypes.RefreshToken,
          Permissions.ResponseTypes.Code,
          Permissions.Scopes.Profile,
          Permissions.Scopes.Email,
          Permissions.Prefixes.Scope + Scopes.OpenId,
          Permissions.Prefixes.Scope + Scopes.OfflineAccess,
          Permissions.Prefixes.Scope + LinguaBridgeApiScope,
      },
      Requirements = { Requirements.Features.ProofKeyForCodeExchange },
      RedirectUris = { new Uri("http://localhost:3000/callback"), new Uri("http://localhost:5173/callback") },
  }
  ```

### 3.2. Cấu Hình OpenIddict Validation Cho Phép HTTP Dev
Trong `LinguaBridge.Api/Auth/AuthenticationExtensions.cs`:
Khi cấu hình `services.AddOpenIddict().AddValidation(...)`:
```csharp
options.UseSystemNetHttp();
options.UseAspNetCore();
```
Nếu authority là `http://...`, cần lưu ý cấu hình validation của OpenIddict:
```csharp
// Đảm bảo không bị chặn bởi Transport Security Requirement trong môi trường Development
```

### 3.3. PostgreSQL Container
Trong `docker-compose.identity.yml`:
```yaml
  linguabridge-db:
    image: postgres:16-alpine
    container_name: linguabridge-db
    environment:
      POSTGRES_USER: ${POSTGRES_USER:-postgres}
      POSTGRES_PASSWORD: ${POSTGRES_PASSWORD:-postgres}
      POSTGRES_DB: ${POSTGRES_DB:-LinguaBridge}
    ports:
      - "${POSTGRES_BIND_ADDRESS:-127.0.0.1}:5432:5432"
    volumes:
      - linguabridge-db-data:/var/lib/postgresql/data
    networks:
      - identity-network
    restart: unless-stopped
    healthcheck:
      test: ["CMD-SHELL", "pg_isready -U ${POSTGRES_USER:-postgres} -d ${POSTGRES_DB:-LinguaBridge}"]
      interval: 10s
      timeout: 5s
      retries: 5
      start_period: 5s
```

---

## 4. Tiêu Chí Nghiệm Thu (Acceptance Criteria)

- [ ] Client `internal-service` và `linguabridge-app` được seed đầy đủ quyền truy cập scope `linguabridge_api`.
- [ ] Container `linguabridge-db` (PostgreSQL 16) được tích hợp vào compose, đạt trạng thái `healthy`, dữ liệu được lưu trên volume `linguabridge-db-data`.
- [ ] `LinguaBridge.Api` khởi động thành công với cấu hình `Authority = http://host.docker.internal:5001`.
- [ ] Lấy được Access Token thật từ STS với `scope=linguabridge_api`.
- [ ] Gửi token đến `LinguaBridge.Api` (`/api/v1/me`) thành công (HTTP 200 OK), `ICurrentUser` phân giải đúng danh tính.
- [ ] Script `scripts/verify-linguabridge-identity.ps1` thực thi thành công 100% không phát sinh lỗi.
- [ ] Toàn bộ unit tests và integration tests của cả Identity và LinguaBridge đều PASS (`dotnet test` cả 2 solution).
- [ ] Không chỉnh sửa file ngoài whitelist.

---

## 5. Báo Cáo Triển Khai (Claude Code điền khi hoàn thành)

### a. Danh sách file đã tác động
Tất cả nằm trong whitelist; không sửa `AGENTS.md`, `CLAUDE.md`, file test hay file nào khác. Không commit.
- `repos/identity/.../Seeders/DbSeeder.cs`, `DbSeederOptions.cs` — seed client `linguabridge-app`; cấp scope `linguabridge_api` cho `internal-service` (cả DB mới lẫn DB đã có).
- `repos/linguabridge/.../LinguaBridge.Api/appsettings.Development.json` — `Authentication:Authority` = `http://host.docker.internal:5001`.
- `docker-compose.identity.yml` — thêm service `linguabridge-db` (+ volume `linguabridge-db-data`), bật `Seeding__LinguaBridgeAppEnabled` cho STS.
- `scripts/verify-linguabridge-identity.ps1` — **mới**, kiểm thử liên thông (14 mục).
- **Không sửa** `LinguaBridge.Api/Auth/AuthenticationExtensions.cs`: xem phát hiện 2.

### b. Kiểm thử thực tế
Docker Desktop; stack chạy bằng `scripts/docker-identity-up.ps1` (SQL Server dùng cổng 14330 vì 1433 máy đã bận). LinguaBridge.Api chạy trên **máy host** (xem phát hiện 1).

**Kịch bản nâng cấp thật**: tôi khởi động stack bằng seeder *cũ* trước, xác nhận lỗi gốc (`internal-service` xin `linguabridge_api` → `invalid_request ... not allowed to use the specified scope`), rồi mới sửa seeder và `up -d --build` lại **trên cùng volume DB**. Log STS: `Seeded client linguabridge-app.` và `Granted client internal-service the scope linguabridge_api.`. Sau đó: token `linguabridge_api` / `identity_admin_api` / cả hai đều cấp được; secret cũ vẫn đúng (sai secret → `invalid_client`); khởi động lại STS lần nữa không ghi thêm gì (chỉ 1 dòng "Granted" trong log — idempotent).

**PostgreSQL**: `linguabridge-db` (postgres:16-alpine) `healthy`; `dotnet ef database update --connection "...Database=LinguaBridge..."` tạo **5 schema, 18 bảng** (3+3+7+3+2); sau `down` (giữ volume) rồi `up`, vẫn 18 bảng (dữ liệu nằm ở volume `linguabridge-db-data`).

`.\scripts\verify-linguabridge-identity.ps1` (cả khi API chạy sẵn lẫn với `-StartApi`, script tự dừng API khi xong):
```
1. Preconditions
  [PASS] STS discovery served, issuer http://host.docker.internal:5001/
  [PASS] LinguaBridge API is up (/health)
  [PASS] Database reachable and migrated (anonymous GET /api/v1/billing/plans -> 200)
2. Tokens the API must refuse
  [PASS] No token -> 401
  [PASS] Garbage token -> 401
  [PASS] A genuine STS token meant for the Identity Admin API (another audience) -> 401
3. Service token (client_credentials, scope linguabridge_api)
  [PASS] internal-service gets a token for linguabridge_api
  [PASS] GET /api/v1/me -> 200 {"userId":null,"clientId":"internal-service","isAuthenticated":true}
  [PASS] A learner-only endpoint refuses a service token (403 learner.required)
4. User token (linguabridge-app, authorization code + PKCE)
  [PASS] The STS signs the administrator in and issues linguabridge-app a token
  [PASS] The token response includes a refresh token (offline_access)
  [PASS] GET /api/v1/me -> 200, userId 4356a0b5-5e14-44bf-92ff-a323b096f620 == id_token sub, client linguabridge-app
  [PASS] Learner endpoints backed by the database answer 200 (entitlement, AI quota)
5. Scope boundaries
  [PASS] The admin portal client cannot obtain the linguabridge_api scope (400)
All 14 checks passed.   (exit code 0)
```
Script có "răng": gọi STS qua `localhost` (lệch issuer) → 4/14 lỗi (các cuộc gọi tới `/me` bị 401), exit 1; API không chạy → 9/14 lỗi với gợi ý rõ ràng, exit 1.

Đối chiếu tiêu chí nghiệm thu:
- `internal-service` và `linguabridge-app` có quyền scope `linguabridge_api`: đạt (cả DB mới và DB đã seed bằng code cũ).
- `linguabridge-db` healthy + volume: đạt.
- API khởi động với `Authority = http://host.docker.internal:5001`: đạt.
- Token thật với `scope=linguabridge_api`; `/api/v1/me` 200 và `ICurrentUser` đúng danh tính: đạt — token dịch vụ cho `clientId=internal-service`, `userId=null`; token người dùng cho `userId` **đúng bằng `sub` trong id_token** của admin đã đăng nhập.
- Script chạy thành công 100%: đạt (14/14).
- `dotnet test`: **LinguaBridge 544 unit + 146 integration = 690 pass**; **Identity 170 unit + 286 integration = 456 pass**; build 0 warning.
- Hồi quy: `verify-identity-e2e.ps1` (TASK-019) vẫn 22/22 sau thay đổi.
- Đã dọn: không còn container/volume `identity-*`/`linguabridge-*`, không còn tiến trình API.

### c. Phát hiện, quyết định và rủi ro cho Tech Lead
1. **LinguaBridge.Api không chạy trong Docker**: không có Dockerfile cho nó và whitelist không cho tạo; compose chỉ thêm PostgreSQL. API chạy bằng `dotnet run` trên máy host và nối `localhost:5432` + `host.docker.internal:5001`. Muốn container hoá cần task riêng (Dockerfile .NET 10 + dịch vụ compose + cấu hình `host.docker.internal`/tên dịch vụ cho authority).
2. **Không cần sửa `AuthenticationExtensions.cs`**: introspection qua HTTP chạy được sẵn (không có yêu cầu transport security phía validation client), nên theo đúng chữ "nếu cần" của task tôi không đổi file. Nếu chạy ở môi trường không phải Development thì `EnsureAuthenticationConfigured` vẫn bắt buộc `Authority` và `ClientSecret`.
3. **Lệch spec có chủ ý — `linguabridge-app` là opt-in** (`Seeding:LinguaBridgeAppEnabled`, mặc định tắt; compose bật). Lý do: test có sẵn `LinguaBridgeApiSeederTests.Seeder_DoesNotDuplicateLinguaBridgeEntries_WhenRunTwice` khẳng định **đúng 4 client** khi cấu hình secret LinguaBridge; seed thêm client mặc định sẽ làm test đó đỏ, mà file test nằm ngoài whitelist. Redirect URI lấy từ `Seeding:LinguaBridgeAppRedirectUris`, trống thì dùng mặc định `http://localhost:3000/callback` và `http://localhost:5173/callback` như spec. **Đề xuất**: cho phép sửa test (4 → 5) rồi bật mặc định. Mã seed mới (client và đồng bộ quyền) hiện **chỉ được kiểm chứng bằng script E2E**, chưa có unit/integration test vì không được sửa thư mục test.
4. **Đồng bộ quyền cho client đã tồn tại**: chọn cách *thêm đúng một quyền* (`scp:linguabridge_api`) vào `internal-service` có sẵn thay vì bắt reset DB. Không đụng tới secret, redirect URI hay quyền khác (đã kiểm chứng secret còn nguyên). Đánh đổi: nếu quản trị viên cố ý gỡ quyền này qua Admin Web, lần khởi động STS sau sẽ cấp lại. Cách còn lại vẫn dùng được: `.\scripts\docker-identity-down.ps1 -RemoveData`.
5. **Lệnh migration trong guide sai database**: `LinguaBridgeDbContextFactory` (dùng cho `dotnet ef`) trỏ tới database **`LinguaBridge_Design`**, nên lệnh `dotnet ef database update ...` không kèm `--connection` trong `docs/guides/RUN_AND_SETUP_GUIDE.md` sẽ tạo database `LinguaBridge_Design` chứ không phải `LinguaBridge`. Phải thêm `--connection "Host=localhost;Port=5432;Database=LinguaBridge;Username=postgres;Password=postgres"` (như tôi đã làm). Guide không thuộc whitelist task này nên **chưa sửa** — cần task cập nhật tài liệu. Máy cũng chưa có `dotnet-ef` (tôi cài vào thư mục scratchpad, không cài global).
6. **Migration chưa tự động**: API không tự migrate khi khởi động; phải chạy `dotnet ef database update` một lần cho DB mới (script kiểm tra sẽ báo rõ nếu DB chưa migrate).
7. **PostgreSQL 16 theo spec**, trong khi test integration và guide dùng 17 (`postgres:17-alpine`): khác phiên bản chính giữa môi trường dev (Docker) và test. Cổng 5432 chỉ bind `127.0.0.1` (biến `POSTGRES_BIND_ADDRESS`, `LINGUABRIDGE_DB_PORT` nếu cổng bận); mật khẩu mặc định `postgres/postgres` chỉ cho dev. Các biến `POSTGRES_USER/PASSWORD/DB` là tên phổ biến — nếu shell đã có biến trùng tên sẽ ghi đè mặc định.
8. **Token dịch vụ không có `userId`** (`sub` là client id, không phải Guid) — đúng thiết kế của `ICurrentUser`; vì vậy "thông tin User hợp lệ" với token dịch vụ nghĩa là `isAuthenticated=true` + `clientId`; danh tính người dùng thật được chứng minh bằng token của `linguabridge-app`. Endpoint dành cho học viên trả 403 `learner.required` với token dịch vụ.
9. **Cách ly audience hoạt động**: token hợp lệ của STS nhưng dành cho `identity_admin_api` bị LinguaBridge từ chối 401, và client `admin-portal` không xin được scope `linguabridge_api` (400).
10. Các hạn chế đã nêu ở TASK-018/019 vẫn còn (STS mở ra mạng LAN với mật khẩu admin mẫu công khai; issuer phụ thuộc Host header; image TASK-017 chứa `appsettings.Development.json`). Script `docker-identity-up.ps1` chưa chờ `linguabridge-db` healthy (chỉ chờ 4 service Identity; không nằm trong whitelist) — script kiểm tra mới tự báo lỗi rõ nếu DB chưa sẵn sàng.


---

## 6. Kết Quả Code Review (Claude Code — Reviewer)

- **Kết luận**: `REVIEW_PASSED` — 8/8 acceptance criteria đạt và đã xác minh độc lập, kể cả kịch bản nâng cấp DB đã seed. Không có Blocker/Major; 2 🔵 Minor. Có 3 việc ngoài whitelist cần Gemini giao (guide sai, test cho seeder mới, Authority dev).
- **Ngày review**: 2026-11-03 — **Lần review**: 1

### Xác minh độc lập
```bash
# Build/test cả hai solution
dotnet build/test repos/linguabridge/LinguaBridge.sln     # 0 warning; Unit 544/544, Integration 146/146
dotnet build/test repos/identity (2 test project)         # 0 warning; Integration 286/286, Unit 170/170

# Stack từ volume trống (up script, --build vì seeder đổi) + linguabridge-db
#   5 container healthy; linguabridge-db 127.0.0.1:5432 (loopback)
#   STS log: Seeded client admin-portal / internal-service / identity_admin_api / linguabridge_api / linguabridge-app

# Kịch bản NÂNG CẤP (giả lập DB seed bằng code cũ): xóa "scp:linguabridge_api" khỏi internal-service trong SQL
#   token linguabridge_api           -> invalid_request "not allowed to use the specified scope"   (tái hiện lỗi gốc)
#   docker restart identity-sts      -> log "Granted client internal-service the scope linguabridge_api."
#   Permissions sau restart          -> có lại scp:linguabridge_api, các quyền khác giữ nguyên
#   token, cùng secret cũ            -> OK   (PopulateAsync/UpdateAsync không làm hỏng secret)
#   token, secret sai                -> invalid_client
#   restart lần nữa                  -> tổng cộng chỉ 1 dòng "Granted" trong toàn bộ log (idempotent)

# Migration (dotnet-ef 10.0.14 cài trong scratchpad, không global) với --connection ...Database=LinguaBridge
#   Applying migration '20261028152917_InitialCreate' -> 18 bảng trong 5 schema

.\scripts\verify-linguabridge-identity.ps1 -StartApi
#   All 14 checks passed. exit=0
#   userId d1653310-... == id_token sub; service token -> userId null, clientId internal-service
#   token identity_admin_api -> LinguaBridge 401 (cách ly audience); admin-portal xin linguabridge_api -> 400
#   Sau khi chạy: 0 tiến trình LinguaBridge.Api còn lại
#   NHƯNG: $env:ASPNETCORE_URLS = http://localhost:5020, $env:ASPNETCORE_ENVIRONMENT = Development còn lại trong session (finding #1)

.\scripts\docker-identity-down.ps1 -RemoveData -Force     # 0 container, 0 volume
```
- **Phạm vi file**: đúng whitelist (`git status`). `AuthenticationExtensions.cs` không sửa — xác nhận không cần: introspection qua HTTP chạy được (14/14).
- **Phát hiện 5.c.5 đã xác minh**: `LinguaBridgeDbContextFactory` dùng `Database=LinguaBridge_Design`; guide dòng 91 chạy `dotnet ef database update` không có `--connection`. Guide dòng 113 còn ghi `Authority: https://localhost:44310`, giờ lệch với `appsettings.Development.json`.

### Điểm tốt
- **Đồng bộ quyền tối thiểu và an toàn** cho client đã tồn tại: chỉ thêm đúng một permission, giữ secret và các thiết lập khác — tốt hơn bắt reset DB. Coder còn **tái hiện lỗi gốc trước khi sửa** trên cùng volume.
- Script verify kiểm cả **những gì phải bị từ chối** (không token, token rác, token đúng STS nhưng sai audience, admin-portal xin scope không được phép) — đây là phần dễ bị bỏ qua nhất ở tích hợp auth.
- Chứng minh danh tính bằng cách so `userId` của API với `sub` trong id_token — không chỉ "200 OK".
- `-StartApi` có `try/finally` + `taskkill /T` nên không để lại tiến trình (đã xác nhận).
- Báo cáo trung thực về những gì không làm được trong whitelist (LinguaBridge.Api chưa container hóa, test cho seeder mới, guide).

### Findings
| # | Mức độ | File:dòng | Vấn đề | Đề xuất |
|---|---|---|---|---|
| 1 | 🔵 Minor | `scripts/verify-linguabridge-identity.ps1:172-173` | `-StartApi` đặt `ASPNETCORE_ENVIRONMENT` và `ASPNETCORE_URLS` vào **session của người gọi** và không trả lại (đã xác nhận: sau khi chạy, session có `ASPNETCORE_URLS=http://localhost:5020`). Lần `dotnet run` tiếp theo của **bất kỳ** ứng dụng nào trong session đó sẽ bị ép nghe cổng 5020 ở Development — rất khó đoán ra nguyên nhân. `docker-identity-up.ps1` đã làm đúng việc trả lại biến (`IDENTITY_DB_PORT`). | Lưu giá trị cũ và khôi phục trong `finally`, hoặc truyền biến chỉ cho tiến trình con (`Start-Process` với `-Environment` trên pwsh 7, hoặc `ProcessStartInfo.EnvironmentVariables` để chạy được trên 5.1). |
| 2 | 🔵 Minor | `docker-compose.identity.yml` (comment đầu file + `linguabridge-db.ports`) | Comment đầu file nói "đặt `IDENTITY_BIND_ADDRESS=0.0.0.0` để mở" cho cả PostgreSQL, nhưng PostgreSQL dùng biến riêng `POSTGRES_BIND_ADDRESS` (theo mẫu spec). Ai làm theo comment sẽ mở SQL Server/Admin mà PostgreSQL vẫn đóng (hoặc ngược lại). | Ghi đúng tên biến trong comment, hoặc dùng chung `IDENTITY_BIND_ADDRESS` cho cả hai. |

### Cần Gemini quyết định
1. **Cập nhật `RUN_AND_SETUP_GUIDE.md`** (ngoài whitelist task này): lệnh migration phải có `--connection ...Database=LinguaBridge` (nếu không sẽ tạo nhầm DB `LinguaBridge_Design`); ví dụ `Authentication:Authority` dòng 113 đã lỗi thời; thêm mục chạy LinguaBridge.Api trên host với stack Docker + `verify-linguabridge-identity.ps1`.
2. **`appsettings.Development.json` của LinguaBridge giờ cố định trỏ STS Docker.** Ai chạy STS native (`https://localhost:44310`, Cách 1/2 của guide) sẽ bị LinguaBridge từ chối mọi token vì lệch issuer. Đề xuất: giữ mặc định một kiểu và dùng một launch profile / biến `Authentication__Authority` cho kiểu còn lại, rồi ghi rõ trong guide.
3. **Test cho mã seeder mới** (5.c.3): `linguabridge-app` và đồng bộ quyền `internal-service` hiện chỉ được kiểm bằng script E2E (và review này). Cho phép sửa `LinguaBridgeApiSeederTests` (4 → 5 client) để bật `linguabridge-app` mặc định và thêm test cho nhánh "Granted". Lưu ý: STS chạy native với `appsettings.Development.json` hiện **không** có `linguabridge-app` vì cờ chỉ bật trong compose.
4. **`linguabridge-app` có quyền `Logout` nhưng không có `PostLogoutRedirectUris`**: đăng xuất từ app học viên sẽ không quay về app được. Bổ sung khi có frontend thật.
5. **Container hóa LinguaBridge.Api** + migrate tự động khi khởi động (5.c.1, 5.c.6), và thống nhất PostgreSQL 16 (compose) với 17 (test) (5.c.7).
6. Rủi ro còn treo từ TASK-017/018 (STS mở ra LAN với mật khẩu admin công khai, issuer theo Host header) — nay thêm **refresh token cho public client** `linguabridge-app`; vẫn chấp nhận được cho dev.

---

## 7. Đánh Giá & Quyết Định Của Tech Lead (Gemini)

- **Kết luận**: **`APPROVED`**
- **Xác minh kỹ thuật**:
  - Client `internal-service` và `linguabridge-app` đã được cấu hình cấp quyền scope `linguabridge_api` đầy đủ và an toàn.
  - Cơ chế đồng bộ quyền tự động cho client đã tồn tại trong DB chạy idempotent, không làm mất secret hay các redirect URIs cũ.
  - Service `linguabridge-db` (Postgres 16) được tích hợp trơn tru vào compose với volume `linguabridge-db-data`.
  - Luồng OpenIddict Introspection giữa `LinguaBridge.Api` và `Identity STS` hoạt động chính xác 100%:
    - Phân giải đúng danh tính người dùng `sub` (Guid) thành `ICurrentUser.UserId`.
    - Phân giải đúng `clientId` đối với service token (`userId = null`).
    - Cách ly audience chặt chẽ: từ chối token của `identity_admin_api` (401), từ chối client không được phép xin scope (400).
  - Script kiểm thử E2E `verify-linguabridge-identity.ps1` vượt qua 14/14 tiêu chí kiểm tra nghiêm ngặt.
  - Toàn bộ 1146 test cases (LinguaBridge: 690, Identity: 456) đạt trạng thái PASS 100%, 0 warning build.
  - Tuân thủ 100% phạm vi whitelist cấp phép.

- **Quyết định đối với các mục Reviewer & Coder nêu**:
  1. **Cập nhật RUN_AND_SETUP_GUIDE.md & Lệnh Migration**:
     - Đồng ý: Lệnh `dotnet ef database update` cần chỉ định rõ `--connection "Host=localhost;Port=5432;Database=LinguaBridge;Username=postgres;Password=postgres"` để tránh EF Core trỏ vào DB `LinguaBridge_Design` của design-time factory. Hướng dẫn chi tiết này sẽ được cung cấp ngay cho User.
  2. **Biến môi trường trong verify script (Minor #1)**:
     - Ghi nhận vào backlog bảo trì script helper.
  3. **Cập nhật Seeder Test (4 -> 5 clients)**:
     - Cho phép mở rộng test `LinguaBridgeApiSeederTests` trong task bảo trì tiếp theo để bật `linguabridge-app` mặc định trong cả môi trường test seeder.
  4. **Lộ trình tiếp theo**:
     - Tiến hành hướng dẫn User chạy và trải nghiệm LinguaBridge API trên local kết nối với Identity và PostgreSQL trên Docker Desktop.

