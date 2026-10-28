# TASK-007: Tự Động Chạy Database Migration & Seeding Khi Khởi Động

## 1. Thông Tin Chung

- **Mã Task**: TASK-007
- **Tên Task**: Tự động áp dụng EF Core Migrations cho cả 3 DbContext và Seeding dữ liệu mẫu khi khởi động ứng dụng
- **Người Giao (Tech Lead)**: Gemini
- **Người Nhận (Implementer)**: Claude Code
- **Trạng Thái**: APPROVED
- **Phụ Thuộc**: Hoàn thành sau TASK-006 (đã có đủ 3 bộ Migration: Identity, Auth, Audit)

---

## 2. Mục Tiêu & Bối Cảnh

Hiện tại, lập trình viên muốn chạy dự án lần đầu phải thực hiện 3 câu lệnh CLI thủ công:
```bash
dotnet ef database update --project repos/identity/src/Infrastructure/AppInHouse.Identity.Infrastructure.Persistence --startup-project repos/identity/src/Presentation/AppInHouse.Identity.STS --context AppIdentityDbContext
dotnet ef database update --project repos/identity/src/Infrastructure/AppInHouse.Identity.Infrastructure.Persistence --startup-project repos/identity/src/Presentation/AppInHouse.Identity.STS --context AppAuthDbContext
dotnet ef database update --project repos/identity/src/Infrastructure/AppInHouse.Identity.Infrastructure.Persistence --startup-project repos/identity/src/Presentation/AppInHouse.Identity.STS --context AppAuditDbContext
```

Mục tiêu của task này là tự động hóa 100% quy trình cập nhật schema database khi khởi động dự án (`dotnet run` hoặc bấm F5 chạy `STS` / `Admin.Web`), loại bỏ sự phụ thuộc vào câu lệnh CLI thủ công nhưng vẫn đảm bảo tính an toàn:
1. Khi khởi động ứng dụng (`STS` hoặc `Admin.Web`) trong môi trường Development (`Database:MigrateOnStartup = true`), tự động áp dụng pending migrations theo thứ tự: `AppIdentityDbContext` -> `AppAuthDbContext` -> `AppAuditDbContext`.
2. Tự động chạy `DbSeeder` để nạp tài khoản admin, vai trò và OpenIddict clients ngay sau khi migration xong.
3. Không làm gãy bất kỳ Integration Test hay Unit Test nào đang dùng EF Core InMemory Database (bằng cách kiểm tra điều kiện `Database.IsRelational()`).
4. Cập nhật tài liệu hướng dẫn `repos/identity/README.md`.

---

## 3. Phạm Vi File Cấp Phép (File Scope)

Claude Code **chỉ được phép** tạo hoặc sửa các file sau:

- `repos/identity/src/Presentation/AppInHouse.Identity.STS/Program.cs`
- `repos/identity/src/Presentation/AppInHouse.Identity.STS/Services/StartupInitializer.cs`
- `repos/identity/src/Presentation/AppInHouse.Identity.STS/appsettings.json`
- `repos/identity/src/Presentation/AppInHouse.Identity.STS/appsettings.Development.json`
- `repos/identity/src/Presentation/AppInHouse.Identity.Admin.Web/Infrastructure/DatabaseMigrator.cs`
- `repos/identity/src/Presentation/AppInHouse.Identity.Admin.Web/appsettings.json`
- `repos/identity/src/Presentation/AppInHouse.Identity.Admin.Web/appsettings.Development.json`
- `repos/identity/src/Presentation/AppInHouse.Identity.Admin.Api/Program.cs`
- `repos/identity/src/Presentation/AppInHouse.Identity.Admin.Api/appsettings.Development.json`
- `repos/identity/README.md`
- `docs/handoffs/TASK-007-auto-database-migration-on-startup.md` (cập nhật báo cáo nghiệm thu)

---

## 4. Yêu Cầu Kỹ Thuật Chi Tiết

