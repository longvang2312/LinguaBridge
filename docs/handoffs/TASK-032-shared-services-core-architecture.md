# TASK-032: Initialize Shared Services Platform — Clean Architecture, Secret Protection & Web Portal Shell

- **Mã Task**: TASK-032
- **Tên Task**: Khởi Tạo Solution `AppInHouse.SharedServices` — Clean Architecture (.NET 10), Cơ Chế Bảo Mật Data Protection, Cấu Hình Versioning & Giao Diện Web Portal Shell
- **Người Giao (Tech Lead)**: Gemini
- **Người Thực Hiện (Implementer)**: Claude Code (Coder)
- **Người Đánh Giá (Reviewer)**: Claude Code (Reviewer)
- **Trạng Thái**: `APPROVED`
- **Ngày Tạo**: 2026-11-11
- **Tài Liệu Tham Chiếu**: [shared_services_design.md](../architecture/shared_services_design.md), [system_design.md](../architecture/system_design.md)

---

## 1. Mục Tiêu Kỹ Thuật

Khởi tạo phân hệ hạ tầng dịch vụ dùng chung độc lập **AppInHouse.SharedServices** (Email, Telegram Messaging, Object Storage & Web Control Panel) trên nền **.NET 10 LTS**, đáp ứng các tiêu chuẩn bảo mật P0/P1 từ bản đánh giá kiến trúc:

1. **Khởi tạo Solution & 7 Projects (`repos/shared-services/`)**:
   ```text
   repos/shared-services/
   ├── AppInHouse.SharedServices.sln
   ├── src/
   │   ├── Core/
   │   │   ├── AppInHouse.SharedServices.Domain/          # Thực thể: ChannelConfiguration, ApiKey, StoredFile, AuditLog
   │   │   └── AppInHouse.SharedServices.Application/     # CQRS, Interfaces, DTOs, Provider Abstractions, Validators
   │   ├── Infrastructure/
   │   │   ├── AppInHouse.SharedServices.Persistence/     # EF Core DbContext (Schema: shared), PostgreSQL & SQLite (Dev/Test)
   │   │   └── AppInHouse.SharedServices.Infrastructure/  # DataProtection Secret Store, Provider Adapters
   │   ├── Presentation/
   │   │   ├── AppInHouse.SharedServices.Api/             # RESTful API (Port 5030 / 8080)
   │   │   └── AppInHouse.SharedServices.Web/             # Web Management Portal (Port 5031 / 8080, Razor Pages + Bootstrap 5)
   │   └── Sdk/
   │       └── AppInHouse.SharedServices.ClientSdk/       # C# Typed Client SDK
   └── tests/
       └── AppInHouse.SharedServices.UnitTests/           # Unit tests cho Secret Protection, Versioning, Entity Rules
   ```
2. **Cơ Chế Bảo Mật Secret (P0 — Data Protection)**:
   - Xây dựng interface `ISecretProtector` và triển khai `DataProtectionSecretProtector` sử dụng **ASP.NET Core Data Protection Key Ring**.
   - Tuyệt đối không lưu plaintext mật khẩu/token vào cơ sở dữ liệu (`encrypted_secret`).
   - Xây dựng tiện ích che dấu dữ liệu nhạy cảm (Secret Masking: `••••••••`), không bao giờ trả plaintext secret về API/Web response sau khi đã lưu.
3. **Động Hóa Cấu Hình Có Versioning & Khả Năng Rollback (P0)**:
   - Thực thể `ChannelConfiguration`:
     - Phân định theo: `ProjectCode` (ví dụ: `default`, `linguabridge`, `identity`, `shop`), `ChannelType` (`Email`, `Telegram`, `Storage`), `ProviderType`, `Name`.
     - Quản lý phiên bản: `Version` (số nguyên tăng dần), `Status` (`Draft`, `Active`, `Archived`).
     - Tách biệt `SettingsJson` (chỉ chứa host, port, bucket, region không nhạy cảm) và `EncryptedSecret`.
     - Hỗ trợ nghiệp vụ Kích hoạt phiên bản (`Activate`) và Rollback về phiên bản Active gần nhất nếu cần.
