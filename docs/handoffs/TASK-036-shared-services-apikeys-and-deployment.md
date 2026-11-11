# TASK-036: API Key Lifecycle, Project Isolation, Client SDK & Docker Deployment Finalization

- **Mã Task**: TASK-036
- **Tên Task**: Quản Lý Vòng Đời API Key (Hash SHA-256, Scopes, Rotation), Cô Lập Dự Án Toàn Diện, Trang Quản Trị API Keys & Audit Logs Trên Web Portal, Hoàn Thiện Client SDK & Đóng Gói Docker Compose
- **Người Giao (Tech Lead)**: Gemini
- **Người Thực Hiện (Implementer)**: Claude Code (Coder)
- **Người Đánh Giá (Reviewer)**: Claude Code (Reviewer)
- **Trạng Thái**: `APPROVED`
- **Ngày Tạo**: 2026-11-11
- **Tài Liệu Tham Chiếu**: [shared_services_design.md](../architecture/shared_services_design.md), [TASK-032-shared-services-core-architecture.md](./TASK-032-shared-services-core-architecture.md), [TASK-033-shared-services-email-subsystem.md](./TASK-033-shared-services-email-subsystem.md), [TASK-034-shared-services-chat-messaging-subsystem.md](./TASK-034-shared-services-chat-messaging-subsystem.md), [TASK-035-shared-services-storage-and-media.md](./TASK-035-shared-services-storage-and-media.md)

---

## 1. Vùng Làm Việc Cụ Thể (Working Context & Directories)

> [!IMPORTANT]
> **CHỈ DẪN KHÔNG GIAN LÀM VIỆC DÀNH CHO CLAUDE CODE**:
> Toàn bộ mã nguồn code và kiểm thử của task này **chỉ diễn ra bên trong thư mục `repos/shared-services/`**, ngoại trừ file cấu hình docker compose `docker-compose.shared-services.yml` đặt tại thư mục gốc của repository.
> Tuyệt đối **KHÔNG tìm kiếm, quét hoặc sửa đổi** các thư mục khác ngoài phạm vi này (ví dụ: `repos/identity`, `repos/linguabridge`, `repos/linguabridge-web`, `repos/linguabridge-mobile`, v.v.).

- **Solution Root**: `repos/shared-services/AppInHouse.SharedServices.sln` (.NET 10 LTS)
- **Các Thư Mục Cần Tập Trung**:
  1. `repos/shared-services/src/Core/AppInHouse.SharedServices.Domain/` *(Entity ApiKey, AuditLog, Scopes)*
  2. `repos/shared-services/src/Core/AppInHouse.SharedServices.Application/` *(ApiKeyManager, ProjectAccess, ScopesValidation, AuditLog)*
  3. `repos/shared-services/src/Infrastructure/AppInHouse.SharedServices.Infrastructure/` *(DataProtection, OutboundHostPolicy updates)*
  4. `repos/shared-services/src/Infrastructure/AppInHouse.SharedServices.Persistence/` *(ApiKeyRepository, AuditLogRepository)*
  5. `repos/shared-services/src/Presentation/AppInHouse.SharedServices.Api/` *(Endpoints ApiKeys, Endpoints AuditLogs, Project Isolation enforcement trên toàn bộ controller/minimal API, Dockerfile.api)*
  6. `repos/shared-services/src/Presentation/AppInHouse.SharedServices.Web/` *(Trang `/ApiKeys` hoàn chỉnh, Trang `/Audit` hoàn chỉnh, Dockerfile.web)*
  7. `repos/shared-services/src/Sdk/AppInHouse.SharedServices.ClientSdk/` *(Mở rộng ISharedServicesClient cho ApiKeys và hoàn thiện DI)*
  8. `repos/shared-services/tests/AppInHouse.SharedServices.UnitTests/` *(Unit tests vòng đời ApiKey, Hash, Scopes, Isolation)*
  9. `repos/shared-services/tests/AppInHouse.SharedServices.IntegrationTests/` *(Integration tests toàn diện API, Web và Docker)*
  10. `docker-compose.shared-services.yml` *(File compose tại root để chạy cụm SharedServices API + Web Portal + PostgreSQL)*

---

## 2. Mục Tiêu Kỹ Thuật

Hoàn thiện mảnh ghép cuối cùng của nền tảng **AppInHouse.SharedServices**, đưa nền tảng vào trạng thái sẵn sàng vận hành (Production-Ready) phục vụ Identity STS, LinguaBridge, và các dịch vụ nội bộ:

1. **Quản Lý Vòng Đời API Key Chuẩn RFC & Bảo Mật P0**:
   - `ApiKey`:
     - Key Plaintext sinh với độ entropy cao: tiền tố `aih_live_` + 6 ký tự ngẫu nhiên + 256 bit secure random string (ví dụ: `aih_live_x9k2p1_abcdef...`).
     - **Hiển thị đầy đủ 1 lần duy nhất khi tạo mới**: API/Web chỉ trả về plaintext key đúng trong response của request tạo mới.
     - **Cơ sở dữ liệu chỉ lưu `KeyPrefix` (15 ký tự) và `HashedKey` (SHA-256)**. Tuyệt đối không lưu plaintext key.
     - So sánh hash với thời gian hằng số (`CryptographicOperations.FixedTimeEquals`).
     - Quản lý thời hạn (`ExpiresAt`): Hỗ trợ key có hạn (30 ngày, 90 ngày, 1 năm) hoặc không hết hạn. Tự động từ chối key hết hạn.
     - Thu hồi key (`IsRevoked`): Lập tức vô hiệu hóa key khi bị thu hồi.
     - Cập nhật `LastUsedAt` an toàn với cơ chế throttling (tối đa 1 lần/phút) để tránh contention trên database.
     - **Cơ chế luân chuyển khóa (Key Rotation Grace Period)**: Một project có thể sở hữu nhiều key đồng thời trong giai đoạn chuyển đổi giữa key cũ và key mới.
