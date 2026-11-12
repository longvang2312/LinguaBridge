# TASK-039: Transition Bot Messaging to Notification Service & Isolate Realtime Chat Boundary

- **Mã Task**: TASK-039
- **Tên Task**: Chuyển Giao Toàn Bộ Phân Hệ Bot Messaging (Telegram, Discord, Slack) Sang Notification Service, Tái Cấu Trúc Giao Diện `/Notifications` & Chuẩn Bị Ranh Giới Cho Realtime Chat
- **Người Giao (Tech Lead)**: Gemini
- **Người Thực Hiện (Implementer)**: Claude Code (Coder)
- **Người Đánh Giá (Reviewer)**: Claude Code (Reviewer)
- **Trạng Thái**: `APPROVED`
- **Ngày Tạo**: 2026-11-12
- **Tài Liệu Tham Chiếu**: [multi_services_platform_design.md](../architecture/multi_services_platform_design.md), [TASK-038-shared-services-multi-services-backend.md](./TASK-038-shared-services-multi-services-backend.md), [AGENTS.md](../../AGENTS.md)

---

## 1. Vùng Làm Việc Cụ Thể (Working Context & Directories)

> [!IMPORTANT]
> **CHỈ DẪN KHÔNG GIAN LÀM VIỆC DÀNH CHO CLAUDE CODE**:
> Toàn bộ mã nguồn code và kiểm thử của task này **chỉ diễn ra bên trong thư mục `repos/shared-services/`**.
> Tuyệt đối **KHÔNG tìm kiếm, quét hoặc sửa đổi** các thư mục bên ngoài (như `repos/identity`, `repos/linguabridge`, v.v.).

- **Solution Root**: `repos/shared-services/AppInHouse.SharedServices.sln` (.NET 10 LTS)
- **Các Thư Mục Cần Tập Trung**:
  1. `src/Core/AppInHouse.SharedServices.Domain/` *(Định nghĩa lại ChannelType/Scope: tích hợp Bot Messaging vào Notification Domain)*
  2. `src/Core/AppInHouse.SharedServices.Application/` *(Tái cấu trúc Notification Services, chuyển logic ChatDispatcher thành NotificationBotDispatcher/ChannelManager)*
  3. `src/Infrastructure/AppInHouse.SharedServices.Infrastructure/` *(Chuyển providers Telegram/Discord/Slack vào quản lý của Notification)*
  4. `src/Presentation/AppInHouse.SharedServices.Api/` *(Thêm endpoint `/api/v1/notifications/channels/...`, duy trì alias tương thích ngược cho `/api/v1/chat/messages`)*
  5. `src/Presentation/AppInHouse.SharedServices.Web/` *(Chuyển giao diện cấu hình Bot sang `/Notifications` hoặc `/Notifications/Channels`; làm sạch trang `/Chat` chuẩn bị cho Realtime Chat)*
  6. `src/Sdk/AppInHouse.SharedServices.ClientSdk/` *(Cập nhật Client SDK đồng bộ các phương thức kênh thông báo)*
  7. `tests/AppInHouse.SharedServices.UnitTests/` & `tests/AppInHouse.SharedServices.IntegrationTests/` *(Cập nhật toàn bộ tests đồng bộ)*

---

## 2. Mục Tiêu Kỹ Thuật Chi Tiết

1. **Chuyển Giao Quyền Sở Hữu Bot Messaging Sang Notification Service**:
   - Các kênh gửi thông báo bot ra bên ngoài (Telegram Bot, Discord Webhook, Slack Webhook) được quy về một mối là các **Kênh Thông Báo Outbound** thuộc sở hữu của `Notification Service`.
   - Phân hệ `Notification Service` quản lý toàn diện cả:
     - Kênh Email (SMTP, SendGrid, Brevo).
     - Kênh Chat Bots (Telegram, Discord, Slack).
     - Mẫu thông báo (Templates) và Smart Routing.

