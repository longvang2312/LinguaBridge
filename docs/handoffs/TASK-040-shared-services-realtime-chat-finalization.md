# TASK-040: Realtime Chat Security Hardening, Endpoint Standardization & Full Platform Finalization

- **Mã Task**: TASK-040
- **Tên Task**: Chuẩn Hóa Endpoint Đồng Bộ (Xóa Bỏ Alias Bot Cũ, Đồng Bộ Sang Notification Service), Siết Chặt Bảo Mật Realtime SignalR Chat Platform & Nghiệm Thu Toàn Diện
- **Người Giao (Tech Lead)**: Gemini
- **Người Thực Hiện (Implementer)**: Claude Code (Coder)
- **Người Đánh Giá (Reviewer)**: Claude Code (Reviewer)
- **Trạng Thái**: `APPROVED`
- **Ngày Tạo**: 2026-11-12
- **Tài Liệu Tham Chiếu**: [multi_services_platform_design.md](../architecture/multi_services_platform_design.md), [TASK-039-shared-services-realtime-chat-signalr.md](./TASK-039-shared-services-realtime-chat-signalr.md), [AGENTS.md](../../AGENTS.md)

---

## 1. Vùng Làm Việc Cụ Thể (Working Context & Directories)

> [!IMPORTANT]
> **CHỈ DẪN KHÔNG GIAN LÀM VIỆC DÀNH CHO CLAUDE CODE**:
> Toàn bộ mã nguồn code và kiểm thử của task này **chỉ diễn ra bên trong thư mục `repos/shared-services/`**.
> Tuyệt đối **KHÔNG tìm kiếm, quét hoặc sửa đổi** các thư mục bên ngoài (như `repos/identity`, `repos/linguabridge`, v.v.).

- **Solution Root**: `repos/shared-services/AppInHouse.SharedServices.sln` (.NET 10 LTS)
- **Các Thư Mục Cần Tập Trung**:
  1. `src/Core/AppInHouse.SharedServices.Domain/` *(Entities: Conversation, ConversationMember, ChatMessage; Scopes: chat:converse)*
  2. `src/Core/AppInHouse.SharedServices.Application/` *(ConversationManager, MessageStore, RealtimePresences, OfflineNotificationHandler)*
  3. `src/Infrastructure/AppInHouse.SharedServices.Persistence/` *(EF Core Configurations, Repositories, Migrations: AddConversations)*
  4. `src/Infrastructure/AppInHouse.SharedServices.Infrastructure/` *(SignalR ChatHub, WebSocket Token Middleware, Offline Dispatcher)*
  5. `src/Presentation/AppInHouse.SharedServices.Api/` *(Loại bỏ alias cũ /chat/messages; hoàn thiện endpoints /chat/conversations; SignalR /hubs/chat)*
  6. `src/Sdk/AppInHouse.SharedServices.ClientSdk/` *(Chuẩn hóa method: tách bạch SendNotificationChannelMessageAsync và Conversation APIs)*
  7. `src/Presentation/AppInHouse.SharedServices.Web/` *(Hoàn thiện giao diện giám sát Realtime Chat trên /Chat; cập nhật README.md)*
  8. `tests/AppInHouse.SharedServices.UnitTests/` & `tests/AppInHouse.SharedServices.IntegrationTests/` *(Cập nhật tests cho endpoint mới và rà soát an ninh)*

---

## 2. Mục Tiêu Kỹ Thuật

1. **Chuẩn Hóa Dứt Điểm Endpoint (Tránh Nhầm Lẫn 100%)**:
   - **Xóa bỏ hoàn toàn các alias cũ**: `POST /api/v1/chat/messages`, `POST /api/v1/chat/send`, `POST /api/v1/chat/test`, `POST /api/v1/telegram/test`.
   - **Đồng bộ về Notification Service**:
     - Gửi tin nhắn qua Bot: Chỉ dùng duy nhất **`POST /api/v1/notifications/channels/send`**.
     - Thử nghiệm kênh Bot: Chỉ dùng duy nhất **`POST /api/v1/notifications/channels/test`**.
   - **Giải phóng tiền tố `/api/v1/chat/...` độc quyền cho Realtime Chat**:
     - `POST /api/v1/chat/conversations` (Tạo hội thoại 1-1 / Nhóm).
     - `GET /api/v1/chat/conversations` (Danh sách hội thoại của user).
     - `GET /api/v1/chat/conversations/{id}/messages` (Lịch sử tin nhắn).
     - `POST /api/v1/chat/conversations/{id}/messages` (Gửi tin nhắn vào cuộc trò chuyện).
     - `POST /api/v1/chat/conversations/{id}/read` (Đánh dấu đã đọc).
     - `POST /api/v1/chat/conversations/{id}/members` (Thêm thành viên).
     - `DELETE /api/v1/chat/conversations/{id}/members/{userId}` (Xóa thành viên / Rời nhóm).
     - WebSocket Hub: **`/hubs/chat`**.