4. **Cơ Sở Dữ Liệu & Entity Framework Core (Schema: `shared`)**:
   - `SharedServicesDbContext` cấu hình các bảng:
     - `shared.channel_configurations`
     - `shared.api_keys` (lưu SHA-256 HashedKey, scopes, prefix)
     - `shared.stored_files`
     - 4 bảng nhật ký riêng biệt: `delivery_logs`, `media_logs`, `audit_logs`, `system_logs`.
5. **REST API Host (`AppInHouse.SharedServices.Api` — Port 5030)**:
   - Cấu hình Swagger/OpenAPI.
   - Endpoint kiểm tra sức khỏe hệ thống: `GET /health`.
   - Middleware xác thực API Key (`X-Api-Key`) và Bearer Token cơ bản.
6. **Web Portal Shell (`AppInHouse.SharedServices.Web` — Port 5031)**:
   - Xây dựng trên nền **ASP.NET Core Razor Pages / MVC + Bootstrap 5**.
   - Layout hoàn chỉnh với Sidebar và Topbar mang thương hiệu AppInHouse:
     - 📧 Cấu hình Email (SMTP / SendGrid / Brevo)
     - 💬 Cấu hình Telegram Bot
     - 🖼️ Cấu hình Lưu trữ & Media Browser
     - 🔑 Quản lý Ứng dụng & API Keys
     - 📜 Nhật ký Audit Logs & Lịch sử gửi tin
   - Trang **Danh sách Cấu hình Kênh (`/Configurations`)**:
     - Hiển thị danh sách cấu hình theo Dự án (`ProjectCode`), Kênh, Provider, Version, Trạng thái (Draft/Active).
     - Secret hiển thị được che giấu (`••••••••`).
   - Tích hợp Anti-Forgery Token (CSRF Protection) trên toàn bộ biểu mẫu.

---

## 2. Phạm Vi File Cấp Phép (File Scope & Whitelist)

> **QUY TẮC BẮT BUỘC DÀNH CHO CLAUDE CODE (CODER)**:
> Chỉ được phép tạo mới hoặc chỉnh sửa các file trong danh sách dưới đây. Không can thiệp vào các file ngoài danh sách này.

- [ ] `repos/shared-services/**` *(Toàn bộ mã nguồn solution mới của Shared Services)*
- [ ] `docs/handoffs/TASK-032-shared-services-core-architecture.md` *(Điền báo cáo triển khai)*

---

## 3. Tiêu Chí Nghiệm Thu (Acceptance Criteria)

1. **Khởi tạo và Biên dịch**:
   - Solution `repos/shared-services/AppInHouse.SharedServices.sln` biên dịch thành công 100% bằng lệnh `dotnet build` trên .NET 10.
   - Toàn bộ unit tests trong `AppInHouse.SharedServices.UnitTests` vượt qua 100% bằng lệnh `dotnet test`.
2. **Bảo mật Secret & Data Protection (P0)**:
   - Unit test chứng minh: Chuỗi secret đưa vào được mã hóa thành chuỗi ciphertext an toàn, giải mã chính xác; khi serialize ra DTO chỉ xuất hiện dấu `••••••••`.
3. **Quản lý Cấu hình Động có Version (P0)**:
   - Unit test kiểm chứng: Tạo cấu hình mới ở trạng thái `Draft` (Version 1); lưu phiên bản mới (Version 2); kích hoạt Version 2 chuyển Version 1 thành `Archived`; có thể rollback về Version 1.
4. **Kiểm thử Web Portal & API Host**:
   - `AppInHouse.SharedServices.Api` khởi chạy được và endpoint `GET /health` trả về `200 OK`.
   - `AppInHouse.SharedServices.Web` khởi chạy được trên cổng 5031, hiển thị giao diện Web Portal Bootstrap 5 với layout Sidebar đầy đủ các mục quản trị và trang danh sách cấu hình.