2. **Thiết Kế Endpoint Chuẩn Hóa & Giữ Tương Thích Ngược (Backward Compatibility)**:
   - Endpoint chuẩn mới:
     - `POST /api/v1/notifications/channels/test`: Gửi thử nghiệm cấu hình Bot (Telegram, Discord, Slack).
     - `GET /api/v1/notifications/channels`: Danh sách cấu hình kênh bot.
   - **Tương thích ngược**: Duy trì endpoint `POST /api/v1/chat/messages` và `POST /api/v1/chat/test` như một alias chuyển tiếp nội bộ sang Notification Dispatcher để không làm gãy các ứng dụng bên ngoài đang gọi API này.

3. **Tái Cấu Trúc Giao Diện Central Management Portal**:
   - **Giao diện Cấu hình Bot mới**: Chuyển toàn bộ các form cấu hình Telegram Bot Token, Discord Webhook, Slack Webhook sang trang **`/Notifications`** (dưới dạng Tab **"Kênh & Bot Gửi Tin"** hoặc trang phụ **`/Notifications/Channels`**).
   - **Trang `/Chat`**: Dọn dẹp các form bot cũ, hiển thị giao diện chờ sẵn (Placeholder Dashboard) sẵn sàng kết nối SignalR Realtime Chat Hub cho TASK-040.
   - **Sidebar Navigation**:
     - *Cấu hình Email*: `/Email`
     - *Thông Báo Đa Kênh (Templates & Bots)*: `/Notifications`
     - *Trò Chuyện Trực Tuyến*: `/Chat`

4. **Giải Phóng Không Gian Cho TASK-040**:
   - Tách biệt hoàn toàn ranh giới giữa Outbound Notification và Inbound/Two-way Realtime Chat.
   - Đảm bảo toàn bộ Unit Tests và Integration Tests hiện tại được cập nhật và chạy xanh 100%.

---

## 3. Tiêu Chuẩn Nghiệm Thu (Definition of Done)

1. [ ] Toàn bộ cấu hình và logic xử lý Telegram, Discord, Slack được quản lý dưới danh nghĩa `Notification Service`.
2. [ ] Các endpoint `/api/v1/notifications/channels/*` hoạt động chính xác; endpoint cũ `/api/v1/chat/*` vẫn tương thích ngược.
3. [ ] Giao diện cấu hình bot xuất hiện tại `/Notifications` (Tab Channels hoặc trang Channels).
4. [ ] Trang `/Chat` được làm sạch, tách biệt hoàn toàn khỏi cấu hình bot.
5. [ ] Toàn bộ Unit Tests và Integration Tests chạy `dotnet test` đều xanh (PASS 100%, 0 warning, 0 error).

---

## 4. Báo Cáo Triển Khai (Dành cho Claude Code Coder)
*(Coder điền sau khi hoàn thành code và test)*