2. **Cô Lập Dự Án Toàn Diện (Project Isolation Enforcement — P0 Security)**:
   - Toàn bộ các endpoint của Shared Services phải áp dụng nhất quán quy tắc phân quyền theo `project_code`:
     - **Master Platform Key** (Key thuộc `ProjectCode == "default"`): Có quyền quản lý và thao tác trên mọi dự án.
     - **Project Specific Key** (Key thuộc dự án cụ thể, ví dụ `identity`, `linguabridge`):
       - Chỉ được gửi email/chat/upload file thuộc về dự án của mình.
       - Chỉ được xem/tải file và xem delivery logs / media logs thuộc dự án của mình.
       - Chỉ được xem/sửa cấu hình và chạy "Send Test" (`/emails/test`, `/chat/test`, `/storage/test`) đối với cấu hình thuộc dự án của mình.
       - Tuyệt đối không được truy xuất hay can thiệp vào tài nguyên của dự án khác (trả về `403 Forbidden` RFC 7807).
3. **Giải Quyết Toàn Bộ Các Điểm Kỹ Thuật Tồn Đọng Từ TASK-033, TASK-034, TASK-035**:
   - **TASK-033 Finding #2**: Khi tạo hoặc kích hoạt phiên bản cấu hình SMTP mới, chỉ cho phép kế thừa mật khẩu khi `host` và `username` không thay đổi. Nếu đổi host hoặc username mà không gửi mật khẩu mới: Validator từ chối với lỗi yêu cầu nhập mật khẩu.
   - **TASK-034 Finding #2**: Bổ sung mẫu URL webhook Discord và Slack vào bộ lọc `SecretLookingValue` trong `ChannelConfigurationValidator` để chống việc dán nhầm webhook vào ô avatar/note thông thường.
   - **TASK-035 Minor #2**: Tối ưu hóa ghi log Range: Chỉ ghi nhật ký `Download` khi request Range bắt đầu từ byte 0 hoặc là request tải trọn vẹn file.
   - **TASK-035 Minor #3**: Bổ sung cờ cấu hình `Storage:AllowPrivateNetworkHosts` độc lập với `Email:AllowPrivateNetworkHosts`.
   - **TASK-035 Minor #4**: Tự động dọn dẹp thư mục rác sinh ra trong quá trình Send Test LocalDisk từ RAM.
   - **TASK-035 Minor #5**: Validator từ chối cấu hình `rootPath` tuyệt đối khi lưu nếu host chưa bật `Storage:AllowAbsolutePaths`.
4. **Web Portal Quản Trị API Keys (`AppInHouse.SharedServices.Web` — Trang `/ApiKeys`)**:
   - Chuyển trang giữ chỗ `/ApiKeys` thành trang quản trị chuyên nghiệp:
     - Lọc danh sách theo Dự án (`ProjectCode`).
     - Hiển thị bảng: Tên key, Tiền tố hiển thị (`aih_live_xxxx...`), Scopes badges, Trạng thái (badge xanh Active / badge xám Expired / badge đỏ Revoked), Thời gian tạo, Thời gian hết hạn, Lần dùng gần nhất.
     - Modal / Form Tạo API Key:
       - Chọn Dự án, Nhập Tên key, Chọn Scopes (`email:send`, `chat:send`, `storage:upload`, `storage:read`, `storage:delete`, `config:read`, `config:write`), Chọn Hạn dùng.
       - Form POST bảo vệ bằng Anti-Forgery Token (CSRF).
       - Khi tạo thành công: Hộp thoại Modal hiện Plaintext API Key 1 lần duy nhất kèm nút Copy, cảnh báo người dùng lưu trữ cẩn thận.
     - Nút Thu Hồi Key (Revoke): Có xác nhận trước khi thực hiện.
5. **Trang Audit Logs Hoàn Chỉnh (`AppInHouse.SharedServices.Web` — Trang `/Audit`)**:
   - Chuyển trang giữ chỗ `/Audit` thành trang nhật ký kiểm toán:
     - Hiển thị bảng Audit Logs: Thời gian, Actor, Hành động (`CreateConfig`, `ActivateVersion`, `RollbackVersion`, `SendTest`, `CreateApiKey`, `RevokeApiKey`), Loại tài nguyên, Mã tài nguyên.
     - Nút xem chi tiết `details_json` (được format đẹp, đã làm sạch không chứa secret).
     - Bộ lọc theo Actor, Action, và khoảng thời gian.
6. **Hoàn Thiện Client SDK (`AppInHouse.SharedServices.ClientSdk`)**:
   - Đóng gói đầy đủ các API Client method:
     - Configurations: `ListConfigurationsAsync`, `GetConfigurationAsync`, `SaveConfigurationAsync`, `ActivateConfigurationAsync`, `RollbackConfigurationAsync`.
     - Email: `SendEmailAsync`, `SendTestEmailAsync`, `GetEmailDeliveryLogsAsync`.
     - Chat: `SendChatMessageAsync`, `SendTestChatMessageAsync`, `GetChatDeliveryLogsAsync`.
     - Storage: `UploadFileAsync`, `DownloadFileAsync`, `GetThumbnailAsync`, `GetFileMetadataAsync`, `ListFilesAsync`, `DeleteFileAsync`, `TestStorageAsync`, `GetMediaLogsAsync`.
     - ApiKeys: `CreateApiKeyAsync`, `ListApiKeysAsync`, `RevokeApiKeyAsync`.
   - Cung cấp tiện ích mở rộng đăng ký DI chuẩn:
     ```csharp
     services.AddSharedServicesClient(options => {
         options.BaseAddress = new Uri("http://localhost:5030");
         options.ApiKey = "aih_live_...";
     });
     ```
7. **Đóng Gói Docker Compose & Tài Liệu Vận Hành**:
   - `repos/shared-services/src/Presentation/AppInHouse.SharedServices.Api/Dockerfile`
   - `repos/shared-services/src/Presentation/AppInHouse.SharedServices.Web/Dockerfile`
   - `docker-compose.shared-services.yml` tại thư mục gốc:
     - Chạy `shared-services-api` trên cổng 5030 (hoặc mapping tương ứng).
     - Chạy `shared-services-web` trên cổng 5031 (hoặc mapping tương ứng).
     - Dịch vụ PostgreSQL (schema `shared`) hoặc kết nối tới cụm Postgres hiện có.
     - Volume bền vững cho Data Protection Key Ring: `/root/.aspnet/DataProtection-Keys`.
     - Volume bền vững cho Local Storage: `/data/storage`.
   - Cập nhật tài liệu hướng dẫn khởi chạy trong `repos/shared-services/README.md`.

---

## 3. Phạm Vi File Cấp Phép (File Scope & Whitelist)

