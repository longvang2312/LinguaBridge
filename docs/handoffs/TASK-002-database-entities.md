# TASK-002: Thiết Kế Domain Entities & EF Core DbContexts Phân Quyền Động

- **Người giao (Tech Lead)**: Gemini Orchestrator
- **Người thực hiện (Implementer)**: Claude Code
- **Trạng thái**: `APPROVED`
- **Ngày hoàn thành**: 2026-10-27
- **Tài liệu tham chiếu**: [docs/architecture/system_design.md](../architecture/system_design.md)

---

## 1. Mục Tiêu Kỹ Thuật

Triển khai tầng **Domain Entities** và **Infrastructure Persistence (EF Core)** theo thiết kế ERD logic đã được phê duyệt tại Phần 5 và 6 của tài liệu kiến trúc:
1. Tạo các thực thể nghiệp vụ (Domain Entities) cho Multi-Project, RBAC, Dynamic Permission, và Audit Trail.
2. Mở rộng `ApplicationUser` và `ApplicationRole` trong `Infrastructure.Identity`.
3. Cài đặt các EF Core `DbContext` và `IEntityTypeConfiguration` với đầy đủ ràng buộc: Khóa chính (PK), Khóa ngoại (FK), Unique Constraint, và Index tối ưu truy vấn.
4. Xóa bỏ các file placeholder mặc định (`Class1.cs` trong các project đã đụng tới).
5. Viết unit test kiểm tra tính toàn vẹn của mô hình thực thể và cấu hình DbContext.

---

## 2. Phạm Vi File Được Phép Tạo / Sửa (File Scope & Whitelist)

> **LƯU Ý DÀNH CHO CLAUDE CODE**:
> Chỉ được phép tạo và chỉnh sửa các file thuộc phạm vi dưới đây.

- [ ] `repos/identity/src/Core/AppInHouse.Identity.Domain/**/*`
- [ ] `repos/identity/src/Core/AppInHouse.Identity.Application/**/*`
- [ ] `repos/identity/src/Infrastructure/AppInHouse.Identity.Infrastructure.Identity/**/*`
- [ ] `repos/identity/src/Infrastructure/AppInHouse.Identity.Infrastructure.Persistence/**/*`
- [ ] `repos/identity/tests/AppInHouse.Identity.UnitTests/**/*`

---

## 3. Yêu Cầu Thiết Kế Chi Tiết

### 3.1. Domain Entities (`repos/identity/src/Core/AppInHouse.Identity.Domain/Entities/`)
- **`Project`**:
  - `Id` (Guid, PK)
  - `ProjectCode` (string, max 50, Unique, không rỗng - vd: `ERP`, `AIChatBot`, `MobileApp`)
  - `Name` (string, max 100)
  - `Description` (string?, max 500)
  - `IsActive` (bool, default true)
  - `IsDeleted` (bool, default false)
  - `CreatedAt` (DateTime, UTC)
  - `ConcurrencyToken` (string)
- **`Permission`**:
  - `Id` (Guid, PK)
  - `ProjectId` (Guid?, FK to Project, `null` = Global Permission)
  - `PermissionCode` (string, max 100, Unique, vd: `users.view`, `roles.manage`)
  - `DisplayName` (string, max 150)
  - `Module` (string, max 50, vd: `Users`, `Roles`, `Clients`, `Projects`)
  - `IsGlobal` (bool)
  - `IsActive` (bool, default true)
  - `CreatedAt` (DateTime, UTC)
- **`RolePermission`**:
  - `Id` (Guid, PK)
  - `RoleId` (string, FK to AspNetRoles)
  - `PermissionId` (Guid, FK to Permission)
  - `GrantedAt` (DateTime, UTC)
- **`ProjectUserAssignment`**:
  - `Id` (Guid, PK)
  - `ProjectId` (Guid, FK to Project)
  - `UserId` (string, FK to AspNetUsers)
  - `IsProjectAdmin` (bool, default false)
  - `IsBlocked` (bool, default false)
  - `AssignedAt` (DateTime, UTC)
  - *Ràng buộc*: Unique cặp `(ProjectId, UserId)`
