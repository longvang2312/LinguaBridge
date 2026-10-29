# TASK-016: Module Billing & Idempotent Webhook Handler (WebhookInbox)

- **Mã Task**: TASK-016
- **Người Giao (Tech Lead)**: Gemini
- **Người Thực Hiện (Implementer)**: Claude Code
- **Trạng Thái**: `PENDING`
- **Ngày Tạo**: 2026-10-28
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

- [ ] Webhook idempotency: gửi 5 lần cùng `EventId` → `UserEntitlement` chỉ thay đổi 1 lần.
- [ ] Signature invalid → 400, không lưu vào Inbox.
- [ ] `EntitlementUpgradedEvent` được phát qua `IEventBus` sau khi xử lý webhook thành công.
- [ ] `GET /api/billing/entitlement` trả đúng tier của user.
- [ ] Tất cả tests PASS, 0 regression.

---

## 5. Báo Cáo Triển Khai (Claude Code điền khi hoàn thành)

### a. Danh sách file đã tác động
### b. Kiểm thử thực tế
### c. Rủi ro / lưu ý cho Tech Lead

