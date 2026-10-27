# TASK-001: Khởi Tạo Multi-Repo Layout & Solution .NET 8 Theo Clean Architecture

- **Người giao (Tech Lead)**: Gemini Orchestrator
- **Người thực hiện (Implementer)**: Claude Code
- **Trạng thái**: `PENDING`
- **Ngày tạo**: 2026-10-27
- **Tài liệu tham chiếu**: [docs/architecture/system_design.md](../architecture/system_design.md)

---

## 1. Mục Tiêu Kỹ Thuật

Thiết lập cấu trúc **Multi-Repository Workspace** gọn gàng, chuẩn mực:
1. Gom toàn bộ Identity Server & Admin Portal vào đúng **1 thư mục repo duy nhất**: `repos/identity/`.
2. Tạo solution riêng của Identity bên trong repo: `repos/identity/AppInHouse.Identity.sln`.
3. Tạo cây project chuẩn Clean Architecture bên trong `repos/identity/src/` và `repos/identity/tests/`.
4. Cập nhật Solution master dùng chung tại thư mục gốc: `AppInHouse.sln` (đã khởi tạo).
   > **Yêu cầu sắp xếp**: Sử dụng cờ `--solution-folder "Identity"` khi add các project vào `AppInHouse.sln`. Điều này đảm bảo khi mở `AppInHouse.sln`, toàn bộ các project của Identity sẽ nằm gọn gàng trong 1 thư mục ảo tên là `Identity`, không làm lộn xộn các repo khác sau này (`ERP`, `AIChat`, `Mobile`...).
5. Cả hai solution (`repos/identity/AppInHouse.Identity.sln` và `AppInHouse.sln`) đều phải build thành công `0 Error(s), 0 Warning(s)`.

---

## 2. Phạm Vi File Được Phép Tạo / Sửa (File Scope & Whitelist)

> **LƯU Ý QUAN TRỌNG DÀNH CHO CLAUDE CODE**:
> Chỉ được phép tạo và chỉnh sửa các file thuộc phạm vi dưới đây. Tuyệt đối không sửa `AGENTS.md`, `CLAUDE.md`, hoặc các file trong `docs/`.

- [ ] `AppInHouse.sln` (Solution master ở thư mục gốc)
- [ ] `repos/identity/AppInHouse.Identity.sln` (Solution riêng của Identity repo)
- [ ] `repos/identity/Directory.Build.props` (Cấu hình chung: TargetFramework `net8.0`, `Nullable: enable`, `ImplicitUsings: enable`)
- [ ] `repos/identity/src/Core/AppInHouse.Identity.Domain/*`
- [ ] `repos/identity/src/Core/AppInHouse.Identity.Application/*`
- [ ] `repos/identity/src/Core/AppInHouse.Identity.SharedKernel/*`
- [ ] `repos/identity/src/Infrastructure/AppInHouse.Identity.Infrastructure.Persistence/*`
- [ ] `repos/identity/src/Infrastructure/AppInHouse.Identity.Infrastructure.Identity/*`
- [ ] `repos/identity/src/Infrastructure/AppInHouse.Identity.Infrastructure.Caching/*`
- [ ] `repos/identity/src/Infrastructure/AppInHouse.Identity.Infrastructure.Logging/*`
- [ ] `repos/identity/src/Presentation/AppInHouse.Identity.STS/*`
- [ ] `repos/identity/src/Presentation/AppInHouse.Identity.Admin.Api/*`
- [ ] `repos/identity/src/Presentation/AppInHouse.Identity.Admin.Web/*`
- [ ] `repos/identity/src/Sdk/AppInHouse.Identity.ClientSdk/*`
- [ ] `repos/identity/tests/AppInHouse.Identity.UnitTests/*`
- [ ] `repos/identity/tests/AppInHouse.Identity.IntegrationTests/*`

---

## 3. Các Bước Thực Hiện Chi Tiết

1. **Khởi tạo thư mục và solution riêng cho Identity Repo**:
   ```bash
   mkdir -p repos/identity
   cd repos/identity
   dotnet new sln -n AppInHouse.Identity
   ```

