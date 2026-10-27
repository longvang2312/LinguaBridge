# TASK-004: Dynamic Authorization Engine, Client SDK & Cache Invalidation

- **Người giao (Tech Lead)**: Gemini Orchestrator
- **Người thực hiện (Implementer)**: Claude Code
- **Trạng thái**: `PENDING`
- **Ngày tạo**: 2026-10-27
- **Tài liệu tham chiếu**: [docs/architecture/system_design.md](../architecture/system_design.md)

---

## 1. Mục Tiêu Kỹ Thuật (Yêu Cầu Trọng Tâm Nhất Của Hệ Thống)

Hiện thực hóa toàn bộ **Cơ chế Phân quyền Động (Dynamic Authorization)** và **Thu hồi quyền tức thì (Instant Revocation Pipeline)**:
1. **Application Layer**:
   - `IPermissionService`: Tính toán quyền thực tế của `(UserId, ProjectId)` = `(Roles của User trong Project -> Permissions) + (Direct Permissions) - (Revoked Permissions)`. Hỗ trợ cả trường hợp `IsProjectAdmin` (toàn quyền trong dự án) hoặc `GlobalAdmin`.
   - `IPermissionCacheService`: Lưu cache quyền kèm `VersionNumber`.
   - `IPermissionInvalidationService`: Kích hoạt tăng `VersionNumber` trong `PermissionCacheVersions` và xóa cache khi Admin cập nhật quyền/role.
2. **Infrastructure Caching**:
   - Hiện thực hóa `PermissionCacheService` sử dụng `IMemoryCache` (với kiến trúc sẵn sàng mở rộng sang Redis).
   - Cơ chế fail-safe: Nếu cache hoặc nguồn dữ liệu lỗi -> **Deny-by-default** (tuyệt đối không mở cửa khi lỗi).
3. **Client SDK (`AppInHouse.Identity.ClientSdk`)**:
   - Đóng gói thành thư viện nhẹ dùng chung cho mọi API client con (ERP, AIChat, Mobile API...).
   - `IProjectContextResolver` & `ProjectContextMiddleware`: Trích xuất Project Code từ Header (`X-Project-Code`) hoặc Route data.
   - Attribute `[HasPermission("users.view")]` và `DynamicPermissionPolicyProvider`.
   - `ProjectAwarePermissionHandler`: Authorization handler kiểm tra quyền thực tế tại thời điểm request, đối chiếu phiên bản cache, chặn IDOR và truy cập chéo dự án.
4. **Integration & Automated Tests**:
   - Viết trọn bộ test chứng minh kịch bản nghiệm thu cốt lõi:
     - User có quyền trong Project A -> Gọi endpoint Project A thành công (`200 OK`).
     - User dùng token đó gọi Project B (không được gán) -> Bị chặn (`403 Forbidden`).
     - Admin thu hồi quyền / gỡ role -> Cache bị invalidate ngay lập tức -> Request tiếp theo bị chặn (`403 Forbidden`) dù token cũ vẫn còn hạn.
     - Direct Revocation: User có role chứa quyền X nhưng có gán trực tiếp `IsRevoked = true` -> Bị chặn.

---

## 2. Phạm Vi File Được Phép Tạo / Sửa (File Scope & Whitelist)

> **LƯU Ý DÀNH CHO CLAUDE CODE**:
> Chỉ được phép tạo và chỉnh sửa các file thuộc phạm vi dưới đây.

- [ ] `repos/identity/src/Core/AppInHouse.Identity.Application/**/*`
- [ ] `repos/identity/src/Infrastructure/AppInHouse.Identity.Infrastructure.Caching/**/*`
- [ ] `repos/identity/src/Infrastructure/AppInHouse.Identity.Infrastructure.Persistence/**/*`
- [ ] `repos/identity/src/Sdk/AppInHouse.Identity.ClientSdk/**/*`
- [ ] `repos/identity/tests/AppInHouse.Identity.UnitTests/**/*`
- [ ] `repos/identity/tests/AppInHouse.Identity.IntegrationTests/**/*`

---

## 3. Hướng Dẫn Kỹ Thuật Chi Tiết

### 3.1. Phân Giải Quyền Thực Tế (`Application/Services/PermissionService.cs`)
Quy tắc tính toán tập quyền hiệu lực (Effective Permissions) của một `UserId` trong `ProjectCode`:
1. Kiểm tra User: Nếu `ApplicationUser.IsActive == false` hoặc `LockoutEnd > DateTimeOffset.UtcNow` -> Trả về danh sách rỗng (vô hiệu hóa hoàn toàn).
2. Lấy `Project`: Tìm project theo `ProjectCode`. Nếu không tìm thấy hoặc `IsActive == false` hoặc `IsDeleted == true` -> Trả về rỗng.
3. Kiểm tra User trong Project (`ProjectUserAssignments`):
   - Nếu `IsBlocked == true` -> Trả về rỗng (bị khóa trong riêng dự án đó).
   - Nếu `IsProjectAdmin == true` -> Trả về toàn bộ Permissions của Project đó + Global Permissions.