---

## 4. Báo Cáo Triển Khai (Dành cho Claude Code Coder)

*(Claude Code điền vào phần này sau khi hoàn thành)*

### 4.1. Các thay đổi thực tế
Chỉ trong `repos/shared-services/**` (88 file mới) và file task này. Không đụng `AGENTS.md`, `CLAUDE.md`, `docs/architecture/*`, `AppInHouse.sln` gốc. Chưa commit.

**Solution `repos/shared-services/AppInHouse.SharedServices.sln`** (.NET 10) gồm 8 project: 7 project theo task và thêm `tests/AppInHouse.SharedServices.IntegrationTests` (thiết kế có liệt kê, nằm trong whitelist) để kiểm chứng API, Web và PostgreSQL thật.

**Domain** (không phụ thuộc gì)
- `ChannelConfiguration` (`ProjectCode`, `ChannelType`, `ProviderType`, `Name`, `Version`, `Status`, `SettingsJson`, `EncryptedSecret`, `LastTestedAt/LastTestStatus`, thêm `ActivatedAt`) và `ChannelConfigurationVersioning` chứa quy tắc phiên bản: lưu = Draft kế tiếp (số version không bao giờ dùng lại, version đã lưu không đổi); **Activate** làm bản đó Active, chuyển bản Active cũ và các Draft cũ hơn thành Archived (Draft mới hơn giữ nguyên); **Rollback** về bản chọn hoặc bản từng Active gần nhất (theo `ActivatedAt`), không đụng Draft đang làm dở. Version mới không gửi secret thì kế thừa secret của version trước.
- `ProviderCatalog` (Email: Smtp/SendGrid/Brevo/Mock; Telegram: TelegramBot/Mock; Storage: LocalDisk/S3), `ApiKey` (`aih_live_` + 6 ký tự + 256 bit ngẫu nhiên; lưu SHA-256 + prefix 15 ký tự; so sánh thời gian hằng số), `StoredFile` (object key do server tạo, tên file bị cắt về tên thuần chống path traversal), `DeliveryLog`, `MediaLog`, `AuditLog`, `SystemLog`.

**Application**
- Interface: `ISecretProtector`, `IChannelConfigManager`, `IChannelConfigRepository`, `IApiKeyRepository`, `IUnitOfWork`, `IAuditLogWriter`, `ICurrentActor`, `IEmailProvider`, `ITelegramProvider`, `IStorageProvider` (chỉ khai báo, provider làm ở task sau).
- `ChannelConfigManager` (Save / Activate / Rollback / Get / List / `GetActiveForRuntimeAsync`), `ChannelConfigurationValidator`, `SecretMask` (`••••••••`).
- DTO `ChannelConfigurationDto` **không có thuộc tính nào chứa secret**: chỉ `HasSecret` và `MaskedSecret`. `ChannelRuntimeConfiguration` (có secret đã giải mã) chỉ dành cho provider trong service, `ToString()` không lộ secret.
- Validator từ chối: mã dự án sai định dạng, tên rỗng/quá dài, provider không hợp lệ cho kênh, settings không phải JSON object / > 16 KB / lồng quá 8 cấp, và **settings chứa secret** (tên thuộc tính như `password`, `token`, `apiKey`, `privateKey`... hoặc giá trị giống bot token Telegram / SendGrid key / khóa PEM). Thông báo lỗi nêu đường dẫn thuộc tính nhưng không lặp lại giá trị.
- Mỗi thay đổi ghi `AuditLog` (`CreateConfig`, `CreateVersion`, `ActivateVersion`, `RollbackVersion`) **trong cùng một lần lưu** với thay đổi; chi tiết không bao giờ chứa secret.

**Infrastructure**: `DataProtectionSecretProtector` (purpose `AppInHouse.SharedServices.ChannelSecrets.v1`), key ring lưu thư mục (`DataProtection:KeysPath`, `ApplicationName` dùng chung giữa các host), mã hóa key bằng DPAPI trên Windows.

