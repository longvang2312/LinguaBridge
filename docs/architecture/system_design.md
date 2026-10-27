# TÀI LIỆU THIẾT KẾ KIẾN TRÚC CHI TIẾT
# CENTRALIZED IDENTITY SERVER + ADMIN PORTAL (NET 8)

- **Dự án**: AppInHouse Identity Platform
- **Tác giả**: Gemini Orchestrator & Solution Architect
- **Phiên bản**: 1.0.0
- **Ngày**: 2026-10-27

---

## 1. Mục Tiêu & Phạm Vi Hệ Thống

Hệ thống cung cấp nền tảng Identity Provider (IdP) tập trung và Admin Portal theo kiến trúc hiện đại, đáp ứng:
1. **Multi-Project / Multi-Tenant Isolation**: Một máy chủ cấp phát quyền cho nhiều dự án độc lập (ERP, AI ChatBot, Mobile App, Internal API).
2. **OpenID Connect & OAuth 2.0 Engine**: Cấp phát Token chuẩn quốc tế, hỗ trợ Authorization Code Flow + PKCE và Client Credentials Flow.
3. **Dynamic RBAC & Permission-Based Authorization**: Quản lý phân quyền động lưu trong Database, cập nhật tức thì qua Admin Portal không cần sửa source code hay restart ứng dụng.
4. **Instant Revocation & Cache Invalidation**: Cơ chế vô hiệu hóa quyền và token theo phiên bản (Version-based Invalidation), độ trễ dưới 1 giây.
5. **Separation of Concerns**: Tuân thủ triệt để Clean Architecture, SOLID, Least Privilege, Zero Trust.

---

## 2. Lựa Chọn Công Nghệ Nền Tảng (ADR - Architectural Decision Record)

- **Ngôn ngữ & Runtime**: C# 12 / **.NET 8 LTS**.
- **OIDC/OAuth 2.0 Core**: **OpenIddict Core 5.x** trên nền .NET 8.
  * *Lý do*: Kế thừa toàn bộ triết lý của IdentityServer4 nhưng hoàn toàn Open-source (Apache 2.0), không có rủi ro bản quyền như Duende, bảo mật tuyệt đối, hỗ trợ sâu với ASP.NET Core Identity và EF Core 8.
- **Identity & Credential Store**: **ASP.NET Core Identity** (quản lý mật khẩu, khóa tài khoản, 2FA/MFA, email confirmation, security stamp).
- **ORM & Database**: **Entity Framework Core 8** với **SQL Server** làm database chính thức.
- **Admin Portal UI**: **ASP.NET Core MVC + Bootstrap 5 + Razor Pages** (theo kiến trúc Skoruba Admin), đóng gói thành một ứng dụng quản trị tập trung.
- **Logging & Auditing**: **Serilog** ghi log có cấu trúc (Structured Logging) và cơ chế tự động ghi nhật ký kiểm toán (Audit Trail) cho mọi thao tác nhạy cảm.

---

## 3. Cấu Trúc Multi-Repo & Solution Layout

Hệ thống được tổ chức theo mô hình **Multi-Repository Workspace** có Solution tổng (`AppInHouse.sln`) tại thư mục gốc để mở đồng thời tất cả các repository, trong khi mỗi service/repo con hoàn toàn độc lập và có solution riêng.

```text
AppInHouse/ (Workspace Root)
├── AppInHouse.sln                                      # Solution master mở tất cả các service/repo
├── AGENTS.md                                           # Quy chuẩn phối hợp Multi-Agent
├── CLAUDE.md                                           # Hướng dẫn dành cho Claude Code
├── docs/                                               # Tài liệu kiến trúc và handoff chung
│   ├── architecture/
│   └── handoffs/
│
└── repos/                                              # Danh mục các repository độc lập
    │
    ├── AppInHouse.Identity/                            # [REPO HIỆN TẠI] Centralized Identity Server & Admin Portal
    │   ├── AppInHouse.Identity.sln                     # Solution riêng của Identity Platform
    │   ├── Directory.Build.props                       # Cấu hình build chung của Identity Platform (.NET 8)
    │   ├── src/
    │   │   ├── Core/
    │   │   │   ├── AppInHouse.Identity.Domain/
    │   │   │   ├── AppInHouse.Identity.Application/
    │   │   │   └── AppInHouse.Identity.SharedKernel/
    │   │   ├── Infrastructure/
    │   │   │   ├── AppInHouse.Identity.Infrastructure.Persistence/
    │   │   │   ├── AppInHouse.Identity.Infrastructure.Identity/
    │   │   │   ├── AppInHouse.Identity.Infrastructure.Caching/
    │   │   │   └── AppInHouse.Identity.Infrastructure.Logging/
    │   │   ├── Presentation/
    │   │   │   ├── AppInHouse.Identity.STS/
    │   │   │   ├── AppInHouse.Identity.Admin.Api/
    │   │   │   └── AppInHouse.Identity.Admin.Web/
    │   │   └── Sdk/
    │   │       └── AppInHouse.Identity.ClientSdk/
    │   └── tests/
    │       ├── AppInHouse.Identity.UnitTests/
    │       └── AppInHouse.Identity.IntegrationTests/
    │
    ├── (Tương lai: AppInHouse.ERP/)                    # Các repo dịch vụ tiếp theo
    └── (Tương lai: AppInHouse.AIChat/)
```

---

## 4. Chi Tiết Thiết Kế Dynamic RBAC & Cache Invalidation

### 4.1. Quy tắc Xác thực Quyền tại API
Khi một API yêu cầu `[HasPermission("users.view")]`:
1. **Trích xuất Context**:
   - `UserId`: lấy từ claim `sub` trong Access Token.
   - `ProjectCode`: trích xuất từ HTTP Header `X-Project-Code` hoặc URL route `/api/{projectCode}/...`.
2. **Kiểm tra Tính Hợp lệ**:
   - Truy vấn Cache theo key: `Perms:{UserId}:{ProjectCode}`.
   - So sánh `CacheVersion` trong header/RAM với bảng `PermissionCacheVersions`.
3. **Phân giải Quyền (Resolution)**:
   - Nếu Cache miss / hết hạn: Truy vấn database, tính toán hợp của mọi role của user trong dự án đó, cộng quyền trực tiếp, trừ quyền bị thu hồi.
   - Lưu kết quả vào Cache với TTL = 5 phút (nhưng bị vô hiệu hóa ngay lập tức nếu Admin cập nhật).

### 4.2. Cơ chế Thu Hồi (Invalidation Pipeline)
Khi Admin bấm "Gỡ quyền" hoặc "Khóa tài khoản":
1. Admin API cập nhật Database trong Transaction.
2. Tăng số `VersionNumber` trong `PermissionCacheVersions`.
3. Phát tín hiệu `PermissionInvalidatedEvent` (Local Event / Redis PubSub).
4. Các node API nhận tín hiệu lập tức xóa Key Cache của user/project đó. Lần gọi API tiếp theo trong SLA < 1 giây sẽ bị chặn ngay lập tức (`403 Forbidden`).

---

## 5. Quy Trình Handoff Kỹ Thuật

Mọi task code giao cho Claude Code sẽ được lập tại thư mục `docs/handoffs/TASK-xxx.md`.
Gemini Tech Lead chịu trách nhiệm review, kiểm tra chất lượng code và nghiệm thu trước khi bước sang task tiếp theo.
