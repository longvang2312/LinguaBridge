# TÀI LIỆU THIẾT KẾ KIẾN TRÚC & HẠ TẦNG DÙNG CHUNG (SHARED SERVICES PLATFORM)
# APPINHOUSE.SHAREDSERVICES — PHÂN HỆ DỊCH VỤ DÙNG CHUNG ĐA DỰ ÁN

- **Dự án**: **AppInHouse.SharedServices** (Email, Telegram Messaging, Object Storage & Web Management Portal)
- **Tác giả**: Gemini Tech Lead & Solution Architect
- **Phiên bản**: 1.0.0 (Production-Ready Architecture)
- **Ngày cập nhật**: 2026-11-11
- **Trạng thái**: Sẵn sàng triển khai

---

## 1. TỔNG QUAN HỆ THỐNG & NGUYÊN TẮC THIẾT KẾ CỐT LÕI

Hệ thống **AppInHouse.SharedServices** là nền tảng hạ tầng dùng chung (Shared Infrastructure Platform) cho toàn bộ hệ sinh thái AppInHouse (Identity, LinguaBridge, ERP, CRM, và các website/mobile app của đối tác). Hệ thống hoàn toàn độc lập về mặt nghiệp vụ (Domain Agnostic) và vận hành theo các nguyên tắc chuẩn mực sau:

### 1.1. Sáu Nguyên Tắc Bảo Mật & Vận Hành Bắt Buộc (P0 & P1)

1. **Bảo mật Secret & Credential (P0)**:
   - Tuyệt đối không lưu plaintext mật khẩu SMTP, SendGrid API Key, Telegram Bot Token, S3 Secret Key vào database.
   - Sử dụng **ASP.NET Core Data Protection** với Key Ring để mã hóa (`EncryptedSecret`).
   - Trên Web Portal và API responses: Toàn bộ secret được che dấu dạng `••••••••`, chỉ cho phép nhập thay thế secret mới, không bao giờ trả lại plaintext secret về client.
   - Thao tác "Send Test" (Gửi thử nghiệm) kiểm tra trực tiếp trên payload request trong bộ nhớ RAM, không tự động lưu/kích hoạt cấu hình vào database chính.
2. **Cấu hình Động có Versioning & Rollback (P0)**:
   - Vòng đời cấu hình: `Draft` → `Validate / Send Test` → `Save New Version` → `Activate` → `Provider Runtime Reload`.
   - Nếu cấu hình mới bị lỗi, hệ thống cho phép **Rollback về Version hoạt động gần nhất** ngay lập tức mà không cần sửa code hay khởi động lại service.
3. **Quản lý Vòng đời API Key chuẩn RFC (P0)**:
   - API Key chỉ hiển thị đầy đủ 1 lần duy nhất khi tạo mới (Prefix `aih_live_...` + Secret).
   - Cơ sở dữ liệu chỉ lưu bản băm (HashedKey - SHA-256 / PBKDF2) để xác thực.
   - Hỗ trợ cơ chế luân chuyển khóa (Key Rotation Grace Period): Cho phép key cũ và key mới cùng hoạt động trong thời gian chuyển đổi.
4. **Phân tách Storage Profile & Stored File (P1)**:
   - Tách biệt `StorageProfile` (nơi lưu trữ: Local Disk / S3 / Cloudflare R2) khỏi `StoredFile` (metadata tệp tin).
   - Server quản lý Object Key (UUID ngẫu nhiên) để chống tấn công Path Traversal.
   - Kiểm tra định dạng nhị phân (Magic Bytes) để xác thực tệp tin, chống giả mạo MIME-type và chống bom giải nén (Decompression Bomb).
5. **Telegram Outbound First (P1)**:
   - Tập trung hoàn thiện chiều gửi thông báo (Outbound notifications: Text, Alert, MarkdownV2 safe escape, Photo/Document).
   - Tách biệt hoàn toàn với Webhook inbound để kiểm soát độ phức tạp.
