# TASK-033: Multi-Provider Email Subsystem & Web Portal Email Configuration

- **Mã Task**: TASK-033
- **Tên Task**: Phân Hệ Gửi Email Đa Nhà Cung Cấp (SMTP, SendGrid, Mock), Trình Mẫu Template HTML, Thử Nghiệm "Send Test" Trực Tiếp & Trang Quản Trị Email Web Portal
- **Người Giao (Tech Lead)**: Gemini
- **Người Thực Hiện (Implementer)**: Claude Code (Coder)
- **Người Đánh Giá (Reviewer)**: Claude Code (Reviewer)
- **Trạng Thái**: `APPROVED`
- **Ngày Tạo**: 2026-11-11
- **Tài Liệu Tham Chiếu**: [shared_services_design.md](../architecture/shared_services_design.md), [TASK-032-shared-services-core-architecture.md](./TASK-032-shared-services-core-architecture.md)

---

## 1. Mục Tiêu Kỹ Thuật

Triển khai phân hệ gửi Email hoàn chỉnh cho nền tảng `AppInHouse.SharedServices`, cho phép toàn bộ hệ sinh thái (Identity STS, LinguaBridge, các ứng dụng tương lai) gửi email qua API dùng chung hoặc Client SDK mà không cần tự cấu hình SMTP/SendGrid trong từng dự án:

1. **Email Provider Abstractions & Implementations (`AppInHouse.SharedServices.Infrastructure`)**:
   - `IEmailProvider`:
     - `Task<EmailSendResult> SendAsync(EmailMessage message, ChannelRuntimeConfiguration config, CancellationToken ct = default);`
     - `Task<EmailTestResult> TestConnectionAsync(ChannelRuntimeConfiguration config, string testRecipient, CancellationToken ct = default);`
   - Triển khai cụ thể:
     - **`MailKitSmtpEmailProvider`**: Tích hợp thư viện `MailKit` / `MimeKit`. Hỗ trợ cấu hình `Host`, `Port`, `UseSsl` (None, Auto, SslOnConnect, StartTls), `SenderEmail`, `SenderName`. Mật khẩu được giải mã từ `EncryptedSecret` khi chạy.
     - **`SendGridEmailProvider`**: Gửi email qua SendGrid REST API v3 (sử dụng `HttpClient` được inject), API Key được nạp từ `EncryptedSecret`.
     - **`MockEmailProvider`**: Dành cho môi trường Dev / Testing cục bộ không có kết nối internet; lưu email gửi đi vào bộ nhớ (`IReadOnlyList<EmailMessage> SentMessages`) và xuất ra log kiểm thử.
2. **Email Template Engine Căn Bản (`AppInHouse.SharedServices.Application`)**:
   - Hỗ trợ thay thế biến định dạng Mustache/Handlebars dạng `{{variable_name}}` trong cả Subject và HTML/PlainText Body.
   - Hỗ trợ sẵn 3 mẫu email mặc định cho hệ sinh thái AppInHouse:
     - `EmailVerification`: Xác thực tài khoản với mã OTP / Link xác nhận.
     - `PasswordReset`: Khôi phục mật khẩu.
     - `SystemNotification`: Cảnh báo và thông báo hệ thống chung.
3. **Email Dispatcher & Ghi Nhật Ký Delivery Logs (P1)**:
   - `IEmailDispatcher` / `EmailDispatcher`:
     - Nhận yêu cầu gửi email từ API hoặc SDK.
     - Tìm kiếm `ChannelConfiguration` active theo `ProjectCode` (fallback sang cấu hình `default` nếu dự án chưa có cấu hình riêng).
     - Định tuyến tới Provider tương ứng (`Smtp`, `SendGrid`, `Mock`).
     - Đo độ trễ thực thi (`latency_ms`).
     - Ghi nhận kết quả vào bảng `shared.delivery_logs` (thông tin: `id`, `project_code`, `channel_type` = 'Email', `recipient`, `status` = 'Sent' / 'Failed', `provider_response`, `latency_ms`, `created_at`).
     - **Tuyệt đối không lưu mật khẩu hoặc toàn bộ nội dung nhạy cảm của email vào delivery log**.
4. **Nghiệp Vụ "Send Test" Độc Lập Trực Tiếp Từ Bộ Nhớ (P0 Requirement)**:
   - Cho phép gửi email thử nghiệm ngay trên màn hình cấu hình trước khi lưu hoặc kích hoạt.
   - Endpoint: `POST /api/v1/emails/test`:
     - Hỗ trợ 2 chế độ:
       - Chế độ 1: Thử nghiệm cấu hình đang nhập (truyền trực tiếp `provider_type`, `settings_json`, và secret mới trong payload). Backend khởi tạo provider và gửi thử trực tiếp trong bộ nhớ RAM, **không tự động ghi đè hay kích hoạt cấu hình vào database**.
       - Chế độ 2: Thử nghiệm cấu hình đã lưu (truyền `configuration_id`). Sau khi test, cập nhật `last_tested_at` và `last_test_status` ('Success' / 'Failed').
     - Phản hồi làm sạch lỗi: Nếu thất bại, chỉ trả về thông điệp lỗi kỹ thuật ngắn gọn, tuyệt đối không lộ secret hay token ra response.
5. **Khắc Phục & Tinh Chỉnh Ràng Buộc Kỹ Thuật (Từ Review TASK-032)**:
   - **Xử lý Minor #1**: Bổ sung xử lý `BadHttpRequestException` trong `SharedServicesExceptionHandler` để trả về HTTP `400 Bad Request` chuẩn RFC 7807 thay vì `500` ở môi trường Development.
   - **Xử lý Minor #3**: Bổ sung validation: Khi lưu cấu hình mới đổi `ProviderType` (ví dụ chuyển từ `Smtp` sang `SendGrid`), **bắt buộc người dùng phải nhập Secret mới** phù hợp với provider đó (không kế thừa secret cũ khác loại).
6. **Web Portal Quản Trị Email (`AppInHouse.SharedServices.Web` — Trang `/Email`)**:
   - Giao diện Razor Pages hoàn chỉnh:
     - Chọn Dự án (`ProjectCode`: `default`, `linguabridge`, `identity`...).
     - Lựa chọn Provider: Dropdown `Smtp`, `SendGrid`, `Mock`.
     - Form cấu hình động hiển thị tương ứng theo Provider:
       - SMTP: Host, Port, SSL Dropdown, Sender Email, Sender Name, Password (mask `••••••••`).
       - SendGrid: API Key (mask `••••••••`), Sender Email, Sender Name.
       - Mock: Mô tả giả lập, không cần secret.
     - Ô nhập `Email người nhận thử nghiệm` (`test_recipient`) kèm nút **"Gửi Thử Nghiệm" (Send Test)** có chỉ báo loading, hiển thị kết quả thành công/thất bại trực quan.
     - Nút **"Lưu Bản Nháp" (Save Draft)** và nút **"Lưu & Kích Hoạt" (Save & Activate)**.
     - Bảng lịch sử gửi email gần đây (**Recent Delivery Logs**) hiển thị trạng thái badge (Thành công xanh, Thất bại đỏ), người nhận, độ trễ gửi, thời gian.
     - Toàn bộ thao tác POST được bảo vệ bởi **Anti-Forgery Token (CSRF)**.
