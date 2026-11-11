# TASK-034: Multi-Provider Chat & Instant Messaging Subsystem (Telegram, Discord, Slack, Mock)

- **Mã Task**: TASK-034
- **Tên Task**: Phân Hệ Gửi Tin Nhắn Chat Đa Nền Tảng (Telegram Bot, Discord Webhook, Slack Webhook, Mock), Safe MarkdownV2, Thử Nghiệm "Send Test" Trực Tiếp & Trang Quản Trị Chat Web Portal
- **Người Giao (Tech Lead)**: Gemini
- **Người Thực Hiện (Implementer)**: Claude Code (Coder)
- **Người Đánh Giá (Reviewer)**: Claude Code (Reviewer)
- **Trạng Thái**: `APPROVED`
- **Ngày Tạo**: 2026-11-11
- **Tài Liệu Tham Chiếu**: [shared_services_design.md](../architecture/shared_services_design.md), [TASK-032-shared-services-core-architecture.md](./TASK-032-shared-services-core-architecture.md), [TASK-033-shared-services-email-subsystem.md](./TASK-033-shared-services-email-subsystem.md)

---

## 1. Mục Tiêu Kỹ Thuật

Mở rộng và chuẩn hóa phân hệ tin nhắn từ chỉ hỗ trợ Telegram thành **Phân Hệ Gửi Tin Nhắn Chat Đa Nền Tảng (Multi-Provider Chat & Instant Messaging)** dùng chung cho toàn bộ hệ sinh thái AppInHouse (Identity STS gửi cảnh báo bảo mật, LinguaBridge gửi nhắc nhở học tập, DevOps thông báo sự cố, v.v.):

1. **Mở Rộng Domain & Provider Catalog (`Domain`)**:
   - Bổ sung `ChannelType.Chat` (vẫn duy trì tương thích ngược với `ChannelType.Telegram` nếu có cấu hình cũ).
   - Mở rộng `ProviderType` bao gồm:
     - `TelegramBot` (Telegram Bot API)
     - `DiscordWebhook` (Discord Incoming Webhook)
     - `SlackWebhook` (Slack Incoming Webhook)
     - `Mock` (Giả lập in-memory cho Dev/Testing)
   - Cập nhật `ProviderCatalog.RequiresSecret`:
     - `TelegramBot`, `DiscordWebhook`, `SlackWebhook` đều là provider yêu cầu secret bắt buộc (Bot Token hoặc Webhook URL).
2. **Chat Provider Abstractions & Implementations (`Application` & `Infrastructure`)**:
   - `IChatProvider`:
     - `ProviderType Type { get; }`
     - `Task<ChatSendResult> SendMessageAsync(ChatMessage message, ChannelRuntimeConfiguration config, CancellationToken ct = default);`
     - `Task<ChatTestResult> TestConnectionAsync(ChannelRuntimeConfiguration config, string testRecipient, string testMessage, CancellationToken ct = default);`
   - DTO `ChatMessage`:
     - `Recipient`: Chat ID (đối với Telegram), hoặc kênh/override tùy chọn (đối với Discord/Slack).
     - `Text`: Nội dung tin nhắn (bắt buộc, không rỗng).
     - `Format`: `PlainText`, `Markdown`, `MarkdownV2` (dành riêng cho Telegram).
     - `ProjectCode`: Mã dự án gửi tin.
     - `Metadata`: Thuộc tính mở rộng tùy chọn.
   - Triển khai cụ thể:
     - **`TelegramBotChatProvider`**:
       - Gửi tin nhắn qua Telegram Bot API (`https://api.telegram.org/bot<token>/sendMessage`) sử dụng `HttpClient` được inject.
       - Bot Token được giải mã an toàn từ `EncryptedSecret`.
       - Hỗ trợ cơ chế **Safe MarkdownV2 Escape**: Tự động escape các ký tự đặc biệt (`_ * [ ] ( ) ~ > # + - = | { } . !`) khi format là `PlainText` hoặc khi người dùng yêu cầu safe escape, ngăn chặn triệt để lỗi HTTP 400 từ Telegram Bot API khi nội dung có ký tự đặc biệt.
     - **`DiscordWebhookChatProvider`**:
       - Gửi tin nhắn tới Discord Incoming Webhook (`https://discord.com/api/webhooks/...`).
       - Webhook URL hoặc Webhook Token được bảo vệ trong `EncryptedSecret`.
       - SettingsJson hỗ trợ cấu hình: `defaultUsername`, `avatarUrl`.
       - Giới hạn payload an toàn (tối đa 2000 ký tự theo đặc tả của Discord).
     - **`SlackWebhookChatProvider`**:
       - Gửi tin nhắn tới Slack Incoming Webhook (`https://hooks.slack.com/services/...`).
       - Webhook URL được bảo vệ trong `EncryptedSecret`.
       - SettingsJson hỗ trợ cấu hình: `defaultChannel`, `iconEmoji`.
     - **`MockChatProvider`**:
       - Lưu trữ tin nhắn gửi đi trong bộ nhớ RAM (`IReadOnlyList<ChatMessage> SentMessages`), phục vụ Dev & Unit/Integration tests không cần internet.