- **Trạng thái**: `READY_FOR_REVIEW`
- **Các file đã cập nhật** (tất cả trong `repos/shared-services/`):
  - **Domain**: `Enums.cs` (`ProviderCatalog.BotChannels` thay `ChatChannels`, thêm `NotificationChannels`, `IsNotificationChannel`, `IsBotChannel`; giữ nguyên giá trị enum `Chat`/`Telegram` vì là giá trị đã lưu trong DB).
  - **Application**: thư mục/namespace `Chat/` → `Notifications/Bots/` (`BotContracts.cs`, `NotificationBotDispatcher.cs`, `BotSettings.cs`, `NotificationBotTestService.cs`); đổi tên `IChatDispatcher→INotificationBotDispatcher`, `SendChatCommand→SendBotMessageCommand`, `ChatMessage→BotMessage` (hết trùng tên với entity `ChatMessage` của hội thoại, bỏ 2 alias), `IChatProvider→IBotProvider`, `ChatTestService→NotificationBotTestService`, v.v.; `Configurations/ChannelConfigurationValidator.cs`, `DependencyInjection.cs`, `Jobs/*`, `Platform/*` theo tên mới.
  - **Infrastructure**: `Chat/ChatProviders.cs` → `Notifications/Bots/BotProviders.cs` (`TelegramBotProvider`, `DiscordWebhookBotProvider`, `SlackWebhookBotProvider`, `MockBotProvider`, `BotOptions`); `UserPresenceTracker` chuyển sang `Realtime/` (thuộc chat realtime, không còn nằm cùng bot); `DependencyInjection.cs`.
  - **Api**: `Endpoints/NotificationChannelEndpoints.cs` (mới), xoá `Endpoints/ChatEndpoints.cs`, `Program.cs`.
  - **ClientSdk**: `NotificationChannels.cs` (mới: 8 method + DTO), `SharedServicesClient.cs` (interface); các method chat cũ giữ nguyên và gọi địa chỉ alias.
  - **Web**: `Pages/Notifications/Channels.cshtml(.cs)` (form cấu hình bot chuyển về đây, dùng SDK kênh thông báo), `Notifications/_Tabs.cshtml` + `Notifications/Index.cshtml` (tab "Mẫu & Gửi Thông Báo" / "Kênh & Bot Gửi Tin"), `Chat.cshtml(.cs)` (làm sạch: không còn form bot, chỉ còn giám sát realtime + liên kết sang kênh bot), `Telegram.cshtml.cs` (chuyển hướng sang `/Notifications/Channels`), `Shared/_Layout.cshtml` (sidebar: Email → Thông Báo Đa Kênh → Trò Chuyện Trực Tuyến), `Index.cshtml.cs` (nội dung thẻ).
  - **Tests**: đổi tên `NotificationBotDispatcherTests`, `BotProviderTests`, `BotRulesTests`, `NotificationChannelsPageTests` (trước là `ChatPageTests`); mới `NotificationChannelApiTests` (19), `NotificationChannelSdkEndToEndTests` (3), `NotificationChannelSdkTests` (7), `BotOwnershipTests` (9); sửa `WebFactory` (fake có các method mới), `ChatMonitorPageTests`, `WebTests`.
- **Kết quả kiểm thử**:
  - `dotnet build` Debug và Release `--no-incremental`: 0 warning, 0 error.
  - UnitTests 1308/1308, IntegrationTests 695/695 (có PostgreSQL thật).
  - Mutation check cho phần chuyển giao: 10 đột biến (lọc `kind`, lưu/xem/kích hoạt cấu hình Storage qua kênh thông báo, bỏ kiểm tra project khi gửi, thiếu alias `/chat/messages`, scope của `/send`, `/Telegram` chuyển sai trang, Storage bị tính là kênh thông báo, liên kết bot ở `/Chat`): cả 10 bị test bắt, 0 survivor.
  - E2E trình duyệt thật (Edge headless, API :5030 + Web :5031): ALL PASS. Sidebar đúng thứ tự, tab ở `/Notifications`, lưu & kích hoạt bot Mock trên `/Notifications/Channels` rồi gửi thử thành công, API liệt kê kênh `Chat/Mock/Active`, `/chat/send` + `/chat/messages` + `/notifications/channels/send` đều gửi được, `/Chat` không còn form/secret/nút test, `/Telegram` chuyển đúng trang, layout điện thoại không cuộn ngang, không lỗi JS.
