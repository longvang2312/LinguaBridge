# KIẾN TRÚC NỀN TẢNG ĐA DỊCH VỤ (MULTI-SERVICES PLATFORM ARCHITECTURE)
# APPINHOUSE.SHAREDSERVICES — MICROSERVICES & CENTRAL HUB PORTAL

- **Tác giả**: Gemini Tech Lead & Solution Architect
- **Phiên bản**: 2.0.0 (Multi-Services Evolution)
- **Ngày cập nhật**: 2026-11-12
- **Trạng thái**: Thiết kế & Triển khai Giao diện Quản trị Tập trung

---

## 1. MÔ HÌNH TỔNG THỂ (HIGH-LEVEL ARCHITECTURE)

```text
               ┌────────────────────────────────────────────────────────┐
               │         AppInHouse Identity Platform (STS)             │
               │         (OAuth2 / OIDC SSO - Port 5001)                │
               └──────────────────────────┬─────────────────────────────┘
                                          │ OIDC Login / Authorization Code + PKCE
                                          ▼
               ┌────────────────────────────────────────────────────────┐
               │    AppInHouse Central Management Portal (Web Hub)      │
               │    - Home Page: Service Launchpad & Health Dashboard   │
               │    - Quản trị cấu hình tập trung cho 8 phân hệ         │
               │    - Role-based Access: GlobalAdmin                    │
               └──────────────────────────┬─────────────────────────────┘
                                          │ REST API (Bearer Token / Platform Key)
     ┌────────────────────────────────────┼────────────────────────────────────┐
     │                                    │                                    │
     ▼                                    ▼                                    ▼
┌──────────────────────┐        ┌──────────────────────┐        ┌──────────────────────┐
│  1. Email Service    │        │   2. Chat Service    │        │ 3. Media/Storage Svc │
│  - SMTP, SendGrid    │        │  - Telegram, Discord │        │ - S3, Local Disk     │
│  - HTML Templates    │        │  - Slack, Outbound   │        │ - Image Processing   │
└──────────────────────┘        └──────────────────────┘        └──────────────────────┘
     │                                    │                                    │
     ▼                                    ▼                                    ▼
┌──────────────────────┐        ┌──────────────────────┐        ┌──────────────────────┐
│ 4. Project & Access  │        │ 5. Notification Svc  │        │ 6. Background Jobs   │
│  - API Keys Lifecycle│        │  - Multi-channel Hub │        │  - Message Queues    │
│  - Scopes & Quotas   │        │  - Smart Routing     │        │  - Retry & Scheduler │
└──────────────────────┘        └──────────────────────┘        └──────────────────────┘
     │                                    │
     ▼                                    ▼
┌──────────────────────┐        ┌──────────────────────┐
│  7. Webhook Service  │        │ 8. Monitoring & Audit│
│  - Inbound Receiver  │        │  - Central Logs      │
│  - Outbound Dispatch │        │  - Metrics & Health  │
└──────────────────────┘        └──────────────────────┘
```

---

## 2. DANH MỤC 8 PHÂN HỆ DỊCH VỤ DÙNG CHUNG (CORE SERVICES)