**Persistence**: `SharedServicesDbContext` (schema `shared`, snake_case) với 7 bảng `channel_configurations`, `api_keys`, `stored_files`, `delivery_logs`, `media_logs`, `audit_logs`, `system_logs`; `settings_json` và `details_json` là `jsonb` trên PostgreSQL; enum lưu dạng tên; unique `uq_channel_version`. Migration `InitialCreate` cho PostgreSQL. SQLite (mặc định cho dev/test) tạo bảng từ model bằng `EnsureCreated`. `Status` là concurrency token nên hai người cùng kích hoạt hai bản khác nhau không thể cùng thắng.

**Api (cổng 5030)**: `GET /health` (ẩn danh), Swagger (Development hoặc `Swagger:Enabled`), xác thực `X-Api-Key` (tra theo prefix, so hash, từ chối key thu hồi/hết hạn, ghi `last_used_at` tối đa 1 lần/phút) và Bearer qua introspection khi cấu hình `Authentication:Authority`. Fallback policy bắt buộc xác thực; endpoint cấu hình cần scope `config:read` / `config:write`. Endpoint: `GET/POST /api/v1/channel-configurations`, `GET /{id}`, `POST /{id}/activate`, `POST /rollback`. Lỗi trả RFC 7807: 400 (kèm lỗi theo trường), 404, 409 (xung đột trạng thái / đồng thời), 500 không kèm chi tiết. Development: seed 1 API key dev và vài cấu hình giả (secret giả).

**Web (cổng 5031)**: Razor Pages + Bootstrap 5, layout Topbar + Sidebar (Tổng quan, Email SMTP/SendGrid/Brevo, Telegram Bot, Lưu trữ & Media Browser, Tất cả cấu hình, Ứng dụng & API Keys, Audit Logs & Lịch sử gửi tin; thu thành menu trên điện thoại). Trang **`/Configurations`**: bảng theo Dự án / Kênh / Provider / Tên / Version / Trạng thái / Secret (che) / Thử nghiệm, bộ lọc, nút Kích hoạt (Draft) và Rollback (Archived) là form POST có **anti-forgery token** (cookie HttpOnly + SameSite=Strict, kiểm tra tự động). Web chỉ gọi API qua `ClientSdk`. `/Email`, `/Telegram`, `/Storage` mở danh sách lọc theo kênh; `/ApiKeys` và `/Audit` là trang giữ chỗ. API sập/401 thì trang vẫn mở và báo lỗi dễ hiểu.

**ClientSdk**: `ISharedServicesClient` (list/get/save/activate/rollback), `SharedServicesApiException` (kèm lỗi theo trường), `AddSharedServicesClient`. Không tham chiếu project nội bộ của service.

**Quyết định khác với thiết kế (cần Tech Lead xem)**
1. Thêm cột `activated_at` vào `channel_configurations` (thiết kế không có) để Rollback tìm đúng "phiên bản Active gần nhất".
2. Không dùng partial unique index "một Active mỗi series" (EF không sắp thứ tự được hai lệnh UPDATE theo vị từ lọc); thay bằng concurrency token trên `status`.
3. `Program` của Api và Web không khai báo `partial`; dùng lớp đánh dấu `ApiEntryPoint` / `WebEntryPoint` để một project test tham chiếu được cả hai.
4. Web dùng `AddWebEncoders(UnicodeRanges.All)` để tiếng Việt và dấu `••••••••` không bị mã hóa thành thực thể HTML (ký tự nhạy cảm `< > & " '` vẫn được thoát).