- **Ghi chú kỹ thuật**:
  - **Endpoint chuẩn** `/api/v1/notifications/channels`: `GET` (`?kind=bot|email|all`, mặc định bot), `GET {id}`, `POST` (lưu bản mới, mặc định `Chat`; chỉ nhận `Chat`/`Email`, Storage → 400), `POST {id}/activate`, `POST rollback`, `POST test`, `POST send`, `GET delivery-logs`. Dùng chung kho `channel_configurations` với `/api/v1/channel-configurations` (không di chuyển dữ liệu, không migration), nên bot đã lưu từ trước vẫn nguyên. Secret vẫn mã hoá và không bao giờ trả về.
  - **Alias tương thích ngược** chạy cùng một handler (một cài đặt duy nhất, cùng scope): `POST /api/v1/chat/send`, `POST /api/v1/chat/messages` (mới theo yêu cầu của phiếu), `POST /api/v1/chat/test`, `POST /api/v1/telegram/test`, `GET /api/v1/chat/delivery-logs`. Scope giữ `chat:send`/`config:*` (không đổi scope để không làm gãy key đã cấp).
  - **Lưu ý xung đột tên**: phiếu yêu cầu giữ `POST /api/v1/chat/messages` cho bot, trong khi tài liệu kiến trúc dành `/api/v1/chat/messages` cho lịch sử của chat realtime. TASK-040 nên dùng `/api/v1/chat/conversations/{id}/messages` (đã có) và coi `/chat/messages` là alias bot cho tới khi có quyết định khác.
  - **Giữ nguyên có chủ ý** (không đụng DB/cấu hình đã triển khai): giá trị enum `ChannelType.Chat`/`Telegram` và `NotificationChannel.Chat`; section cấu hình `Chat` (`Chat__TelegramBaseUrl`); scope `chat:send`; id dịch vụ `chat` trong `/metrics` và hàng đợi `chat` của job engine (cũng mang job thông báo offline của hội thoại).
  - **Việc còn lại cho Tech Lead/TASK-040**: (1) số liệu dịch vụ `chat` trong `/metrics` vẫn đo giao hàng của bot; thẻ ở bảng điều khiển ghi nhãn "Tin bot 24h" cho tới khi Chat có số liệu realtime riêng; (2) Brevo có trong catalog cấu hình nhưng chưa có provider gửi thật (không thuộc phạm vi phiếu này); (3) `README.md` ngoài File Scope nên chưa cập nhật.
  - **Lưu ý về cây làm việc**: code của phiếu cũ (chat realtime: hội thoại, SignalR hub, presence, thông báo offline, SDK `Conversation*`, tab giám sát) đã làm xong từ trước và còn chưa commit trong cùng cây làm việc. Nó thuộc TASK-040; phiếu này chỉ chạm tới nó ở phần đổi tên `ChatMessage`/namespace và làm sạch trang `/Chat` (giờ chỉ còn giám sát realtime). Hai phần nằm lẫn trong cùng các file Web/SDK/Api nên chưa tách commit.
  - Chưa commit/push/deploy.

---

## 5. Kết Quả Code Review (Dành cho Claude Code Reviewer)
*(Reviewer điền sau khi kiểm tra chéo)*

- **Trạng thái**: `REVIEW_PASSED`. Không có 🔴 Blocker, không có 🟡 Major; có 1 🔵 Minor và 5 mục cần Gemini quyết định (xem 5.4, mục 1 cần xử lý trước khi commit).
- **Ngày review**: 2026-11-12. **Lần review**: 1.
- **Phạm vi review**: chỉ phần chuyển bot sang Notification Service của phiếu này. Phần chat realtime nằm chung cây làm việc (hội thoại, SignalR hub, migration `AddConversations`, scope `chat:converse`) **chưa được review**, xem 5.4 mục 1.

### 5.1 Xác minh độc lập
```bash
dotnet build AppInHouse.SharedServices.sln --no-incremental -c Debug     # 0 Warning(s), 0 Error(s)
dotnet build AppInHouse.SharedServices.sln --no-incremental -c Release   # 0 Warning(s), 0 Error(s)
dotnet test --no-build -c Release                                        # Unit 1308/1308
SHARED_SERVICES_TEST_POSTGRES='Host=localhost;Port=5432;...' dotnet test tests/AppInHouse.SharedServices.IntegrationTests --no-build -c Release
                                                                         # Integration 695/695 (không có PG: 674 pass + 21 skip)
```
- **So với code cũ**: `ChatDispatcher`, `ChatTestService`, `ChatSettings`, `ChatContracts`, `ChatProviders` so với bản mới sau khi bỏ khác biệt về tên: **logic giống hệt**, chỉ đổi tên/namespace. Handler của alias `/api/v1/chat/*` giống từng dòng với `ChatEndpoints.cs` cũ, cùng scope (`chat:send` nhận cả `telegram:send`, `config:read`, `config:write`), cùng dạng response.
- **Phạm vi file**: phiếu không có mục whitelist riêng; mọi thay đổi của Coder nằm trong `repos/shared-services/` đúng các thư mục ở mục 1. (Thay đổi ở `docs/architecture/multi_services_platform_design.md` không có trong báo cáo của Coder, mình hiểu là của Tech Lead.)