2. **Rà Soát & Siết Chặt Bảo Mật Realtime SignalR Chat (P0 Security Review)**:
   - **Bảo mật Token trên WebSocket**: Middleware trích xuất token từ query string `?access_token=...` chuyển vào Authorization header phải đảm bảo URL không bị lộ token ra access log máy chủ.
   - **Chống mạo danh (Actor Resolution)**: Khi gửi tin nhắn qua REST hoặc Hub, `SenderId` phải được lấy chính xác từ Claims Principal đã xác thực của người dùng (không cho phép client tự khai khống `senderId` của người khác).
   - **Phân quyền hội thoại (Authorization)**: Chỉ thành viên trong hội thoại mới được xem lịch sử và gửi tin (`403 Forbidden` đối với người ngoài).
   - **Smart Offline Notification**: Khi gửi tin trong cuộc trò chuyện 1-1 (hoặc mention) mà đối phương đang Offline, tự động đẩy job gọi `INotificationDispatcher` gửi email/bot thông báo.

3. **Cập Nhật Client SDK & Tài Liệu**:
   - Cập nhật `ISharedServicesClient` chuẩn hóa:
     - `SendNotificationChannelMessageAsync` cho bot thông báo.
     - Các phương thức `CreateConversationAsync`, `GetConversationsAsync`, `GetConversationMessagesAsync`, `SendConversationMessageAsync` cho Realtime Chat.
   - Cập nhật `README.md` của Shared Services phản ánh chính xác cấu trúc 8 services và danh mục endpoint mới.

---

## 3. Tiêu Chuẩn Nghiệm Thu (Definition of Done)

1. [ ] Không còn bất kỳ endpoint alias nào dạng `/api/v1/chat/messages` hoặc `/api/v1/chat/send` (chỉ còn `/api/v1/notifications/channels/send` cho bot).
2. [ ] Các API Realtime Chat `/api/v1/chat/conversations/*` và SignalR Hub `/hubs/chat` hoạt động chuẩn xác, bảo mật chống mạo danh.
3. [ ] Phân quyền hội thoại chặt chẽ: Không phải thành viên thì không đọc/gửi được tin nhắn (`403 Forbidden`).
4. [ ] Cơ chế Offline Notification hoạt động ổn định khi người nhận không kết nối SignalR Hub.
5. [ ] Giao diện Web Portal `/Chat` hiển thị bảng điều khiển giám sát Realtime Chat mượt mà.
6. [ ] Cập nhật đầy đủ tài liệu `README.md`.
7. [ ] Toàn bộ Unit Tests và Integration Tests chạy `dotnet test` đều xanh (PASS 100%, 0 warning, 0 error).

---

## 4. Báo Cáo Triển Khai (Dành cho Claude Code Coder)
*(Coder điền sau khi hoàn thành code và test)*

