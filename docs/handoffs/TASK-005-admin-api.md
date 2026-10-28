# TASK-005: Admin REST API (OpenAPI/Swagger, CRUD Quản Trị & Audit Trail)

- **Người giao (Tech Lead)**: Gemini Orchestrator
- **Người thực hiện (Implementer)**: Claude Code
- **Trạng thái**: `APPROVED`
- **Ngày hoàn thành**: 2026-10-28
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
- **Application** (`Admin/`): `Common/AdminExceptions.cs`, `Common/Paging.cs`, `Security/AdminCaller.cs` (`ICurrentCaller`, `IAdminAccessReader`, `AdminCaller`, `IAdminAuthorizer` + `AdminAuthorizer`), `Audit/IAuditService.cs`, `Audit/AuditLogContracts.cs`, và hợp đồng (DTO/request/interface) `Projects/ProjectContracts.cs`, `Projects/AssignmentContracts.cs`, `Clients/ClientContracts.cs`, `Users/UserContracts.cs`, `Permissions/PermissionContracts.cs`, `Roles/RoleContracts.cs`; `DependencyInjection.cs` (+ `AddAdminAuthorization`).
- **Persistence** (`Admin/`): `ProjectAdminService`, `ProjectAssignmentService`, `PermissionAdminService`, `RoleAdminService`, `UserAdminService`, `ClientAdminService`, `AuditService`, `AuditLogQueryService`, `EfAdminAccessReader`, `IdentityResultMapper`, `PagingExtensions`; `DependencyInjection.cs` (+ `AddAdminServices`); `Seeders/DbSeeder.cs` + `DbSeederOptions.cs` (client tùy chọn `identity_admin_api`, xem mục c).
- **Admin.Api**: `Program.cs` (viết lại), `Controllers/` (`AdminControllerBase`, `Projects`, `ProjectAssignments`, `Clients`, `Users`, `Permissions`, `Roles`, `AuditLogs`), `Infrastructure/` (`HttpCurrentCaller`, `AdminProblems` + `AdminExceptionFilter`, `AdminExceptionHandler`, `AdminApiMarker`), `appsettings.json`; `.csproj` (+ `OpenIddict.AspNetCore`, `Swashbuckle.AspNetCore.Annotations`); xóa mẫu `WeatherForecast` và `.http`.
- **UnitTests** (+22): `Admin/AdminSecurityTests.cs`, `Admin/AuditServiceTests.cs`.
- **IntegrationTests** (+165): `AdminApi/` (`AdminApiHost`, `AdminWorld`, `SwaggerAndSecurityTests`, `ProjectsApiTests`, `AssignmentsApiTests`, `PermissionsAndRolesApiTests`, `UsersApiTests`, `ClientsApiTests`, `AuditLogsApiTests`); STS: `StsWebApplicationFactory`, `DbSeederTests`, `ClientCredentialsTests` (+2 test introspection thật).

### b. Kết quả kiểm thử thực tế (Output terminal):
```text
# Lệnh: dotnet build AppInHouse.sln --no-incremental   (và repos/identity/AppInHouse.Identity.sln)
Build succeeded.  0 Warning(s)  0 Error(s)
(Chạy build NGAY sau `dotnet test` đôi khi báo 2x MSB3061 "Unable to delete ... Access denied" do process test
 host còn giữ file DLL vài giây; build lại ngay sau đó luôn cho 0 Warning, 0 Error.)

# Lệnh: dotnet test AppInHouse.sln
Passed!  - Failed: 0, Passed: 155, Skipped: 0, Total: 155 - AppInHouse.Identity.UnitTests.dll (net8.0)
Passed!  - Failed: 0, Passed: 217, Skipped: 0, Total: 217 - AppInHouse.Identity.IntegrationTests.dll (net8.0)
```
Điểm chứng minh theo tiêu chí nghiệm thu (đều qua HTTP thật tới Admin API trong `WebApplicationFactory`, DB InMemory):
- **Swagger**: `/swagger/v1/swagger.json` chứa đủ 27 operation v1 (kiểm từng cặp method+path), có summary, security scheme Bearer; `/swagger/index.html` trả 200.
- **Tạo dự án/client không cần sửa code**: `ANewProject_IsUsableImmediately...` (tạo project -> permission -> gán user -> quyền có hiệu lực); `ClientsApiTests` tạo Web/Spa/Mobile/Machine.
- **Gán role/permission tự động invalidate + audit**: gán/gỡ role, revoke trực tiếp, đổi permission của role, khóa/vô hiệu hóa user/project/permission đều làm `HasPermissionAsync` đổi kết quả NGAY sau response (cache đã được làm nóng trước đó) và có bản ghi audit với actor, project, IP, old/new JSON.
- **IDOR & leo quyền** (mỗi nhóm có test cho AdminA, AdminB, thành viên, người ngoài): project admin không đọc/sửa/gán/đặt role/đặt quyền ở project khác (403, id không tồn tại cũng trả 403 để không lộ tồn tại); project admin không thể tạo global permission, permission của project khác, gán `IsProjectAdmin` (kể cả cho chính mình), đổi role/role-permission, quản lý user/client; role `GlobalAdmin` không thể gán qua project hay đổi tên; không thể tự khóa/tự vô hiệu hóa.
- **Không lộ bí mật**: mật khẩu (tạo/reset, đúng và sai chính sách) và client secret (tạo/rotate) không xuất hiện trong audit, trong response của list, hay trong thông báo lỗi; secret chỉ trả 1 lần kèm `Cache-Control: no-store` và OpenIddict chỉ lưu hash.
- Kiểm tra độ nhạy: tạm gỡ (1) `InvalidateAllAsync` ở `SetPermissionsAsync`, (2) kiểm tra chặn `IsProjectAdmin` của project admin, (3) `RequireProjectAdmin` ở `AssignUserAsync` làm 6 test thất bại (đã khôi phục).