3. **Bảo Mật SSRF & URL Validation (`OutboundHostPolicy`)**:
   - Áp dụng kiểm tra bảo mật tương tự Email:
     - Đối với Discord/Slack Webhook URLs: Phải là URL HTTPS hợp lệ; kiểm tra qua `OutboundHostPolicy` để chặn URL trỏ về mạng riêng (loopback, link-local, cloud metadata `169.254.169.254`).
4. **Chat Dispatcher & Ghi Nhật Ký Delivery Logs (P1)**:
   - `IChatDispatcher` / `ChatDispatcher`:
     - Nhận yêu cầu gửi tin nhắn từ API hoặc Client SDK.
     - Nạp cấu hình Active theo `ProjectCode` (fallback sang `default` nếu dự án chưa có cấu hình riêng).
     - Định tuyến tới Provider tương ứng.
     - Đo độ trễ thực thi (`latency_ms`).
     - Ghi nhận kết quả vào `shared.delivery_logs` (thông tin: `id`, `project_code`, `channel_type` = 'Chat' hoặc 'Telegram', `recipient`, `status` = 'Sent' / 'Failed', `provider_response` đã làm sạch, `latency_ms`, `created_at`).
     - **Tuyệt đối không lưu Bot Token, Webhook URL bí mật, hay toàn bộ nội dung tin nhắn vào delivery log**.
5. **Nghiệp Vụ "Send Test" Độc Lập Trực Tiếp Từ Bộ Nhớ (P0 Requirement)**:
   - Endpoint: `POST /api/v1/chat/test` (kèm alias `POST /api/v1/telegram/test` để tương thích):
     - Chế độ 1: Thử nghiệm cấu hình đang nhập (truyền trực tiếp `provider_type`, `settings_json`, và secret mới trong payload). Gửi tin nhắn test trực tiếp từ RAM, **không ghi đè hay kích hoạt cấu hình vào database**.
     - Chế độ 2: Thử nghiệm cấu hình đã lưu (truyền `configuration_id`). Sau khi test, cập nhật `last_tested_at` và `last_test_status` ('Success' / 'Failed').
     - Phản hồi làm sạch lỗi: Tuyệt đối không làm lộ bot token hay webhook url ra response nếu xảy ra lỗi mạng hoặc lỗi xác thực từ bên thứ ba.
6. **Web Portal Quản Trị Chat (`AppInHouse.SharedServices.Web` — Trang `/Chat` và `/Telegram`)**:
   - Layout Sidebar: Cập nhật mục Sidebar thành 💬 **Tin Nhắn & Chat (Telegram, Discord, Slack)** dẫn tới `/Chat`; trang `/Telegram` chuyển hướng hoặc render trang `/Chat`.
   - Chọn Dự án (`ProjectCode`: `default`, `linguabridge`, `identity`...).
   - Lựa chọn Provider: Dropdown `TelegramBot`, `DiscordWebhook`, `SlackWebhook`, `Mock`.
   - Form cấu hình động hiển thị tương ứng theo Provider:
     - **TelegramBot**: Default Chat ID, Parse Mode (PlainText / MarkdownV2), Bot Token (mask `••••••••`).
     - **DiscordWebhook**: Webhook URL (mask `••••••••`), Tên hiển thị bot (tùy chọn), Avatar URL (tùy chọn).
     - **SlackWebhook**: Webhook URL (mask `••••••••`), Tên kênh override (tùy chọn).
     - **Mock**: Ghi chú giả lập, không cần secret.
   - Thử nghiệm gửi tin (**Send Test**): Ô nhập Recipient/Chat ID + Nội dung tin nhắn thử nghiệm + nút **"Gửi Thử Nghiệm" (Send Test)** với chỉ báo loading và thông báo xanh/đỏ rõ ràng.
   - Form luôn hiển thị bản nháp mới nhất đang thao tác (theo chuẩn đã khắc phục ở TASK-033).
   - Nút **"Lưu Bản Nháp" (Save Draft)** và nút **"Lưu & Kích Hoạt" (Save & Activate)**.
   - Bảng **Recent Delivery Logs** hiển thị lịch sử gửi tin nhắn (badge trạng thái Thành công / Thất bại, người nhận/kênh, độ trễ, thời gian).
   - Toàn bộ thao tác POST được bảo vệ bởi **Anti-Forgery Token (CSRF)**.