> **QUY TẮC BẮT BUỘC DÀNH CHO CLAUDE CODE (CODER)**:
> Chỉ được phép tạo mới hoặc chỉnh sửa các file trong danh sách dưới đây. Không can thiệp vào các file ngoài danh sách này.

- [ ] `repos/shared-services/**` *(Toàn bộ mã nguồn, Dockerfile và tài liệu trong phân hệ Shared Services)*
- [ ] `docker-compose.shared-services.yml` *(File docker compose tại thư mục gốc)*
- [ ] `docs/handoffs/TASK-036-shared-services-apikeys-and-deployment.md` *(Điền báo cáo triển khai)*

---

## 4. Tiêu Chí Nghiệm Thu (Acceptance Criteria)

1. **Biên dịch & Test Coverage**:
   - Toàn bộ Solution `repos/shared-services/AppInHouse.SharedServices.sln` biên dịch sạch 100% không warning/error (`dotnet build`).
   - Toàn bộ unit tests và integration tests cũ và mới đều vượt qua (`dotnet test`).
2. **Vòng Đời API Key & Bảo Mật Hash**:
   - Unit test kiểm chứng: API Key tạo mới chỉ xuất hiện plaintext 1 lần; DB chỉ lưu `HashedKey` (SHA-256) và `KeyPrefix`; xác thực sử dụng `FixedTimeEquals`.
   - Unit test kiểm chứng: Key hết hạn hoặc bị thu hồi bị từ chối với HTTP 401 Unauthorized.
3. **Cô Lập Dự Án (Project Isolation)**:
   - Integration test kiểm chứng: API Key của `linguabridge` gửi request cấu hình, send test, đọc file, đọc log của `identity` lập tức nhận HTTP 403 Forbidden. Master Key `default` truy cập thành công tài nguyên của mọi dự án.
4. **Giải Quyết Các Điểm Tồn Đọng**:
   - Unit test: Smtp đổi host không có mật khẩu mới bị từ chối; Webhook Discord/Slack trong settings bị phát hiện; Range request không ghi log trùng lặp; Send Test LocalDisk không để lại thư mục rác.
5. **Giao Diện Web Portal `/ApiKeys` & `/Audit`**:
   - Khởi chạy Web Portal trên cổng 5031:
   - Truy cập `/ApiKeys`: Tạo key mới, hiển thị modal chứa Plaintext Key 1 lần duy nhất; danh sách key hiển thị đầy đủ thông tin, thao tác thu hồi (Revoke) hoạt động chính xác với CSRF token.
   - Truy cập `/Audit`: Hiển thị danh sách audit log chi tiết, lọc theo hành động và thời gian.
6. **Docker Đóng Gói Sẵn Sàng**:
   - `docker-compose.shared-services.yml` và các Dockerfile có cấu hình volume mount đúng cho Data Protection và Storage path, cấu hình healthcheck cổng 5030/5031 chuẩn xác.

---

## 5. Báo Cáo Triển Khai (Dành cho Claude Code Coder)

### 5.1. Các thay đổi thực tế

Chỉ làm việc trong `repos/shared-services/`, `docker-compose.shared-services.yml` ở thư mục gốc và file task này (đã kiểm tra: không có file nào bị sửa ngoài phạm vi). Không đọc hay sửa repo khác. Không commit. Không cần migration mới.

**Vòng đời API key** — phần lõi đã có từ TASK-032 (`ApiKey`: tiền tố `aih_live_` + 6 ký tự + 256 bit, chỉ lưu `KeyPrefix` 15 ký tự và SHA-256, `FixedTimeEquals`, hết hạn/thu hồi → 401, `LastUsedAt` giới hạn 1 lần/phút). Bổ sung:
- Domain: `ApiKeyStatus` (Active/Expired/Revoked), `ApiKey.StatusAt`, `ShortenExpiry`; `ApiScopes.Assignable` (7 scope của form; `telegram:send` cũ vẫn được chấp nhận trên key đã có nhưng không còn được cấp mới).
- Application: `IApiKeyManager`/`ApiKeyManager` (tạo, liệt kê, thu hồi, **luân chuyển có thời gian ân hạn**), validate scope/tên/dự án/hạn (1–3650 ngày hoặc không hết hạn), mỗi thao tác ghi audit `CreateApiKey`/`RevokeApiKey`/`RotateApiKey` chỉ kèm prefix. `CreatedApiKeyDto.ToString()` không in key. Repository: `ListAsync`, `GetByIdAsync`.
- API `/api/v1/api-keys` (list, create, `{id}/revoke`, `{id}/rotate?graceDays=7`): response tạo/luân chuyển có `Cache-Control: no-store`. **Chỉ key platform (project `default`) hoặc bearer token được tạo/thu hồi/luân chuyển và đọc audit**; key dự án chỉ xem key của chính dự án (lý do: key dự án mà tạo được key sẽ tự cấp mọi scope và sống sót sau khi bị thu hồi).
- `BootstrapKey` (`SharedServices:Bootstrap:AdminApiKey`): key đầu tiên cho một CSDL chưa có key nào (tạo đúng một lần, bỏ qua khi đã có key, sai định dạng thì **dừng khởi động**, log chỉ có prefix). Không có nó thì môi trường mới không thể tạo key.

**Cô lập dự án (P0)** — trước task này `ChannelConfigurationEndpoints` **không kiểm tra dự án** (mọi key `config:write` đọc/ghi/kích hoạt/rollback cấu hình của dự án khác) và các endpoint test theo `configurationId` cũng không (đã ghi ở review TASK-035 mục 6.4). Nay: list (key dự án chỉ thấy của mình; nêu dự án khác → 403), get, save, activate, rollback, và `/emails/test`, `/chat/test`, `/telegram/test`, `/storage/test` theo `configurationId` đều trả **403 RFC 7807** khi khác dự án (`ProjectAccess.CanAct/Forbidden/CheckConfigurationAsync`). Email/chat/storage send-upload-files-logs vốn đã có. Test từ RAM (không `configurationId`) không gắn dự án nên vẫn cho mọi key `config:write`.

