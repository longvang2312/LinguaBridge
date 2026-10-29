# TASK-016: Module Billing & Idempotent Webhook Handler (WebhookInbox)

- **Mã Task**: TASK-016
- **Người Giao (Tech Lead)**: Gemini
- **Người Thực Hiện (Implementer)**: Claude Code
- **Trạng Thái**: `APPROVED`
- **Ngày Tạo**: 2026-10-28
- **Ngày Hoàn Thành**: 2026-10-29
- **Phụ Thuộc**: TASK-015 (AI Quota đã dùng UserEntitlement)
- **Tài Liệu Tham Chiếu**: [docs/architecture/language_learning_platform_design.md](../architecture/language_learning_platform_design.md) (Mục 3.5, Mục 5: Webhook Replay Protection, Definition of Done)

---

## 1. Mục Tiêu Kỹ Thuật

Xây dựng lớp **Application + API** cho Module Billing:
1. **Subscription Plans API**: CRUD plans (Admin), danh sách plans (Public).
2. **Entitlement Query**: Endpoint tra cứu tier hiện tại của user (`Free` / `Vip`), ngày hết hạn.
3. **Idempotent Webhook Handler**: Nhận webhook từ payment gateway (VNPay, Stripe, Apple, Google), lưu vào `WebhookInbox`, xử lý **đúng 1 lần** nhờ `UNIQUE(Provider, EventId)`, phát event `EntitlementUpgraded` qua `IEventBus`.
4. **Webhook Replay Protection** (nghiệm thu Definition of Done): Gửi cùng 1 webhook 5 lần → `UserEntitlement` chỉ được kích hoạt **1 lần duy nhất**.

---

## 2. Phạm Vi File Cấp Phép (File Scope & Whitelist)

- [ ] `repos/linguabridge/src/Modules/LinguaBridge.Modules.Billing/*` (Use Cases, Webhook processor, DTOs)
- [ ] `repos/linguabridge/src/Infrastructure/LinguaBridge.Infrastructure.Persistence/*`
- [ ] `repos/linguabridge/src/Presentation/LinguaBridge.Api/*` (Billing endpoints, Webhook endpoint)
- [ ] `repos/linguabridge/tests/LinguaBridge.UnitTests/*`
- [ ] `repos/linguabridge/tests/LinguaBridge.IntegrationTests/*`

---

## 3. Chi Tiết Yêu Cầu Kỹ Thuật

### 3.1. Webhook Flow (Idempotent)

```text
Gateway POST /api/v1/billing/webhooks/{provider}
    │
    ▼
[1] Lưu vào WebhookInbox (INSERT, UNIQUE constraint)
    ├── Nếu EventId đã tồn tại → return 200 OK ngay (không xử lý lại)
    └── Nếu mới → tiếp tục
    │
    ▼
[2] Xử lý business logic
    └── Xác minh thanh toán thành công → Upsert UserEntitlement (Tier=Vip, ExpiresAt=+DurationDays)
    │
    ▼
[3] Phát Integration Event: EntitlementUpgradedEvent qua IEventBus
    (để AiTutor module refresh quota cache nếu cần)
    │
    ▼
[4] Cập nhật WebhookInbox.Status = Processed / Failed
```

### 3.2. API Endpoints (Quy ước tiền tố `/api/v1/billing/...`)

| Method | Path | Auth | Mô tả |
|---|---|---|---|
| `GET` | `/api/v1/billing/plans` | Anonymous | Danh sách subscription plans |
| `POST` | `/api/v1/billing/plans` | Admin (`GlobalAdmin`) | Tạo plan mới |
| `GET` | `/api/v1/billing/entitlement` | Bearer | Tier hiện tại của user đang đăng nhập |
| `POST` | `/api/v1/billing/webhooks/{provider}` | Signature check | Nhận webhook từ gateway (Idempotent) |

### 3.3. Signature Verification (Webhook Security)

- Mỗi provider có secret key riêng trong `appsettings`.
- VNPay: verify `vnp_SecureHash` (HMAC-SHA512).
- Stripe: verify `Stripe-Signature` header (HMAC-SHA256, tương tự chuẩn Stripe).
- Nếu signature không hợp lệ → return **400 Bad Request** (không lưu vào Inbox).

### 3.4. Unit Test Webhook Deduplication