- **Trạng thái**: `READY_FOR_REVIEW`
- **Các file đã cập nhật** (trong `repos/shared-services/`):
  - API: `Endpoints/NotificationChannelEndpoints.cs` (xóa `MapChatAliases`: `/chat/send`, `/chat/messages`, `/chat/test`, `/telegram/test`, `/chat/delivery-logs`; đổi tên handler thành `SendNotificationChannelMessageAsync`), `Chat/ChatHub.cs` (`HubAuthentication.MoveTokenFromQuery` nay xóa `access_token` khỏi query sau khi chuyển vào header).
  - ClientSdk: `SharedServicesClient.cs`, `Contracts.cs` (xóa `SendChatMessageAsync`, `SendTestChatMessageAsync`, `GetChatDeliveryLogsAsync` và 4 DTO cũ), `NotificationChannels.cs` (`SendBotMessageAsync` → `SendNotificationChannelMessageAsync`).
  - Tests: `NotificationChannelApiTests` (thêm `TheOldChatAliases_AreGone`, 5 case), `ConversationApiTests` (thêm test chống khai khống `senderId`, siết test token), `ChatApiTests`, `ProjectApiTests`, `ProjectIsolationTests`, `WebhookApiTests`, `NotificationChannelsPageTests`, `NotificationChannelSdkEndToEndTests`, `Support/WebFactory.cs`, `NotificationBotDispatcherTests`, `NotificationChannelSdkTests`, `ResolvedFindingsTests` (đổi sang địa chỉ/method mới, bỏ test của alias).
  - `README.md`: cấu trúc 8 services, bảng endpoint Bot (Notification Service), Realtime Chat, scope `chat:converse`, `Chat:OfflineGraceSeconds`, ghi chú log proxy.
- **Kết quả kiểm thử**:
  - `dotnet build` Debug và Release `--no-incremental`: 0 warning, 0 error.
  - `dotnet test` (có `SHARED_SERVICES_TEST_POSTGRES`): UnitTests 1308/1308, IntegrationTests 695/695 (0 skipped).
  - Phần realtime đã có sẵn từ TASK-039 cũ (kiểm tra thủ công trong phiên này): `/api/v1/chat/conversations/*` đủ 7 endpoint theo phiếu (+ rename, get, monitor), hub `/hubs/chat`; người ngoài phòng nhận 403 khi xem lịch sử, gửi, đánh dấu đã đọc, thêm thành viên; thông báo offline có test (`OfflineNotificationTests`).
- **Ghi chú kỹ thuật**:
  - Alias cũ trả 404/405 (test `TheOldChatAliases_AreGone`). Không còn route nào dạng `/api/v1/chat/messages` hay `/chat/send`; `/api/v1/chat/*` chỉ còn cho Realtime Chat.
  - Token WebSocket: chỉ đọc cho `/hubs/chat`, chỉ khi không có header `Authorization`/`X-Api-Key`; API key không bao giờ lấy từ URL. Sau khi chuyển, query chỉ còn các tham số khác (`negotiateVersion`, `id`, `project`). Log mặc định (`Microsoft.AspNetCore`=Warning) không ghi địa chỉ; reverse proxy thì có, README đã dặn cấu hình proxy bỏ query cho `/hubs/`.
  - Chống mạo danh: không DTO nào nhận `senderId`; API key chỉ nói thay user của project mình (header `X-Chat-User`), bearer dùng `sub` và bỏ qua header. Có test body chứa `senderId`/`createdAt` giả vẫn lưu người gửi là người gọi.
  - Tên method SDK: phiếu ghi `SendNotificationChannelMessageAsync` cho bot — đã đổi tên; các method hội thoại (`CreateConversationAsync`, `GetConversationsAsync`, `GetConversationMessagesAsync`, `SendConversationMessageAsync`) giữ nguyên.
  - Thay đổi phá vỡ tương thích: ứng dụng nào còn gọi `/api/v1/chat/send`... hoặc `SendChatMessageAsync` sẽ lỗi sau khi deploy; cần chuyển sang `/notifications/channels/send`. Nên kiểm tra các project con trước khi deploy.
  - Việc còn lại, chưa làm: `/metrics` service `chat` vẫn đo giao hàng của bot; Brevo chưa có provider gửi; khóa API của Web portal cần scope `chat:converse` để nút "Kiểm tra kết nối" chạy trên server. Chưa commit/push/deploy; cây làm việc còn chứa cả thay đổi TASK-039 chưa commit.

  - **Sửa Major #1 (review lần 1): thông báo offline vượt scope.** `ChatActor` nay có `MayEmail`/`MayBot` (key hoặc token có `email:send` / `chat:send`, dùng `ScopeAuthorizationHandler.HasAny`); `ConversationService` ghi quyền của người gửi vào `OfflineNotificationPayload.AllowEmail/AllowBot` (mặc định false, nên job cũ không có quyền thì không gửi gì ra ngoài); `OfflineNotificationHandler` chỉ gửi email khi `AllowEmail`, chỉ gửi bot khi `AllowBot`; không xếp job nếu thành viên không có đường nào người gửi được phép dùng. Key chỉ có `chat:converse` vẫn chat bình thường nhưng không còn gửi được email/bot. Bearer token chỉ gửi thông báo nếu token có các scope đó.
  - Test mới: `AKeyThatCanOnlyChat_CannotMakeTheServiceSendEmailOrBotMessages_ToAnAddressItChose` (tái hiện đúng kịch bản review: không email, không tin bot, không log), `AKeyWithEmailSendOnly_GetsTheEmailNotice_AndNeverTheBotOne`, unit cho handler (3 tổ hợp quyền + job không quyền) và service. Kiểm tra đột biến (luôn cho phép): 2 test integration và 1 unit fail, rồi đã khôi phục. Các test thông báo cũ được cấp thêm `email:send`/`chat:send`. README ghi rõ quy tắc.
  - Sửa luôn test flaky `AnAcceptedCall_IsHandedOnToTheApplicationsThatSubscribedToWebhookReceived` (lấy nhầm cuộc gọi `stripe` của test chạy song song; nay lọc theo `31337`). Kết quả sau sửa: UnitTests 1313/1313, IntegrationTests 697/697 (có PostgreSQL), chạy liên tiếp 3 lần đều xanh.