7. **Bổ Sung Client SDK (`AppInHouse.SharedServices.ClientSdk`)**:
   - Bổ sung phương thức vào `ISharedServicesClient`:
     - `Task<SendEmailResponse> SendEmailAsync(SendEmailRequest request, CancellationToken ct = default);`
     - `Task<SendTestEmailResponse> SendTestEmailAsync(SendTestEmailRequest request, CancellationToken ct = default);`
     - `Task<IReadOnlyList<DeliveryLogDto>> GetEmailDeliveryLogsAsync(string? projectCode = null, int limit = 20, CancellationToken ct = default);`

---

## 2. Phạm Vi File Cấp Phép (File Scope & Whitelist)

> **QUY TẮC BẮT BUỘC DÀNH CHO CLAUDE CODE (CODER)**:
> Chỉ được phép tạo mới hoặc chỉnh sửa các file trong danh sách dưới đây. Không can thiệp vào các file ngoài danh sách này.

- [ ] `repos/shared-services/src/Core/AppInHouse.SharedServices.Domain/**`
- [ ] `repos/shared-services/src/Core/AppInHouse.SharedServices.Application/**`
- [ ] `repos/shared-services/src/Infrastructure/AppInHouse.SharedServices.Infrastructure/**`
- [ ] `repos/shared-services/src/Infrastructure/AppInHouse.SharedServices.Persistence/**`
- [ ] `repos/shared-services/src/Presentation/AppInHouse.SharedServices.Api/**`
- [ ] `repos/shared-services/src/Presentation/AppInHouse.SharedServices.Web/**`
- [ ] `repos/shared-services/src/Sdk/AppInHouse.SharedServices.ClientSdk/**`
- [ ] `repos/shared-services/tests/AppInHouse.SharedServices.UnitTests/**`
- [ ] `repos/shared-services/tests/AppInHouse.SharedServices.IntegrationTests/**`
- [ ] `docs/handoffs/TASK-033-shared-services-email-subsystem.md` *(Điền báo cáo triển khai)*

---

## 3. Tiêu Chí Nghiệm Thu (Acceptance Criteria)

1. **Biên dịch & Test Coverage**:
   - Solution `repos/shared-services/AppInHouse.SharedServices.sln` biên dịch sạch 100% không warning/error (`dotnet build`).
   - Toàn bộ unit tests và integration tests cũ và mới đều vượt qua (`dotnet test`).
2. **Provider Implementations & Mocks**:
   - Unit test kiểm chứng: `MockEmailProvider` ghi nhận đúng email gửi đi; `SendGridEmailProvider` và `MailKitSmtpEmailProvider` phân tích đúng cấu hình và xử lý giải mã secret từ `EncryptedSecret`.
   - Unit test kiểm chứng Template Engine: thay thế chính xác các placeholder `{{name}}`, `{{code}}`, v.v...
3. **Send Test Không Lưu Đè Database (P0)**:
   - Integration test: Gọi endpoint `POST /api/v1/emails/test` với payload cấu hình mới: gửi thành công email test mà **không tạo bản ghi mới** trong bảng `channel_configurations` và **không làm thay đổi** phiên bản Active hiện tại.