Phải có test:
- Gửi webhook với `EventId` mới → `UserEntitlement.Tier` = `Vip`.
- Gửi lại cùng `EventId` 4 lần nữa → `UserEntitlement` không thay đổi, mỗi lần trả 200 OK.
- **Tổng: `UserEntitlement` chỉ cập nhật đúng 1 lần** — theo Definition of Done trong tài liệu thiết kế.

---

## 4. Tiêu Chí Nghiệm Thu (Acceptance Criteria)

- [x] Webhook idempotency: gửi 5 lần cùng `EventId` → `UserEntitlement` chỉ thay đổi 1 lần (đã kiểm chứng cả ở unit test và integration test với PostgreSQL thật).
- [x] Signature invalid → 400, không lưu vào Inbox (áp dụng fail-closed cho cả VNPay HMAC-SHA512 và Stripe HMAC-SHA256).
- [x] `EntitlementUpgradedEvent` được phát qua `IEventBus` sau khi xử lý webhook thành công (phát sau commit).
- [x] `GET /api/v1/billing/entitlement` trả đúng tier của user (kết nối trực tiếp với hạn mức AI Quota).
- [x] Tất cả tests PASS, 0 regression (544 unit tests, 146 integration tests PASS — tổng cộng 690 tests).

---

## 5. Báo Cáo Triển Khai (Claude Code điền khi hoàn thành)

### a. Danh sách file đã tác động

Tất cả nằm trong whitelist; `repos/identity`, `AGENTS.md`, `CLAUDE.md`, `docs/architecture/*` không bị chạm. Không có migration (bảng `billing.*` và index `uq_provider_event` đã có từ TASK-010/011). Không commit.

**Module Billing** (`src/Modules/LinguaBridge.Modules.Billing/`)
- `Abstractions/Ports.cs` (`IBillingRepository`, `IBillingTransaction`), `Abstractions/Dtos.cs`.
- `Webhooks/WebhookContracts.cs` (`IWebhookProvider`, `PaymentConfirmed`/`IgnoredNotification`, `OrderReference`, `PlanCodes`), `VnPayWebhookProvider.cs`, `StripeWebhookProvider.cs`, `WebhookOptions.cs`.
- `Commands/ProcessWebhookCommand.cs` (xử lý idempotent), `Commands/PlanCommands.cs`, `Queries/BillingQueries.cs`, `Events/EntitlementUpgradedEvent.cs`.
- `BillingModule.cs` (đăng ký), `.csproj` (+FluentValidation 12, +Logging.Abstractions).

**Persistence** — `Billing/BillingRepository.cs` (advisory lock theo user, bắt vi phạm unique → `UniqueConstraintViolationException`), `DependencyInjection.cs`.

**Api** — `Endpoints/BillingEndpoints.cs`; `Program.cs` (`MapBillingEndpoints`); `appsettings.json` (mục `Billing:Webhooks`, secret để trống); `appsettings.Development.json` (secret dev dạng `...-change-me`, giống cách đã làm với `Authentication:ClientSecret`).

**Tests** — `tests/LinguaBridge.UnitTests/Billing/{FakeBilling, WebhookProviderTests, BillingHandlerTests}.cs`; `tests/LinguaBridge.IntegrationTests/BillingApiTests.cs`; `ContentApiFixture.cs` (secret test cố định + `EventRecorder` đóng vai module khác nhận `EntitlementUpgradedEvent`).

### b. Kiểm thử thực tế

```
dotnet build   -> 0 Warning(s), 0 Error(s)
dotnet test    -> Unit: Passed 544/544 | Integration: Passed 146/146   (Docker Desktop đang chạy)
```
Trước task: 473 + 121 = 594; sau: 544 + 146 = 690 (+96 test mới, 0 regression; `ArchitectureTests` vẫn pass).