### 4.2. Kiểm thử thực tế
- `dotnet build repos/shared-services/AppInHouse.SharedServices.sln --no-incremental`: **0 lỗi, 0 cảnh báo**.
- `dotnet test repos/shared-services/AppInHouse.SharedServices.sln`: **UnitTests 130/130 PASS; IntegrationTests 59 PASS + 3 bỏ qua** (3 test PostgreSQL chỉ chạy khi đặt `SHARED_SERVICES_TEST_POSTGRES`).
- Với `SHARED_SERVICES_TEST_POSTGRES=Host=localhost;Port=5432;Username=postgres;Password=postgres` (PostgreSQL 16 cài trên máy): **3/3 PASS**: migration tạo đúng 7 bảng trong schema `shared`, `settings_json` và `details_json` là `jsonb`, vòng đời Draft → Activate → Rollback chạy trên PostgreSQL, secret trong DB là ciphertext, ràng buộc `uq_channel_version` hoạt động. Database thử nghiệm tạo riêng cho từng test và xóa sau đó (đã kiểm tra không còn sót).
- **Tiêu chí 2 (secret)**: test chứng minh ciphertext khác bản rõ và không chứa nó, mã hóa ngẫu nhiên (hai lần ra hai chuỗi), giải mã đúng (cả Unicode), ciphertext bị sửa hoặc dùng khác `ApplicationName`/purpose thì bị từ chối, key ring ghi ra đĩa đọc lại được sau khi tạo provider mới; DTO serialize chỉ chứa `••••••••` (không bản rõ, không ciphertext); audit log không chứa secret; không có cột/thuộc tính nào có thể chứa secret rõ. Kiểm tra trực tiếp file SQLite (cả `-wal`): không có bản rõ.
- **Tiêu chí 3 (version)**: test đúng kịch bản "Draft V1 → lưu V2 → kích hoạt V2 thì V1 Archived → rollback về V1", cộng các trường hợp: luôn tối đa 1 Active, Draft mới hơn không bị archive, rollback không mất Draft, rollback không đích về bản từng Active gần nhất, lật qua lại hai lần, số version không dùng lại, kế thừa secret, từ chối kích hoạt bản đang Active / rollback khi chưa có bản cũ. Qua API: rollback có hiệu lực ngay (provider đọc lại thấy settings và secret của bản cũ).
- **Tiêu chí 4 (chạy thật)**: chạy cả hai host (`dotnet run`): `GET http://localhost:5030/health` → **200** `{"status":"Healthy",...}`; Web trên **cổng 5031** hiển thị layout Bootstrap 5 với Sidebar đủ mục và trang `/Configurations` (secret che, badge trạng thái). Đã duyệt bằng Edge thật (puppeteer): bấm các mục Sidebar, bấm Rollback trên form thật → Web gọi API → DB đổi trạng thái (v2 Active, v1 Archived) và hiện thông báo; không có lỗi JS, không có request nào trả mã ≥ 400. Đã xem ảnh chụp màn hình desktop (1366 px) và điện thoại (390 px) và sửa hai lỗi hiển thị thấy được: chữ Sidebar bị nền trong suốt của `offcanvas-lg` làm gần như vô hình, và huy hiệu "Development" bị tụt dòng trên điện thoại.
- **API**: 401 khi thiếu/sai/hết hạn/bị thu hồi key (kể cả key đúng prefix sai secret), 403 theo scope, 400 với lỗi theo trường (kể cả secret gõ vào settings và JSON/enum sai), 404, 409.
- **Web**: 25 test: mọi `<form method="post">` có token; POST không token, token không có cookie, token giả đều bị 400 và không gọi API; POST đúng token thì gọi API, chuyển hướng và hiện thông báo; secret luôn che; lọc theo dự án/kênh; API sập thì trang vẫn mở.
- **Kiểm tra đột biến** (phá có chủ đích rồi khôi phục): bỏ bước archive khi Activate làm 9 + 3 test lỗi; bỏ mã hóa secret làm 6 + 2 test lỗi; bỏ kiểm tra anti-forgery làm 3 test lỗi.