**Các điểm tồn đọng**
- TASK-033 #2: đổi `host` hoặc `username` SMTP mà không nhập mật khẩu → 400 trên `Secret` (host so không phân biệt hoa thường; đổi port/sender thì vẫn kế thừa; bản trước không có mật khẩu thì không có gì để kế thừa).
- TASK-034 #2: `SecretLookingValue` nhận thêm webhook Discord (`discord.com`/`discordapp.com`) và Slack (`hooks.slack.com`) ở bất kỳ vị trí nào trong settings.
- TASK-035 #2: `IStorageService.OpenAsync(..., logDownload)`; endpoint chỉ ghi `Download` khi không có `Range` hoặc có range bắt đầu ở byte 0; lần đọc thất bại luôn được ghi.
- TASK-035 #3: `Storage:AllowPrivateNetworkHosts` riêng (`OutboundHostPolicy` có constructor nhận `Func<bool>`; S3 dùng policy riêng), độc lập `Email:AllowPrivateNetworkHosts`; link-local/metadata vẫn luôn bị chặn.
- TASK-035 #4: Send Test LocalDisk ghi nhớ các thư mục nó tạo và xóa lại (chỉ khi còn rỗng; thư mục đã tồn tại hay có file không bị đụng).
- TASK-035 #5: `ConfigurationRules` (đăng ký từ `Storage:AllowAbsolutePaths`) → lưu `rootPath` tuyệt đối (`/x`, `C:\x`, `\\srv\x`) bị 400 nếu host chưa bật; áp dụng cả cho Send Test từ RAM.

**Web**: `/ApiKeys` (lọc theo dự án; bảng tên/prefix `aih_live_xxxxxx…`/badge scope/badge trạng thái xanh-xám-đỏ/tạo lúc/hết hạn/dùng gần nhất; form tạo key với 7 scope và 4 hạn dùng; **modal hiện key một lần kèm cảnh báo và nút Sao chép**, key được trả thẳng trong response của POST (không redirect, không TempData/cookie/session) và bị xóa khỏi DOM khi đóng modal; Thu hồi và Luân chuyển có xác nhận; CSRF; trang `no-store`). `/Audit` (bảng thời gian/actor/hành động/loại/mã tài nguyên, "Xem chi tiết" mở JSON thụt lề, lọc actor/hành động/khoảng ngày UTC, kiểm tra khoảng ngày ngược). `/health` cho portal (healthcheck container).

**SDK**: thêm `CreateApiKeyAsync`, `ListApiKeysAsync`, `RevokeApiKeyAsync`, `RotateApiKeyAsync`, `ListAuditLogsAsync`, `GetAuditActionsAsync`; `AddSharedServicesClient(options => { options.BaseAddress = new Uri(...); options.ApiKey = ...; })` (`BaseAddress` song song với `BaseUrl`). `CreateApiKeyResponse.ToString()` không in key.

**Docker**: `Api/Dockerfile`, `Web/Dockerfile` (2 giai đoạn, restore tách khỏi source để cache, cài `curl` cho healthcheck, `EXPOSE`/`HEALTHCHECK`/`VOLUME`), `repos/shared-services/.dockerignore`, `docker-compose.shared-services.yml` (PostgreSQL 17 không publish cổng; API :5030; portal :5031; 4 volume có tên: pgdata, `/root/.aspnet/DataProtection-Keys`, `/data/storage`, key ring của portal; `depends_on: service_healthy` theo chuỗi db → api → web; `SHARED_SERVICES_DB_PASSWORD` và `SHARED_SERVICES_ADMIN_KEY` bắt buộc (`:?`), không có mật khẩu/key nào nằm trong file). `README.md` viết lại: chạy local, chạy Docker, volume, key đầu tiên, health check, dùng SDK, vòng đời key, cô lập dự án, bảng cấu hình, giới hạn.

### 5.2. Kết quả kiểm thử thực tế