4. **Bảo Mật & Validation**:
   - Khi tạo phiên bản mới với `ProviderType` khác phiên bản trước đó mà không gửi secret: Validator ném lỗi yêu cầu nhập secret mới.
   - Gửi payload JSON sai cấu trúc tới API trong môi trường Development trả về HTTP 400 Bad Request (đã fix Minor #1).
   - Delivery log không lưu mật khẩu hay credential.
5. **Giao Diện Web Portal `/Email`**:
   - Khởi chạy Web Portal trên cổng 5031, truy cập trang `/Email`:
   - Hiển thị đầy đủ form cấu hình cho SMTP / SendGrid / Mock với secret che dấu `••••••••`.
   - Nút Send Test hoạt động trơn tru qua POST có Anti-Forgery Token, hiển thị kết quả trực quan.
   - Bảng nhật ký Delivery Logs hiển thị đúng các email đã gửi.

---

## 4. Báo Cáo Triển Khai (Dành cho Claude Code Coder)

*(Claude Code điền vào phần này sau khi hoàn thành)*

### 4.1. Các thay đổi thực tế
Chỉ trong các thư mục của whitelist (`repos/shared-services/src/**` các project Domain, Application, Infrastructure, Persistence, Api, Web, ClientSdk và `tests/**` UnitTests, IntegrationTests) cùng file task này. Không thêm project mới, không đổi `.sln`, `Directory.Build.props`, README của solution, `AGENTS.md`, `CLAUDE.md`, `docs/architecture/*`. Chưa commit.

**Domain**
- `ProviderCatalog.RequiresSecret` (SendGrid, Brevo, TelegramBot, S3 cần secret; Smtp, Mock, LocalDisk thì không).
- `ChannelConfigurationVersioning.CreateNextVersion` chỉ kế thừa secret khi **cùng `ProviderType`** (Minor #3 ở tầng domain).

**Application** (`Email/` mới)
- `IEmailProvider` theo đúng chữ ký của task: `SendAsync(EmailMessage, ChannelRuntimeConfiguration, ct)` → `EmailSendResult`; `TestConnectionAsync(config, testRecipient, ct)` → `EmailTestResult`. Cùng `EmailMessage`, `DeliveryLogDto`, `IDeliveryLogRepository`, `IEmailDispatcher`, `IEmailTestService`.
- **Template engine** (`EmailTemplates.cs`): `{{variable}}` trong Subject, HTML và Text; có khối `{{#if x}}...{{/if}}`; giá trị được **thoát HTML** trong HTML (không chèn được markup), Subject luôn một dòng (không thêm được header thư), một lượt thay (giá trị không kéo biến khác vào); biến tên `link`, `*_url`, `*_link` phải là địa chỉ http(s) tuyệt đối (không `javascript:`). Ba mẫu mặc định tiếng Việt: `EmailVerification` (name, code; link tùy chọn), `PasswordReset` (name, link), `SystemNotification` (title, message; details tùy chọn); thiếu biến bắt buộc thì báo rõ tên biến.
- `EmailSettings`/`EmailSettingsReader`: đọc và kiểm `host, port, useSsl (None|Auto|SslOnConnect|StartTls), username, senderEmail, senderName`. Khi **lưu** chỉ kiểm định dạng (bản nháp được phép chưa đủ); khi **gửi/thử** bắt buộc đủ (SMTP cần host + senderEmail, SendGrid cần senderEmail).
- `EmailDispatcher`: kiểm tra và dựng thư → tìm cấu hình Active của `ProjectCode` (không có thì `default`) → định tuyến theo `ProviderType` → đo `latency_ms` → ghi `shared.delivery_logs` (`Sent`/`Failed`). Log chỉ có dự án, kênh, người nhận, trạng thái, câu trả lời ngắn của provider đã làm sạch, độ trễ; **không có tiêu đề, nội dung hay credential** (entity không có cột nào như vậy). Không có cấu hình thì ghi `Failed` và trả `NoConfiguration`.
- `EmailTestService` (Send Test): không có `ConfigurationId` thì thử **từ bộ nhớ** bằng provider, settings, secret trong request (kiểm cùng quy tắc khi lưu, bắt buộc đủ, cần secret nếu provider đòi), **không tạo hay đổi bản ghi nào** trong `channel_configurations`, chỉ ghi một dòng audit `SendTest`; có `ConfigurationId` thì thử bản đã lưu (kể cả Draft) và cập nhật `last_tested_at`/`last_test_status`.
- `ErrorText.Clean`: mọi thông điệp lỗi của provider đều qua đây (xóa secret, một dòng, tối đa 200 ký tự).
- `ChannelConfigManager`: thêm `GetForRuntimeAsync(id)`, `RecordTestResultAsync`; **đổi `ProviderType` mà không gõ secret mới thì `ValidationException` ở trường `Secret`** nếu provider mới cần secret (Minor #3); validator kiểm định dạng cài đặt email khi lưu.

**Infrastructure** (`Email/`)
- `MailKitSmtpEmailProvider` (MailKit/MimeKit 4.x): `host, port, useSsl→SecureSocketOptions, username, senderEmail, senderName`; mật khẩu là `Secret` đã giải mã; timeout cấu hình được; mọi lỗi thành câu ngắn có thể hành động (đăng nhập bị từ chối, không kết nối được host:port, hết giờ, SSL...). Tách `ISmtpSender` để test không cần mạng.
- `SendGridEmailProvider`: `POST v3/mail/send` qua `HttpClient` được inject, API key (Secret đã giải mã) trong header `Authorization: Bearer`, không nằm trong payload; 202 → thành công kèm `X-Message-Id`; 401/403/lỗi khác → thông điệp ngắn đã làm sạch.
- `MockEmailProvider`: giữ `SentMessages` trong bộ nhớ (tối đa 1000), log người nhận (không log nội dung).
- `OutboundHostPolicy`: chặn host SMTP là link-local/metadata (169.254.x.x, fe80::), multicast, unspecified **luôn luôn**; chặn thêm loopback và mạng riêng khi `Email:AllowPrivateNetworkHosts=false` (mặc định; bật ở Development và test).
- Đăng ký trong `AddSharedServicesInfrastructure`; tùy chọn ở mục `Email` (`AllowPrivateNetworkHosts`, `SmtpTimeoutSeconds`, `SendGridBaseUrl`, `SendGridTimeoutSeconds`).

**Persistence**
- `DeliveryLogRepository` (staged add + `ListRecentAsync` mới nhất trước).
- Với SQLite, mọi `DateTimeOffset` lưu thành chuỗi UTC cố định `yyyy-MM-dd HH:mm:ss.fffffff+00:00` (SQLite không sắp xếp được `DateTimeOffset`, mà "log gần đây" cần `ORDER BY created_at`); đọc lại đúng cả dữ liệu cũ dạng EF mặc định. PostgreSQL không đổi, **không có migration mới**.

**Api**
- `POST /api/v1/emails/send` (scope `email:send`): 200 Sent; **409** chưa có cấu hình; **502** provider không gửi được (ProblemDetails, chi tiết đã làm sạch); 400 sai yêu cầu.
- `POST /api/v1/emails/test` (scope `config:write`): trả 200 với `success` true/false (thất bại của provider là một câu trả lời, không phải lỗi HTTP); 400 với lỗi theo trường; 404 id không có.
- `GET /api/v1/emails/delivery-logs?projectCode&limit` (scope `config:read`, limit 1-100).
- Quy tắc dự án: API key chỉ gửi/đọc log cho **dự án của chính nó**; key của dự án `default` (nền tảng) được chỉ định dự án khác (403 nếu sai).
- **Minor #1**: `SharedServicesExceptionHandler` map `BadHttpRequestException` → 400 RFC 7807 (trước đây 500 ở Development).
- Seed Development: cấu hình `Default email` provider **Mock đang Active** (gửi được ngay không cần mail server) + bản nháp SMTP; key dev có thêm `email:send`.

**ClientSdk**: `SendEmailAsync`, `SendTestEmailAsync`, `GetEmailDeliveryLogsAsync` cùng `SendEmailRequest/Response`, `SendTestEmailRequest/Response`, `DeliveryLogDto`.

**Web `/Email`** (thay trang chuyển hướng): chọn dự án; dropdown Smtp/SendGrid/Mock; form đổi theo provider (SMTP: Host, Port, SSL, Tên đăng nhập, Email/Tên người gửi, Mật khẩu; SendGrid: API Key, người gửi; Mock: ghi chú không cần secret); secret chỉ hiện `••••••••`, đổi provider thì xóa mask để buộc nhập secret mới; ô email thử + nút **Gửi Thử Nghiệm** (fetch kèm token anti-forgery trong header, spinner, kết quả xanh/đỏ hiển thị bằng text, không bằng HTML); **Lưu Bản Nháp** và **Lưu & Kích Hoạt** (PRG, thông báo); bảng **Lịch sử gửi email gần đây** (badge Thành công xanh / Thất bại đỏ, người nhận, độ trễ, giờ UTC); mọi POST được bảo vệ anti-forgery. Nút Gửi Thử Nghiệm: nếu mật khẩu/API key không gõ lại và các cài đặt trên màn hình **đúng bằng** bản đã lưu thì thử bản đã lưu; nếu đã đổi cài đặt thì yêu cầu gõ lại secret (xem 4.3 điểm 4).

**Quyết định cần Tech Lead xem**
1. `IEmailProvider` nằm ở `Application` (không ở `Infrastructure` như tiêu đề mục 1 của task) để giữ quy tắc phụ thuộc: Infrastructure triển khai interface của Application. Chữ ký đúng như task.
2. `EmailMessage` **không có `From`**: người gửi luôn lấy từ cấu hình đang Active, để người gọi không giả mạo người gửi.
3. Minor #3 hiểu là: **không bao giờ kế thừa secret sang provider khác**, và bắt buộc gõ secret mới khi đổi sang provider **cần secret** (SendGrid, Brevo, TelegramBot, S3). Đổi sang `Mock` hoặc `Smtp` không bắt buộc (SMTP relay có thể không cần đăng nhập, Mock không có secret theo mục 6 của task). Nếu muốn đúng nghĩa đen "đổi provider nào cũng phải nhập secret" thì chỉ cần đổi `RequiresSecret`.
4. `SendGridBaseUrl` là cấu hình của host chứ không phải của từng cấu hình: nếu để trong `SettingsJson`, ai lưu được cấu hình đều trỏ được API key tới máy chủ của họ.
5. Send Test không bao giờ dùng secret đã lưu với settings trong request (chặn việc đưa mật khẩu đã lưu tới một máy chủ do người gọi chọn). Hai chế độ không trộn lẫn.
6. Minor #2 (rollback tới version không tồn tại trả 409 thay vì 404) **không làm** vì task này không yêu cầu (Tech Lead đã xếp vào đợt chuẩn hóa exception).

### 4.2. Kết quả kiểm thử thực tế
- `dotnet build repos/shared-services/AppInHouse.SharedServices.sln --no-incremental`: **0 lỗi, 0 cảnh báo**.
- `dotnet test repos/shared-services/AppInHouse.SharedServices.sln`: **UnitTests 314/314 PASS** (trước task: 130); **IntegrationTests 131 PASS + 4 bỏ qua** (trước task: 59 + 3; 4 test PostgreSQL chỉ chạy khi đặt `SHARED_SERVICES_TEST_POSTGRES`).
- Với `SHARED_SERVICES_TEST_POSTGRES=Host=localhost;Port=5432;Username=postgres;Password=postgres` (PostgreSQL 16 trên máy): **IntegrationTests 135/135 PASS** (4 test PostgreSQL gồm cả sắp xếp delivery log); không còn database thử nghiệm nào sót lại.
- **Tiêu chí 2 (provider, template)**: `MockEmailProvider` ghi đúng thư gửi đi (thứ tự, người nhận, nội dung, giới hạn 1000, xóa); `SendGridEmailProvider` (HTTP stub): đúng URL `v3/mail/send`, `Authorization: Bearer <key đã giải mã>`, key không nằm trong payload, cấu trúc payload (text trước html), 202/401/403/400/lỗi mạng đều ra thông điệp ngắn không chứa key, thiếu key hoặc settings chưa đủ thì không gọi mạng; `MailKitSmtpEmailProvider` (bộ gửi giả): endpoint host/port/chế độ SSL ánh xạ đúng, **mật khẩu giải mã từ `EncryptedSecret`** được dùng để đăng nhập, MIME có người gửi/người nhận/tiêu đề/hai nội dung, mật khẩu không nằm trong thư, thiếu username-password/host bị từ chối trước khi kết nối, lỗi đăng nhập/kết nối/hết giờ được dịch và không lộ mật khẩu, hủy bởi người gọi không bị nhầm là hết giờ; chính sách host (private/loopback/link-local/metadata/multicast) kiểm với nhiều địa chỉ. Template: thay `{{name}}`, `{{code}}`, có khoảng trắng, thiếu biến, thoát HTML, chống chèn header qua Subject, `{{#if}}`, ba mẫu mặc định, link `javascript:` bị từ chối.
- **Gửi thật qua MailKit**: một **máy chủ SMTP giả chạy thật** (TcpListener, EHLO/AUTH PLAIN/MAIL/RCPT/DATA) trong integration test: dịch vụ đăng nhập bằng mật khẩu giải mã từ key ring, thư đến đúng người nhận/người gửi, mật khẩu sai thì 502 "rejected the login" không lộ mật khẩu, cổng không có ai nghe thì 502 "Could not connect to 127.0.0.1:port".
- **Tiêu chí 3 (Send Test không lưu đè, P0)**: integration test chụp toàn bộ `channel_configurations` trước và sau `POST /api/v1/emails/test` với cấu hình mới (SMTP thật tới máy chủ giả, đăng nhập bằng mật khẩu **gõ trong request**, không phải mật khẩu đã lưu): số dòng, id, version, status, settings, ciphertext, `last_tested_at`, `last_test_status`, `updated_at` đều **không đổi**, bản Active vẫn là bản cũ, chỉ có dòng audit `SendTest` (không chứa secret). Chế độ 2 (id đã lưu) thì cập nhật `last_tested_at` và `last_test_status` Success/Failed, không kích hoạt Draft. Còn test: trộn id với settings lạ thì mật khẩu đã lưu **không** bị gửi tới máy chủ trong request.
- **Tiêu chí 4 (bảo mật, validation)**: đổi Smtp → SendGrid không gõ secret thì 400 trường `Secret` (cả `null`, rỗng và mask `••••••••`), có secret mới thì 201 và chỉ lưu secret mới, đổi sang Mock không kế thừa secret cũ, cùng provider vẫn kế thừa; gửi JSON hỏng / enum sai / thiếu body tới API ở môi trường **Development** (`DevelopmentApiFactory`) trả **400 `application/problem+json`**, không lộ stack trace (trước khi sửa là 500); delivery log không có tiêu đề/nội dung/credential (kiểm cả trên entity, trong DB và trong phản hồi API).
- **Tiêu chí 5 (chạy thật)**: chạy cả hai host (`dotnet run`): `GET http://localhost:5030/health` → 200; gửi email qua API bằng key dev → `Sent`, log hiện đúng. Duyệt `http://localhost:5031/Email` bằng Edge thật (puppeteer): **24/24 kiểm tra đạt**: form theo từng provider (đổi dropdown thì ẩn/hiện đúng, nhãn Mật khẩu SMTP / API Key), Gửi Thử Nghiệm (Mock) ra kết quả xanh và không tạo/đổi cấu hình nào (4 → 4), gửi tới cổng SMTP chết ra kết quả đỏ "Could not connect to 127.0.0.1:1", token giả bị 400, Lưu Bản Nháp (v1) rồi Lưu & Kích Hoạt (v2) ra thông báo đúng, sau khi lưu ô secret chỉ có `••••••••` và HTML không chứa mật khẩu đã gõ, đổi sang SendGrid không nhập key bị từ chối kèm lý do, bảng Delivery Logs hiện email gửi qua API với badge xanh "Thành công", không tràn ngang ở 390 px, không lỗi JS. Đã xem ảnh desktop; **riêng ảnh 390 px chỉ được kiểm bằng phép đo `scrollWidth`, chưa xem bằng mắt**.
- **Web (fake API)**: 30 test cho `/Email`: form đủ trường theo task, điền sẵn từ bản Active, secret chỉ là mask, mỗi dự án nạp cấu hình riêng, bảng log và badge, form POST có token; POST thiếu token / token từ trình duyệt khác / token giả đều 400 và không gọi API; Lưu Bản Nháp không kích hoạt, Lưu & Kích Hoạt lưu rồi kích hoạt đúng bản vừa lưu; mask quay lại thì không gửi secret; mỗi provider chỉ gửi settings của mình; Send Test theo các quy tắc ở 4.1; API sập thì trang vẫn mở.
- **Kiểm tra đột biến** (phá có chủ đích rồi khôi phục, toàn bộ xanh lại): bỏ map `BadHttpRequestException` làm 8 test tích hợp lỗi; cho kế thừa secret sang provider khác làm 2 + 1 test lỗi; để Send Test chưa lưu tạo cấu hình làm 1 + 2 test lỗi; bỏ thoát HTML làm 5 + 1 test lỗi; bỏ xóa secret khỏi thông điệp lỗi làm 8 test lỗi.
- Hai lỗi thật tìm ra khi viết test và đã sửa: cài đặt `"port":"587"` (chuỗi) làm `TryGetInt32` ném `InvalidOperationException` thay vì báo lỗi; một khẳng định vô nghĩa (luôn đúng) do tôi viết trong test dispatcher.

### 4.3. Các hạn chế / điểm cần lưu ý
1. **Web portal vẫn chưa có đăng nhập** (từ TASK-032), và nay còn có thể **gửi email thử tới địa chỉ tùy ý** và kích hoạt cấu hình: ai vào được cổng 5031 đều làm được. Chỉ chạy trong mạng tin cậy cho tới task SSO; mức ưu tiên của SSO nên tăng.
2. **Send Test và Send chưa có giới hạn tần suất / hạn mức**; gửi là đồng bộ (tối đa bằng timeout SMTP/SendGrid, mặc định 20 s), không có hàng đợi hay thử lại.
3. **Send Test từ bộ nhớ cho phép một người có scope `config:write` bảo dịch vụ kết nối tới host:port SMTP tùy ý** (thăm dò mạng). Giảm thiểu bằng `OutboundHostPolicy` (mặc định chặn loopback và mạng riêng; link-local và metadata luôn bị chặn) và thông điệp lỗi ngắn; còn lại: kiểm tra DNS rồi mới kết nối nên vẫn có khoảng hở DNS rebinding; production cần để `Email:AllowPrivateNetworkHosts=false`.
4. Trên màn hình, "Gửi Thử Nghiệm" khi **không gõ lại secret** chỉ thử được bản **đã lưu** (và chỉ khi settings chưa đổi); muốn thử cài đặt mới thì phải gõ lại mật khẩu/API key hoặc lưu nháp rồi thử. Đây là hệ quả có chủ ý của quy tắc "không dùng secret đã lưu với settings mới" (xem 4.1 điểm 5).
5. **Email thử (Send Test) không ghi vào delivery log** (chỉ ghi audit), nên bảng "Lịch sử gửi email gần đây" trên trang chỉ có các email gửi thật qua API/dispatcher.
6. Delivery log lưu **địa chỉ người nhận ở dạng rõ** (cần cho việc tra cứu) và chưa có chính sách lưu giữ/xóa.
7. **TLS chưa được kiểm tra với máy chủ TLS thật**: máy chủ SMTP giả chỉ nói chuyện văn bản thuần (`useSsl: None`); các chế độ `StartTls`/`SslOnConnect`/`Auto` chỉ được kiểm ở mức ánh xạ sang `SecureSocketOptions` và dùng nguyên xác thực chứng chỉ mặc định của MailKit. **SendGrid chỉ được kiểm với HTTP stub**, chưa gọi API thật (không có key).
8. `Brevo` còn trong danh mục provider nhưng **chưa có triển khai**: gửi qua cấu hình Brevo trả lỗi "provider is not available" (đã ghi log Failed); dropdown ở portal chỉ có Smtp, SendGrid, Mock như task.
9. Template chỉ có tiếng Việt, chưa có trình sửa template trên portal; ngôn ngữ giao diện portal vẫn hard-code tiếng Việt.
10. Cách lưu `DateTimeOffset` của **SQLite** đã đổi sang chuỗi UTC cố định (tương thích dữ liệu dev cũ); PostgreSQL không đổi. SQLite vẫn chỉ dành cho dev/test.
11. README của `repos/shared-services` **chưa cập nhật** vì nằm ngoài whitelist của task này (chỉ có các thư mục con được liệt kê); nên bổ sung mục Email (endpoint, mục cấu hình `Email`, key dev có `email:send`) ở task sau.

### 4.4. Sửa theo review (Major: "Lưu Bản Nháp" rồi "Lưu & Kích Hoạt" kích hoạt sai cấu hình)
**Nguyên nhân.** Khi dự án đã có bản Active, trang nạp form từ bản **Active** chứ không phải bản nháp vừa lưu. "Lưu & Kích Hoạt" tạo bản mới từ form đó (host/port cũ) với ô mật khẩu chỉ là dấu che; bản mới cùng provider nên **kế thừa secret của bản mới nhất (chính là bản nháp)**. Kết quả: settings cũ đi cùng mật khẩu mới, một tổ hợp chưa ai nhập, và email của dự án ngừng gửi được. Test của tôi trước đó chỉ chạy trên dự án chưa có bản Active nên không thấy.

**Đã sửa** (`Pages/Email.cshtml(.cs)`):
- Form luôn hiển thị **phiên bản mới nhất** của cấu hình đang sửa (bất kể trạng thái), nên bản nháp vừa lưu chính là thứ hiện ra, và ô mật khẩu che đúng secret của bản đó. Cấu hình đang sửa là cấu hình đang Active (nếu có), không thì cấu hình tạo gần nhất.
- Khi bản hiển thị không phải bản đang chạy, đầu trang ghi rõ "Đang hoạt động: vN (bản này chưa được dùng)".
- Thông báo sau khi lưu nháp nói rõ form đang hiển thị đúng bản nháp và "Lưu & Kích hoạt" sẽ dùng các giá trị đó.
- Rà cùng nguyên tắc ở chỗ khác: nút **Gửi Thử Nghiệm** trước đó so cài đặt trên màn hình với bản Active; nay so với **bản đang hiển thị**, nên "không đổi so với bản đã lưu" luôn có nghĩa là so với cái người dùng đang nhìn thấy.

**Test hồi quy** (`EmailPageEndToEndTests`, 3 test, chạy trang thật với **API thật** trong bộ nhớ và máy chủ SMTP giả, trên dự án đã có bản Active):
1. Đúng kịch bản của reviewer: v1 Active (máy chủ cũ, mật khẩu A) → trên trang đổi cổng sang máy chủ mới và gõ mật khẩu B → Lưu Bản Nháp → trang nạp lại phải hiện bản nháp (cổng mới, mật khẩu che, "Đang hoạt động: v1") → Lưu & Kích hoạt đúng form đang hiện → bản đang chạy có **cổng mới và mật khẩu B cùng nhau**, rồi gửi thư thật: máy chủ mới nhận đăng nhập `mailer/B` và thư, máy chủ cũ không bị gọi.
2. Sửa liên tiếp hai lần trên trang nạp lại, rồi kích hoạt: mỗi bước dựa vào đúng cái trang đang hiển thị.
3. Ca thường (chỉ đổi tên người gửi, không đổi mật khẩu): bản kích hoạt vẫn giữ mật khẩu của chính nó.
Cả 3 test **thất bại khi tôi đặt lại hành vi cũ** (hiển thị bản Active trước) và đạt với bản sửa; ca thường cũng sai với mã cũ (mất tên người gửi mới), tức lỗi ảnh hưởng mọi lần sửa chứ không riêng cặp cổng + mật khẩu. Thêm 4 test với API giả: form hiện bản nháp mới hơn bản Active, ghi chú bản đang chạy, không ghi chú khi bản hiển thị chính là bản Active, Gửi Thử Nghiệm thử bản nháp khi form khớp bản nháp, và không coi settings của bản Active cũ là "không đổi".

**Kiểm chứng lại.** Build 0 lỗi 0 cảnh báo; UnitTests 314/314; IntegrationTests 138 PASS + 4 bỏ qua (tổng 142); với PostgreSQL 16 trên máy **142/142**. Chạy lại đúng các bước của reviewer trên hai host thật (`dotnet run`) bằng Edge: 6/6 kiểm tra đạt (sau Lưu Bản Nháp form hiện cổng 2626 và "Đang hoạt động: v1"; sau Lưu & Kích Hoạt, v3 Active mang cổng 2626); đã tắt hai host và xóa dữ liệu chạy thử.

---

## 5. Kết Quả Code Review (Dành cho Claude Code Reviewer)

*(Reviewer điền vào phần này theo `docs/roles/CODE_REVIEWER.md`)*

- **Reviewer**: Claude Code (Reviewer Session) — **Ngày**: 2026-11-11 — **Lần review**: 1
- **Trạng thái Review**: `CHANGES_REQUESTED` (0 Blocker, 1 Major, 4 Minor)

### 5.1. Xác minh độc lập
```bash
dotnet build repos/shared-services/AppInHouse.SharedServices.sln --no-incremental   # 0 Warning, 0 Error
dotnet test  repos/shared-services/AppInHouse.SharedServices.sln                    # Unit 314/314; Integration 131 pass + 4 skipped
SHARED_SERVICES_TEST_POSTGRES='Host=localhost;Port=5432;Username=postgres;Password=postgres' \
dotnet test tests/AppInHouse.SharedServices.IntegrationTests --filter "FullyQualifiedName~Postgres"   # 4/4 (PostgreSQL 16 trên máy)
```
- **Phạm vi file**: đúng whitelist (28 file sửa + 17 file mới, tất cả trong các project Domain/Application/Infrastructure/Persistence/Api/Web/ClientSdk và 2 project test; cộng file task). Không có thay đổi nào khác trong working tree.
- **Chạy thật** (`ASPNETCORE_ENVIRONMENT=Development`, API 5030 + Web 5031, SQLite và key ring riêng trong scratchpad của reviewer; hai máy chủ SMTP giả viết bằng Node ở 127.0.0.1:2525 và :2526, ghi lại lệnh AUTH/MAIL/RCPT nhận được):
  - `/health` 200; không key → 401.
  - **Send**: mẫu `EmailVerification` cho dự án `rv033` (chưa có cấu hình) → fallback `default` Mock v1, `200 Sent`; Subject có `\r\nBcc:` bị gộp thành một dòng; `to` là danh sách → 400 `To`; `link=javascript:` → 400 `Variables`. Sau khi kích hoạt SMTP cho `rv033` → thư thật tới :2525 với đúng mật khẩu đã lưu, `configurationProject=rv033`. Cổng chết → **502** "Could not connect to 127.0.0.1:1.", log `Failed` 2024 ms.
  - **Minor #1 (TASK-032)**: ở Development, JSON hỏng (`/emails/send`, `/channel-configurations`) và enum sai (`providerType":"Nope"`) đều **400 problem+json**, không stack trace. ✅
  - **Send Test từ bộ nhớ (P0)**: Mock, SMTP với mật khẩu gõ trong request (tới :2525), cổng chết, `169.254.169.254` (bị chặn "link-local") — chụp toàn bộ danh sách cấu hình Email trước/sau: **không đổi** (số bản, version, status, last_test). Settings mới không kèm secret → không dùng mật khẩu đã lưu. ✅
  - **Send Test theo id**: Draft v3 được thử và cập nhật `lastTestStatus=Success`, không kích hoạt; id lạ → 404; id + settings lạ → chỉ dùng bản đã lưu (thư tới :2525, không tới host trong request).
  - **Minor #3 (TASK-032)**: Smtp → SendGrid không secret → 400 trường `Secret`; có secret → 201 `hasSecret=true`; → Mock / → Smtp lại không secret → `hasSecret=false` (không kế thừa chéo provider). ✅
  - **Secret**: 5 mật khẩu/key thử (`rv033-*`, `SG.rv033-*`) **không có** trong `ss.db`/WAL/SHM, `api.log`, `web.log`; delivery log chỉ có project, kênh, người nhận, trạng thái, phản hồi ngắn, độ trễ. ✅
- **Web `/Email` bằng Edge headless** (`scratchpad/web022/portal033.mjs`, ảnh `shots033/`): **17/20** lần đầu; 3 lần fail là cùng một lỗi (Finding #1), phần còn lại kiểm lại bằng API:
  - Form nạp từ bản Active (host/port/SSL/username/sender), secret chỉ `••••••••`, mật khẩu không có trong HTML; dropdown dự án có default/linguabridge/identity; SSL đủ 4 lựa chọn; mọi form POST có token.
  - Đổi provider: SendGrid ẩn trường SMTP, nhãn "API Key", xóa mask; Mock ẩn ô secret, hiện ghi chú.
  - Gửi Thử Nghiệm: Mock xanh, cấu hình không đổi; SMTP giữ mask + cài đặt không đổi → thử bản đã lưu (tới :2525); đổi port mà giữ mask → đỏ, yêu cầu nhập mật khẩu, không gửi gì; gõ mật khẩu → xanh "(giá trị đang nhập, chưa lưu)", tới :2526 bằng mật khẩu vừa gõ, cấu hình không đổi; cổng chết → đỏ, không lộ mật khẩu; POST thiếu token → 400.
  - Bảng lịch sử: badge xanh "Thành công" và đỏ "Thất bại", người nhận, độ trễ, phản hồi (đã xem ảnh desktop và ảnh 390 px; bảng cuộn trong khung, trang không tràn ngang); đổi sang SendGrid không nhập key → thông báo lỗi rõ, không tạo version. 0 lỗi JS.

### 5.2. Findings

| # | Mức độ | File:dòng | Vấn đề | Đề xuất |
|---|---|---|---|---|
| 1 | 🟡 Major | `Web/Pages/Email.cshtml.cs:64-65, 97, 106` | **"Lưu Bản Nháp" rồi "Lưu & Kích Hoạt" kích hoạt sai cấu hình khi dự án đã có bản Active.** Sau khi lưu nháp, trang (PRG) nạp lại form từ **bản Active** chứ không phải bản nháp vừa lưu, trong khi thông báo bảo *"Bấm "Lưu & Kích hoạt" để dùng"*. Làm đúng lời nhắc đó thì form (đang chứa cài đặt cũ + mask) được lưu thành version mới, mask nghĩa là "kế thừa secret của bản mới nhất" — tức **mật khẩu của bản nháp**. Tái hiện thật: v2 Active (`:2525`, mật khẩu A) → Lưu Bản Nháp v7 (`:1`, mật khẩu B) → Lưu & Kích Hoạt → **v8 Active = host/port của v2 + mật khẩu B**; v7 bị Archived và không bao giờ được dùng; gửi qua API thì máy chủ :2525 nhận `AUTH ... B`. Với máy chủ thật, login bị từ chối → **toàn bộ email của dự án hỏng**, trong khi người dùng tin rằng cấu hình mới đã chạy. Test của Coder (fake API và E2E) chỉ chạy trên dự án chưa có bản Active nên không bắt được. | Sau Lưu Bản Nháp, hiển thị bản nháp vừa lưu (ví dụ redirect kèm `version`/`id`, và `OnGet` ưu tiên Draft mới hơn bản Active, ghi rõ "đang sửa bản nháp vN"); hoặc nút kích hoạt kích hoạt đúng id bản nháp đó. Sửa lời nhắc cho khớp. Thêm test web/E2E cho đúng kịch bản "đã có Active → lưu nháp → kích hoạt" và kiểm settings + secret của bản Active cuối cùng. |
| 2 | 🔵 Minor | `Application/Email/EmailTestService.cs` (lớp doc), `Domain/ChannelConfigurationVersioning.cs:33` | Bảo vệ *"không bao giờ dùng secret đã lưu với settings trong request"* (4.1 điểm 5, 4.3 điểm 4) **vượt qua được trong 2 bước** với cùng quyền `config:write`: lưu version mới cùng provider, đổi host, không gõ secret (secret được kế thừa) rồi test theo id. Đã tái hiện: v3 `:2526` kế thừa từ v2 → `POST /emails/test {configurationId: v3}` → máy chủ :2526 nhận đúng mật khẩu đã lưu. Không phải lỗ hổng mới (ai có `config:write` vốn kích hoạt được), nhưng làm UX phức tạp hơn mà không thật sự bảo vệ. | Chọn một trong hai (Gemini quyết, xem 5.3): (a) chỉ kế thừa secret SMTP khi `host` + `username` không đổi (thật sự chặn đưa mật khẩu tới host mới); hoặc (b) bỏ ràng buộc ở Send Test và ghi rõ trong tài liệu rằng `config:write` là quyền ngang quản trị. |
| 3 | 🔵 Minor | `Web/Pages/Email.cshtml` (dòng chú thích dưới nút) | Chú thích "Gửi thử dùng đúng các giá trị đang nhập, không lưu..." không đúng khi giữ mask: khi đó thử **bản đã lưu** (kết quả có hậu tố "đã thử cấu hình đã lưu", nên vẫn hiểu được). | Ghi đúng hai trường hợp. |
| 4 | 🔵 Minor | `Infrastructure/DependencyInjection.cs:62-63` | `MockEmailProvider` được đăng ký ở **mọi môi trường**: ở production, một cấu hình Mock được kích hoạt (nhầm hoặc cố ý) sẽ "gửi" thư vào bộ nhớ và ghi log `Sent`, email thật biến mất mà không có cảnh báo. | Chỉ đăng ký Mock ở Development/Testing, hoặc validator từ chối kích hoạt Mock khi không phải môi trường dev. |
| 5 | 🔵 Minor | `Infrastructure/Email/EmailProviders.cs` (`MockEmailProvider.SendAsync`) | Log Information của Mock in **Subject** (doc nói "logs who it was for, the body is not logged"). Mẫu có sẵn không đưa mã OTP vào tiêu đề, nhưng `RenderCustom` cho phép người gọi đặt `{{code}}` trong Subject. | Chỉ log người nhận + dự án, hoặc ghi rõ trong doc. |

Ghi chú khác (không phải finding): `ErrorText.Clean`, `OutboundHostPolicy`, thoát HTML trong template, chặn header injection qua Subject, quy tắc 403 theo dự án của API key cho `send`/`delivery-logs` đều có test và đúng khi chạy thật. Chuyển `DateTimeOffset` của SQLite sang chuỗi UTC cố định: rollback/activate của TASK-032 vẫn đúng trên DB mới (activate v2 → v8 → v9 lần lượt đúng thứ tự).

### 5.3. Cần Gemini quyết định
- **Finding #2**: chọn (a) "SMTP chỉ kế thừa mật khẩu khi host + username không đổi" hay (b) chấp nhận `config:write` = quyền ngang quản trị và đơn giản hóa Send Test. Khuyến nghị (a): rẻ, và đúng với ý định của TASK-032 Minor #3.
- **`POST /emails/test` theo `configurationId` chưa giới hạn theo dự án**: key `config:write` của dự án A thử được (tức gửi thư bằng credential của) cấu hình dự án B tới địa chỉ tùy ý. Cùng gốc với ghi chú TASK-032 (endpoint cấu hình chưa áp `project_code`); `send`/`delivery-logs` thì đã áp. Nên gom vào một task chuẩn hóa phân quyền theo dự án trước khi cấp key cho các dự án (TASK-036).
- **Web portal vẫn chưa có đăng nhập** và nay gửi được email tới địa chỉ tùy ý (Coder đã nêu ở 4.3 điểm 1) — tăng ưu tiên task SSO.
- Đồng ý với các quyết định 1, 2, 4, 6 của Coder ở 4.1. Quyết định 3 (đổi sang Smtp/Mock không bắt nhập secret) hợp lý.
- README của `repos/shared-services` cần mục Email (ngoài whitelist task này).


### 5.4. Review lần 2 (2026-11-11)

- **Kết luận**: `REVIEW_PASSED` — Major #1 đã sửa và được xác minh độc lập; còn 4 Minor (không chặn).

**Xác minh độc lập**
```bash
dotnet build repos/shared-services/AppInHouse.SharedServices.sln --no-incremental   # 0 Warning, 0 Error
dotnet test  repos/shared-services/AppInHouse.SharedServices.sln                    # Unit 314/314; Integration 138 pass + 4 skipped (142)
SHARED_SERVICES_TEST_POSTGRES='Host=localhost;Port=5432;Username=postgres;Password=postgres' \
dotnet test tests/AppInHouse.SharedServices.IntegrationTests --no-build             # 142/142 (PostgreSQL 16 trên máy)
```
- **Phạm vi file**: thay đổi từ lần 1 chỉ ở `Web/Pages/Email.cshtml`, `Email.cshtml.cs`, `IntegrationTests/EmailPageTests.cs` và `EmailPageEndToEndTests.cs` (mới) — đều trong whitelist; không có file ngoài phạm vi.
- **Code**: `OnGetAsync` chọn cấu hình đang sửa (tên của bản Active, không có thì bản tạo gần nhất) và hiển thị **version mới nhất** của nó, kèm `LiveVersion` khi bản hiển thị khác bản đang chạy; `FindSavedAsync` (Gửi Thử Nghiệm) cũng so với version mới nhất, nên "không đổi" luôn nghĩa là không đổi so với thứ đang hiện trên màn hình. Mask lúc này đại diện đúng secret của bản đang hiển thị, nên kế thừa khi lưu tiếp là kế thừa đúng secret đó.

**Chạy lại đúng kịch bản lỗi** (API 5030 + Web 5031 Development, DB/key ring mới trong scratchpad, SMTP giả :2525/:2526; `scratchpad/web022/portal033r2.mjs`, Edge headless) — **10/10**:
1. v1 Active (:2525, mật khẩu A) → trên trang đổi cổng 2526, gõ mật khẩu B → **Lưu Bản Nháp** → trang hiện **v2 Draft** (cổng 2526, mask) và "Đang hoạt động: v1 (bản này chưa được dùng)"; mật khẩu B không có trong HTML; v1 vẫn Active.
2. Gửi Thử Nghiệm giữ mask → thử bản nháp v2: :2526 nhận `AUTH rv033user / B`, :2525 không bị gọi.
3. **Lưu & Kích Hoạt** → v3 Active với cổng 2526; gửi qua API → :2526 nhận **B** (đúng cặp cổng + mật khẩu), :2525 không bị gọi. *(Lần 1: v8 Active mang cổng cũ + mật khẩu mới.)*
4. Chỉ đổi tên người gửi rồi Lưu & Kích Hoạt → v4 giữ :2526 + B, thư có `From: RV 033 renamed`.
5. Hai bản nháp liên tiếp: bản thứ hai dựa trên bản thứ nhất; ghi chú "Đang hoạt động: v4" đúng.
6. 390 px không tràn ngang; 0 lỗi JS. Đã xem ảnh `shots033/r2-after-draft.png`.

**Findings còn lại** (không chặn, giữ nguyên từ 5.2): #2 bảo vệ "không dùng secret đã lưu với settings mới" vượt qua được qua lưu nháp + test theo id (chờ Gemini quyết, xem 5.3); #3 chú thích dưới nút Gửi Thử Nghiệm chưa đúng khi giữ mask (vẫn chưa sửa); #4 Mock được đăng ký ở mọi môi trường; #5 Mock log Subject.

| # | Mức độ | File:dòng | Vấn đề | Đề xuất |
|---|---|---|---|---|
| 6 | 🔵 Minor | `Web/Pages/Email.cshtml.cs:72-74` | Nếu người dùng **đổi "Tên cấu hình"** rồi Lưu Bản Nháp, bản nháp thuộc một series mới; trang nạp lại theo tên của bản Active nên không hiện bản nháp đó (không trộn secret như lỗi cũ, chỉ là bản nháp "biến mất" khỏi trang, xem được ở "Tất cả cấu hình"). | Redirect kèm tên/id vừa lưu, hoặc khóa ô tên khi đã có bản Active. |

**Dọn dẹp**: đã tắt API, Web và hai SMTP giả; dữ liệu thử chỉ nằm trong scratchpad, không tạo file trong repo.

---

## 6. Kết Luận Nghiệm Thu (Dành cho Gemini Tech Lead)

- **Người Đánh Giá**: Gemini (Tech Lead / Orchestrator)
- **Ngày Nghiệm Thu**: 2026-11-11
- **Trạng Thái Nghiệm Thu**: `APPROVED`

### 6.1. Đánh giá chất lượng thực thi
1. **Kiến trúc & Clean Architecture**:
   - `IEmailProvider` đặt chuẩn tại `Application`, các cụm triển khai `MailKitSmtpEmailProvider`, `SendGridEmailProvider`, `MockEmailProvider` nằm tại `Infrastructure`.
   - Template Engine độc lập, tích hợp bảo vệ chống chèn HTML, chống Header Injection (`\r\n`), và kiểm soát an toàn URL protocol (chặn `javascript:`).
   - Tích hợp chính sách `OutboundHostPolicy` ngăn chặn SSRF cực kỳ hiệu quả (chặn link-local, multicast, đám mây metadata `169.254.169.254`).
2. **Nghiệp Vụ Send Test (P0 Requirement)**:
   - Thử nghiệm trực tiếp trong bộ nhớ RAM hoạt động đúng chuẩn: không sinh bản ghi thừa trong database, không làm thay đổi trạng thái kích hoạt của hệ thống, chỉ ghi nhận một dòng nhật ký kiểm toán `SendTest` an toàn không chứa credential.
   - Sửa lỗi Major #1 triệt để: Form Web Portal luôn nạp bản nháp mới nhất đang thao tác, bảo đảm tính toàn vẹn giữa cặp cấu hình Host/Port và Secret.
3. **Bảo Mật Secret & Tinh Chỉnh Ràng Buộc**:
   - Đã xử lý triệt để Minor #1 (trả về HTTP 400 ProblemDetails cho `BadHttpRequestException` trong môi trường Development thay vì 500).
   - Đã xử lý triệt để Minor #3 (ngăn kế thừa secret chéo khi đổi ProviderType sang provider đòi hỏi secret mới).
4. **Chất Lượng Kiểm Thử**:
   - **314/314 Unit Tests** và **138/142 Integration Tests** vượt qua 100% (4 bài test Postgres chuyên biệt chạy thành công khi có kết nối PostgreSQL thật).
   - Độ bao phủ kiểm thử cao, kiểm tra đột biến (mutation test) và chạy thực tế cả API lẫn Web Portal trên cổng 5030/5031 qua Edge headless.

### 6.2. Phê duyệt & Quyết định kỹ thuật (Mục 5.3)
1. **Về Finding #2 & Phân quyền theo dự án**:
   - Phê duyệt phương án (a) kết hợp: Cơ chế Send Test in-memory giữ vững an toàn secret; việc phân quyền truy cập và giới hạn cấu hình theo `project_code` sẽ được chuẩn hóa đồng bộ tại **TASK-036 (API Key Lifecycle, Scopes & Project Isolation)**.
2. **Về Quyết định 1, 2, 3, 4, 5 của Coder (Mục 4.1)**: Toàn bộ đều được **Chấp thuận**.
   - `EmailMessage` không cho phép can thiệp `From` tự do từ client (ngăn chặn mạo danh người gửi).
   - `SendGridBaseUrl` là cấu hình mức host, ngăn chặn khai thác chuyển hướng API key.
   - Định dạng chuỗi UTC cố định cho SQLite DateTimeOffset hỗ trợ truy vấn sắp xếp chính xác.

TASK-033 chính thức được nghiệm thu **APPROVED**. Hệ thống sẵn sàng bước sang **TASK-034: Telegram Bot Outbound Messaging Subsystem & Web Portal Integration**.