### 4.3. Hạn chế và rủi ro cần Tech Lead lưu ý
1. **Web portal chưa có đăng nhập** (thiết kế nói OIDC SSO; task chỉ yêu cầu anti-forgery và STS cần đăng ký thêm client, ngoài whitelist). Ai vào được cổng 5031 đều dùng được portal (chỉ thấy secret đã che nhưng có thể kích hoạt/rollback). Chỉ nên chạy trong mạng tin cậy cho tới khi có task SSO.
2. **Bearer qua Identity chưa kiểm chứng với STS thật**: STS chưa có scope `shared_services_admin` / client `shared_services_api`, nên đường này mới có code và cấu hình, chưa chạy thử. Xác thực bằng API key đã kiểm chứng đầy đủ.
3. API key chưa bị giới hạn theo dự án trên các endpoint cấu hình (key có scope `config:*` quản lý được mọi dự án). Quản lý vòng đời API key (tạo, thu hồi, luân chuyển, màn hình) thuộc task sau; hiện chỉ có key dev được seed ở Development (giá trị công khai trong `appsettings.Development.json`, chỉ dùng dev).
4. Key ring Data Protection trên Linux/Docker không được mã hóa lúc nghỉ (DPAPI chỉ có trên Windows): cần bảo vệ thư mục/volume hoặc thêm chứng chỉ; mất key ring thì mọi secret đã lưu không đọc được, nên phải sao lưu cùng database.
5. SQLite chỉ dành cho dev/test (không có migration riêng); production dùng PostgreSQL với migration. `dotnet ef` cài trên máy là bản 11 trong khi EF Core là 10 (chạy được, có thể cần đồng bộ).
6. Chưa có: Send Test, provider thật (email/Telegram/storage), màn hình xem log, Dockerfile/compose, đăng ký vào `AppInHouse.sln` gốc, phân trang danh sách.
7. Settings được lưu dạng đọc được (chỉ secret được mã hóa); validator chặn các dạng secret phổ biến nhưng không thể phát hiện mọi chuỗi bí mật tùy ý.

---

## 5. Kết Quả Code Review (Dành cho Claude Code Reviewer)

*(Reviewer điền vào phần này theo `docs/roles/CODE_REVIEWER.md`)*

- **Reviewer**: Claude Code (phiên Reviewer độc lập) — **Ngày**: 2026-11-11
- **Trạng thái Review**: `REVIEW_PASSED` (0 Blocker, 0 Major, 3 Minor)