4. Tập hợp từ Roles:
   - Lấy tất cả `RoleId` mà User được gán trong Project (`UserRoleProjectAssignments`).
   - Lấy tất cả `Permission` thuộc các Role đó (`RolePermissions` join `Permissions` where `IsActive == true`).
5. Gộp quyền trực tiếp (`UserPermissionAssignments`):
   - Thêm các quyền gán trực tiếp (`IsRevoked == false`).
   - Loại trừ các quyền bị chặn trực tiếp (`IsRevoked == true`).
6. Trả về `HashSet<string>` chứa danh sách `PermissionCode`.

### 3.2. Cache Versioning & Invalidation (`Infrastructure.Caching`)
- Cache Key: `Perms:{UserId}:{ProjectCode}`
- Lưu trữ: `CachedPermissionsModel { long Version, HashSet<string> Permissions }`
- Khi kiểm tra:
  - Lấy `CurrentVersion` từ bảng `PermissionCacheVersions` (hoặc cached version counter).
  - Nếu `CachedVersion != CurrentVersion` -> Invalidate cache, query lại DB, nạp cache mới.
- Khi Admin thay đổi phân quyền (Sửa Role, Gán quyền, Khóa User...):
  - Gọi `IPermissionInvalidationService.InvalidateUserAsync(userId)` hoặc `InvalidateProjectAsync(projectCode)`.
  - Tăng `VersionNumber` trong database.
  - Xóa cache entry tương ứng trong `IMemoryCache`.

### 3.3. Client SDK (`src/Sdk/AppInHouse.Identity.ClientSdk/`)
- Cung cấp extension method đăng ký DI siêu đơn giản cho mọi API:
  ```csharp
  services.AddAppInHouseAuthorization(options => { ... });
  ```
- `HasPermissionAttribute`:
  ```csharp
  [AttributeUsage(AttributeTargets.Method | AttributeTargets.Class, AllowMultiple = true)]
  public class HasPermissionAttribute : AuthorizeAttribute
  {
      public HasPermissionAttribute(string permission) : base(policy: $"PERM:{permission}") {}
  }
  ```
- `ProjectAwarePermissionHandler`:
  - Trích xuất `UserId` từ Claims (`ClaimTypes.NameIdentifier` hoặc `sub`).
  - Trích xuất `ProjectCode` từ `IProjectContextResolver`.
  - Gọi `IPermissionService.HasPermissionAsync(userId, projectCode, requiredPermission)`.
  - Nếu thỏa mãn: `context.Succeed(requirement)`.
  - Nếu không thỏa mãn hoặc có lỗi ngoại lệ: `context.Fail()`.

### 3.4. Kiểm Thử Nghiệm Thu (Integration Tests)
- Tạo một Sample Controller giả lập API được bảo vệ bởi `[HasPermission("orders.create")]` trong test project.
- Test 1: User gán role `OrderManager` (có `orders.create`) trong `Project A` -> Gọi thành công `200 OK`.
- Test 2: User gọi với header `X-Project-Code: Project B` (chưa được gán) -> Nhận `403 Forbidden` (Chống IDOR & cách ly dự án).
- Test 3: Admin gọi `InvalidateUser` hoặc gỡ permission khỏi role -> Lần gọi tiếp theo của user với cùng access token đó lập tức nhận `403 Forbidden` (< 1 giây).
- Test 4: Gán trực tiếp `IsRevoked = true` cho `orders.create` -> Nhận `403 Forbidden` dù Role vẫn có quyền đó.

---

## 4. Tiêu Chí Nghiệm Thu (Acceptance Criteria)

- [ ] Lệnh `dotnet build AppInHouse.sln` chạy thành công `0 Error(s), 0 Warning(s)`.
- [ ] Lệnh `dotnet test AppInHouse.sln` pass 100% bao gồm toàn bộ các kịch bản phân quyền động mới.
- [ ] Chứng minh được việc thu hồi quyền ngay lập tức khi Admin cập nhật mà không cần cấp lại token.
- [ ] Chứng minh được tính năng cách ly dự án (Project Isolation & Anti-Cross-Project).
- [ ] ClientSdk độc lập, đóng gói sạch sẽ, sẵn sàng tích hợp cho các API con.
- [ ] Điền báo cáo đầy đủ vào Mục 5 và đổi trạng thái sang `READY_FOR_REVIEW`.

---

## 5. Báo Cáo Triển Khai (Claude Code điền khi hoàn thành)

### a. Danh sách file đã tạo / chỉnh sửa:
- 

### b. Kết quả kiểm thử thực tế (Output terminal):
```text
# Lệnh: dotnet build AppInHouse.sln

# Lệnh: dotnet test AppInHouse.sln

```

### c. Đánh giá rủi ro hoặc ghi chú kỹ thuật cho Tech Lead:
- 