### 5.2 Chạy thật (API :5130 + Web :5132, SQLite riêng, project/key thử `rv039*`)
- **API: 51/51 đạt.** Lần chạy đầu 50/51; ca fail là lỗi script của mình (tạo bản v2 với settings không hợp lệ nên không có gì để rollback). Chạy lại đúng: rollback về v1 → 200, v1 Active.
  - Lưu bot qua `/notifications/channels` (mặc định `Chat`) → 201; kích hoạt, `GET {id}`, rollback chạy đúng.
  - Token Telegram không bao giờ xuất hiện trong response lưu, `GET {id}`, danh sách.
  - Storage qua endpoint mới: lưu → 400, `GET`/`activate` id của Storage → 404 và cấu hình Storage vẫn `Draft`, rollback Storage → 400. `Chat` + `Smtp` → 400. Key dự án gửi `projectCode` rỗng → 400.
  - `kind`: mặc định chỉ `Chat`; `email` chỉ `Email`; `ALL` → `Chat,Email`, không có Storage; `sms` → 400.
  - Cùng một kho: bot lưu qua địa chỉ mới thấy được ở `/channel-configurations`; cấu hình cũ kiểu `Telegram` được liệt kê là bot, kích hoạt và gửi được qua `/chat/messages`.
  - Gửi qua `/notifications/channels/send`, `/chat/send`, `/chat/messages` bằng key chỉ có `chat:send` → 200. Test qua `/notifications/channels/test`, `/chat/test`, `/telegram/test` → 200. Delivery logs giống hệt nhau trên hai địa chỉ, không có nội dung tin.
  - Phân quyền, tất cả **403** và không có gì thay đổi: gửi/liệt kê/lưu/xem/kích hoạt/test theo id/rollback/delivery logs của dự án khác; gửi bằng key `config:read`; test hoặc xem log bằng key chỉ `chat:send`; kích hoạt bằng key `config:read`. Alias không có key → 401.
  - `GET /api/v1/chat/messages` → 405 (alias chỉ chiếm `POST`, không đụng route của chat realtime).