| # | Phân Hệ Dịch Vụ | Vai Trò & Nghiệp Vụ Cốt Lõi | Giao Tiếp API / Endpoint |
|---|---|---|---|
| **1** | **Email Service** | Gửi email đơn, hàng loạt, quản lý mẫu HTML động (Template Engine), hỗ trợ đa provider (SMTP, SendGrid, Brevo) với failover tự động. | `POST /api/v1/emails/send`<br>`POST /api/v1/emails/test`<br>`/api/v1/email-templates` |
| **2** | **Chat Service (Realtime Platform)** | Trò chuyện trực tuyến thời gian thực giữa người dùng với người dùng (1-1 Direct & Group Rooms), SignalR WebSocket Hub, quản lý kết nối & trạng thái Online/Offline, lưu trữ & phân trang lịch sử tin nhắn. | WebSocket `/hubs/chat`<br>`/api/v1/chat/conversations`<br>`/api/v1/chat/messages` |
| **3** | **Media & Storage Service** | Lưu trữ tệp tin (S3 / MinIO / Local), xác thực Magic Bytes nhị phân chống giả mạo, tự động tạo Thumbnail và Browser tệp tin. | `POST /api/v1/storage/upload`<br>`GET /api/v1/storage/files/{id}` |
| **4** | **Project & Access Service** | Quản lý định danh các dự án con (`identity`, `linguabridge`, `crm`...), vòng đời API Key (Hash SHA-256, Scopes hạt nhân, Key Rotation), Quotas. | `/api/v1/projects`<br>`/api/v1/api-keys` |
| **5** | **Notification Service (Omnichannel)** | Trung tâm thông báo đa kênh toàn diện: Quản lý toàn bộ Outbound Bots & Webhooks (Telegram Bot, Discord, Slack, Email), Smart Routing, Templates động và tự động phát cảnh báo khi người dùng offline. | `POST /api/v1/notifications/dispatch`<br>`/api/v1/notifications/channels`<br>`/api/v1/notification-templates` |
| **6** | **Background Job Service** | Xử lý tác vụ ngầm, hàng đợi tin nhắn (Queue), lập lịch định kỳ (Cron Schedule), Outbox pattern và tự động thử lại khi nhà cung cấp gián đoạn. | `/api/v1/jobs/queues`<br>`/api/v1/jobs/schedules` |
| **7** | **Webhook Service** | Tiếp nhận Webhook từ bên thứ 3 (Telegram Updates, Stripe, GitHub...), xác thực chữ ký (HMAC SHA-256) và phân phối webhook outbound đến các ứng dụng nội bộ. | `POST /api/v1/webhooks/incoming/{source}`<br>`/api/v1/webhooks/subscriptions` |
| **8** | **Monitoring & Audit Service** | Thu thập tập trung 4 loại logs (Delivery, Media, Audit, Operations), đo lường Latency, Uptime, Success Rate và cung cấp dữ liệu giám sát thời gian thực. | `/api/v1/audit-logs`<br>`/api/v1/metrics/overview` |

---

## 3. THIẾT KẾ CENTRAL MANAGEMENT PORTAL (APP LAUNCHPAD & HUB)

### 3.1. Cơ Chế Đăng Nhập & Bảo Mật
- Sử dụng chuẩn **OpenID Connect (OIDC / PKCE)** qua AppInHouse Identity Platform.
- Phân quyền theo Role Quản trị viên (`GlobalAdmin` hoặc `SharedServicesAdmin`).
- Topbar tích hợp User Profile (Tên, Email, Avatar, Nút Đăng xuất SSO).

### 3.2. Cấu Trúc Trang Home Page (Application Launchpad)
1. **Hero Header & Global Status**:
   - Tên hệ thống: **AppInHouse Shared Services Hub**.
   - Trạng thái tổng thể: "Tất Cả Dịch Vụ Đang Hoạt Động Bình Thường" (All systems operational).
   - Thanh chọn Dự án nhanh (`Project Switcher`: Tất cả dự án, Identity, LinguaBridge...).
2. **Thống Kê Tổng Quan (Quick Metrics Bar)**:
   - Số Services đang chạy (8/8 Active).
   - Tổng lưu lượng API hôm nay (Requests / 24h).
   - Tỷ lệ gửi thành công (Success Rate: 99.8%).
   - Dung lượng lưu trữ đã sử dụng (Total Storage Used).
3. **Service Launchpad Grid (Bento Grid 8 Thẻ Dịch Vụ)**:
   - Mỗi thẻ đại diện cho 1 Service:
     - **Biểu tượng (Icon) & Màu sắc định danh chuyên biệt**.
     - **Tên dịch vụ & Mô tả ngắn**.
     - **Chỉ báo trạng thái (Health Badge)**: `Healthy` (Xanh lá), `Warning` (Vàng), `Offline` (Đỏ).
     - **Chỉ số thời gian thực (Live Metrics)**: Ví dụ Email hiển thị số email gửi trong ngày; Chat hiển thị số bot online; Storage hiển thị số lượng file.
     - **Nút truy cập nhanh (Manage Service / Configure)** và menu thao tác phụ (Test, Xem Logs).
4. **Nhật Ký Hoạt Động Gần Đây (Recent Activity & Audit Ticker)**:
   - Luồng sự kiện mới nhất: Ai vừa tạo API key, ai vừa kích hoạt phiên bản cấu hình, cảnh báo phát sinh lỗi.