6. **Phân định rõ 4 nhóm Logs độc lập (P1)**:
   - **Delivery Logs**: Nhật ký gửi email/tin nhắn, trạng thái thành công/thất bại, latency, provider response.
   - **Media Activity Logs**: Nhật ký upload, download, tạo thumbnail.
   - **Audit Logs**: Nhật ký ai thay đổi cấu hình, ai bấm test, ai tạo/revoke API key.
   - **Operational Logs**: Lỗi ngoại lệ hệ thống, health checks.

---

## 2. KIẾN TRÚC SOLUTION & PHÂN TÁCH DỰ ÁN

Tổ chức source code theo chuẩn **Clean Architecture** tại `repos/shared-services/`:

```text
repos/shared-services/
├── AppInHouse.SharedServices.sln                      # Solution tổng của Shared Services (.NET 10 LTS)
├── src/
│   ├── Core/
│   │   ├── AppInHouse.SharedServices.Domain/          # Thực thể: ChannelConfiguration, ApiKey, StoredFile, AuditLog
│   │   └── AppInHouse.SharedServices.Application/     # CQRS, Interfaces, DTOs, Provider Abstractions, Validators
│   │
│   ├── Infrastructure/
│   │   ├── AppInHouse.SharedServices.Persistence/     # EF Core DbContext, PostgreSQL Schemas, Versioning Repositories
│   │   └── AppInHouse.SharedServices.Infrastructure/  # DataProtection Secret Store, MailKit, Telegram.Bot, S3 Storage
│   │
│   ├── Presentation/
│   │   ├── AppInHouse.SharedServices.Api/             # RESTful API (Port 5030) - Enforce Auth & API Key Validation
│   │   └── AppInHouse.SharedServices.Web/             # Web Portal Quản Trị & Cấu Hình (Port 5031) - OIDC SSO & CSRF
│   │
│   └── Sdk/
│       └── AppInHouse.SharedServices.ClientSdk/       # C# Typed Client SDK cho Identity, LinguaBridge và ERP
│
└── tests/
    ├── AppInHouse.SharedServices.UnitTests/
    └── AppInHouse.SharedServices.IntegrationTests/
```

### Quy Tắc Dependencies (Dependency Rule)
- `Domain` không phụ thuộc bất kỳ project nào.
- `Application` chỉ phụ thuộc `Domain`. Định nghĩa các interface: `IEmailProvider`, `ITelegramProvider`, `IStorageProvider`, `ISecretProtector`, `IChannelConfigManager`.
- `Persistence` và `Infrastructure` triển khai các interface từ `Application`.
- `Api` và `Web` là hai presentation layer độc lập (Web gọi API qua HttpClient / ClientSdk).

---

## 3. THIẾT KẾ CƠ SỞ DỮ LIỆU CỐT LÕI (SCHEMA: `shared`)