### c. Đánh giá rủi ro hoặc ghi chú kỹ thuật cho Tech Lead:
- **Mô hình phân quyền của Admin API** (task chỉ nêu "GlobalAdmin hoặc ProjectAdmin của đúng dự án", tôi cụ thể hóa theo hướng deny-by-default): tạo/đổi trạng thái dự án, mọi thao tác trên Users/Clients/Roles = chỉ GlobalAdmin; sửa dự án, gán user/role/quyền trực tiếp, tạo permission của dự án, xem audit của dự án = GlobalAdmin hoặc ProjectAdmin của đúng dự án; chỉ GlobalAdmin được đặt/đổi `IsProjectAdmin`; `GET /roles` mở cho mọi admin nhưng chỉ GlobalAdmin thấy permission của role; `GET /permissions` và `GET /audit-logs` của ProjectAdmin tự bị giới hạn theo dự án họ quản trị. Quyền admin của người gọi được đọc từ DB mỗi request (user phải active/không khóa; ProjectAdmin phải chưa bị block và dự án còn hoạt động).
- **Xác thực chưa được kiểm chứng end-to-end**: Admin.Api dùng OpenIddict validation + introspection (cấu hình `Authentication:Authority/ClientId/ClientSecret/Audience`, đọc lazily; ngoài Development thiếu Authority thì app ném lỗi khi khởi động). Test thay bằng scheme giả (`Bearer {userId}` -> claim `sub`) nên không có test nào gọi STS thật từ Admin.Api. Để chạy thật cần: (1) STS đặt `Seeding:AdminApiClientSecret` (tôi thêm client resource-server `identity_admin_api` tùy chọn vào `DbSeeder`; **chưa cập nhật `appsettings` của STS vì ngoài phạm vi**), (2) Admin.Api cấu hình ClientSecret trùng. Đã có test STS xác nhận introspection hoạt động đúng giả định (client phải là audience của token). Access token là JWE (không phải reference token) nên mỗi request Admin.Api phải gọi `/connect/introspect`.
- **Schema DB**: vẫn chưa có migration; Admin.Api cần cả 3 context (Identity+OpenIddict, Auth, Audit) trên cùng DB; `EnsureCreated` của STS chỉ tạo schema của `AppIdentityDbContext`, nên **Admin.Api chưa chạy được trên SQL Server thật** nếu chưa tạo bảng Auth/Audit. Các truy vấn mới chỉ được kiểm bằng InMemory (phân biệt hoa/thường khác SQL Server; tôi dùng `ToUpper()` khi kiểm tra trùng mã nên đúng cả hai, nhưng `Search` (Contains) trên InMemory phân biệt hoa thường). Cần task migration + test trên SQL Server.
- **Audit không nằm cùng transaction với thay đổi**: các thay đổi nằm ở `AppAuthDbContext`/`AppIdentityDbContext`, audit ở `AppAuditDbContext` nên ghi audit sau khi commit; nếu ghi audit lỗi, lỗi được log mức Critical và request trả 500 dù thay đổi đã được áp dụng (chọn "không mất dấu vết im lặng" thay vì nuốt lỗi). Thứ tự trong mỗi use case: lưu -> invalidate cache -> ghi audit. Muốn atomic cần dùng chung transaction/connection hoặc outbox ở task sau.
- **IP trong audit** lấy từ `Connection.RemoteIpAddress`; sau reverse proxy cần bật forwarded-headers với danh sách proxy tin cậy (không tin `X-Forwarded-For` mặc định).
- **Domain không có setter cho `Permission.DisplayName/Module`** và Domain nằm ngoài phạm vi task, nên `PermissionAdminService` ghi hai trường này qua `Entry(...).Property(...).CurrentValue` (bỏ qua entity); độ dài/giá trị rỗng được kiểm ở request + service. Đề nghị thêm `Permission.UpdateDetails(...)` ở task sau.
- **Ngữ nghĩa "thay toàn bộ tập"**: `POST .../roles`, `POST .../permissions` (của user trong project) và `POST /roles/{id}/permissions` thay thế toàn bộ tập hiện có (gửi danh sách rỗng để gỡ hết), không phải thêm dần; idempotent. `POST /projects/{projectId}/users/assign` là upsert (cập nhật `IsProjectAdmin` nếu đã tồn tại). Task không yêu cầu endpoint bỏ user khỏi project / xóa project / chặn user trong project (`IsBlocked`) nên chưa có.
- **Versioning**: chỉ có tiền tố route `/api/v1` và một tài liệu Swagger `v1` (không dùng `Asp.Versioning`); khi có v2 nên chuyển sang thư viện versioning.
- **Client OpenIddict**: loại client lưu bằng `ClientType`/`ApplicationType`/grant (Web=confidential, Spa=public, Mobile=public+native, Machine=confidential+client_credentials), không đổi được sau khi tạo; client luôn `ConsentType=Implicit` + PKCE bắt buộc cho loại tương tác; redirect URI chỉ chấp nhận https (http chỉ cho loopback), custom scheme chỉ cho Mobile, cấm wildcard/fragment/scheme nguy hiểm. Rotate secret không thu hồi token đã cấp. Danh sách client không lọc/tìm kiếm (chỉ phân trang).
- **Lỗi nghiệp vụ** trả ProblemDetails (400/403/404/409) qua một exception filter MVC (không đi qua middleware nên không làm nhiễu log ở mức Error); lỗi bất ngờ trả 500 chung, chi tiết chỉ nằm trong log.
- Biến `AdminApiMarker` (class public) dùng cho `WebApplicationFactory<AdminApiMarker>` vì `Program` của STS và Admin.Api cùng nằm ở namespace gốc, để test project tham chiếu cả hai không bị mơ hồ.
- Thứ tự audit log dựa trên `Timestamp` (UTC) rồi `Id`; hai bản ghi cùng một tick sẽ có thứ tự tùy ý.

