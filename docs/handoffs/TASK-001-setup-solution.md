# TASK-001: Khởi Tạo Solution .NET 8 & Khung Clean Architecture

- **Người giao (Tech Lead)**: Gemini Orchestrator
- **Người thực hiện (Implementer)**: Claude Code
- **Trạng thái**: `PENDING`
- **Ngày tạo**: 2026-10-27
- **Tài liệu tham chiếu**: [docs/architecture/system_design.md](../architecture/system_design.md)

---

## 1. Mục Tiêu Kỹ Thuật

Thiết lập nền tảng dự án .NET 8 theo tiêu chuẩn Clean Architecture cho hệ thống **Centralized Identity Server + Admin Portal**.
Solution phải biên dịch được ngay lập tức, phân tách tầng rõ ràng, liên kết reference đúng quy tắc phụ thuộc (Dependency Rule: Presentation -> Infrastructure -> Application -> Domain / SharedKernel).

---

## 2. Phạm Vi File Được Phép Tạo / Sửa (File Scope & Whitelist)

> **LƯU Ý QUAN TRỌNG DÀNH CHO CLAUDE CODE**:
> Chỉ được phép tạo và chỉnh sửa các file thuộc phạm vi dưới đây. Tuyệt đối không sửa `AGENTS.md`, `CLAUDE.md`, hoặc các file trong `docs/`.

- [ ] `AppInHouse.Identity.sln` (Solution file ở thư mục gốc)
- [ ] `Directory.Build.props` (Cấu hình chung: TargetFramework `net8.0`, `Nullable: enable`, `ImplicitUsings: enable`)
- [ ] `src/Core/AppInHouse.Identity.Domain/*`
- [ ] `src/Core/AppInHouse.Identity.Application/*`
- [ ] `src/Core/AppInHouse.Identity.SharedKernel/*`
- [ ] `src/Infrastructure/AppInHouse.Identity.Infrastructure.Persistence/*`
- [ ] `src/Infrastructure/AppInHouse.Identity.Infrastructure.Identity/*`
- [ ] `src/Infrastructure/AppInHouse.Identity.Infrastructure.Caching/*`
- [ ] `src/Infrastructure/AppInHouse.Identity.Infrastructure.Logging/*`
- [ ] `src/Presentation/AppInHouse.Identity.STS/*`
- [ ] `src/Presentation/AppInHouse.Identity.Admin.Api/*`
- [ ] `src/Presentation/AppInHouse.Identity.Admin.Web/*`
- [ ] `src/Sdk/AppInHouse.Identity.ClientSdk/*`
- [ ] `tests/AppInHouse.Identity.UnitTests/*`
- [ ] `tests/AppInHouse.Identity.IntegrationTests/*`

---

## 3. Yêu Cầu Chi Tiết & Ràng Buộc Phụ Thuộc (Project References)

1. **Tạo Solution**:
   ```bash
   dotnet new sln -n AppInHouse.Identity
   ```
2. **Quy tắc References (Tuân thủ Clean Architecture)**:
   - `SharedKernel`: Độc lập, không phụ thuộc project nội bộ nào.
   - `Domain`: Phụ thuộc `SharedKernel`.
   - `Application`: Phụ thuộc `Domain`, `SharedKernel`.
   - `Infrastructure.Persistence`: Phụ thuộc `Application`, `Domain`.
   - `Infrastructure.Identity`: Phụ thuộc `Application`, `Domain`.
   - `Infrastructure.Caching`: Phụ thuộc `Application`.
   - `Infrastructure.Logging`: Phụ thuộc `Application`.
   - `ClientSdk`: Phụ thuộc `SharedKernel`.
   - `STS` (Presentation): Phụ thuộc `Infrastructure.Identity`, `Infrastructure.Persistence`, `Infrastructure.Logging`, `Application`.
   - `Admin.Api` (Presentation): Phụ thuộc `Infrastructure.Persistence`, `Infrastructure.Caching`, `Infrastructure.Logging`, `Application`.
   - `Admin.Web` (Presentation): Phụ thuộc `Infrastructure.Persistence`, `Infrastructure.Caching`, `Application`.
   - `UnitTests`: Phụ thuộc `Domain`, `Application`, `ClientSdk`.
   - `IntegrationTests`: Phụ thuộc `Admin.Api`, `STS`.

3. **Cài đặt Package NuGet cơ bản ban đầu**:
   - `OpenIddict.AspNetCore` (phiên bản `5.*` cho .NET 8)
   - `Microsoft.EntityFrameworkCore` & `Microsoft.EntityFrameworkCore.SqlServer` (`8.*`)
   - `Microsoft.AspNetCore.Identity.EntityFrameworkCore` (`8.*`)
   - `Serilog.AspNetCore` (`8.*`)
   - `Swashbuckle.AspNetCore` (`6.5.*` cho Swagger của Admin.Api)
   - `xunit`, `FluentAssertions`, `Moq` cho test projects.

---

## 4. Tiêu Chí Nghiệm Thu (Acceptance Criteria)

- [ ] Lệnh `dotnet build AppInHouse.Identity.sln` chạy thành công `0 Error(s), 0 Warning(s)`.
- [ ] Lệnh `dotnet test AppInHouse.Identity.sln` chạy thành công.
- [ ] Tất cả các project nằm đúng thư mục `src/` và `tests/` theo sơ đồ Clean Architecture.
- [ ] Không có circular dependency (phụ thuộc vòng tròn).
- [ ] File report được điền đầy đủ bên dưới.

---

## 5. Báo Cáo Triển Khai (Claude Code điền khi hoàn thành)

### a. Danh sách project và file đã tạo:
- 

### b. Kết quả kiểm thử thực tế (Output terminal):
```text
# Lệnh: dotnet build AppInHouse.Identity.sln

# Lệnh: dotnet test AppInHouse.Identity.sln

```

### c. Đánh giá rủi ro hoặc ghi chú kỹ thuật cho Tech Lead:
- 