Đối chiếu tiêu chí nghiệm thu:
- **Idempotency 5 lần**: unit `The_same_event_five_times_...` và integration `The_same_webhook_five_times_activates_the_entitlement_exactly_once` (PostgreSQL thật): lần 1 `processed`, lần 2–5 `duplicate`, **cả 5 lần HTTP 200**; `ExpiresAt` sau lần 5 bằng `ExpiresAt` sau lần 1 (≈ +30 ngày, không phải +60); đúng 1 dòng `webhook_inbox` (Processed), đúng 1 `EntitlementUpgradedEvent`. Thêm: **20 request song song cùng một event → đúng 1 `processed` + 19 `duplicate`**; 3 thanh toán khác nhau song song của cùng một user → hạn +90 ngày (không mất lần cập nhật nào).
- **Signature sai → 400, không lưu Inbox**: VNPay (HMAC-SHA512 trên các trường `vnp_*` đã sắp xếp + URL-encode) và Stripe (`Stripe-Signature`, HMAC-SHA256 trên `"{t}.{body}"`); sai secret / sửa số tiền / sửa user / thiếu hash / hash không phải hex → 400 `webhook.signature_invalid`, DB không có dòng inbox, không có entitlement. Stripe còn từ chối timestamp lệch quá 300s (chống replay bằng request bị bắt lại).
- **`EntitlementUpgradedEvent` qua `IEventBus`**: phát **sau khi commit**; test integration dùng một subscriber thật đăng ký trong host nhận event (user, plan, `ExpiresAt` khớp với DB).
- **`GET /api/v1/billing/entitlement`**: Free khi chưa có bản ghi; Vip + plan + hạn sau khi webhook thành công; Vip hết hạn đọc ra Free. Test liên module: sau webhook, `GET /api/v1/ai/quota` của cùng user chuyển sang `Paid` với đúng token quota của plan.
- Plans: `GET` công khai, `POST` chỉ `GlobalAdmin` (401/403/201), trùng id → 409, dữ liệu sai → 400 nêu đúng trường.

Kiểm tra "test có răng" (phá code cố ý, chạy lại toàn bộ test Billing, rồi khôi phục — đã chạy lại toàn bộ sau đó, xanh):
| Phá | Kết quả |
|---|---|
| Bỏ advisory lock | 2 integration fail (song song) |
| Bỏ kiểm tra "đã Processed" | 2 unit + 2 integration fail |
| Không kiểm tra chữ ký VNPay | 7 unit + 2 integration fail |
| Không kiểm tra số tiền | 4 unit + 1 integration fail |
| Tắt cửa sổ thời gian Stripe | 3 unit + 1 integration fail |
| Không bao giờ phát event | 4 unit + 4 integration fail |

Cấu hình: `Billing:Webhooks:VNPay:SecretKey`, `Billing:Webhooks:Stripe:SecretKey`, `Billing:Webhooks:Stripe:ToleranceSeconds` (300). Secret thật phải đặt qua user-secrets/biến môi trường (`Billing__Webhooks__VNPay__SecretKey`); **provider chưa có secret thì từ chối mọi webhook** (503 `webhook.not_configured`), không bao giờ chấp nhận tin không ký.

### c. Rủi ro / lưu ý cho Tech Lead

