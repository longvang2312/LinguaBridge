# TASK-008: Cấu Hình Đồng Bộ Cổng (Ports) Theo Chuẩn Skoruba

## 1. Thông Tin Chung

- **Mã Task**: TASK-008
- **Tên Task**: Điều chỉnh cổng khởi chạy của STS, Admin Web và Admin API theo chuẩn Skoruba IdentityServer4
- **Người Giao (Tech Lead)**: Gemini
- **Người Nhận (Implementer)**: Claude Code
- **Trạng Thái**: `APPROVED`
- **Phụ Thuộc**: Hoàn thành sau TASK-007

---

## 2. Mục Tiêu & Bối Cảnh

Hiện tại hệ thống đang sử dụng các cổng mặc định phát sinh từ template ASP.NET Core (`7046`, `7111`, `7267`/`7192`). Để thuận tiện cho việc đối chiếu kiến trúc và quen thuộc với hệ sinh thái Skoruba, toàn bộ các cổng sẽ được đồng bộ theo cấu hình tiêu chuẩn của Skoruba IdentityServer4:

1. **Security Token Service (STS)**:
   - HTTPS: `https://localhost:44310`
   - HTTP: `http://localhost:5001`
2. **Admin Web Portal (UI)**:
   - HTTPS: `https://localhost:44303`
   - HTTP: `http://localhost:5000`
3. **Admin REST API**:
   - HTTPS: `https://localhost:44302`
   - HTTP: `http://localhost:5002`

Đồng thời cập nhật toàn bộ cấu hình phụ thuộc:
- `Authority` của Admin Web trỏ tới `https://localhost:44310/`
- Redirect URIs của client `admin-portal` trong STS Seeder trỏ tới `https://localhost:44303/signin-oidc` và post-logout redirect tới `https://localhost:44303/signout-callback-oidc`
- Cập nhật `StsWebApplicationFactory.cs` trong bộ Integration Tests.
- Cập nhật tài liệu hướng dẫn `repos/identity/README.md`.

---

## 3. Phạm Vi File Cấp Phép (File Scope)

Claude Code **chỉ được phép** tạo hoặc sửa các file sau:

- `repos/identity/src/Presentation/AppInHouse.Identity.STS/Properties/launchSettings.json`
- `repos/identity/src/Presentation/AppInHouse.Identity.STS/appsettings.Development.json`
- `repos/identity/src/Presentation/AppInHouse.Identity.Admin.Web/Properties/launchSettings.json`
- `repos/identity/src/Presentation/AppInHouse.Identity.Admin.Web/appsettings.Development.json`
- `repos/identity/src/Presentation/AppInHouse.Identity.Admin.Api/Properties/launchSettings.json`
- `repos/identity/tests/AppInHouse.Identity.IntegrationTests/StsWebApplicationFactory.cs`
- `repos/identity/README.md`

> **Nghiêm cấm sửa**: Mọi file nằm ngoài danh sách trên, đặc biệt là `AGENTS.md` và `docs/architecture/*`.

---

## 4. Chi Tiết Yêu Cầu Kỹ Thuật

### 4.1. `AppInHouse.Identity.STS/Properties/launchSettings.json`
Đổi `applicationUrl` trong profile `https` (hoặc profile mặc định khi chạy):
- HTTPS: `https://localhost:44310;http://localhost:5001`

### 4.2. `AppInHouse.Identity.STS/appsettings.Development.json`
Cập nhật danh sách redirect URIs cho client `admin-portal`:
```json
"AdminPortalRedirectUris": [ "https://localhost:44303/signin-oidc" ],
"AdminPortalPostLogoutRedirectUris": [ "https://localhost:44303/signout-callback-oidc" ]
```

### 4.3. `AppInHouse.Identity.Admin.Web/Properties/launchSettings.json`
Đổi `applicationUrl`:
- HTTPS: `https://localhost:44303;http://localhost:5000`

### 4.4. `AppInHouse.Identity.Admin.Web/appsettings.Development.json`
Cập nhật địa chỉ `Authority` của STS:
```json
"Authentication": {
  "Authority": "https://localhost:44310/"
}
```