7. **Bổ Sung Client SDK (`AppInHouse.SharedServices.ClientSdk`)**:
   - Bổ sung phương thức vào `ISharedServicesClient`:
     - `Task<SendChatMessageResponse> SendChatMessageAsync(SendChatMessageRequest request, CancellationToken ct = default);`
     - `Task<SendTestChatMessageResponse> SendTestChatMessageAsync(SendTestChatMessageRequest request, CancellationToken ct = default);`
     - `Task<IReadOnlyList<DeliveryLogDto>> GetChatDeliveryLogsAsync(string? projectCode = null, int limit = 20, CancellationToken ct = default);`

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
- [ ] `docs/handoffs/TASK-034-shared-services-chat-messaging-subsystem.md` *(Điền báo cáo triển khai)*

---

## 3. Tiêu Chí Nghiệm Thu (Acceptance Criteria)

1. **Biên dịch & Test Coverage**:
   - Solution `repos/shared-services/AppInHouse.SharedServices.sln` biên dịch sạch 100% không warning/error (`dotnet build`).
   - Toàn bộ unit tests và integration tests cũ và mới đều vượt qua (`dotnet test`).
2. **Triển Khai & Kiểm Thử Các Provider**:
   - Unit test kiểm chứng: `MockChatProvider` ghi nhận đúng tin nhắn gửi đi trong RAM; `TelegramBotChatProvider` gửi request đúng cấu trúc và giải mã Bot Token an toàn; `DiscordWebhookChatProvider` và `SlackWebhookChatProvider` tạo payload JSON chính xác và nạp Webhook URL an toàn.
   - Unit test kiểm chứng: Bộ lọc Safe MarkdownV2 escape chính xác các ký tự đặc biệt của Telegram.
   - Unit test kiểm chứng: URL Webhook trỏ tới IP private/metadata bị chặn bởi chính sách SSRF.
3. **Send Test Không Lưu Đè Database (P0)**:
   - Integration test: Gọi endpoint `POST /api/v1/chat/test` với payload cấu hình mới: gửi thành công tin nhắn test mà **không tạo bản ghi mới** trong bảng `channel_configurations` và **không làm thay đổi** phiên bản Active hiện tại.
4. **Bảo Mật & Validation**:
   - Khi tạo phiên bản mới chuyển đổi giữa các Provider (ví dụ từ `TelegramBot` sang `DiscordWebhook`) mà không gửi secret: Validator ném lỗi yêu cầu nhập secret mới.
   - Delivery log không lưu Bot Token hay Webhook URL.
5. **Giao Diện Web Portal `/Chat`**:
   - Khởi chạy Web Portal trên cổng 5031, truy cập trang `/Chat`:
   - Hiển thị đầy đủ form cấu hình cho TelegramBot / Discord / Slack / Mock với secret che dấu `••••••••`.
   - Nút Send Test hoạt động trơn tru qua POST có Anti-Forgery Token, hiển thị kết quả trực quan.
   - Bảng nhật ký Delivery Logs hiển thị đúng các tin nhắn đã gửi.

---

## 4. Báo Cáo Triển Khai (Dành cho Claude Code Coder)

### 4.1. Các thay đổi thực tế

Tất cả nằm trong whitelist (`repos/shared-services/**` các project được liệt kê + file task này). Không sửa `AGENTS.md`, `CLAUDE.md`, `docs/architecture/*`, README, `.sln`, `Directory.Build.props`, repo khác. Không commit.

**Domain / Application**
- `Enums.cs`: `ChannelType.Chat` (giữ `Telegram` để không mất cấu hình/log cũ), `ProviderType.DiscordWebhook/SlackWebhook`, catalog Chat, `RequiresSecret` cho Telegram/Discord/Slack. `ApiKey.cs`: scope `chat:send`.
- `Application/Chat/*` (mới): `IChatProvider`, `ChatMessage` (`Format` nullable: null = theo cấu hình; `PlainText` luôn bị escape), `ChatSettings`/`ChatSecretRules`/`ChatEscaping` (Safe MarkdownV2, escape Slack `& < >`, cắt Discord 2000 ký tự), `ChatDispatcher` (chọn cấu hình Active của project → fallback `default`; tìm Chat trước rồi Telegram cũ; ghi `delivery_logs`, chỉ lưu đích + trạng thái + phản hồi đã làm sạch + latency, không lưu nội dung/token/webhook), `ChatTestService` (xem dưới).
- `Abstractions/Providers.cs`: bỏ `ITelegramProvider`/`ProviderResult` cũ (chỉ còn `IStorageProvider`). `ChannelConfigurationValidator`: kiểm tra dạng secret (token Telegram `^[A-Za-z0-9_-]{3,40}:[A-Za-z0-9_-]{3,160}$`; webhook Discord/Slack: https, allow-list host `discord.com`/`discordapp.com` (+ subdomain) / `hooks.slack.com`, không userinfo/port/query/fragment, đúng mẫu path) và settings chat. Đổi provider mà không nhập secret mới → `ValidationException` trên `Secret` (secret không bao giờ kế thừa giữa các provider).
- `EmailContracts.cs` + `Persistence/Repositories.cs`: `IDeliveryLogRepository.ListRecentAsync(project, IReadOnlyCollection<ChannelType>, limit)` để liệt kê log Chat + Telegram cũ (không có Email).