- **`UserRoleProjectAssignment`**:
  - `Id` (Guid, PK)
  - `UserId` (string, FK to AspNetUsers)
  - `RoleId` (string, FK to AspNetRoles)
  - `ProjectId` (Guid, FK to Project)
  - `AssignedAt` (DateTime, UTC)
  - *Ràng buộc*: Unique bộ ba `(UserId, RoleId, ProjectId)`
- **`UserPermissionAssignment`** (Gán quyền trực tiếp / thu hồi quyền cụ thể):
  - `Id` (Guid, PK)
  - `UserId` (string)
  - `PermissionId` (Guid)
  - `ProjectId` (Guid)
  - `IsRevoked` (bool, true nếu dùng để chặn quyền dù role có quyền đó)
  - `AssignedAt` (DateTime, UTC)
- **`AuditLog`**:
  - `Id` (Guid, PK)
  - `UserId` (string?, Actor)
  - `ProjectId` (Guid?)
  - `Action` (string, max 50: `CREATE`, `UPDATE`, `REVOKE`, `LOGIN`...)
  - `EntityName` (string, max 100: `Role`, `Client`, `Permission`, `User`...)
  - `EntityId` (string, max 100)
  - `OldValuesJson` (string?)
  - `NewValuesJson` (string?)
  - `IpAddress` (string?, max 50)
  - `Timestamp` (DateTime, UTC)
- **`PermissionCacheVersion`** (Phục vụ Cache Invalidation tức thì):
  - `ContextKey` (string, PK, max 150 - vd: `User:{id}`, `Project:{code}`)
  - `VersionNumber` (long, tăng lên khi có bất kỳ thay đổi quyền nào)
  - `LastInvalidatedAt` (DateTime, UTC)
- **`ConfigurationVersion`**:
  - `Id` (Guid, PK)
  - `VersionNumber` (int)
  - `Description` (string, max 255)
  - `AppliedAt` (DateTime, UTC)

### 3.2. Identity Models (`repos/identity/src/Infrastructure/AppInHouse.Identity.Infrastructure.Identity/Models/`)
- `ApplicationUser : IdentityUser` (kèm các trường bổ sung: `FullName`, `IsActive`, `CreatedAt`, `LastLoginAt`)
- `ApplicationRole : IdentityRole` (kèm `Description`, `CreatedAt`)

### 3.3. EF Core DbContexts (`repos/identity/src/Infrastructure/AppInHouse.Identity.Infrastructure.Persistence/Contexts/`)
Thiết lập DbContext theo chuẩn separation of concerns:
1. **`AppIdentityDbContext`**: Kế thừa `IdentityDbContext<ApplicationUser, ApplicationRole, string>`
2. **`AppAuthDbContext`**: Quản lý các bảng phân quyền `Projects`, `Permissions`, `RolePermissions`, `ProjectUserAssignments`, `UserRoleProjectAssignments`, `UserPermissionAssignments`, `PermissionCacheVersions`.
3. **`AppAuditDbContext`**: Quản lý bảng `AuditLogs`, `ConfigurationVersions`.
*(Lưu ý: Bạn có thể tạo 1 class `AppDbContext` tổng hợp kế thừa hoặc kết hợp nếu cần cho đơn vị công việc Transaction, hoặc giữ 3 DbContext riêng biệt rõ ràng).*

### 3.4. Fluent Configurations (`.../Persistence/Configurations/`)
- Cấu hình từng Entity bằng class riêng thực thi `IEntityTypeConfiguration<T>`.
- Đảm bảo index tối ưu cho:
  - `Permissions.PermissionCode` (Unique)
  - `Projects.ProjectCode` (Unique)
  - `ProjectUserAssignments (ProjectId, UserId)` (Unique Index)
  - `UserRoleProjectAssignments (UserId, ProjectId)` (Index)
  - `AuditLogs.Timestamp` và `AuditLogs.UserId` (Index)