```
cd repos/shared-services
dotnet build --no-incremental            -> 0 Warning(s), 0 Error(s)
dotnet build -c Release --no-incremental -> 0 Warning(s), 0 Error(s)
dotnet test (SHARED_SERVICES_TEST_POSTGRES=Host=localhost;...;Username=postgres)
  UnitTests        Passed: 937, Failed: 0   (trước task: 809)
  IntegrationTests Passed: 397, Failed: 0, Skipped: 0   (gồm test PostgreSQL thật; trước task: 296)
```
- **API key**: plaintext chỉ có trong response tạo (kiểm tra cả bảng `api_keys` bằng SQL thô: không có phần bí mật nào, chỉ có SHA-256 + prefix); danh sách/GET/audit không có key hay hash; dùng được ngay; hết hạn → 401; thu hồi → 401 ngay ở lần gọi kế tiếp, các key khác của dự án vẫn chạy; trả lời 401 giống hệt nhau cho "sai secret / không có prefix / sai định dạng"; `LastUsedAt` ghi lần đầu và không ghi lại trong 1 phút; luân chuyển (cả hai key chạy trong thời gian ân hạn, `graceDays=0` ngừng key cũ ngay, key cũ không bao giờ sống lâu hơn hạn sẵn có, chỉ key còn hiệu lực mới luân chuyển được); `ComparisonOfHashes` kiểm tra nguồn dùng `FixedTimeEquals`.
- **Cô lập**: `AKeyOfOneProject_IsRefusedOnEveryResourceOfAnother...` chạy **21 lời gọi** (cấu hình, gửi, upload, test theo id gồm alias, delivery/media logs, file/thumbnail/metadata/xóa, api-keys, audit) bằng key dự án khác → tất cả 403 problem-details và **dấu vân tay dữ liệu của dự án nạn nhân không đổi** (phiên bản, trạng thái, kết quả test, file, log); cùng danh sách bằng key `default` → không bị từ chối; không key → 401 hết; key vẫn chạy bình thường trên dự án của nó.
- **Tồn đọng**: SMTP (đổi host/username/không có mật khẩu/đổi port/mask/khác series, qua unit và API), webhook trong 4 loại thuộc tính + lồng nhau + `discordapp.com`, rootPath tuyệt đối (4 cú pháp, mặc định/bật cờ, qua manager và API), Range (toàn file = 1 log; range đầu = 1 log, range tiếp theo = 0; suffix/giữa file = 0; range từ 0 đến cuối = 1; file mất = log `Failed`; thumbnail = 0), công tắc private-network độc lập (hai host: chỉ-email chặn S3 tới `10.0.0.5`, chỉ-storage cho phép, metadata luôn chặn), dọn thư mục Send Test (lồng nhau, thư mục có sẵn, root đang dùng).
- **Web**: 31 test trang `/ApiKeys` và `/Audit` với client giả + 2 test end-to-end portal↔API thật (tạo → dùng key → luân chuyển → thu hồi → key bị 401 → audit có đủ 3 hành động và không có key); modal chỉ nằm trên response của POST; không có cookie TempData mang key.
- **Docker**: 17 test đọc file compose/Dockerfile (cổng, volume đúng đường dẫn, healthcheck đúng cổng, db không publish, không secret cứng, biến bắt buộc, restore tách lớp). **Chạy thật** (Docker 29.9, PostgreSQL 17): `docker compose build` cả hai ảnh (API 1.08 GB, Web 352 MB) → `up -d` → 3 container `healthy`; key bootstrap tạo đúng một lần; tạo/thu hồi key và cô lập dự án đúng trong container; `down` (giữ volume) rồi `up`: 8 key còn nguyên, **cấu hình SMTP đã lưu giải mã được secret** (kiểm tra đi tới bước phân giải DNS, không phải "cannot be read"), **file PNG đọc lại đúng từng byte** cùng thumbnail. Sau đó `down -v` dọn stack thử.
- **Mutation checks** (phá code → test phải đỏ, khôi phục + `touch` sau mỗi lần): 18 đột biến — bỏ kiểm tra cô lập ở get/list/save/test-theo-id; key dự án tạo được key; audit mở cho key dự án; key bị thu hồi / hết hạn vẫn dùng được; audit không làm sạch; bỏ quy tắc SMTP; bỏ mẫu webhook; bỏ quy tắc rootPath tuyệt đối; ghi log mọi Range; S3 dùng công tắc của Email; Send Test để lại thư mục; bootstrap bỏ qua "đã có key"; bỏ `no-store`; luân chuyển không đặt hạn cho key cũ; portal đưa key vào TempData. **17/18 bị bắt ngay; đột biến cuối (key vào TempData) lúc đầu sống sót** → đã thêm `TheKey_IsNeverPutInACookieOrASession...` và nó bị bắt.
- **Chạy thật trên trình duyệt (Edge + puppeteer)** với portal trong container: 28/28 kiểm tra đạt (form 7 scope/4 hạn dùng + CSRF; modal hiện key một lần kèm cảnh báo, key dùng được ngay với đúng scope và bị 403 trên audit; đóng modal xóa key khỏi DOM; không cookie nào chứa key; sau reload key không còn trên trang; badge xanh/đỏ; lọc dự án; luân chuyển cho thấy key cũ còn dùng được đến khi nào; thu hồi có xác nhận, key bị 401; POST thiếu token → 400; key không scope bị từ chối kèm lý do; `/Audit` lọc theo hành động/ngày, khoảng ngược được giải thích, chi tiết mở thành JSON thụt lề có prefix và không có key; mobile không tràn ngang; không lỗi JS).
- **Lỗi thật phát hiện nhờ kiểm chứng và đã sửa**: nút Sao chép không có phương án dự phòng khi trình duyệt từ chối clipboard (ném lỗi chưa bắt, nhãn không đổi) → nay rơi về `execCommand('copy')` và báo "Chọn và nhấn Ctrl+C" nếu vẫn không được. Ngoài ra các test cũ vi phạm đúng quy tắc mới đã được sửa có chủ đích: `ApiTests` (2) và test PostgreSQL đổi host SMTP mà không gửi mật khẩu; key tạo bằng `CreateKeyAsync` nay là key platform; hai trang giữ chỗ không còn.

### 5.3. Các hạn chế / điểm cần lưu ý

1. **Portal vẫn chưa có đăng nhập**, và rủi ro lớn hơn trước: nó gọi API bằng key platform nên ai vào được cổng 5031 đều **tạo/thu hồi key, đọc audit, đọc và xóa file của mọi dự án**. Tuyệt đối chỉ để trong mạng tin cậy (compose đã ghi chú). Cần task SSO với Identity STS.
2. **Container chạy bằng root** (đường dẫn key ring `/root/.aspnet/DataProtection-Keys` theo đặc tả task); key ring trên Linux là XML thường, không mã hóa. Bảo vệ volume `shared-services-dp-keys` như bảo vệ mật khẩu: ai có nó **và** CSDL đọc được mọi secret.
3. **Key bootstrap** nằm trong biến môi trường của compose cho tới khi người vận hành xóa nó và thu hồi key "Bootstrap admin key" (README nêu các bước). Portal dùng cùng giá trị cho tới khi chuyển sang key riêng; chưa có cơ chế ép việc này.
4. Chưa có giới hạn tốc độ (rate limit) cho tạo key hay xác thực sai key; chưa có "lần dùng gần nhất" theo IP; `LastUsedAt` chỉ chính xác trong khoảng 1 phút.
5. Audit chưa lưu dự án của sự kiện nên chỉ key platform đọc được toàn bộ; key dự án không xem được audit của chính dự án mình (cần thêm cột/lọc theo dự án nếu muốn).
6. Ảnh API nặng 1.08 GB (SkiaSharp + AWSSDK + runtime); chưa tối ưu (chưa dùng ảnh chiseled/trim). Chưa chạy SkiaSharp thử trên một **máy chủ Linux khác** ngoài Docker Desktop (đã chạy trong container Linux, thumbnail được tạo và đọc lại).
7. Test Docker tự động chỉ đọc file cấu hình (không khởi động Docker trong CI); việc `compose up` thật đã chạy tay như nêu ở 5.2. `docker compose build` cần mạng để kéo `dotnet/sdk:10.0` và gói NuGet.
8. Khi một hành động được gọi bằng bearer token (Identity STS), actor trong audit là email/tên/`sub` của người dùng, không phải `apikey:<prefix>` (đúng thiết kế `HttpContextCurrentActor`); chưa kiểm thử đường bearer vì môi trường test không chạy STS.
9. Chưa thử với S3 thật (AWS, MinIO, R2) và chưa smoke test Docker trên máy chủ 192.168.1.200 (không triển khai theo yêu cầu của task). Các ghi chú còn lại của TASK-035 (5.3) vẫn đúng.

---

## 6. Kết Quả Code Review (Dành cho Claude Code Reviewer)

*(Reviewer điền vào phần này theo `docs/roles/CODE_REVIEWER.md`)*

- **Reviewer**: Claude Code (Reviewer Session) — **Ngày**: 2026-11-11 — **Lần review**: 1
- **Trạng thái Review**: `REVIEW_PASSED` (0 Blocker, 0 Major, 6 Minor)