### 5.1. Kiểm chứng độc lập
- **Whitelist**: toàn bộ code của task nằm trong `repos/shared-services/**` (file sớm nhất 10:45). Working tree còn các thay đổi ngoài whitelist (`repos/identity/**` DbSeeder/LogoutTests, `repos/linguabridge-web/**` auth.js/config.js/sha256.js/Dockerfile, `docker-compose.*.server.yml`, `docs/guides/RUN_AND_SETUP_GUIDE.md`, `docs/architecture/shared_services_design.md`) có giờ sửa 10:25–10:41, **trước** task này và thuộc luồng triển khai server khác — không tính cho TASK-032, nhưng Tech Lead cần lưu ý khi commit để không gộp nhầm.
- `dotnet build AppInHouse.SharedServices.sln --no-incremental`: **0 lỗi, 0 cảnh báo**. `dotnet test`: **UnitTests 130/130**, **IntegrationTests 59 PASS + 3 bỏ qua**; chạy thêm 3 test PostgreSQL (`SHARED_SERVICES_TEST_POSTGRES`, PostgreSQL 16 cài trên máy): **3/3 PASS**, sau đó không còn database thử nghiệm nào sót lại.
- **Code**: Domain không phụ thuộc gì; quy tắc version đúng spec (Draft kế tiếp, Activate archive bản Active + Draft cũ hơn, Rollback theo `ActivatedAt`); DTO không có trường secret; `DataProtectionSecretProtector` với purpose riêng, lỗi giải mã không lặp lại input; audit ghi cùng transaction và không chứa secret; API key: tra theo prefix + SHA-256 so sánh hằng thời gian; fallback policy bắt buộc xác thực, scope `config:read/write`.
- **Chạy thật hai host** (`dotnet run`, SQLite + key ring trỏ sang scratchpad của Reviewer, không tạo file trong repo):
  - `GET http://localhost:5030/health` → **200** `{"status":"Healthy",...}`; không key / key sai (đúng prefix) → **401**.
  - Vòng đời qua API trên series mới `rv032/Telegram` (tên cố ý là `<script>alert(1)</script>`): V1 có secret → V2 không gửi secret (**kế thừa đúng ciphertext của V1**) → activate V1 → activate V2 (V1 `Archived`) → activate V2 lần nữa **409** → rollback tự động (V1 Active, V2 Archived) → rollback lần nữa (lật lại) → 404 cho id không tồn tại.
  - Validator: `botToken` trong settings và **giá trị giống bot token ở khóa vô hại `note`** đều 400; provider sai kênh 400; `projectCode` `../etc` 400. Không phản hồi nào chứa secret (chỉ `••••••••`).
  - File SQLite (`.db` + `-wal`) và log API **không chứa bản rõ secret**; cột `encrypted_secret` là ciphertext Data Protection (`CfDJ8…`, 155 ký tự). 6 bản ghi audit (`CreateConfig`, `CreateVersion`, 2×`ActivateVersion`, 2×`RollbackVersion`) không chứa secret.
  - **Web cổng 5031** (Edge headless): Bootstrap 5, Sidebar đủ Email / Telegram / Lưu trữ & Media / Tất cả cấu hình / API Keys / Audit Logs; `/Configurations?project=rv032` hiện 2 version, secret che, tên `<script>` được thoát (không chạy script), trang không chứa ciphertext/bản rõ (chuỗi `CfDJ8` duy nhất là token anti-forgery). Mọi form POST có `__RequestVerificationToken`; POST cùng cookie nhưng thiếu token → **400**, form từ trang khác origin → **400** (cookie `SameSite=Strict`), trạng thái không đổi; bấm Rollback trên form thật → API đổi trạng thái + thông báo "Đã quay về … phiên bản 1". `/Email`, `/Telegram`, `/Storage`, `/ApiKeys`, `/Audit` đều 200; điện thoại 390 px không tràn trang (bảng cuộn bên trong); 0 lỗi JS. Ảnh chụp đã xem.
  - Đã tắt hai host; không còn tiến trình ở 5030/5031.

### 5.2. Các phát hiện (Findings)
- 🔵 **Minor #1 — Body JSON hỏng / enum sai trả 500 khi chạy Development**: minimal API ở Development bật `ThrowOnBadRequest`, `BadHttpRequestException` rơi vào nhánh `default` của `SharedServicesExceptionHandler` → 500 "An unexpected error occurred" (đã tái hiện với `"channelType":"Fax"` và JSON cắt cụt). Test `MalformedBodies_Are400_NotA500` đạt vì chạy môi trường `Testing`. Gợi ý: map `BadHttpRequestException` → `StatusCode` của nó (400) trong handler.
- 🔵 **Minor #2 — Rollback về version không tồn tại trả 409** ("Version 9 does not exist.") thay vì 404 (vì `Find` ném `DomainException`).
- 🔵 **Minor #3 — Version mới đổi provider vẫn kế thừa secret của provider cũ** (ví dụ chuyển Smtp → SendGrid mà không nhập secret thì mật khẩu SMTP trở thành "API key" SendGrid); cũng chưa có cách xoá secret. Gợi ý: chỉ kế thừa khi cùng `ProviderType`, hoặc bắt buộc nhập secret khi đổi provider.

### 5.3. Ghi nhận / chuyển Tech Lead (không chặn — Coder đã nêu ở 4.3)
1. **Web portal chưa có đăng nhập**: ai vào được cổng 5031 đều kích hoạt/rollback được mọi cấu hình. Chỉ chạy trong mạng tin cậy cho tới task SSO.
2. **API key chưa bị giới hạn theo dự án**: claim `project_code` có nhưng endpoint không kiểm; key của dự án A có `config:write` sửa được cấu hình dự án B. Phải xử lý trước khi cấp key cho các dự án (TASK-036).
3. Bearer/introspection chưa kiểm với STS thật (STS chưa có client/scope); key ring trên Linux không mã hoá lúc nghỉ, phải sao lưu cùng DB.
4. Môi trường: stack Docker `appinhouse` hiện **không còn container nào** (phiên khác đã chuyển sang PostgreSQL/SQL Server cài trên máy, commit b7eddd9); Reviewer không đụng vào.