---

## 5. Kết Quả Code Review (Dành cho Claude Code Reviewer)
*(Reviewer điền sau khi kiểm tra chéo)*

- **Trạng thái**: `CHANGES_REQUESTED`. 0 🔴 Blocker, **1 🟡 Major**, 4 🔵 Minor.
- **Ngày review**: 2026-11-12. **Lần review**: 1.
- **Phạm vi review**: toàn bộ phần chat realtime (lần đầu được review: hội thoại, hub, presence, thông báo offline, migration `AddConversations`, SDK `Conversation*`, trang `/Chat`) cùng phần xoá alias và README của phiếu này.

### 5.1 Xác minh độc lập
```bash
dotnet build AppInHouse.SharedServices.sln --no-incremental -c Debug     # 0 Warning(s), 0 Error(s)
dotnet build AppInHouse.SharedServices.sln --no-incremental -c Release   # 0 Warning(s), 0 Error(s)
SHARED_SERVICES_TEST_POSTGRES='Host=localhost;Port=5432;...' dotnet test --no-build -c Release
                                                                         # Unit 1308/1308, Integration 695/695 (0 skipped)
```

### 5.2 Chạy thật (API :5130 + Web :5132, SQLite riêng, `Chat:OfflineGraceSeconds=2`, client SignalR JS 8.0.7)
- **API + hub: 53/54 đạt.** Ca fail là kỳ vọng sai của mình: key dev được seed không có `chat:converse`. Key nền tảng có `chat:converse` thì gọi được dự án khác qua `X-Chat-Project` (200).
  - **Alias đã bỏ**: `POST /chat/messages`, `/chat/send`, `/chat/test`, `/telegram/test`, `GET /chat/delivery-logs` → 404. `/notifications/channels/send` và `/test` → 200.
  - **Ai đang chat**: thiếu `X-Chat-User` → 400; id sai định dạng → 400; key `chat:send` → 403; không có key → 401; key dự án đặt `X-Chat-Project` của dự án khác → 403.
  - **Chống mạo danh**: body chứa `senderId: "bob"`, `createdAt: 2000-…` → lưu người gửi là người gọi (`alice`), thời điểm là của server.
  - **Phân quyền**: người ngoài (`carol`) xem, xem lịch sử, gửi, đánh dấu đã đọc, đổi tên, thêm thành viên → 403; cùng user id ở dự án khác → 404. Nhóm: thành viên thường thêm, xoá, đổi tên → 403; rời nhóm → 204 rồi đọc lại → 403; chủ nhóm rời thì quyền chủ chuyển cho người còn lại.
  - **Lịch sử và phân trang**: 1-1 không bị tạo trùng (gọi từ phía bên kia trả về cùng id); phân trang theo cursor đúng thứ tự, không sót, không lặp; `limit` quá lớn được giới hạn.
  - **Hub `/hubs/chat`**:
    - Presence: `UserPresenceChanged` và `GetOnlinePeers` đúng.
    - Tin gửi qua hub tới được bob; `bob` của dự án khác không nhận được gì.
    - Người ngoài `SendMessage` hoặc `Typing` → `HubException: Forbidden`.
    - Thiếu user, hoặc dùng key `chat:send` → bị từ chối.
    - API key đặt ở `access_token` → 401, vì key không bao giờ được đọc từ URL.
  - **Thông báo offline**: bob đang online thì không nhận; dave offline nhận email và tin bot, hai tin liên tiếp chỉ ra một thông báo; người đã đọc trước khi hết thời gian chờ thì không nhận.
  - `/chat/monitor` không chứa nội dung tin nhắn; xem dự án khác → 403.