### 4.1. Đăng ký DbContext trong `AppInHouse.Identity.STS/Program.cs`
- Hiện tại STS chỉ đăng ký `AppIdentityDbContext` với `options.UseSqlServer(...)`.
- Cần đăng ký cả 3 context với helper method `UseIdentitySqlServer<TContext>`:
  ```csharp
  var connectionString = configuration.GetConnectionString(SqlServerOptionsExtensions.ConnectionStringName);
  builder.Services.AddDbContext<AppIdentityDbContext>(options => options.UseIdentitySqlServer<AppIdentityDbContext>(connectionString));
  builder.Services.AddDbContext<AppAuthDbContext>(options => options.UseIdentitySqlServer<AppAuthDbContext>(connectionString));
  builder.Services.AddDbContext<AppAuditDbContext>(options => options.UseIdentitySqlServer<AppAuditDbContext>(connectionString));
  ```

### 4.2. Cập nhật `StartupInitializer.cs` trong `AppInHouse.Identity.STS`
- Thay thế `EnsureCreatedAsync` bằng quy trình migration chuẩn:
  ```csharp
  if (_configuration.GetValue<bool>("Database:MigrateOnStartup"))
  {
      var identityContext = scope.ServiceProvider.GetRequiredService<AppIdentityDbContext>();
      var authContext = scope.ServiceProvider.GetRequiredService<AppAuthDbContext>();
      var auditContext = scope.ServiceProvider.GetRequiredService<AppAuditDbContext>();

      // Relational database (SQL Server, PostgreSQL, etc.): apply EF Core migrations
      if (identityContext.Database.IsRelational())
      {
          await identityContext.Database.MigrateAsync(cancellationToken);
          await authContext.Database.MigrateAsync(cancellationToken);
          await auditContext.Database.MigrateAsync(cancellationToken);
      }
  }
  else if (_configuration.GetValue<bool>("Database:EnsureCreated"))
  {
      // Legacy / InMemory fallback
      var identityContext = scope.ServiceProvider.GetRequiredService<AppIdentityDbContext>();
      await identityContext.Database.EnsureCreatedAsync(cancellationToken);
  }

  if (_configuration.GetValue<bool>($"{DbSeederOptions.SectionName}:Enabled"))
  {
      var seeder = scope.ServiceProvider.GetRequiredService<DbSeeder>();
      await seeder.SeedAsync(cancellationToken);
  }
  ```

### 4.3. Cập nhật `DatabaseMigrator.cs` trong `Admin.Web`
- Bổ sung kiểm tra `if (!identityContext.Database.IsRelational()) return;` để phòng vệ an toàn khi chạy trong môi trường test/in-memory.

### 4.4. Cập nhật `Admin.Api/Program.cs`
- Đảm bảo cấu hình DbContext sử dụng đồng nhất `options.UseIdentitySqlServer<TContext>(connectionString)` để nhất quán với migration history table.

### 4.5. Cập nhật cấu hình `appsettings.*.json`
- Trong `STS/appsettings.Development.json`:
  ```json
  "Database": {
    "MigrateOnStartup": true
  }
  ```
  (Loại bỏ `EnsureCreated: true`).
- Đảm bảo trong `Admin.Web/appsettings.Development.json` đã có `"Database": { "MigrateOnStartup": true }`.

### 4.6. Cập nhật tài liệu `repos/identity/README.md`
- Cập nhật mục Khởi tạo Database:
  - Nêu rõ: Khi chạy `AppInHouse.Identity.STS` hoặc `AppInHouse.Identity.Admin.Web` lần đầu (với `Database:MigrateOnStartup = true`), hệ thống sẽ **tự động áp dụng toàn bộ migration và seed tài khoản mặc định**.
  - Các câu lệnh `dotnet ef database update` thủ công vẫn được giữ lại trong tài liệu nhưng đóng vai trò là phương án thay thế (optional / manual CLI approach).

---

## 5. Tiêu Chuẩn Nghiệm Thu (Definition of Done)