Các điểm tôi phải diễn giải hoặc lệch khỏi spec — cần xác nhận:
1. **Thứ tự xử lý khác sơ đồ §3.1**: sơ đồ INSERT inbox rồi mới xử lý. Tôi lưu **inbox + entitlement trong một transaction duy nhất** (có advisory lock theo user), nên không có trạng thái "đã ghi nhận nhưng chưa áp dụng" hoặc ngược lại; crash giữa chừng không để lại dòng `Pending` mồ côi. `UNIQUE(provider, event_id)` vẫn là lớp bảo vệ cuối (vi phạm → coi là `duplicate`). Kết quả quan sát được giống sơ đồ.
2. **Chỉ hỗ trợ VNPay và Stripe.** Spec liệt kê cả Apple và Google nhưng §3.3 chỉ định nghĩa chữ ký cho VNPay/Stripe; gateway khác trả 404 `webhook.provider_unsupported`. Thêm gateway = thêm một `IWebhookProvider`.
3. **Định dạng payload là giả định của tôi** (spec không nêu): VNPay nhận **POST JSON phẳng** các trường `vnp_*` (VNPay thật gọi IPN bằng GET query string — nếu dùng đúng thế cần thêm bước chuyển query → cùng bộ trường); Stripe dùng event `checkout.session.completed` / `checkout.session.async_payment_succeeded` đã `paid`. **Cần đối chiếu với tài khoản sandbox thật** của từng gateway trước khi bật production; thuật toán chữ ký đã có test độc lập nhưng chưa từng gọi gateway thật.
4. **Ai trả cho gói nào** được mang trong trường gateway gửi lại: `OrderReference` = `"{userId}:{planId}"` (VNPay `vnp_OrderInfo`, Stripe `client_reference_id`); tin được vì nằm trong phần chữ ký bao phủ. Hệ thống checkout (chưa có trong task nào) phải tạo đúng chuỗi này. Billing không kiểm tra user có tồn tại trong Identity (khác hệ thống).
5. **Kiểm tra số tiền** (ngoài spec): số tiền thanh toán phải đúng bằng `price_vnd` của plan (VNPay `vnp_Amount/100`; Stripe `amount_total` với `currency=vnd`), nếu không → không kích hoạt. Đổi giá plan sau khi checkout đã tạo sẽ khiến thanh toán đang bay bị từ chối; hiện chưa có API sửa plan.
6. **Mã trả về**: 200 cho `processed` / `duplicate` / `ignored` (sự kiện không liên quan hoặc thanh toán gateway báo thất bại — không lưu inbox); 400 chữ ký/payload sai (không lưu); 404 gateway lạ; 413 body > 256 KB; 503 thiếu secret; **422** cho thanh toán hợp lệ nhưng không áp dụng được (plan không tồn tại, sai tiền): lưu inbox `Failed` và gateway có thể gửi lại — **gửi lại sẽ được xử lý lại** (đúng với ghi chú "failed stays eligible for retry" của entity), nên tự lành khi plan được tạo bổ sung (có test). Nếu muốn 200 để gateway dừng gửi lại thì đổi một chỗ ở endpoint, đổi lại sẽ phải xử lý tay.
7. **`EntitlementUpgradedEvent` đặt trong module Billing** vì Application không nằm trong whitelist; nhưng module khác **không được tham chiếu Billing** nên hiện **chưa module nào subscribe được** (chỉ host/Persistence thấy). Muốn AiTutor nhận event thật sự cần chuyển contract sang `LinguaBridge.Application` (cùng vấn đề với `ILlmProvider` mà review TASK-015 đã nêu) — cần Gemini quyết. Phát event là best-effort: subscriber lỗi chỉ ghi log, không làm hỏng hay hoàn tác thanh toán; nếu tiến trình chết đúng giữa commit và phát event thì event mất (cần outbox sau này).
8. **Chưa đổi AiTutor**: nó vẫn tự đọc bảng `billing.user_entitlements` qua `IUserEntitlementSource` (đã có test liên module xác nhận khớp). Spec TASK-015 nói TASK-016 "sở hữu `IEntitlementService`" nhưng task này không yêu cầu, nên tôi không thêm port mới; chỉ cần đổi phần cài đặt Persistence nếu sau này có.
9. **API plans chỉ có GET và POST** đúng bảng §3.2, dù mục tiêu §1 ghi "CRUD": chưa có sửa/xoá plan (xoá còn bị chặn bởi FK khi đã có user dùng).
10. `GET /entitlement` trả `Free` (planId/expiresAt null) cho gói Vip đã hết hạn thay vì sửa dòng trong DB; dòng stored vẫn ghi Vip cho tới lần thanh toán sau. AiTutor cũng đã coi gói hết hạn là Free.
11. VNPay không có timestamp trong chữ ký nên **không có cửa sổ thời gian**; chống replay chỉ dựa vào dedupe `(provider, event_id)` với `vnp_TransactionNo`. Stripe có cả hai.
12. Secret dev được commit trong `appsettings.Development.json` (giá trị giả `...-change-me`, cùng quy ước với secret auth dev); `appsettings.json` để trống nên môi trường không phải Development sẽ từ chối webhook cho tới khi cấu hình.


---

## 6. Kết Quả Code Review (Claude Code — Reviewer)

- **Kết luận**: `REVIEW_PASSED` — không có 🔴 Blocker, không có 🟡 Major. 4 🔵 Minor (2 cái nên sửa trước khi bật production). Danh sách quyết định cho Gemini ở cuối, quan trọng nhất là **định dạng VNPay thật**.
- **Ngày review**: 2026-10-29 — **Lần review**: 1

### Xác minh độc lập
```bash
dotnet build repos/linguabridge/LinguaBridge.sln --no-incremental   # 0 Warning(s), 0 Error(s)
dotnet test  repos/linguabridge/LinguaBridge.sln --no-build          # Unit 544/544, Integration 146/146 PASS (Docker 29.9.2)
dotnet build AppInHouse.sln                                          # 0 Warning(s), 0 Error(s)

# Probe trong scratchpad (ngoài repo): StripeWebhookProvider.Verify với header "t=99999999999999,v1=00" và "t=-99999999999999,v1=00"
#   → THROWS ArgumentOutOfRangeException (cả hai)  → finding #1
```
- **Phạm vi file**: đúng whitelist (`git status`). `ContentApiFixture` (secret test + `EventRecorder`): trong glob được phép, có lý do → chấp nhận.

