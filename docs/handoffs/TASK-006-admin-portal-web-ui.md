# TASK-006: Admin Web Portal UI (Skoruba-Style), EF Migrations & Final Hardening

- **Người giao (Tech Lead)**: Gemini Orchestrator
- **Người thực hiện (Implementer)**: Claude Code
- **Trạng thái**: `PENDING`
- **Ngày tạo**: 2026-10-28
- **Tài liệu tham chiếu**: [docs/architecture/system_design.md](../architecture/system_design.md)

---

## 1. Mục Tiêu Kỹ Thuật

Xây dựng giao diện web quản trị tập trung **Admin Portal Web UI** (theo chuẩn phong cách Skoruba IdentityServer4.Admin) tại `AppInHouse.Identity.Admin.Web` và hoàn thiện tầng lưu trữ SQL Server:
1. **Domain Improvement**:
   - Bổ sung phương thức `UpdateDetails(string displayName, string module)` vào thực thể `Permission` tại `Domain/Entities/Permission.cs` để đóng gói sạch sẽ (loại bỏ workaround `Entry.Property.CurrentValue`).
2. **Admin Portal UI (ASP.NET Core MVC + Bootstrap 5)**:
   - **Dashboard**: Thống kê số lượng Projects, Clients, Users, Roles, Permissions và các sự kiện bảo mật / audit gần đây.
   - **Projects**: Danh sách dự án (phân trang, tìm kiếm), Tạo mới dự án, Sửa dự án, Khóa / Kích hoạt dự án.
   - **Clients**: Cấu hình client (Web, SPA, Mobile, Machine), cấu hình Redirect URIs, Allowed Scopes, Sinh / Xoay vòng Client Secret (hiển thị 1 lần an toàn).
   - **Users**: Quản lý tài khoản, tạo mới, khóa / mở khóa, đổi mật khẩu, gán dự án (`ProjectUserAssignment`).
   - **Roles & Permissions (RBAC Studio)**:
     - Danh sách Role & Permission theo từng module (`Users`, `Roles`, `Clients`, `Projects`...).
     - Ma trận phân quyền trực quan: Checkbox gán Permissions cho Role.
     - Gán Role và gán quyền trực tiếp (`IsRevoked`) cho User theo dự án.
   - **Audit Trail Viewer**: Tra cứu nhật ký kiểm toán, lọc theo Actor, Project, Entity, Thời gian, có modal hiển thị Before/After JSON.
   - Bảo mật UI: Chống CSRF (`[ValidateAntiForgeryToken]`), xác thực đầu vào, thông báo toast / alert thân thiện.
3. **EF Core Migrations & SQL Server Configuration**:
   - Tạo bộ Migrations cho:
     - `AppIdentityDbContext`
     - `AppAuthDbContext`
     - `AppAuditDbContext`
   - Cấu hình Connection String SQL Server chuẩn trong `appsettings.json` (LocalDB / SQL Server Express / Docker).
4. **Integration Tests**:
   - Viết Integration tests cho `Admin.Web` kiểm tra: Dashboard render 200 OK, Project Explorer, Role matrix POST có token CSRF hợp lệ.

---

## 2. Phạm Vi File Được Phép Tạo / Sửa (File Scope & Whitelist)

> **LƯU Ý DÀNH CHO CLAUDE CODE**:
> Chỉ được phép tạo và chỉnh sửa các file thuộc phạm vi dưới đây.

- [ ] `repos/identity/src/Core/AppInHouse.Identity.Domain/**/*`
- [ ] `repos/identity/src/Core/AppInHouse.Identity.Application/**/*`
- [ ] `repos/identity/src/Infrastructure/AppInHouse.Identity.Infrastructure.Persistence/**/*`
- [ ] `repos/identity/src/Presentation/AppInHouse.Identity.Admin.Web/**/*`
- [ ] `repos/identity/tests/AppInHouse.Identity.IntegrationTests/**/*`
- [ ] `repos/identity/tests/AppInHouse.Identity.UnitTests/**/*`

---

## 3. Tiêu Chí Nghiệm Thu (Acceptance Criteria)

- [ ] Lệnh `dotnet build AppInHouse.sln` chạy thành công `0 Error(s), 0 Warning(s)`.
- [ ] Lệnh `dotnet test AppInHouse.sln` pass 100% (cả UnitTests và IntegrationTests).
- [ ] Giao diện `Admin.Web` tải trang Dashboard, Projects, Clients, Users, Roles, Audit Logs mượt mà.
- [ ] Thao tác thay đổi quyền trên UI tự động kích hoạt `IPermissionInvalidationService` và ghi `AuditLog`.
- [ ] Bộ file EF Core Migrations được sinh đầy đủ sẵn sàng migrate lên SQL Server.
- [ ] Điền báo cáo đầy đủ vào Mục 5 và đổi trạng thái sang `READY_FOR_REVIEW`.

---

## 4. Báo Cáo Triển Khai (Claude Code điền khi hoàn thành)

### a. Danh sách file đã tạo / chỉnh sửa:
- 

### b. Kết quả kiểm thử thực tế (Output terminal):
```text
# Lệnh: dotnet build AppInHouse.sln

# Lệnh: dotnet test AppInHouse.sln

```

### c. Đánh giá rủi ro hoặc ghi chú kỹ thuật cho Tech Lead:
- 