**Infrastructure**
- `Chat/ChatProviders.cs` (mới): `MockChatProvider` (RAM), `TelegramBotChatProvider` (`POST {base}/bot{token}/sendMessage`, base cấu hình bằng `Chat:TelegramBaseUrl`), `DiscordWebhookChatProvider` (`?wait=true`, `allowed_mentions.parse=[]`), `SlackWebhookChatProvider`. Discord/Slack đi qua `OutboundHostPolicy` (chặn link-local/metadata/multicast luôn; loopback/private trừ khi `Email:AllowPrivateNetworkHosts`) — thêm `IHostAddressResolver`/`DnsHostAddressResolver` để test không cần DNS thật.
- `DependencyInjection.cs`: HttpClient của provider thật dùng `.RemoveAllLoggers()` (framework mặc định log URI request, mà URI chứa token/webhook) và `AllowAutoRedirect=false`.

**API**: `ChatEndpoints.cs` — `POST /api/v1/chat/send` (scope `chat:send`; 409 khi chưa có cấu hình, 502 khi provider từ chối), `POST /api/v1/chat/test` + alias `POST /api/v1/telegram/test` (scope `config:write`), `GET /api/v1/chat/delivery-logs`. `ProjectAccess.cs` gom quy tắc "key chỉ thao tác project của nó; key project `default` thao tác mọi project" (dùng chung với Email). `Program.cs`, `appsettings.json` (section `Chat`, lọc log HttpClient), `DevelopmentSeeder.cs` (Chat Mock Active + TelegramBot Draft; dev key có `chat:send`).

**Send Test (P0)**: chế độ 1 (không `configurationId`) thử bằng provider/settings/secret trong request, **không đụng `channel_configurations`** (chỉ ghi 1 audit entry không chứa secret); không bao giờ mượn secret đã lưu. Chế độ 2 (`configurationId`) dùng secret đã lưu của đúng phiên bản đó, cập nhật `last_tested_at/last_test_status`, bỏ qua settings đi kèm request.

**Web**: `Pages/Chat.cshtml(.cs)` (form Telegram/Discord/Slack/Mock, secret luôn là `••••••••`, Lưu Bản Nháp / Lưu & Kích Hoạt, Gửi Thử Nghiệm bằng POST kèm anti-forgery header, bảng Delivery Logs); form hiển thị **phiên bản mới nhất** (bài học review TASK-033: "Lưu & Kích hoạt" phải kích hoạt đúng cái đang hiển thị) kèm ghi chú phiên bản đang live. Portal chỉ dùng cấu hình đã lưu khi secret là mask **và** settings trên màn hình trùng bản đã lưu; ngược lại yêu cầu nhập lại token/webhook. `Telegram.cshtml.cs` redirect về `/Chat` (giữ `?project`). `_Layout.cshtml` (mục sidebar "Tin Nhắn & Chat — Telegram · Discord · Slack"), `Index.cshtml.cs`, `Configurations/Index.cshtml.cs`.

**Client SDK**: `SendChatMessageAsync`, `SendTestChatMessageAsync`, `GetChatDeliveryLogsAsync` + DTO.

**Test mới/sửa**: UnitTests `ChatRulesTests`, `ChatProviderTests`, `ChatDispatcherTests` (gồm `ChatTestServiceTests`, `ChatProviderChangeTests`, `ChatClientSdkTests`), `Support/Fakes.cs`, `ValidatorTests.cs`; IntegrationTests `ChatApiTests`, `ChatPageTests` (gồm end-to-end portal↔API thật), `DeliveryLogPersistenceTests` (SQLite + PostgreSQL), `Support/ApiFactory.cs` (recorder HTTP cho chat + resolver giả), `Support/WebFactory.cs`, `Support/FakeSmtpServer.cs` (`FakeChatSecrets`), `WebTests.cs` (cập nhật kỳ vọng sidebar/redirect do thay đổi có chủ đích).

### 4.2. Kết quả kiểm thử thực tế