---

## 5. Đánh Giá & Nghiệm Thu Kỹ Thuật (Tech Lead Review)

- **Kết luận**: `APPROVED`
- **Người nghiệm thu**: Gemini Orchestrator (Tech Lead)
- **Đánh giá chi tiết & Phản hồi các ghi chú kỹ thuật**:
  1. **Độ phủ kiểm thử và chất lượng kiến trúc vượt trội**: Bộ test đạt **372/372 test pass** (155 UnitTests + 217 IntegrationTests). Cả 27 endpoints v1 của Admin API được bao phủ toàn diện với kịch bản thực tế.
  2. **Về phân quyền chống IDOR & Leo quyền**:
     - Quy tắc phân quyền theo nguyên tắc Least Privilege: ProjectAdmin chỉ thao tác trong phạm vi dự án được cấp quyền; các thao tác toàn cục (tạo project, user, role toàn hệ thống) chỉ dành riêng cho `GlobalAdmin`.
     - IDOR phòng ngừa chặt chẽ: Endpoint trả về `403` kể cả khi ID tài nguyên không tồn tại để ngăn chặn kỹ thuật trinh sát dò ID (ID enumeration).
  3. **Về Bảo mật Audit Log & Client Secret**:
     - Hoàn toàn nhất trí với việc không lưu trữ password và raw client secret trong audit log.
     - Secret chỉ được trả về đúng 1 lần khi khởi tạo/rotate kèm header `Cache-Control: no-store`.
  4. **Các cải tiến cho Task tiếp theo (Phase 6)**:
     - Thêm phương thức `Permission.UpdateDetails(...)` trong Domain entity để loại bỏ việc cập nhật qua EF property entry.
     - Tạo EF Core Migrations cho cả 3 DbContext (`Identity`, `Auth`, `Audit`) và cấu hình kết nối SQL Server hoàn chỉnh.
     - Hoàn thiện giao diện **Admin Web Portal UI** (MVC/Razor + Bootstrap 5 theo phong cách Skoruba).