### 3.5. Unit Tests (`repos/identity/tests/AppInHouse.Identity.UnitTests/`)
- Viết test kiểm tra khởi tạo Entity invariants.
- Viết test kiểm tra cấu hình DbContext in-memory (`Microsoft.EntityFrameworkCore.InMemory`) để chứng minh các quan hệ, navigation properties và validation hoạt động chính xác.

---

## 4. Tiêu Chí Nghiệm Thu (Acceptance Criteria)

- [ ] Lệnh `dotnet build AppInHouse.sln` chạy thành công `0 Error(s), 0 Warning(s)`.
- [ ] Lệnh `dotnet test AppInHouse.sln` chạy pass 100% các unit test mới viết.
- [ ] Đầy đủ các Entities, DbContexts, Fluent Configurations theo danh sách thiết kế.
- [ ] Đã dọn dẹp các file rác `Class1.cs` / `UnitTest1.cs` trong các project liên quan.
- [ ] Điền báo cáo đầy đủ vào Mục 5 dưới đây và đổi trạng thái sang `READY_FOR_REVIEW`.

---

## 5. Báo Cáo Triển Khai (Claude Code điền khi hoàn thành)

### a. Danh sách file đã tạo / chỉnh sửa:
- **Domain**: `Common/Guard.cs`; `Entities/` gồm `Project`, `Permission`, `RolePermission`, `ProjectUserAssignment`, `UserRoleProjectAssignment`, `UserPermissionAssignment`, `AuditLog`, `PermissionCacheVersion`, `ConfigurationVersion` (constructor kiểm tra invariant, private setter, private ctor cho EF).
- **Infrastructure.Identity**: `Models/ApplicationUser.cs`, `Models/ApplicationRole.cs`.
- **Infrastructure.Persistence**: `Contexts/AppIdentityDbContext`, `AppAuthDbContext`, `AppAuditDbContext`; `Configurations/` gồm 11 class `IEntityTypeConfiguration<T>` (9 entity + `ApplicationUser` + `ApplicationRole`); sửa `.csproj` thêm ProjectReference tới `Infrastructure.Identity`.
- **UnitTests**: `Domain/EntityInvariantTests.cs`, `Persistence/DbContextModelTests.cs`; sửa `.csproj` thêm reference tới `Persistence`, `Infrastructure.Identity` và package `Microsoft.EntityFrameworkCore.InMemory 8.*`.
- **Đã xóa**: `Class1.cs` ở Domain, Application, Infrastructure.Identity, Infrastructure.Persistence; `UnitTest1.cs` ở UnitTests. Project `Application` hiện không còn file nào (không có yêu cầu thiết kế trong task này).

### b. Kết quả kiểm thử thực tế (Output terminal):
```text
# Lệnh: dotnet build AppInHouse.sln --no-incremental
Build succeeded.  0 Warning(s)  0 Error(s)

# Lệnh: dotnet test AppInHouse.sln
Passed!  - Failed: 0, Passed:  1, Skipped: 0, Total:  1 - AppInHouse.Identity.IntegrationTests.dll (net8.0)  (test mặc định có sẵn, ngoài phạm vi task)
Passed!  - Failed: 0, Passed: 36, Skipped: 0, Total: 36 - AppInHouse.Identity.UnitTests.dll (net8.0)
```