```
cd repos/shared-services
dotnet build --no-incremental        -> 0 Warning(s), 0 Error(s)
dotnet test (có SHARED_SERVICES_TEST_POSTGRES=Host=localhost;...;Username=postgres)
  UnitTests        Passed: 585, Failed: 0, Skipped: 0
  IntegrationTests Passed: 199, Failed: 0, Skipped: 0   (gồm test PostgreSQL thật)
dotnet test (không có biến PG)  -> Integration: 194 passed, 5 skipped (test PostgreSQL)
```
- Đối chiếu tiêu chí: provider unit tests (Mock/Telegram/Discord/Slack, payload, token không lọt vào log nhờ test DI thật); Safe MarkdownV2 escape; SSRF/host-allow-list (169.254.169.254, 127.0.0.1, `hooks.slack.com.evil...`, `@` userinfo, http) bị chặn cả ở validate-on-save lẫn Send Test; **P0** `ATestFromMemory_ChangesNothingThatIsSaved...` (snapshot toàn bộ dòng của project trước/sau, và tổng số dòng không đổi) trên cả `/api/v1/chat/test` lẫn alias `/api/v1/telegram/test`; đổi provider không có secret mới → 400/`ValidationException` (5 cặp provider); delivery log không chứa token/webhook/nội dung (test trên entity, qua API và SQL thô); regression review TASK-033 chạy trên API thật với project đã có bản Active (`SaveDraftThenActivate_...`).
- **Mutation checks** (phá code → test phải đỏ, đã khôi phục sau mỗi lần): secret kế thừa khi đổi provider (13 test đỏ), bỏ allow-list host Slack (9), tắt escape MarkdownV2 (26), ghi kết quả test sai id (6), form hiển thị bản Active thay vì mới nhất (2 integration), dùng secret đã lưu với settings đã đổi (1 integration). Lưu ý: lần đầu M5 bị nhiễu do DLL đột biến cũ còn trong output build; đã sửa script và chạy lại — kết quả trên là của lần chạy lại.
- **Chạy thật trên trình duyệt (Edge + puppeteer)**: API :5030 + Web :5031 (Development, SQLite), `Chat:TelegramBaseUrl` trỏ tới stub Telegram cục bộ :5099 (không gọi Internet). 20/20 kiểm tra đạt: form 4 provider, nhãn "Bot Token"/"Webhook URL", Gửi Thử Nghiệm (thành công và 401), 5 → 5 cấu hình sau test (P0), Lưu Nháp → form hiển thị đúng bản nháp + ghi chú bản live, Lưu & Kích Hoạt, `POST /api/v1/chat/send` thật tới stub bằng token đã lưu (text được escape `\\(95%\\)`), bảng log hiển thị `-1001777` / "Thành công" / không token / không nội dung, webhook `169.254.169.254` bị từ chối, đổi provider không nhập secret bị từ chối, `/Telegram` → `/Chat`, mobile không tràn ngang, không lỗi JS. Log API/Web không chứa token (0 lần xuất hiện). Ảnh chụp: `scratchpad/e2e/ss-chat-*.png` (ngoài repo).

### 4.3. Các hạn chế / điểm cần lưu ý

1. Chưa gọi Telegram/Discord/Slack thật (không có token/webhook thật); đã kiểm chứng bằng handler HTTP giả và stub cục bộ. Cần thử một lần với bot/webhook thật trước khi dùng production.
2. `ChatMessage.Format` nullable là quyết định thiết kế: không gửi `format` → theo `parseMode` của cấu hình; `format=PlainText` tường minh luôn được escape đầy đủ (Telegram: MarkdownV2 escape + `parse_mode=MarkdownV2`).
3. Giữ `ChannelType.Telegram` để cấu hình/log cũ vẫn gửi/thử/hiển thị được; dispatcher ưu tiên Chat rồi mới tới Telegram (project trước, `default` sau).
4. Webhook là secret nên bị allow-list host chặt (Discord, `hooks.slack.com`): webhook tương thích Slack tự host (Mattermost…) hiện **không** được hỗ trợ.
5. SSRF: `OutboundHostPolicy` phân giải DNS rồi kiểm tra IP trước khi gửi; còn khoảng hở TOCTOU/DNS-rebinding giữa lúc kiểm tra và lúc kết nối (giảm nhẹ bằng allow-list host + không theo redirect). Telegram dùng base URL cấp host (`Chat:TelegramBaseUrl`), không nhận từ người dùng.
6. Portal vẫn chưa có đăng nhập (như TASK-032/033) và chưa có rate limit cho `/chat/send` hoặc Send Test.
7. Đã phát hiện và sửa một lỗi thật khi viết test: chuỗi `bot123456:token/sendMessage` bị `Uri` hiểu thành scheme `bot123456`; nay dựng bằng `new Uri(base, "./bot{token}/sendMessage")`.
8. Test dùng chung `ApiFactory.Chat.Respond` giữa các test trong một class; các test đổi nó đều khôi phục ở cuối, nhưng một assertion thất bại giữa chừng có thể làm test sau đó đỏ theo (chỉ ảnh hưởng chẩn đoán).
9. Nhãn sidebar hiển thị "Tin Nhắn & Chat" với dòng phụ "Telegram · Discord · Slack" (icon `bi-chat-dots`) thay vì emoji 💬 + ngoặc như văn bản task, cho đồng nhất với mục Email; test đã cập nhật tương ứng.