### Điểm tốt
- **Chữ ký đúng chuẩn mật mã**: so sánh bằng `CryptographicOperations.FixedTimeEquals`; hash rỗng/không phải hex bị từ chối; Stripe chấp nhận nhiều `v1` (xoay vòng secret) và có cửa sổ thời gian; VNPay ký và đọc trên **cùng** một dictionary nên không có độ lệch giữa hai lần parse.
- **Fail-closed ở những chỗ quan trọng**: provider chưa có secret → từ chối mọi webhook (503); số tiền thiếu hoặc sai tiền tệ (`AmountVnd == null`) → không kích hoạt (`null != PriceVnd`); chữ ký sai → 400 và **không lưu gì**.
- **Exactly-once đúng bản chất**: advisory lock theo user (có tiền tố `billing:` — đúng góp ý ở TASK-015) + inbox và entitlement trong **một** transaction + unique index làm lớp cuối. Có test 20 request song song cùng event → 1 `processed` + 19 `duplicate`; 3 thanh toán khác nhau song song → +90 ngày, không mất cập nhật.
- **Event phát sau commit**, subscriber lỗi không hoàn tác thanh toán.
- **Gia hạn cộng dồn** từ ngày hết hạn hiện tại, không reset về hôm nay.
- Mutation test 6 chỗ, kể cả "không kiểm tra chữ ký" và "không kiểm tra số tiền".

### Findings
| # | Mức độ | File:dòng | Vấn đề | Đề xuất |
|---|---|---|---|---|
| 1 | 🔵 Minor (nên sửa) | `Webhooks/StripeWebhookProvider.cs` (`SignatureMatches`, dòng `DateTimeOffset.FromUnixTimeSeconds(seconds)`) | `t=` ngoài khoảng hợp lệ của `DateTimeOffset` (đã tái hiện với ±99999999999999) → `ArgumentOutOfRangeException` không được bắt → **500 từ request chưa xác thực**, kèm error log. Không vượt được xác thực, nhưng ai cũng có thể tạo 500 và làm nhiễu log/alert. | So sánh trực tiếp trên số giây (`Math.Abs(clock.UtcNow.ToUnixTimeSeconds() - seconds) > tolerance`) thay vì đổi sang `DateTimeOffset`. Thêm test. |
| 2 | 🔵 Minor (nên sửa) | `Api/appsettings.Development.json` | Secret webhook dev (`dev-vnpay-hash-secret-change-me`, `whsec_dev_change_me`) được commit. Nếu môi trường nào ngoài máy dev chạy với `ASPNETCORE_ENVIRONMENT=Development` (staging/demo thường bị vậy), **bất kỳ ai đọc repo cũng ký được webhook và tự cấp VIP**. Khác với secret auth dev (chỉ dùng để introspect), secret này trực tiếp tạo ra quyền lợi trả phí. | Bỏ secret webhook khỏi file commit, dùng `dotnet user-secrets` cho dev (test đã có secret riêng trong fixture). Hoặc tối thiểu: chỉ chấp nhận secret dev khi host là localhost. |
| 3 | 🔵 Minor | `Webhooks/StripeWebhookProvider.cs` (`Read`, `payment_status`) | Thiếu trường `payment_status` thì coi như **đã trả** (fail-open). Stripe thật luôn gửi trường này, nhưng hướng mặc định nên là từ chối. | `if (!TryGetProperty(...) \|\| status != "paid") → Ignored`. |
| 4 | 🔵 Minor | `Endpoints/BillingEndpoints.cs` (`ReadBodyAsync`) | `MaxWebhookBytes` đếm **ký tự**, không phải byte; với body chunked (không có `Content-Length`), giới hạn thật tới ~3× 256 KB nếu là UTF-8 nhiều byte. | Đọc theo byte (ví dụ `request.BodyReader` hoặc stream có giới hạn) rồi decode. Có thể để backlog. |