### 4.5. `AppInHouse.Identity.Admin.Api/Properties/launchSettings.json`
Đổi `applicationUrl`:
- HTTPS: `https://localhost:44302;http://localhost:5002`

### 4.6. `AppInHouse.Identity.IntegrationTests/StsWebApplicationFactory.cs`
Cập nhật hằng số redirect URI test:
- `AdminPortalRedirectUri = "https://localhost:44303/signin-oidc"`
- Cấu hình test `"Seeding:AdminPortalPostLogoutRedirectUris:0" = "https://localhost:44303/signout-callback-oidc"`

### 4.7. `repos/identity/README.md`
Cập nhật mục **5. Hướng Dẫn Khởi Chạy Ứng Dụng (Run & Launch)** và các đường dẫn URL:
- STS: `https://localhost:44310` (Discovery: `https://localhost:44310/.well-known/openid-configuration`, Login: `https://localhost:44310/Account/Login`)
- Admin REST API: `https://localhost:44302` (Swagger: `https://localhost:44302/swagger`)
- Admin Web Portal UI: `https://localhost:44303`
- Client `admin-portal` Redirect URI: `https://localhost:44303/signin-oidc`
- Đoạn code mẫu cấu hình client OIDC: `options.Authority = "https://localhost:44310"`

---

## 5. Tiêu Chí Nghiệm Thu (Acceptance Criteria)

- [ ] Tất cả các file trong danh sách File Scope được cập nhật chính xác theo các cổng Skoruba:
  - STS: `44310`
  - Admin Web: `44303`
  - Admin API: `44302`
- [ ] Chạy lệnh `dotnet build AppInHouse.sln` thành công 100%, không phát sinh cảnh báo lỗi.
- [ ] Chạy toàn bộ Unit Tests và Integration Tests (`dotnet test repos/identity/tests/...`) pass 100% (282+ tests).
- [ ] Điền đầy đủ mục 6 "Báo cáo Triển khai" vào phiếu này.

---

## 6. Báo Cáo Triển Khai (Claude Code điền khi hoàn thành)

### a. Danh Sách File Đã Tác Động
- `STS/Properties/launchSettings.json`: https `44310`, http `5001` (cả hai profile `http` và `https`).
- `STS/appsettings.Development.json`: redirect URIs của `admin-portal` → `https://localhost:44303/signin-oidc`, `.../signout-callback-oidc`.
- `Admin.Web/Properties/launchSettings.json`: https `44303`, http `5000`.
- `Admin.Web/appsettings.Development.json`: `Authentication:Authority` = `https://localhost:44310/`.
- `Admin.Api/Properties/launchSettings.json`: https `44302`, http `5002`.
- `IntegrationTests/StsWebApplicationFactory.cs`: `AdminPortalRedirectUri` và post-logout URI → cổng `44303`.
- `repos/identity/README.md`: mục 4 (Redirect URI), mục 5 (cổng + URL STS/API/Web), mục 6, mục 7 (`options.Authority`).

### b. Kiểm Thử Thực Tế (Commands & Output)
```bash
# Lệnh đã chạy:
dotnet build AppInHouse.sln --no-incremental
dotnet test AppInHouse.sln --no-build

# Output:
0 Warning(s), 0 Error(s)
UnitTests:        Passed 170, Failed 0
IntegrationTests: Passed 282, Failed 0
# grep các cổng cũ (7046/7111/7267/5114/5253/5247/5192/5111) trong repos/identity: không còn kết quả.
```

### c. Đánh Giá Rủi Ro & Ghi Chú
- Profile `IIS Express` giữ nguyên (`sslPort` 443xx do VS sinh ra), không nằm trong yêu cầu.
- Cổng `5000/5001/5002` có thể trùng với ứng dụng khác trên máy; khi đó Kestrel báo lỗi bind.
- Client `admin-portal` đã seed trong DB cũ vẫn giữ redirect URI cũ (`7111`). Cần xóa DB dev để seed lại, hoặc sửa client trong Admin Portal.
- Chưa chạy thực tế bằng trình duyệt (cần dev certificate HTTPS); chỉ kiểm tra qua build + test.