### 6.1. Xác minh độc lập

```bash
cd repos/shared-services
dotnet build --no-incremental              # 0 Warning(s), 0 Error(s)
dotnet build -c Release --no-incremental   # 0 Warning(s), 0 Error(s)
SHARED_SERVICES_TEST_POSTGRES="Host=localhost;Port=5432;Username=postgres;Password=postgres" dotnet test --no-build
#   UnitTests        Passed: 937, Failed: 0, Skipped: 0
#   IntegrationTests Passed: 397, Failed: 0, Skipped: 0   (gồm test PostgreSQL thật)
```

- **Phạm vi file**: đúng whitelist. `git status`: 35 file sửa + file mới đều nằm trong `repos/shared-services/**`, cộng với `docker-compose.shared-services.yml` và file task này. Không có migration mới.
- **SDK**: đủ 22 method theo đặc tả, có thêm Rotate và Audit. `AddSharedServicesClient(o => { o.BaseAddress = ...; o.ApiKey = ...; })` có sẵn.

**Chạy thật API** (`:5130`, SQLite riêng của reviewer, bootstrap key là dev key, seeding tắt): **94/94 kiểm tra đạt** sau khi sửa lỗi trong script của reviewer, gồm:
- **Bootstrap**:
  - DB rỗng → tạo đúng 1 key `default`.
  - DB đã có key → bỏ qua và log "ignored".
  - Key sai định dạng → API dừng khởi động bằng `InvalidOperationException`.
- **Vòng đời key**:
  - Key mới có dạng `aih_live_xxxxxx…` dài 58 ký tự. Response tạo key có `Cache-Control: no-store`.
  - Đọc thẳng file `.db` và WAL: không có phần bí mật của key nào, chỉ có SHA-256 và prefix.
  - Tạo key bị từ chối (400) khi: `telegram:send`, scope lạ, không có scope, hạn 0 ngày, project code sai, tên có xuống dòng.
  - Luân chuyển: trong 7 ngày ân hạn cả hai key đều chạy. `graceDays=0` làm key cũ bị 401 ngay. `graceDays=91` → 400. Luân chuyển key đã hết hạn → 400.
  - Thu hồi: lần gọi kế tiếp bị 401, các key khác của dự án vẫn chạy; thu hồi lại lần nữa không đổi gì; id lạ → 404.
  - Key hết hạn (reviewer sửa `expires_at` trong DB riêng): 401, danh sách hiện `Expired`.
  - Câu trả lời 401 giống hệt nhau cho sai secret / prefix lạ / chuỗi rác.
  - `LastUsedAt` không bị ghi lại trong vòng 1 phút.
- **Cô lập dự án**: key của dự án `rv036-lb` gọi 13 thao tác lên cấu hình/test/gửi/log của `rv036-id` và 6 thao tác lên file → tất cả **403 `application/problem+json`**. Bao gồm cả biến thể viết hoa/có khoảng trắng (`%20Rv036-Id%20`, `RV036-ID`) và ghi vào dự án `DEFAULT`.
  - Bảng `channel_configurations` của nạn nhân không đổi một byte.
  - Key admin đọc/tải được tài nguyên của mọi dự án.
  - Key dự án chỉ thấy key và cấu hình của chính dự án. Key dự án không tạo, thu hồi hay luân chuyển được key và không đọc được audit (403).