1. [x] `dotnet build AppInHouse.sln --no-incremental` đạt 0 Error, 0 Warning.
2. [x] `dotnet test AppInHouse.sln` chạy thành công toàn bộ test hiện có (toàn bộ 452 test pass, 0 fail).
3. [x] Chạy thực tế STS trên SQL Server LocalDB tự động sinh đủ 3 bảng migration history:
   - `__EFMigrationsHistory_AppIdentityDbContext`
   - `__EFMigrationsHistory_AppAuthDbContext`
   - `__EFMigrationsHistory_AppAuditDbContext`
   và các bảng nghiệp vụ, cùng tài khoản `admin@appinhouse.local`.
4. [x] Tài liệu `repos/identity/README.md` phản ánh chính xác tính năng tự động migration.
5. [x] Điền báo cáo nghiệm thu vào mục 6 bên dưới.

---

## 6. Báo Cáo Nghiệm Thu Của Claude Code

*(Claude Code điền vào phần này sau khi hoàn thành task)*

- **Build Output**: `dotnet build AppInHouse.sln --no-incremental` → `0 Warning(s)`, `0 Error(s)`.
- **Test Output**: `dotnet test AppInHouse.sln` → UnitTests `Passed: 170, Failed: 0`; IntegrationTests `Passed: 282, Failed: 0` (tổng 452, 0 fail).
- **Chạy thực tế**: `dotnet run` STS (Development) trên LocalDB, DB trống `AppInHouse.Task007` → tự sinh 3 bảng `__EFMigrationsHistory_AppIdentityDbContext/AppAuthDbContext/AppAuditDbContext`, tổng 23 bảng, user `admin@appinhouse.local`, clients `admin-portal` và `internal-service`. Đã xóa DB kiểm thử sau đó.
- **Danh Sách File Đã Thay Đổi**: STS `Program.cs` (3 DbContext qua `UseIdentitySqlServer`), STS `Services/StartupInitializer.cs` (migrate theo thứ tự + `IsRelational()`, `EnsureCreated` chỉ còn là nhánh dự phòng), STS `appsettings.json` (+`MigrateOnStartup:false`), STS `appsettings.Development.json` (`MigrateOnStartup:true`, bỏ `EnsureCreated`), Admin.Web `Infrastructure/DatabaseMigrator.cs` (bỏ qua nếu không phải relational), Admin.Api `Program.cs` (`UseIdentitySqlServer`), `repos/identity/README.md` (mục 3.2; sửa luôn khóa ví dụ `DefaultConnection` → `IdentityDb` cho khớp code).
- **Ghi Chú Kỹ Thuật Thêm**: (1) Test STS vẫn dùng `Database:EnsureCreated=true` + InMemory nên không đổi. (2) Khi `MigrateOnStartup=true` mà DB không quan hệ thì bỏ qua migrate (không EnsureCreated), đúng như spec. (3) DB dev cũ đã được tạo bằng `EnsureCreated` sẽ lỗi "table already exists" khi migrate: cần xóa DB hoặc dùng DB mới. (4) STS và Admin.Web cùng migrate trên DB trống nếu khởi động đồng thời có thể tranh chấp; README khuyến nghị chạy STS trước. (5) Admin.Api chỉ đổi cách đăng ký, không tự migrate (đúng phạm vi). (6) `appsettings.json` của Admin.Web không cần sửa (đã có `MigrateOnStartup:false`; connection string bạn đã chỉnh được giữ nguyên).

---

## 7. Kết Luận Nghiệm Thu Của Tech Lead (Gemini)

- **Kết Luận**: **APPROVED**
- **Đánh Giá**:
  - Tuân thủ 100% phạm vi file được giao. Không chỉnh sửa ngoài ranh giới cấp phép.
  - Xử lý migration theo đúng thứ tự phụ thuộc khóa ngoại: `Identity` -> `Auth` -> `Audit`.
  - Cơ chế phòng vệ `Database.IsRelational()` hoạt động tốt, giữ vững 100% tỷ lệ pass của toàn bộ 452 automated tests.
  - Tài liệu `README.md` cập nhật mạch lạc, hướng dẫn rõ quy trình tự động và phương án CLI dự phòng.
