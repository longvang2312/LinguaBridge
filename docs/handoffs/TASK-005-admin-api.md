# TASK-005: Admin REST API (OpenAPI/Swagger, CRUD Quản Trị & Audit Trail)

- **Người giao (Tech Lead)**: Gemini Orchestrator
- **Người thực hiện (Implementer)**: Claude Code
- **Trạng thái**: `PENDING`
- **Ngày tạo**: 2026-10-27
- **Tài liệu tham chiếu**: [docs/architecture/system_design.md](../architecture/system_design.md)

---

## 1. Mục Tiêu Kỹ Thuật

Xây dựng bộ **Admin REST API** hoàn chỉnh có bảo vệ bảo mật, phân trang, lọc, validation và ghi nhật ký kiểm toán (Audit Trail) tại project `AppInHouse.Identity.Admin.Api`:
1. **API Endpoints (Versioning v1 & Swagger OpenAPI)**:
   - **Projects**: `GET /api/v1/projects`, `GET /api/v1/projects/{id}`, `POST /api/v1/projects`, `PUT /api/v1/projects/{id}`, `POST /api/v1/projects/{id}/status` (Khóa/Kích hoạt).
   - **OpenIddict Clients**: `GET /api/v1/clients`, `POST /api/v1/clients` (Web/SPA/Mobile/Machine), `PUT /api/v1/clients/{id}`, `POST /api/v1/clients/{id}/rotate-secret` (sinh secret mới, thu hồi secret cũ).
   - **Users**: `GET /api/v1/users`, `POST /api/v1/users`, `GET /api/v1/users/{id}`, `PUT /api/v1/users/{id}`, `POST /api/v1/users/{id}/lock` & `/unlock`, `POST /api/v1/users/{id}/reset-password`.
   - **Roles & Permissions**:
     - `GET /api/v1/permissions`, `POST /api/v1/permissions`, `PUT /api/v1/permissions/{id}`.
     - `GET /api/v1/roles`, `POST /api/v1/roles`, `PUT /api/v1/roles/{id}`.
     - `POST /api/v1/roles/{id}/permissions`: Cập nhật danh sách permissions cho role -> **Bắt buộc kích hoạt `IPermissionInvalidationService.InvalidateAllAsync()`**.
   - **Project & User Assignments**:
     - `POST /api/v1/projects/{projectId}/users/assign`: Gán user vào project (kèm vai trò `IsProjectAdmin`).
     - `POST /api/v1/projects/{projectId}/users/{userId}/roles`: Gán role cho user trong dự án -> **Kích hoạt `InvalidateUserAsync(userId)`**.
     - `POST /api/v1/projects/{projectId}/users/{userId}/permissions`: Gán / thu hồi quyền trực tiếp (`IsRevoked`) -> **Kích hoạt `InvalidateUserAsync(userId)`**.
   - **Audit Logs**: `GET /api/v1/audit-logs`: Tra cứu nhật ký kiểm toán có phân trang, lọc theo Actor, Project, Entity, Action và khoảng thời gian.
2. **Audit Logging Interceptor / Service**:
   - Tự động ghi bản ghi `AuditLog` vào `AppAuditDbContext` mỗi khi có thao tác Create/Update/Delete/StatusChange nhạy cảm.
   - Ghi nhận `UserId`, `ProjectId`, `Action`, `EntityName`, `EntityId`, `OldValuesJson`, `NewValuesJson`, `IpAddress`, `Timestamp`.
3. **Bảo Mật & Validation**:
   - Least privilege, Deny-by-default.
   - Chống IDOR: Xác thực quyền của người gọi (chỉ GlobalAdmin hoặc ProjectAdmin của đúng dự án mới được sửa dự án đó).
   - Không log mật khẩu hoặc client secret thô.
4. **Integration Tests**:
   - Viết trọn bộ integration tests cho Admin API bằng `WebApplicationFactory`.

---

## 2. Phạm Vi File Được Phép Tạo / Sửa (File Scope & Whitelist)

> **LƯU Ý DÀNH CHO CLAUDE CODE**:
> Chỉ được phép tạo và chỉnh sửa các file thuộc phạm vi dưới đây.

- [ ] `repos/identity/src/Core/AppInHouse.Identity.Application/**/*`
- [ ] `repos/identity/src/Infrastructure/AppInHouse.Identity.Infrastructure.Persistence/**/*`
- [ ] `repos/identity/src/Presentation/AppInHouse.Identity.Admin.Api/**/*`
- [ ] `repos/identity/tests/AppInHouse.Identity.IntegrationTests/**/*`
- [ ] `repos/identity/tests/AppInHouse.Identity.UnitTests/**/*`

---

## 3. Tiêu Chí Nghiệm Thu (Acceptance Criteria)

- [ ] Lệnh `dotnet build AppInHouse.sln` chạy thành công `0 Error(s), 0 Warning(s)`.
- [ ] Lệnh `dotnet test AppInHouse.sln` pass 100% bao gồm cả integration tests mới của Admin API.
- [ ] Swagger UI tại `Admin.Api` hiển thị đầy đủ tài liệu các endpoint v1.
- [ ] Tạo dự án mới và client mới qua API thành công không cần sửa code.
- [ ] Thao tác gán role/permission qua API tự động kích hoạt cache invalidation và ghi audit log.
- [ ] Chống IDOR và chống leo quyền (privilege escalation) được kiểm chứng qua test.
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