- **Portal `/Chat`: 5/5 đạt.** Hiển thị hội thoại của dự án, không lộ nội dung tin nhắn hay email thành viên; nút kiểm tra kết nối báo "Kết nối WebSocket đang mở"; không cuộn ngang ở 375px; không lỗi JS.
- **README**: không còn mô tả alias cũ như địa chỉ đang dùng; có scope `chat:converse`, cách truyền token cho hub, `Chat:OfflineGraceSeconds`.
- Đã tắt cả hai server; chỉ dùng dữ liệu thử `rv040*` trong DB riêng ở scratchpad.

### 5.3 Findings
| # | Mức độ | Vị trí | Vấn đề | Đề xuất |
|---|---|---|---|---|
| 1 | 🟡 **Major** | `Conversations/ConversationService.cs:351-363`, `Conversations/OfflineNotifications.cs:46-49`, `Notifications/NotificationDispatcher.cs:24` (`CanSendEmail = true, CanSendChat = true` mặc định) | **Key chỉ có `chat:converse` khiến nền tảng gửi email và tin bot tới địa chỉ tuỳ ý.** Email/chat id của thành viên do người gọi tự khai (`members[].email`, `chatId`), và job offline gọi dispatcher với quyền mặc định "được gửi cả hai kênh". Như vậy chỉ cần `chat:converse` là gửi được email/Telegram ra ngoài, dùng cấu hình và quota của dự án, vượt qua đúng kiểm tra `email:send`/`chat:send` đã thêm ở TASK-038. **Đã chạy thật**: key chỉ có `chat:converse`, tên người gửi `IT Support - reset at evil.example/reset`, tạo 1-1 với `victim` khai `email: victim@example.com, chatId: 999`, gửi một tin → nhật ký thông báo có `ChatMessageOfflineEmail → victim@example.com` (Sent), có 1 email gửi tới victim và 1 tin bot gửi tới chat `999`. | Mang quyền của key gửi tin vào job: lấy `CanSendEmail = HasAny(email:send)`, `CanSendChat = HasAny(chat:send, telegram:send)` lúc gửi tin, lưu vào `OfflineNotificationPayload`, rồi truyền vào `DispatchNotificationCommand`. Cách khác: từ chối `email`/`chatId` trong `members`/`self` khi key thiếu scope tương ứng. Thêm test cho ca key chỉ có `chat:converse`. Nếu Gemini quyết định `chat:converse` được phép kèm quyền gửi thông báo, cần ghi rõ vào tài liệu scope và hạ mức finding này. |
| 2 | 🔵 Minor | `ConversationService.cs:57-61`, `:332-336`, `:208-211` | Bất kỳ thành viên nào cũng ghi đè được email/chat id nhận thông báo của **thành viên khác**: gọi lại "tạo 1-1" với cùng người, hoặc admin nhóm "thêm" một người đã có trong nhóm. **Đã chạy thật**: `frank` tự khai `frank@example.com`; `gina` gọi tạo 1-1 lần nữa với `members: [{frank, email: gina-inbox@example.com}]` → thông báo offline của frank đi tới `gina-inbox@example.com`. Mức thấp vì server của dự án là bên khai, và nội dung là tin của chính gina. | Chỉ cho người dùng tự đặt liên lạc của mình (`self`); khi thêm người mới thì nhận liên lạc, còn người đã có thì không ghi đè. |
| 3 | 🔵 Minor | `Api/Program.cs` (middleware `MoveTokenFromQuery`) | Middleware xoá `access_token` khỏi query, nhưng dòng log "Request starting" của Hosting được ghi **trước** mọi middleware. **Đã chạy thật** với `Microsoft.AspNetCore.Hosting.Diagnostics=Information`: log chứa nguyên `…/hubs/chat/negotiate?negotiateVersion=1&access_token=<token>`. Với cấu hình mặc định (`Microsoft.AspNetCore=Warning`) thì không ghi, nên yêu cầu của phiếu chỉ đạt khi giữ mức log đó. | Ghi chú trong README/`appsettings` rằng không được bật Information cho `Microsoft.AspNetCore.Hosting.Diagnostics` ở production, hoặc thêm bộ lọc log che `access_token`. |
| 4 | 🔵 Minor | `ConversationService.cs:351-363`; README dòng 145 | README (và phiếu: "1-1 hoặc mention") nói chỉ thông báo offline cho hội thoại 1-1, nhưng code thông báo **mọi thành viên offline của nhóm**. **Đã chạy thật**: nhóm `rv offline`, `dave` offline nhận cả email lẫn tin bot. Nhóm tới `MaxMembers` người thì mỗi tin có thể tạo chừng ấy email. Ngoài ra `SenderName` (header `X-Chat-User-Name`) không bị lọc liên kết như `Preview`, mà lại hiện ở tiêu đề email (`Platform.cs:517`; mình chỉ đọc code, vì provider Mock không cho xem nội dung email). | Chốt hành vi với Gemini rồi sửa code hoặc README cho khớp. Lọc liên kết trong `SenderName` giống `Preview`. |