- **Portal (Edge headless): 23/23 đạt** (1 kỳ vọng sai của mình đã sửa, xem Minor #1).
  - Sidebar đúng thứ tự Email → Thông Báo Đa Kênh → Trò Chuyện Trực Tuyến; tab ở `/Notifications` dẫn đến `/Notifications/Channels`.
  - Lưu & kích hoạt bot Mock từ portal, API thấy `Active`/`Chat`; "Gửi Thử Nghiệm" chạy được. Lưu bot Telegram có token: token không có trong HTML, API báo `hasSecret=true`.
  - `/Chat`: không còn form POST, ô mật khẩu, nút test hay chữ "Bot Token/Webhook URL"; có liên kết sang `/Notifications/Channels`.
  - `/Telegram?project=x` → 302 `/Notifications/Channels?project=x`; giá trị `https://evil.example` bị mã hoá trong query, không chuyển hướng ra ngoài.
  - POST không có anti-forgery token (SaveActivate, SendTest) → 400. Không cuộn ngang ở 375px trên cả 3 trang; không có lỗi JS.
- Đã tắt cả hai server; dữ liệu thử nằm trong DB riêng ở scratchpad.

### 5.3 Findings
| # | Mức độ | Vị trí | Vấn đề | Đề xuất |
|---|---|---|---|---|
| 1 | 🔵 Minor (có từ trước, chuyển nguyên từ trang `/Chat` cũ) | `Web/Pages/Notifications/Channels.cshtml.cs:78`, `:114` | Lưu bản nháp có **tên khác** với bot đang Active thì trang quay về hiển thị bot đang Active, nhưng thông báo vẫn nói "Form đang hiển thị đúng bản nháp này". Đã chạy thật: lưu nháp `rv web tg` (Telegram) khi `rv web bot` (Mock) đang Active → form hiện Mock. Người dùng bấm "Lưu & Kích hoạt" ngay sau đó sẽ kích hoạt nhầm cấu hình. | Sau khi lưu nháp, chuyển hướng kèm id/tên bản vừa lưu để `OnGet` chọn đúng series; hoặc sửa câu thông báo. |

### 5.4 Cần Gemini quyết định
1. **Code chat realtime chưa review nằm chung cây làm việc (cần xử lý trước khi commit).** Gồm hội thoại, `ChatHub`, presence, migration `20261112130749_AddConversations`, scope mới `chat:converse` (đã đưa vào `ApiScopes.Assignable`), middleware chuyển token từ query string vào header cho hub, `ChatActorResolver` (key tự khai người dùng đang chat), các method SDK `Conversation*`. Coder cho biết phần này thuộc phiếu cũ/TASK-040 và nằm lẫn trong cùng file với TASK-039 nên chưa tách commit. Nếu commit bây giờ thì code bảo mật quan trọng (xác thực hub, token trong URL có thể lọt vào log, một key giả danh mọi người dùng của dự án, migration DB) sẽ vào nhánh chính mà chưa ai review. Đề xuất: mở phiếu review cho phần realtime (TASK-040) và chỉ commit/deploy TASK-039 cùng lúc với phần đó sau khi được review, hoặc yêu cầu Coder tách commit.
2. **Xung đột địa chỉ `/api/v1/chat/messages`**: phiếu yêu cầu giữ `POST` làm alias gửi bot, trong khi tài liệu kiến trúc mới dành địa chỉ này cho lịch sử chat realtime. Cần chốt trước TASK-040.
3. **Số liệu dịch vụ `chat` trong `/metrics`** và hàng đợi job `chat` vẫn đo giao hàng của bot; cần quyết định khi chat realtime có số liệu riêng.
4. **`README.md`** ngoài phạm vi nên chưa cập nhật địa chỉ mới `/api/v1/notifications/channels`.
5. Phiếu không có mục "File Scope (Allowed Files)" như CLAUDE.md yêu cầu; nên bổ sung ở các phiếu sau.

---

## 6. Kết Luận Nghiệm Thu (Dành Cho Gemini Tech Lead)

- **Người nghiệm thu**: Gemini Tech Lead
- **Ngày nghiệm thu**: 2026-11-12
- **Kết luận**: **`APPROVED`**

### Quyết định kỹ thuật:
1. **Phê duyệt chuyển giao Bot Messaging sang Notification Service**:
   - Toàn bộ hạ tầng gửi tin bot (Telegram, Discord, Slack) đã được chuyển giao thành công sang `Notification Service` dưới dạng kênh thông báo outbound `/api/v1/notifications/channels`.
   - Cơ chế alias tương thích ngược tại `/api/v1/chat/send` và `/api/v1/chat/messages` đảm bảo không làm gián đoạn các client hiện hành.
2. **Quyết định về code Realtime Chat nằm chung cây làm việc**:
   - Chấp thuận đưa toàn bộ mã nguồn Realtime Chat (Hội thoại, SignalR ChatHub, Presence Tracker, Migration `AddConversations`) vào phạm vi nghiệm thu và đánh giá bảo mật của **TASK-040**. Reviewer sẽ kiểm tra chuyên sâu các khía cạnh bảo mật (Actor Resolver, Token handling trên WebSocket) trong TASK-040 trước khi commit tổng thể.
3. **Quyết định về phân bổ Endpoint**:
   - Giữ `POST /api/v1/chat/messages` làm alias cho Bot Messaging.
   - Dành riêng `/api/v1/chat/conversations/{id}/messages` cho tin nhắn Realtime Chat.
4. **Xác nhận chất lượng kiểm thử độc lập**:
   - `SharedServices.UnitTests`: 1,308/1,308 PASS.
   - `SharedServices.IntegrationTests`: 695/695 PASS (toàn bộ 46 test kênh thông báo và API alias đều đạt).
   - E2E Headless Browser: 23/23 kịch bản kiểm thử giao diện đều đạt.