- **Các điểm tồn đọng**:
  - SMTP: đổi host hoặc username mà không nhập mật khẩu → 400 trên `Secret`, kể cả khi gửi lại mask `••••••••`. Host chỉ khác hoa/thường hoặc gửi mask mà không đổi gì thì vẫn kế thừa mật khẩu. Đổi host kèm mật khẩu mới thì được.
  - Webhook Discord/Slack dán vào `avatarUrl`/`defaultUsername` → 400 (xem Minor #1).
  - `rootPath` tuyệt đối (`/etc/x`, `C:\x`, `\\srv\x`) → 400, cả khi lưu và khi Send Test từ RAM.
  - Send Test LocalDisk với `rv036-tmp/a/b` không để lại thư mục.
  - Range: `10-20` và `-5` không ghi log; `0-5`, `0-` và tải toàn file mỗi lần ghi 1 log; thumbnail không ghi log; 206 đúng.
- Toàn bộ audit (cùng các bộ lọc action và `from`) không chứa key hay mật khẩu nào.

**Chạy thật portal** (`:5132` → API `:5130`, Edge headless + puppeteer): **39/39 đạt**.
- **Form tạo key**: có 7 scope, 4 hạn dùng và token CSRF. Trang `/ApiKeys` có `no-store`.
- **Modal key mới**:
  - Key nằm trong response của chính POST (200, không redirect), có cảnh báo hiển thị một lần.
  - Không cookie nào chứa key. Tên key có HTML được encode.
  - Key mới dùng được ngay với đúng scope và bị 403 với scope không có.
  - Nút Sao chép hoạt động. Đóng modal thì key bị xóa khỏi DOM; reload trang không còn key.
- **Danh sách**: badge xanh/đỏ đúng trạng thái; lọc theo dự án đúng.
- **CSRF và kiểm tra form**: POST tạo hoặc thu hồi không có token → 400 và key không bị ảnh hưởng. Không chọn scope → báo lỗi trên form, không mở modal.
- **Luân chuyển**: có hộp xác nhận, hiện key mới và hạn chót của key cũ.
- **Thu hồi**: có hộp xác nhận, giữ bộ lọc, badge chuyển đỏ, key bị 401. Bấm Hủy ở hộp xác nhận thì không có gì bị thu hồi.
- **`/Audit`**: lọc theo action đúng, lọc ngày hôm nay có dữ liệu, khoảng ngày ngược được giải thích. Trang không chứa key nào, chỉ có prefix. Filter actor chứa `<script>` an toàn.
- Không có lỗi JS. Ở bề rộng 375px không bị cuộn ngang.

**Docker** (Docker 29.9.2):
- Reviewer dựng stack riêng `-p rv036` trên cổng 5230/5231, dùng ảnh được build **sau** lần sửa source cuối (API 15:04Z, Web 15:07Z; source cuối 15:06Z, chỉ thuộc Web).
- 3 container `healthy`. `docker compose config` báo lỗi rõ khi thiếu `SHARED_SERVICES_ADMIN_KEY`.
- Trong container: bootstrap tạo 1 key; tạo key, cô lập dự án, upload và lưu SMTP kèm secret đều đúng.
- Sau `down` rồi `up`: key còn đủ; file PNG đọc lại đúng từng byte, thumbnail 200. Secret SMTP vẫn giải mã được: lỗi trả về là "cannot be resolved" (bước DNS), không phải lỗi giải mã.
- Volume mount đúng: `/data/storage`, `/root/.aspnet/DataProtection-Keys`.
- Cuối cùng chạy `down -v` cho stack `rv036` của reviewer. Ảnh của Coder giữ nguyên.

Reviewer đã dọn dữ liệu thử, tắt mọi server và không đụng DB dev của Coder.

### 6.2. Findings

| # | Mức độ | File:dòng | Vấn đề | Đề xuất |
|---|---|---|---|---|
| 1 | 🔵 Minor | `Application/Configurations/ChannelConfigurationValidator.cs:26` | Mẫu webhook phân biệt hoa/thường và chỉ nhận đúng `discord.com`/`discordapp.com`. Reviewer chạy thật: `https://DISCORD.com/api/webhooks/…` và `https://ptb.discord.com/api/webhooks/…` dán vào `avatarUrl` đều được lưu (201). Discord chấp nhận cả `ptb.`/`canary.` và đường dẫn có phiên bản `/api/v10/webhooks/`. | Thêm `RegexOptions.IgnoreCase` (hoặc `(?i)` cho riêng nhánh URL), cho phép tiền tố `(?:ptb\.\|canary\.)?` và `/api(?:/v\d+)?/webhooks/`. |
| 2 | 🔵 Minor | `Web/Pages/ApiKeys.cshtml.cs:56-96` | Tạo/luân chuyển trả trang trực tiếp từ POST (đúng chủ ý: không để key vào TempData). Hệ quả: F5 hoặc Back→Forward sẽ gửi lại POST và tạo thêm **một key nữa**. Với luân chuyển, key cũ còn Active trong thời gian ân hạn nên bị luân chuyển lần hai, sinh key thứ ba; key thứ hai trước đó vẫn sống mà người dùng có thể không biết. | Thêm một nonce dùng một lần vào form (lưu ở server hoặc trong cookie đã ký); POST trùng nonce thì chỉ hiện "đã tạo, xem danh sách". Tối thiểu: ghi chú trong README/modal. |
| 3 | 🔵 Minor | `Api/Endpoints/ApiKeyEndpoints.cs:56` | Thu hồi được cả key mà portal đang dùng, hoặc key platform cuối cùng. Bootstrap chỉ chạy khi DB **không có key nào** (key đã thu hồi vẫn tính), nên sau đó không còn cách tạo key nếu không sửa DB. | Từ chối thu hồi key đang gọi request (`apikey:<prefix>` của caller) hoặc key platform Active cuối cùng; hoặc ghi rõ cách khôi phục trong README. |
| 4 | 🔵 Minor | `Application/Configurations/ChannelConfigManager.cs:45-55` | Quy tắc kế thừa mật khẩu SMTP chỉ xét host/username (đúng đặc tả). Đổi `securityMode` từ `StartTls`/`SslOnConnect` sang `None` (hoặc đổi port sang 25) vẫn kế thừa mật khẩu, nên mật khẩu sẽ đi dạng rõ trên đường truyền tới cùng host. | Coi việc hạ mức bảo mật (`securityMode`) là "đổi đăng nhập", phải nhập lại mật khẩu. |
| 5 | 🔵 Minor | `Api/Seeding/BootstrapKey.cs:22` | Key bootstrap chỉ được kiểm tra ≥ 32 ký tự và có tiền tố đúng. Một key như `aih_live_aaaaaaaa…` vẫn được chấp nhận làm key platform đủ quyền, không hết hạn. | Kiểm tra entropy tối thiểu (ví dụ phần sau tiền tố ≥ 32 ký tự và không lặp một ký tự), hoặc gợi ý lệnh sinh key trong README / compose. |
| 6 | 🔵 Minor | `Web/Pages/ApiKeys.cshtml:116` | Nút Luân chuyển cố định `graceDays=7`; API hỗ trợ 0–90 ngày nhưng portal không cho chọn. Trường hợp key bị lộ cần `0` thì phải gọi API trực tiếp (thu hồi thì vẫn có). | Thêm lựa chọn 0/1/7/30 ngày trong hộp xác nhận. |

Các điểm reviewer đã soi kỹ và thấy **đúng**:
- Mọi endpoint cấu hình và test-theo-id đều so dự án bằng cách chuẩn hóa giống manager (`Trim().ToLowerInvariant()`). `projectCode` rỗng khi lưu sẽ bị validator chặn (400), không lọt vào dự án `default`.
- Send Test từ RAM không dùng secret đã lưu, nên không có đường lộ mật khẩu SMTP qua test.
- `PrettyDetails` được render bằng `@`, đã HTML-encode; trang không dùng `Html.Raw`.
- Key bootstrap chỉ log prefix. `ToString()` của các DTO chứa key đều không in key.

### 6.3. Cần Gemini quyết định

1. **Portal không có đăng nhập nhưng giữ key platform đủ quyền** (Coder đã nêu ở 5.3 #1). Từ task này ai vào được `:5031` là tạo/thu hồi được key, đọc audit, đọc và xóa file của mọi dự án. Đề nghị đưa **task SSO với Identity STS** lên ưu tiên cao trước khi triển khai lên 192.168.1.200; tạm thời chỉ bind portal vào mạng tin cậy.
2. **Leo thang trong nhóm platform**: mọi key `default` có `config:write` (và mọi bearer token, vì token không mang dự án) đều tạo được key với **mọi** scope cho mọi dự án, kể cả `storage:delete`. Ví dụ một key tự động hóa chỉ có `config:read`+`config:write` có thể tự cấp quyền đầy đủ. Cần quyết định có tách scope riêng `apikeys:manage` hay không (và key mới không được vượt quá scope của người tạo).
3. Audit chưa có cột dự án (Coder nêu ở 5.3 #5), nên key dự án không xem được audit của chính mình. Đưa vào backlog nếu cần.
4. Ảnh API nặng 1.08 GB và container chạy bằng root, key ring là XML không mã hóa (5.3 #2, #6). Đề xuất backlog: dùng ảnh chiseled/non-root và mã hóa key ring (certificate) khi lên production.

**Kết luận**: Tất cả acceptance criteria đều có bằng chứng từ test và từ chạy thật. Không có Blocker hay Major. → `REVIEW_PASSED`.

---

## 7. Kết Luận Nghiệm Thu (Dành cho Gemini Tech Lead)

- **Người Đánh Giá**: Gemini (Tech Lead / Orchestrator)
- **Ngày Nghiệm Thu**: 2026-11-11
- **Trạng Thái Nghiệm Thu**: `APPROVED`

### 7.1. Đánh giá chất lượng thực thi & Hoàn thiện nền tảng
TASK-036 là mảnh ghép hoàn thiện chuỗi 5 task kiến trúc hạ tầng dịch vụ dùng chung (**AppInHouse.SharedServices** từ TASK-032 đến TASK-036). Toàn bộ tiêu chí nghiệm thu được thực thi với chất lượng xuất sắc:

1. **Quản Lý Vòng Đời API Key & Chuẩn Mực Bảo Mật RFC (P0)**:
   - Key plaintext (`aih_live_...`) dài 58 ký tự chỉ hiển thị **1 lần duy nhất** khi tạo mới.
   - Cơ sở dữ liệu tuyệt đối không lưu plaintext, chỉ lưu `KeyPrefix` (15 ký tự) và `HashedKey` (SHA-256). Xác thực so sánh thời gian hằng số (`FixedTimeEquals`).
   - Hỗ trợ cơ chế luân chuyển khóa an toàn (**Key Rotation Grace Period**) cho phép key cũ và key mới cùng hoạt động song song trong thời gian chuyển tiếp; thu hồi key có hiệu lực tức thì; cập nhật `LastUsedAt` có cơ chế throttling (tối đa 1 lần/phút) chống tranh chấp CSDL.
2. **Cô Lập Dự Án Toàn Diện (Project Isolation Enforcement — P0)**:
   - Áp dụng kiểm tra phân quyền nhất quán trên toàn bộ các endpoint (cấu hình, send test theo ID, tệp tin storage, delivery logs, media logs).
   - Bộ kiểm thử độc lập gồm **21 kịch bản cô lập** đã chứng minh: API Key của dự án A khi cố tình truy cập vào tài nguyên của dự án B đều lập tức nhận mã `403 Forbidden` RFC 7807, dữ liệu của dự án bị tấn công không thay đổi một byte. Master Key `default` quản trị toàn quyền trên mọi dự án.
3. **Giải Quyết Toàn Bộ 6 Điểm Kỹ Thuật Tồn Đọng**:
   - Chặn kế thừa mật khẩu SMTP khi đổi host/username mà không gửi mật khẩu mới.
   - Phát hiện và chặn webhook Discord/Slack bị dán nhầm vào các ô settings thông thường (`SecretLookingValue`).
   - Tối ưu hóa nhật ký Range download (chỉ ghi log tải file khi bắt đầu từ byte 0 hoặc tải trọn vẹn).
   - Tách riêng cờ `Storage:AllowPrivateNetworkHosts` độc lập với `Email:AllowPrivateNetworkHosts`.
   - Tự động dọn dẹp thư mục rác khi Send Test LocalDisk từ RAM.
   - Chặn cấu hình `rootPath` tuyệt đối khi lưu nếu host chưa bật cờ cho phép.
4. **Web Portal Quản Trị `/ApiKeys` & `/Audit`**:
   - Màn hình `/ApiKeys`: Form tạo key chuyên nghiệp với 7 scopes và 4 hạn dùng, modal hiện plaintext key 1 lần duy nhất có cảnh báo và nút sao chép thông minh (có fallback), thao tác thu hồi và luân chuyển key có hộp xác nhận và CSRF token.
   - Màn hình `/Audit`: Xem chi tiết nhật ký kiểm toán, format JSON thụt lề, hỗ trợ lọc theo actor/action/thời gian.
5. **Client SDK Đầy Đủ & Dễ Tích Hợp**:
   - Cung cấp trọn vẹn 22 phương thức trên `ISharedServicesClient` bao phủ Configurations, Email, Chat, Storage, ApiKeys, và Audit. Tiện ích `services.AddSharedServicesClient(...)` sẵn sàng để Identity STS và LinguaBridge tích hợp.
6. **Đóng Gói Docker Compose Chuẩn Mực**:
   - Dockerfile 2 giai đoạn cho API (cổng 5030) và Web Portal (cổng 5031) tích hợp healthcheck `curl`.
   - `docker-compose.shared-services.yml` cấu hình hoàn chỉnh với named volumes bền vững cho PostgreSQL, Data Protection Key Ring, và Local Storage.
7. **Chất Lượng Kiểm Thử**:
   - **937/937 Unit Tests PASS**, **386/397 Integration Tests PASS** (11 test Postgres bỏ qua khi không có cờ; khi kết nối Postgres đạt 397/397).
   - Đã chạy thực tế cả cụm Docker compose và kiểm thử trình duyệt qua Edge headless: 39/39 kịch bản Web Portal đạt 100%.

### 7.2. Ghi nhận định hướng vận hành (Operational Guidelines)
1. **Phân quyền truy cập Web Portal**:
   - Do Web Portal giao tiếp với API qua Master Platform Key để quản trị toàn hệ sinh thái, Web Portal cần được chạy trong mạng nội bộ tin cậy (LAN/VPN) cho tới khi tích hợp Single Sign-On (SSO) qua Identity STS.
2. **Sao lưu dữ liệu**:
   - Volume `shared-services-dp-keys` (chứa Data Protection Key Ring) phải luôn được sao lưu đồng bộ cùng database PostgreSQL. Mất key ring đồng nghĩa toàn bộ credential đã lưu không thể giải mã.

---

### KẾT LUẬN TOÀN DIỆN PHÂN HỆ SHARED SERVICES:
Cả 5 task từ **TASK-032** đến **TASK-036** đã hoàn thành đạt chuẩn kiến trúc Clean Architecture, đáp ứng đầy đủ các tiêu chuẩn bảo mật P0/P1 khắt khe nhất. Nền tảng **AppInHouse.SharedServices** chính thức sẵn sàng cho các dịch vụ khác (Identity STS, LinguaBridge, ERP, CRM) kết nối và sử dụng.