### c. Đánh giá rủi ro hoặc ghi chú kỹ thuật cho Tech Lead:
- **Thêm reference `Persistence` -> `Infrastructure.Identity`**: bắt buộc để `AppIdentityDbContext` dùng `ApplicationUser/Role`; task 001 không có reference này. Vẫn đúng hướng phụ thuộc (không có vòng).
- **FK sang AspNetUsers/AspNetRoles**: Domain không được tham chiếu Identity nên entity chỉ giữ `UserId/RoleId` (string, max 450). Để vẫn có FK thật, `AppAuthDbContext` map `ApplicationUser/Role` vào `AspNetUsers/AspNetRoles` với `ExcludeFromMigrations()`. Hệ quả: khi tạo migration, migration của Auth phải chạy SAU migration của Identity (cùng một database). **Chưa tạo migration** (ngoài phạm vi) và chưa chạy thử trên SQL Server thật; chỉ kiểm bằng InMemory + kiểm tra metadata model.
- **Kiểm tra bằng InMemory không enforce unique/FK/check constraint**; các ràng buộc này được chứng minh qua metadata model (index, FK, độ dài), không qua vi phạm thực tế.
- **Quyết định ngoài danh sách tường minh** (để ngăn dữ liệu trùng/mơ hồ): thêm unique `RolePermissions (RoleId, PermissionId)` và `UserPermissionAssignments (UserId, PermissionId, ProjectId)`; check constraint `Permissions`: `IsGlobal = 1 <=> ProjectId IS NULL` (cú pháp SQL Server); `IsGlobal` do constructor tự suy ra từ `ProjectId`.
- **Delete behavior**: FK tới `Project` và `Permission` dùng `Restrict` (tránh multiple cascade paths trên SQL Server, project dùng soft delete); FK tới User/Role dùng `Cascade`. `AuditLog` cố ý không có FK để log tồn tại sau khi xóa user/project.
- **Không đặt DB default cho bool** (`IsActive`...) vì `HasDefaultValue(true)` làm EF bỏ qua giá trị `false`; giá trị mặc định do constructor đảm nhiệm.
- **`ProjectCode` unique bao gồm cả project đã soft delete** (không dùng filtered index/global query filter) - cần Tech Lead xác nhận nếu muốn tái sử dụng code sau khi xóa.
- **`Project.ConcurrencyToken`** là chuỗi tự xoay (Guid) trong các method đổi trạng thái, không phải `rowversion`.
- Không tạo `AppDbContext` tổng hợp (giữ 3 DbContext riêng như task cho phép) và chưa có đăng ký DI/connection string.

---

## 6. Đánh Giá & Nghiệm Thu Kỹ Thuật (Tech Lead Review)

- **Kết luận**: `APPROVED`
- **Người nghiệm thu**: Gemini Orchestrator (Tech Lead)
- **Đánh giá chi tiết & Phản hồi các ghi chú kỹ thuật**:
  1. **Chất lượng mã nguồn**: Code phân tách rất tốt; `Guard.cs` đảm bảo domain invariants chặt chẽ. Bộ 36 unit tests đạt độ bao phủ cao (Domain Invariants + EF Model Metadata Verification).
  2. **Về Reference `Persistence` -> `Infrastructure.Identity`**: Chấp thuận. Đây là hướng phụ thuộc hợp lệ trong cấu trúc phân tầng hiện tại, không gây circular dependency.
  3. **Về các ràng buộc dữ liệu chủ động**:
     - Unique `(RoleId, PermissionId)` và `(UserId, PermissionId, ProjectId)`: **Phê duyệt**.
     - Check constraint `IsGlobal <=> ProjectId IS NULL`: **Phê duyệt**.
     - `AuditLog` không có FK cứng để bảo tồn lịch sử kiểm toán: **Phê duyệt** (rất chuẩn triết lý security auditing).
  4. **Về Filtered Index cho `ProjectCode`**:
     - **Quyết định**: Đồng ý chuyển đổi sang Filtered Index `.HasFilter("[IsDeleted] = 0")` trong task cấu hình migration tiếp theo để cho phép tái sử dụng `ProjectCode` khi dự án cũ đã xóa mềm.
  5. **Về Migrations & SQL Server**:
     - Sẽ được đóng gói và kiểm thử thực tế trong **Phase 3 / TASK-003** khi tích hợp OpenIddict và hoàn thiện Connection String.