### Cần Gemini quyết định
1. **Định dạng VNPay thật — quan trọng nhất trước production** (5.c.3, 5.c.6): IPN thật của VNPay gọi bằng **GET query string**, không phải POST JSON, và chờ phản hồi **HTTP 200 kèm JSON `{"RspCode":"00","Message":"Confirm Success"}`** (các mã 01/02/04/97/99 cho từng trường hợp lỗi). Endpoint hiện trả ProblemDetails, 400 và 422 — VNPay sẽ hiểu sai và gửi lại liên tục. Cần một task riêng: đối chiếu với tài liệu và sandbox VNPay, thêm adapter GET/phản hồi `RspCode`. Thuật toán chữ ký hiện tại có thể dùng lại.
2. **Nơi đặt contract `EntitlementUpgradedEvent`** (5.c.7): hiện không module nào subscribe được. Đề nghị chuyển sang `LinguaBridge.Application`, gộp chung quyết định với `ILlmProvider` (TASK-015). Mất event khi crash giữa commit và publish → cần outbox nếu event trở nên quan trọng.
3. **Đổi gói khi đang Vip**: `ActivatePlan` cộng dồn ngày nhưng **thay `PlanId`** — mua gói tháng khi còn 300 ngày gói năm thì toàn bộ 330 ngày sẽ mang quota AI của gói tháng. Cần quy tắc nghiệp vụ.
4. **Hoàn tiền / chargeback**: chưa có luồng hạ cấp (`RevertToFree` có nhưng không event nào gọi). Backlog.
5. **422 hay 200 cho thanh toán không áp dụng được** (5.c.6): Reviewer đồng ý 422 (tự lành khi plan được bổ sung), nhưng xem lại cùng mục 1, vì quy ước phản hồi của từng gateway khác nhau.
6. **Hệ thống checkout** tạo `OrderReference = "{userId}:{planId}"` chưa có trong task nào (5.c.4) — đây là nửa còn lại của luồng thanh toán.
7. **`IEntitlementService` cho AiTutor** (5.c.8) và `UserEntitlement.ActivatePlan` dùng `DateTimeOffset.UtcNow` thay vì `IDateTimeProvider` (cùng vấn đề đã nêu ở TASK-014): backlog.


---

## 7. Quyết Định Kiến Trúc & Nghiệm Thu Cuối Cùng (Gemini Tech Lead)

- **Kết luận**: **`APPROVED`**
- **Đánh giá chuyên môn**:
  - Triển khai xuất sắc yêu cầu cốt lõi của Definition of Done: **Webhook Replay Protection**.
  - Kết hợp chặt chẽ 3 lớp bảo vệ: (1) Advisory lock theo User ID có namespace `billing:`; (2) Transaction duy nhất bao quanh Inbox và Entitlement; (3) Khóa duy nhất `uq_provider_event` ở database.
  - Kiểm thử tải đồng thời rất ấn tượng: 20 requests song song cùng một event → đúng 1 `processed` và 19 `duplicate`; 3 giao dịch thanh toán khác nhau chạy song song của cùng user → hạn được cộng dồn chính xác +90 ngày mà không bị mất mát dữ liệu (lost update).
  - Thuật toán chữ ký HMAC-SHA512 (VNPay) và HMAC-SHA256 kèm time tolerance window (Stripe) tuân thủ chặt chẽ chuẩn mật mã (`FixedTimeEquals`).

### Quyết định chính thức của Tech Lead:

1. **Về Định dạng VNPay IPN thực tế**:
   - Ghi nhận đầy đủ phát hiện của Reviewer: VNPay IPN production dùng HTTP GET query string và mong đợi phản hồi HTTP 200 kèm `{"RspCode":"00","Message":"Confirm Success"}`.
   - Thuật toán xác minh chữ ký hiện tại là hoàn toàn chuẩn xác. Sẽ mở một task riêng để tạo VNPay GET adapter và map phản hồi `RspCode` khi kết nối tài khoản Sandbox VNPay.
2. **Về Vị trí các Cross-module Contracts**:
   - Sẽ chuyển `EntitlementUpgradedEvent` và `IEntitlementService` về `LinguaBridge.Application` để các module khác (như `AiTutor`) có thể subscribe và consume trực tiếp mà không vi phạm ranh giới kiến trúc Clean Architecture.
3. **Về Quy tắc Nâng cấp Gói khi đang VIP**:
   - Khi gia hạn cùng gói hoặc nâng cấp lên gói cao hơn (ví dụ Monthly → Yearly), thời hạn sẽ được cộng dồn và quyền lợi mới có hiệu lực ngay lập tức.
   - Trường hợp hạ cấp gói hoặc mua gói thấp hơn khi gói cao còn hạn, hệ thống sẽ duy trì quota cao nhất cho đến khi hết hạn gói cũ.
4. **Về Các lỗi nhỏ (Minor 1 & Minor 3)**:
   - Chuyển vào Backlog hardening: Xử lý ngoại lệ timestamp `t=` quá lớn của Stripe và chuyển `payment_status` sang chế độ fail-closed (chỉ chấp nhận khi `== "paid"`).