2. **Tạo các project trong `repos/identity/`**:
   - `src/Core/AppInHouse.Identity.SharedKernel` (classlib)
   - `src/Core/AppInHouse.Identity.Domain` (classlib)
   - `src/Core/AppInHouse.Identity.Application` (classlib)
   - `src/Infrastructure/AppInHouse.Identity.Infrastructure.Persistence` (classlib)
   - `src/Infrastructure/AppInHouse.Identity.Infrastructure.Identity` (classlib)
   - `src/Infrastructure/AppInHouse.Identity.Infrastructure.Caching` (classlib)
   - `src/Infrastructure/AppInHouse.Identity.Infrastructure.Logging` (classlib)
   - `src/Sdk/AppInHouse.Identity.ClientSdk` (classlib)
   - `src/Presentation/AppInHouse.Identity.STS` (web)
   - `src/Presentation/AppInHouse.Identity.Admin.Api` (webapi)
   - `src/Presentation/AppInHouse.Identity.Admin.Web` (mvc)
   - `tests/AppInHouse.Identity.UnitTests` (xunit)
   - `tests/AppInHouse.Identity.IntegrationTests` (xunit)

3. **Cấu hình References chuẩn Clean Architecture**:
   - `SharedKernel`: Độc lập.
   - `Domain`: Tham chiếu `SharedKernel`.
   - `Application`: Tham chiếu `Domain`, `SharedKernel`.
   - `Infrastructure.Persistence`: Tham chiếu `Application`, `Domain`.
   - `Infrastructure.Identity`: Tham chiếu `Application`, `Domain`.
   - `Infrastructure.Caching`: Tham chiếu `Application`.
   - `Infrastructure.Logging`: Tham chiếu `Application`.
   - `ClientSdk`: Tham chiếu `SharedKernel`.
   - `STS`: Tham chiếu `Infrastructure.Identity`, `Infrastructure.Persistence`, `Infrastructure.Logging`, `Application`.
   - `Admin.Api`: Tham chiếu `Infrastructure.Persistence`, `Infrastructure.Caching`, `Infrastructure.Logging`, `Application`.
   - `Admin.Web`: Tham chiếu `Infrastructure.Persistence`, `Infrastructure.Caching`, `Application`.
   - `UnitTests`: Tham chiếu `Domain`, `Application`, `ClientSdk`.
   - `IntegrationTests`: Tham chiếu `Admin.Api`, `STS`.

4. **Thêm các project vào cả 2 Solutions**:
   - Thêm vào solution riêng:
     ```bash
     dotnet sln repos/identity/AppInHouse.Identity.sln add repos/identity/src/**/*.csproj repos/identity/tests/**/*.csproj
     ```
   - Thêm vào solution master tại root (gom gọn vào thư mục ảo `Identity`):
     ```bash
     dotnet sln AppInHouse.sln add --solution-folder "Identity" repos/identity/src/**/*.csproj repos/identity/tests/**/*.csproj
     ```

5. **Cài đặt các gói NuGet cốt lõi (.NET 8)**:
   - `OpenIddict.AspNetCore` (5.*)
   - `Microsoft.EntityFrameworkCore.SqlServer` (8.*)
   - `Microsoft.AspNetCore.Identity.EntityFrameworkCore` (8.*)
   - `Serilog.AspNetCore` (8.*)
   - `Swashbuckle.AspNetCore` (6.5.*)

---

## 4. Tiêu Chí Nghiệm Thu (Acceptance Criteria)

- [ ] Lệnh `dotnet build repos/identity/AppInHouse.Identity.sln` chạy thành công `0 Error(s), 0 Warning(s)`.
- [ ] Lệnh `dotnet build AppInHouse.sln` chạy thành công `0 Error(s), 0 Warning(s)`.
- [ ] Lệnh `dotnet test AppInHouse.sln` chạy thành công.
- [ ] Toàn bộ các project của Identity nằm gọn trong thư mục `repos/identity/`.
- [ ] Trong `AppInHouse.sln`, tất cả project được gom trong solution folder `Identity`.
- [ ] Điền báo cáo nghiệm thu vào mục bên dưới.

---

## 5. Báo Cáo Triển Khai (Claude Code điền khi hoàn thành)

### a. Danh sách project và file đã tạo:
- 

### b. Kết quả kiểm thử thực tế (Output terminal):
```text
# Lệnh: dotnet build repos/identity/AppInHouse.Identity.sln

# Lệnh: dotnet build AppInHouse.sln

# Lệnh: dotnet test AppInHouse.sln

```

### c. Đánh giá rủi ro hoặc ghi chú kỹ thuật cho Tech Lead:
- 