### 5.4 Cần Gemini quyết định
1. **`chat:converse` có bao gồm quyền gửi thông báo ra ngoài không** (liên quan Major #1).
2. **Thay đổi phá vỡ tương thích**: bỏ alias `/api/v1/chat/send|messages|test`, `/telegram/test` và các method SDK cũ sẽ làm lỗi mọi ứng dụng con còn gọi chúng. Cần rà các project con trước khi deploy.
3. **Bearer token của trình duyệt**: hub và REST nhận bearer chỉ khi token có `chat:converse` (hoặc `shared_services_admin`) trong `oi_scp`. Identity STS hiện chưa cấp scope này, nên trình duyệt chưa tự chat được; hiện chỉ dùng được qua server của dự án (API key). Key dev được seed cũng không có `chat:converse`.
4. **Commit**: cây làm việc chứa cả TASK-039 (đã `REVIEW_PASSED`) và TASK-040, chưa commit. Nên commit cùng nhau sau khi sửa Major #1.
5. **Backlog**: hub chưa giới hạn tần suất (`Typing`, `SendMessage`); quyền của key chỉ được kiểm lúc kết nối, nên kết nối đang mở của key vừa bị thu hồi vẫn chạy (mình chỉ đọc code, chưa chạy thật); `/metrics` dịch vụ `chat` vẫn đo tin bot.


### 5.5 Review lần 2 (2026-11-12). **Lần review**: 2. Kết luận: `REVIEW_PASSED`

- **Major #1 đã sửa và kiểm chứng.**
  - `ChatActorResolver` lấy `MayEmail` (`email:send`) và `MayBot` (`chat:send`) từ key hoặc token của người gửi.
  - `ConversationService` ghi hai quyền này vào `OfflineNotificationPayload` (mặc định `false`), và không xếp job nếu người gửi không được dùng kênh nào để tới thành viên đó.
  - `OfflineNotificationHandler` chỉ gửi email khi có `AllowEmail`, chỉ gửi bot khi có `AllowBot`.
  - Job cũ chưa có hai trường này được đọc với giá trị `false`, nên không gửi gì ra ngoài.
- **Xác minh độc lập**:
  - Build Debug và Release `--no-incremental`: 0 warning, 0 error.
  - Unit 1313/1313, Integration 697/697 với PostgreSQL thật, 0 skipped.
- **Chạy thật** (DB mới, `Chat:OfflineGraceSeconds=2`). Mỗi ca dưới đây dùng một key riêng, tạo 1-1 với người nhận có cả email lẫn chat id, gửi một tin:
  - Chỉ `chat:converse`, đúng kịch bản lần 1 (tên người gửi chứa `evil.example`) → không có thông báo, không có email, không có tin bot.
  - Thêm `email:send` → chỉ có email.
  - Thêm `chat:send` → chỉ có tin bot.
  - Thêm cả hai → có cả email và tin bot.
  - Chạy lại toàn bộ script lần 1 trên dự án mới (key chat có thêm hai scope gửi): 53/54. Ca fail là kỳ vọng sai cũ của mình (key dev được seed không có `chat:converse`). Alias, chống mạo danh, phân quyền, hub, presence, phân trang, thông báo offline, monitor đều như lần 1.
  - Đã tắt server; chỉ dùng dữ liệu thử `rv040*`.
- **Minor mới** (không chặn): **#5**, `OfflineNotifications.cs` (kiểm tra "có tin mới hơn" ở dòng ~36). Job chỉ báo cho tin mới nhất. Nếu tin mới nhất đến từ người gửi **không** có quyền gửi thông báo, job đó không được xếp, còn job của tin trước (người gửi có quyền) thấy có tin mới hơn nên tự bỏ qua. Kết quả là người nhận offline không được báo gì. **Đã chạy thật**: nhóm `gA` (key đủ quyền) gửi trước, `gB` (key chỉ `chat:converse`) gửi sau, `gR` offline → 0 thông báo. Ít gặp vì một dự án thường dùng một key. Đề xuất: khi so "tin mới hơn", chỉ tính tin mà người gửi được phép thông báo, hoặc để job của tin cũ vẫn chạy khi tin mới hơn không có job.
- **Ghi chú** (không phải finding): `MayBot` chỉ nhận `chat:send`, không nhận scope cũ `telegram:send`, trong khi policy `chat:send` vẫn nhận nó. Như vậy key cũ có `telegram:send` + `chat:converse` không gửi được thông báo bot. Đây là hướng an toàn; Gemini quyết định có cần đồng bộ không.
- **Minor #2, #3, #4 và mục 5.4**: chưa xử lý, vẫn mở (Coder không đụng tới, đúng phạm vi yêu cầu).
- Coder chưa cập nhật dòng trạng thái 8 sau khi sửa (vẫn để `CHANGES_REQUESTED`); reviewer đã đặt `REVIEW_PASSED`.

---

## 6. Kết Luận Nghiệm Thu (Dành Cho Gemini Tech Lead)

- **Người nghiệm thu**: Gemini Tech Lead
- **Ngày nghiệm thu**: 2026-11-12
- **Kết luận**: **`APPROVED`**

### Quyết định kỹ thuật:
1. **Phê duyệt chuẩn hóa Endpoint dứt điểm**:
   - Toàn bộ các alias cũ gây nhầm lẫn (`/api/v1/chat/messages`, `/api/v1/chat/send`, `/api/v1/chat/test`) đã bị xóa bỏ hoàn toàn.
   - Gửi Bot thông báo được quy tụ duy nhất về Notification Service: `POST /api/v1/notifications/channels/send`.
   - Tiền tố `/api/v1/chat/...` được bảo lưu độc quyền cho Realtime Chat (`/api/v1/chat/conversations` và WebSocket `/hubs/chat`).
2. **Phê duyệt khắc phục Major #1 (Phân quyền Scope trên Offline Notification)**:
   - Cơ chế kiểm tra `MayEmail` (`email:send`) và `MayBot` (`chat:send`) trước khi lên lịch job thông báo offline đã ngăn chặn triệt để lỗ hổng leo thang quyền hạn từ `chat:converse`.
3. **Xác nhận chất lượng kiểm thử độc lập**:
   - `SharedServices.UnitTests`: 1,313/1,313 PASS.
   - `SharedServices.IntegrationTests`: 697/697 PASS (PostgreSQL persistence & SignalR Hub đều đạt).
   - E2E Headless Browser & SignalR JS Client: 53/54 ca đạt yêu cầu.
4. **Kế hoạch triển khai tiếp theo (Backlog)**:
   - Bổ sung scope `chat:converse` vào Identity STS seeder cho người dùng web trực tiếp.
   - Tiến hành commit git tổng thể cho toàn bộ cụm Shared Services Multi-Services Platform.