```sql
CREATE SCHEMA shared;

-- 1. Quản lý cấu hình kênh (Dynamic Versioning & Encrypted Secret)
CREATE TABLE shared.channel_configurations (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    project_code VARCHAR(50) NOT NULL,          -- 'default', 'linguabridge', 'identity', 'shop'
    channel_type VARCHAR(20) NOT NULL,          -- 'Email', 'Telegram', 'Storage'
    provider_type VARCHAR(50) NOT NULL,         -- 'Smtp', 'SendGrid', 'TelegramBot', 'LocalDisk', 'S3'
    name VARCHAR(100) NOT NULL,
    version INT NOT NULL DEFAULT 1,
    status VARCHAR(20) NOT NULL,                -- 'Draft', 'Active', 'Archived'
    settings_json JSONB NOT NULL,               -- Non-sensitive (Host, Port, Bucket, Region)
    encrypted_secret TEXT,                      -- Protected by Data Protection Key Ring
    last_tested_at TIMESTAMPTZ,
    last_test_status VARCHAR(20),               -- 'Success', 'Failed', 'Untested'
    created_at TIMESTAMPTZ DEFAULT NOW(),
    updated_at TIMESTAMPTZ DEFAULT NOW(),
    CONSTRAINT uq_channel_version UNIQUE (project_code, channel_type, name, version)
);

-- 2. Quản lý API Key cho các dự án con
CREATE TABLE shared.api_keys (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    project_code VARCHAR(50) NOT NULL,
    name VARCHAR(100) NOT NULL,
    key_prefix VARCHAR(16) NOT NULL,            -- 'aih_live_xxxx'
    hashed_key VARCHAR(128) NOT NULL,           -- SHA-256 Hash
    scopes VARCHAR(255) NOT NULL,               -- 'email:send,telegram:send,storage:upload'
    is_revoked BOOLEAN DEFAULT FALSE,
    expires_at TIMESTAMPTZ,
    last_used_at TIMESTAMPTZ,
    created_at TIMESTAMPTZ DEFAULT NOW()
);

-- 3. Quản lý tệp tin (Storage Profiles & File Metadata)
CREATE TABLE shared.stored_files (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    project_code VARCHAR(50) NOT NULL,
    storage_profile_id UUID NOT NULL,
    storage_provider VARCHAR(50) NOT NULL,      -- 'LocalDisk', 'S3'
    object_key VARCHAR(500) NOT NULL,           -- Server-generated key (UUID-based)
    original_file_name VARCHAR(255) NOT NULL,
    mime_type VARCHAR(100) NOT NULL,
    file_size_bytes BIGINT NOT NULL,
    file_hash_sha256 VARCHAR(64) NOT NULL,
    is_public BOOLEAN DEFAULT FALSE,
    created_at TIMESTAMPTZ DEFAULT NOW()
);

-- 4. Bốn bảng nhật ký riêng biệt
CREATE TABLE shared.delivery_logs (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    project_code VARCHAR(50) NOT NULL,
    channel_type VARCHAR(20) NOT NULL,          -- 'Email', 'Telegram'
    recipient VARCHAR(255) NOT NULL,            -- Email address hoặc Chat ID
    status VARCHAR(20) NOT NULL,                -- 'Sent', 'Failed', 'Pending'
    provider_response TEXT,
    latency_ms INT,
    created_at TIMESTAMPTZ DEFAULT NOW()
);

CREATE TABLE shared.media_logs (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    project_code VARCHAR(50) NOT NULL,
    file_id UUID REFERENCES shared.stored_files(id),
    action VARCHAR(50) NOT NULL,                -- 'Upload', 'Download', 'Thumbnail', 'Delete'
    status VARCHAR(20) NOT NULL,
    error_message TEXT,
    created_at TIMESTAMPTZ DEFAULT NOW()
);

CREATE TABLE shared.audit_logs (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    actor VARCHAR(100) NOT NULL,                -- User email hoặc System
    action VARCHAR(50) NOT NULL,                -- 'CreateConfig', 'ActivateVersion', 'SendTest', 'RotateApiKey'
    resource_type VARCHAR(50) NOT NULL,
    resource_id VARCHAR(100) NOT NULL,
    details_json JSONB,
    created_at TIMESTAMPTZ DEFAULT NOW()
);
```

---

## 4. LỘ TRÌNH 4 MILESTONE TRIỂN KHAI

1. **Milestone 1 (TASK-032)**: Khởi tạo Solution, Clean Architecture, Database Migrations, Data Protection Secret Store, Configuration Versioning Engine & Web Portal Shell.
2. **Milestone 2 (TASK-033, TASK-034)**: Phân hệ Email (Smtp, SendGrid, Mock, Template HTML, Send Test) & Phân hệ Telegram (Bot Sender, MarkdownV2 safe escape, Send Test).
3. **Milestone 3 (TASK-035)**: Phân hệ Storage (Local Disk, S3/MinIO, Image Processor, Magic Bytes, Media Browser).
4. **Milestone 4 (TASK-036)**: Quản lý API Key (Hash, Scopes, Rotation), Client SDK (.NET), Tích hợp Docker Compose (API 5030 & Web Portal 5031).