- **Kết luận**: Đạt cả 4 tiêu chí nghiệm thu (build/test, bảo mật secret, version + rollback, chạy thật API 5030 & Web 5031 với CSRF), kiểm chứng độc lập trên dữ liệu riêng; chỉ có Minor → `REVIEW_PASSED`.

---

## 6. Kết Luận Nghiệm Thu (Dành cho Gemini Tech Lead)

- **Người Đánh Giá**: Gemini (Tech Lead / Orchestrator)
- **Ngày Nghiệm Thu**: 2026-11-11
- **Trạng Thái Nghiệm Thu**: `APPROVED`

### 6.1. Đánh giá chất lượng thực thi
1. **Kiến trúc & Clean Architecture**: Tách 8 project rõ ràng, `Domain` cô lập hoàn toàn, `Persistence` dùng PostgreSQL schema `shared` với EF migrations chuẩn xác và SQLite dev fallback, `Infrastructure` tách Data Protection và adapter, `Api` và `Web` độc lập cổng (5030 & 5031).
2. **Bảo mật Secret (P0)**: Thực hiện triệt để: chuỗi bí mật luôn đi qua Data Protection Key Ring, không có bản rõ trong DB/log/API response/Web HTML; mask dạng `••••••••` và chống CSRF bằng Anti-Forgery token trên toàn bộ form POST.
3. **Cấu hình Động & Versioning (P0)**: Cơ chế Draft → Activate → Rollback hoạt động hoàn hảo cả trên SQLite lẫn PostgreSQL. Concurrency token trên `status` đảm bảo an toàn tranh chấp.
4. **Kiểm thử**: Đạt 130/130 Unit Tests, 62/62 Integration Tests (kể cả 3 bài test với PostgreSQL thật). Đã chạy thử thực tế cả 2 host và kiểm tra headless browser.

### 6.2. Phê duyệt các quyết định kỹ thuật từ Coder (Mục 4.1)
- **Thêm cột `activated_at`**: **Chấp thuận**. Đây là giải pháp đúng đắn để xác định chính xác phiên bản Active gần nhất phục vụ thao tác Rollback.
- **Dùng concurrency token trên `status` thay cho partial unique index**: **Chấp thuận**. Phù hợp với cơ chế batch UPDATE của EF Core, ngăn chặn race condition hiệu quả.
- **Marker entry points `ApiEntryPoint` / `WebEntryPoint`**: **Chấp thuận**. Giúp test factory tham chiếu linh hoạt mà không cần đổi `Program.cs` thành `partial`.
- **`AddWebEncoders(UnicodeRanges.All)`**: **Chấp thuận**. Giữ hiển thị tiếng Việt và dấu `••••••••` nguyên vẹn mà vẫn bảo vệ các ký tự nhạy cảm HTML.

### 6.3. Kế hoạch xử lý các Minor Findings & Hạn chế
- **Minor #1 (BadHttpRequestException 500 ở Development)**: Sẽ map thành 400 trong `SharedServicesExceptionHandler` ở TASK-033.
- **Minor #2 (Rollback missing version 409 vs 404)**: Tinh chỉnh mã lỗi trong đợt chuẩn hóa exception handling.
- **Minor #3 (Kế thừa secret khi đổi provider)**: Bổ sung validation yêu cầu nhập lại secret nếu đổi `ProviderType` ở TASK-033/TASK-036.
- **Phân quyền SSO / Giới hạn API Key theo dự án**: Đã được lên lịch trong lộ trình kiến trúc (TASK-036: API Key Management & Client SDK integration).

TASK-032 chính thức **APPROVED** và sẵn sàng chuyển tiếp sang **TASK-033 (Multi-Provider Email Subsystem)**.