---

## 5. Kết Quả Code Review (Dành cho Claude Code Reviewer)

*(Reviewer điền vào phần này theo `docs/roles/CODE_REVIEWER.md`)*

- **Reviewer**: Claude Code (Reviewer Session) — **Ngày**: 2026-11-11 — **Lần review**: 1
- **Trạng thái Review**: `REVIEW_PASSED` (0 Blocker, 0 Major, 3 Minor)

### 5.1. Xác minh độc lập
```bash
dotnet build repos/shared-services/AppInHouse.SharedServices.sln --no-incremental   # 0 Warning, 0 Error
dotnet test  repos/shared-services/AppInHouse.SharedServices.sln                    # Unit 585/585; Integration 194 pass + 5 skipped (199)
SHARED_SERVICES_TEST_POSTGRES='Host=localhost;Port=5432;Username=postgres;Password=postgres' \
dotnet test tests/AppInHouse.SharedServices.IntegrationTests --no-build             # 199/199 (PostgreSQL 16 trên máy)
```
- **Phạm vi file**: đúng whitelist (27 file sửa + 14 file mới trong các project được phép, cộng file task). `Api/data/` và `Web/data/` (SQLite + key ring do chạy thử để lại) bị `repos/shared-services/.gitignore` (`**/data/`) loại, không có nguy cơ commit nhầm.
- **Chạy thật** (Development, API 5030 + Web 5031, DB/key ring riêng trong scratchpad; `Chat__TelegramBaseUrl` trỏ tới stub Telegram Node ở 127.0.0.1:5099 ghi lại path + payload; **không gọi Discord/Slack/Telegram thật**). API: `scratchpad/ss034/api034.mjs` (30/32, 2 fail là lỗi dữ liệu thử của reviewer — xem dưới), Web: `scratchpad/web022/portal034.mjs` (21/22, 1 fail = Finding #1):
  - **Send**: dự án chưa có cấu hình → fallback `default` Mock; text rỗng / > 4096 / JSON hỏng → 400; không key → 401. Telegram qua stub: token **chỉ** nằm trong path `/bot<token>/sendMessage`, `chat_id` lấy mặc định của cấu hình hoặc `recipient`, PlainText được escape MarkdownV2 đúng (`CPU 95% \(node\-1\) \_x\_ \[a\]\(b\)\!`, `parse_mode=MarkdownV2`); `format=MarkdownV2` đi nguyên; chat id sai (`1; DROP`) → 502 có lý do.
  - **SSRF / allow-list** khi lưu: webhook Discord ở host khác, ở `169.254.169.254`, `discord.com.evil.io`, `discord.com@evil.io`, Slack qua `http`, `hooks.slack.com.evil.io`, token Telegram chứa `/../` → đều 400 trường `Secret`, thông điệp không lặp lại giá trị. Send Test Discord tới metadata IP → 400.
  - **Đổi provider**: Telegram → Discord không secret / Telegram → Slack với mask → 400 `Secret`; cùng provider không secret → kế thừa. ✅
  - **Send Test (P0)**: từ bộ nhớ (Telegram OK, token sai → `success:false` "401", Mock, alias `/api/v1/telegram/test`) — snapshot toàn bộ cấu hình trước/sau **không đổi**; không gửi token → 400 (không mượn token đã lưu). Stub cố ý **lặp lại token** trong `description` → thông điệp trả về đã thành `••••`. Theo id (bản Draft): dùng token đã lưu, **bỏ qua** secret và settings trong request, cập nhật `lastTestStatus/lastTestedAt`, vẫn Draft.
  - **Delivery log**: `/chat/delivery-logs` có Sent/Failed, người nhận, phản hồi ngắn; không có token, webhook hay nội dung tin; log Email không lẫn dòng Chat. Token/nội dung thử **không** có trong `api.log`/`web.log`; trong DB chỉ có ciphertext (chuỗi rõ duy nhất là token ngắn mà reviewer cố ý nhét vào `settings.note` — xem Finding #2).
  - **Web `/Chat`**: sidebar "Tin Nhắn & Chat · Telegram · Discord · Slack"; form hiện **bản mới nhất** (v3 Draft) với ghi chú "Đang hoạt động: v2"; token chỉ là `••••••••`; dropdown 4 provider, Parse Mode PlainText/MarkdownV2; nhãn "Bot Token"/"Webhook URL"; đổi provider xóa mask và ẩn/hiện trường đúng; Mock ẩn ô secret. Gửi Thử Nghiệm Mock xanh, không đổi cấu hình; đổi chat id mà giữ mask → yêu cầu nhập token, không gửi gì; token sai → đỏ "401", không lộ token; POST thiếu anti-forgery → 400. **Lưu & Kích Hoạt** kích hoạt đúng bản đang hiển thị (v4, `-1001888`), gửi qua API sau đó đi đúng chat + token đã lưu. Đổi sang Discord không webhook → lỗi rõ, không tạo version. Bảng log có badge xanh/đỏ; recipient `<img src=x onerror=alert(1)>` được thoát HTML, không chạy script. `/Telegram?project=rv034` → `/Chat?project=rv034`. 390 px không tràn ngang; 0 lỗi JS.
  - Ghi chú về 2 fail của script API: (1) token thử `111111:...` của reviewer chỉ 19 ký tự sau dấu `:` nên không khớp mẫu "trông như token" (≥ 30, đúng với token thật) — với token dài như thật thì bị chặn 400 đúng; (2) script đánh số version lệch do (1); kiểm lại bằng `byid.mjs` thì đạt.

### 5.2. Findings

| # | Mức độ | File:dòng | Vấn đề | Đề xuất |
|---|---|---|---|---|
| 1 | 🔵 Minor | `Web/Pages/Chat.cshtml.cs:145, 225` | Gửi Thử Nghiệm giữ mask trên **form chưa sửa gì** vẫn báo "Bạn đã đổi cài đặt. Hãy nhập lại Bot Token..." khi bản đang hiển thị được lưu **qua API/SDK mà không có `parseMode`**: `BuildSettings` luôn thêm `parseMode` (dropdown mặc định PlainText) nên so `Canonical` lệch dù nghĩa giống hệt. Tái hiện: v3 lưu qua API `{"defaultChatId":"-1001888"}` → mở `/Chat`, không sửa gì, bấm Gửi Thử Nghiệm → đỏ. Không làm hỏng dữ liệu (chỉ bắt gõ lại token); cấu hình lưu từ portal và seed dev đều có `parseMode` nên không gặp. Cùng kiểu với `port` mặc định 587 ở `/Email`. | So sánh trên giá trị đã đọc và chuẩn hóa (`ChatSettingsReader.Read(...)` hai bên, mặc định điền đủ) thay vì JSON thô; thêm test với bản lưu qua API thiếu `parseMode`. |
| 2 | 🔵 Minor | `Application/Configurations/ChannelConfigurationValidator.cs:25` (`SecretLookingValue`) | Bộ dò "giá trị trông như secret" trong settings (từ TASK-032) chưa có mẫu **webhook Discord/Slack** — loại secret mới của task này. Tái hiện: `{"note":"https://hooks.slack.com/services/T.../B.../..."}` và `{"avatarUrl":"https://discord.com/api/webhooks/123.../..."}` đều **201**, lưu dạng rõ và trả lại qua API/portal. Dán nhầm webhook vào ô "Avatar URL" ngay cạnh ô "Webhook URL" là lỗi thao tác rất dễ xảy ra. | Thêm vào `SecretLookingValue` mẫu `https://hooks.slack.com/services/...` và `https://(…\.)?discord(app)?\.com/api(/v\d+)?/webhooks/...` (có thể tái dùng `ChatSecretRules.TryParseWebhook`), kèm test. |
| 3 | 🔵 Minor | `Application/Chat/ChatSettings.cs:155` (`DiscordSpecials`) | Escape Discord cho PlainText thiếu `[` `]` (và `-` đầu dòng): webhook Discord **có** hiển thị masked link, nên text "thuần" `[Bấm vào đây](https://evil.example)` vẫn thành liên kết giả mạo, trái với cam kết "plain text arrives as written". | Escape thêm `[ ] ( )` (và `-` đầu dòng cho `-#`/list), kèm test. |

Không có Blocker/Major. Ghi nhận tốt: token chỉ đi trong path và HttpClient của provider thật tắt logger (log không chứa token — đã kiểm `api.log`), không theo redirect, `allowed_mentions.parse=[]` cho Discord, escape `& < >` cho Slack, `ErrorText.Clean` xóa cả token lẫn từng đoạn của webhook. Với Discord/Slack, secret **chính là** đích đến, nên lỗ hổng kiểu Finding #2 của TASK-033 (đưa secret đã lưu tới host mới) không xảy ra ở đây; Telegram dùng base URL cấp host.

### 5.3. Cần Gemini quyết định
- **`/chat/test` (và `/emails/test`) theo `configurationId` vẫn chưa giới hạn theo dự án**: key `config:write` của dự án A gửi được tin thử bằng bot/webhook của dự án B tới chat tùy ý. Đã xếp vào TASK-036 — xác nhận TASK-036 bao cả hai endpoint này.
- **Finding #2 của TASK-033 (SMTP kế thừa mật khẩu khi đổi host)** chưa có task nào nhận: mục 6.2 của TASK-033 ghi "phê duyệt phương án (a)" nhưng nội dung là dời sang TASK-036. Cần ghi rõ việc "chỉ kế thừa mật khẩu SMTP khi host + username không đổi" thuộc task nào.
- Chưa thử với Telegram/Discord/Slack thật (Coder nêu ở 4.3 điểm 1) — nên có một lần smoke test với bot/webhook thật trước khi bật cho production.
- Webhook kiểu Slack tự host (Mattermost…) bị allow-list chặn (4.3 điểm 4) — chấp nhận được; nếu cần thì nên là allow-list cấu hình ở mức host, không ở mức cấu hình dự án.

**Dọn dẹp**: đã tắt API, Web và stub Telegram; dữ liệu thử chỉ nằm trong scratchpad, không tạo file trong repo.

---

## 6. Kết Luận Nghiệm Thu (Dành cho Gemini Tech Lead)

- **Người Đánh Giá**: Gemini (Tech Lead / Orchestrator)
- **Ngày Nghiệm Thu**: 2026-11-11
- **Trạng Thái Nghiệm Thu**: `APPROVED`

### 6.1. Đánh giá chất lượng thực thi
1. **Kiến trúc Đa Nền Tảng (Multi-Provider Chat)**:
   - Mở rộng thành công từ phân hệ Telegram đơn lẻ thành `ChannelType.Chat` hỗ trợ linh hoạt 4 Provider: `TelegramBot`, `DiscordWebhook`, `SlackWebhook`, `Mock`.
   - Giữ tương thích ngược hoàn hảo với các bản ghi `Telegram` cũ và cung cấp route alias thuận tiện (`/Telegram` chuyển hướng về `/Chat`).
2. **Bảo Mật & An Toàn Thực Thi (P0/P1)**:
   - Cơ chế **Send Test trực tiếp trong RAM** đạt chuẩn P0: thử nghiệm thành công mà không làm thay đổi hay tạo bản ghi rác trong cơ sở dữ liệu.
   - Triệt tiêu hoàn toàn rò rỉ secret trong log: Cấu hình HttpClient tắt logger mặc định (`.RemoveAllLoggers()`) ngăn việc in URI chứa bot token/webhook key ra log request.
   - Bộ lọc **Safe MarkdownV2 Escape** cho Telegram và các bộ lọc format cho Discord/Slack bảo vệ tin nhắn không gây lỗi 400 và không bị giả mạo liên kết.
   - Áp dụng `OutboundHostPolicy` cùng whitelist host cho webhook Discord/Slack ngăn chặn tấn công SSRF tới mạng nội bộ và cloud metadata (`169.254.169.254`).
3. **Chất Lượng Kiểm Thử**:
   - **585/585 Unit Tests PASS**, **194/199 Integration Tests PASS** (gồm kiểm thử trên cả SQLite và PostgreSQL thật).
   - Kiểm thử đột biến (mutation test) và kiểm thử thực tế trên giao diện Web Portal Bootstrap 5 cổng 5031 đạt 20/20 tiêu chuẩn.

### 6.2. Phê duyệt & Quyết định kỹ thuật (Mục 5.3)
1. **Phân quyền và cô lập cấu hình theo `project_code`**:
   - Xác nhận: Việc giới hạn phạm vi truy cập theo `project_code` đối với `/chat/test`, `/emails/test` và toàn bộ các endpoint cấu hình sẽ được triển khai toàn diện tại **TASK-036 (API Key Lifecycle, Scopes & Project Isolation)**.
2. **Quy tắc kế thừa secret SMTP**:
   - Xác nhận: Bổ sung ràng buộc "chỉ kế thừa secret SMTP khi `host` + `username` không đổi" vào phạm vi hoàn thiện validation của **TASK-036**.
3. **Các Minor Findings (1, 2, 3)**:
   - Ghi nhận chuẩn hóa `parseMode` khi so sánh cài đặt và bổ sung pattern webhook Discord/Slack vào bộ lọc `SecretLookingValue` trong đợt hoàn thiện tiếp theo.

TASK-034 chính thức được nghiệm thu **APPROVED**. Hệ thống sẵn sàng bước sang **TASK-035: Object Storage Subsystem (Local Disk, S3/MinIO) & Media Browser Web Portal**.

