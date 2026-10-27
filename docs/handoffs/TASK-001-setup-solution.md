# TASK-001: Khởi Tạo Multi-Repo Layout & Solution .NET 8 Theo Clean Architecture

- **Người giao (Tech Lead)**: Gemini Orchestrator
- **Người thực hiện (Implementer)**: Claude Code
- **Trạng thái**: `PENDING`
- **Ngày tạo**: 2026-10-27
- **Tài liệu tham chiếu**: [docs/architecture/system_design.md](../architecture/system_design.md)

---

## 1. Mục Tiêu Kỹ Thuật

Thiết lập cấu trúc **Multi-Repository Workspace** theo yêu cầu của Project Owner:
1. Thư mục repo độc lập cho dịch vụ Identity Server: `repos/AppInHouse.Identity/`.
2. Tạo solution riêng bên trong repo: `repos/AppInHouse.Identity/AppInHouse.Identity.sln`.
3. Tạo cây project chuẩn Clean Architecture bên trong `repos/AppInHouse.Identity/src/` và `repos/AppInHouse.Identity/tests/`.
4. Cập nhật Solution master dùng chung tại thư mục gốc: `AppInHouse.All.sln` (đã khởi tạo) bằng cách add toàn bộ các project của `AppInHouse.Identity` vào Solution tổng này để lập trình viên có thể mở một lần tất cả các repository trong workspace.
5. Cả hai solution (`AppInHouse.Identity.sln` và `AppInHouse.All.sln`) đều phải build thành công `0 Error(s), 0 Warning(s)`.

---

## 2. Phạm Vi File Được Phép Tạo / Sửa (File Scope & Whitelist)

> **LƯU Ý QUAN TRỌNG DÀNH CHO CLAUDE CODE**:
> Chỉ được phép tạo và chỉnh sửa các file thuộc phạm vi dưới đây. Tuyệt đối không sửa `AGENTS.md`, `CLAUDE.md`, hoặc các file trong `docs/`.

- [ ] `AppInHouse.All.sln` (Solution master ở thư mục gốc)
- [ ] `repos/AppInHouse.Identity/AppInHouse.Identity.sln` (Solution riêng của Identity repo)
- [ ] `repos/AppInHouse.Identity/Directory.Build.props` (Cấu hình chung: TargetFramework `net8.0`, `Nullable: enable`, `ImplicitUsings: enable`)
- [ ] `repos/AppInHouse.Identity/src/Core/AppInHouse.Identity.Domain/*`
- [ ] `repos/AppInHouse.Identity/src/Core/AppInHouse.Identity.Application/*`
- [ ] `repos/AppInHouse.Identity/src/Core/AppInHouse.Identity.SharedKernel/*`
- [ ] `repos/AppInHouse.Identity/src/Infrastructure/AppInHouse.Identity.Infrastructure.Persistence/*`
- [ ] `repos/AppInHouse.Identity/src/Infrastructure/AppInHouse.Identity.Infrastructure.Identity/*`
- [ ] `repos/AppInHouse.Identity/src/Infrastructure/AppInHouse.Identity.Infrastructure.Caching/*`
- [ ] `repos/AppInHouse.Identity/src/Infrastructure/AppInHouse.Identity.Infrastructure.Logging/*`
- [ ] `repos/AppInHouse.Identity/src/Presentation/AppInHouse.Identity.STS/*`
- [ ] `repos/AppInHouse.Identity/src/Presentation/AppInHouse.Identity.Admin.Api/*`
- [ ] `repos/AppInHouse.Identity/src/Presentation/AppInHouse.Identity.Admin.Web/*`
- [ ] `repos/AppInHouse.Identity/src/Sdk/AppInHouse.Identity.ClientSdk/*`
- [ ] `repos/AppInHouse.Identity/tests/AppInHouse.Identity.UnitTests/*`
- [ ] `repos/AppInHouse.Identity/tests/AppInHouse.Identity.IntegrationTests/*`

---

## 3. Các Bước Thực Hiện Chi Tiết

1. **Khởi tạo thư mục và solution riêng cho Identity Repo**:
   ```bash
   mkdir -p repos/AppInHouse.Identity
   cd repos/AppInHouse.Identity
   dotnet new sln -n AppInHouse.Identity
   ```

2. **Tạo các project trong `repos/AppInHouse.Identity/`**:
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
   - Thêm vào solution riêng: `dotnet sln repos/AppInHouse.Identity/AppInHouse.Identity.sln add ...`
   - Thêm vào solution master tại root: `dotnet sln AppInHouse.All.sln add ...`

5. **Cài đặt các gói NuGet cốt lõi (.NET 8)**:
   - `OpenIddict.AspNetCore` (5.*)
   - `Microsoft.EntityFrameworkCore.SqlServer` (8.*)
   - `Microsoft.AspNetCore.Identity.EntityFrameworkCore` (8.*)
   - `Serilog.AspNetCore` (8.*)
   - `Swashbuckle.AspNetCore` (6.5.*)

---

## 4. Tiêu Chí Nghiệm Thu (Acceptance Criteria)

- [ ] Lệnh `dotnet build repos/AppInHouse.Identity/AppInHouse.Identity.sln` chạy thành công `0 Error(s), 0 Warning(s)`.
- [ ] Lệnh `dotnet build AppInHouse.All.sln` chạy thành công `0 Error(s), 0 Warning(s)`.
- [ ] Lệnh `dotnet test AppInHouse.All.sln` chạy thành công.
- [ ] Solution master `AppInHouse.All.sln` tại thư mục gốc mở được tất cả project con.
- [ ] Điền báo cáo nghiệm thu vào mục bên dưới.

---

## 5. Báo Cáo Triển Khai (Claude Code điền khi hoàn thành)

### a. Danh sách project và file đã tạo:
- 

### b. Kết quả kiểm thử thực tế (Output terminal):
```text
# Lệnh: dotnet build repos/AppInHouse.Identity/AppInHouse.Identity.sln

# Lệnh: dotnet build AppInHouse.All.sln

# Lệnh: dotnet test AppInHouse.All.sln

```

### c. Đánh giá rủi ro hoặc ghi chú kỹ thuật cho Tech Lead:
- 
