# HƯỚNG DẪN CẤU HÌNH VÀ KHỞI CHẠY DỰ ÁN (SETUP & RUN GUIDE)
## Hệ Sinh Thái AppInHouse: Identity STS & LinguaBridge Platform

Tài liệu này hướng dẫn chi tiết từng bước từ cài đặt môi trường, cấu hình cơ sở dữ liệu, thiết lập secret cho đến thứ tự khởi chạy toàn bộ các dịch vụ trong repository **AppInHouse**.

---

## 1. Bản Đồ Dịch Vụ & Cổng Khởi Chạy (Services & Ports Map)

Toàn bộ hệ thống được đồng bộ hóa theo chuẩn cổng Skoruba và phân bổ cổng riêng biệt, tránh xung đột:

| Dịch vụ / Ứng dụng | Project Path | Giao thức & Cổng | Mô tả / Chức năng |
| :--- | :--- | :--- | :--- |
| **Identity STS** | `repos/identity/src/Presentation/AppInHouse.Identity.STS` | `https://localhost:44310`<br>`http://localhost:5001` | Máy chủ cấp phát mã xác thực OpenIddict (OIDC), cấp token JWT/Introspection |
| **Identity Admin Web** | `repos/identity/src/Presentation/AppInHouse.Identity.Admin.Web` | `https://localhost:44303`<br>`http://localhost:5000` | Web Portal quản trị người dùng, vai trò, OIDC clients & scopes |
| **Identity Admin API** | `repos/identity/src/Presentation/AppInHouse.Identity.Admin.Api` | `https://localhost:44302`<br>`http://localhost:5002` | REST API quản trị (Swagger) cho các client khác; Admin Web **không** gọi API này mà dùng trực tiếp cùng các use case trên cùng CSDL |
| **LinguaBridge API** | `repos/linguabridge/src/Presentation/LinguaBridge.Api` | `https://localhost:44320`<br>`http://localhost:5020` | Modular Monolith API (.NET 10) phục vụ học ngoại ngữ, AI Tutor & Billing |
| **LinguaBridge Web** | `repos/linguabridge-web/LinguaBridge.Web.Host` | `http://localhost:3000` | Web học viên (HTML/JS thuần) kèm proxy cùng origin: `/sts/*` → STS, `/lb/*` → LinguaBridge API. **Phải đúng cổng 3000** vì STS chỉ đăng ký `http://localhost:3000/callback` |
| **SQL Server** | LocalDB / SQLEXPRESS / Docker | `localhost:1433` | Cơ sở dữ liệu cho cụm Identity (`AppInHouse.Identity`) |
| **PostgreSQL** | Local / Docker | `localhost:5432` | Cơ sở dữ liệu cho LinguaBridge (`LinguaBridge` gồm 5 schemas) |

> **Địa chỉ STS khi chạy trên máy (Visual Studio / `dotnet run`) là `http://localhost:5001`** và phải là *đúng chuỗi đó* ở mọi nơi: `Authentication:Authority` của Admin API, Admin Web và LinguaBridge API, `stsUrl` của web (`assets/js/config.js`), và cấu hình mobile. Token chỉ được chấp nhận khi issuer khớp. Bộ Docker Compose dùng một địa chỉ khác (`http://host.docker.internal:5001`, xem Cách 3): đừng trộn hai kiểu trong cùng một lần chạy.

---

## 2. Yêu Cầu Môi Trường (Prerequisites)

Trước khi bắt đầu, hãy đảm bảo máy tính đã cài đặt các công cụ sau:

1. **.NET SDK**:
   - Bắt buộc cài đặt **.NET 10 SDK** (phiên bản `10.0.102` trở lên).
   - SDK .NET 10 tự động tương thích và biên dịch được cả các project target `net8.0` (Identity) và `net10.0` (LinguaBridge).
   - Kiểm tra bằng lệnh: `dotnet --info`.
2. **EF Core CLI Tools (`dotnet-ef`)**:
   - Cài đặt công cụ EF Core toàn cục:
     ```bash
     dotnet tool install --global dotnet-ef --version 10.*
     ```
   - Nếu đã cài bản cũ, cập nhật bằng: `dotnet tool update --global dotnet-ef`.
3. **Hệ Quản Trị Cơ Sở Dữ Liệu**:
   - **PostgreSQL 16/17**: Dành cho LinguaBridge.
   - **SQL Server**: (SQL Server Developer, Express hoặc LocalDB) dành cho Identity.
4. **Docker Desktop** (Khuyến nghị):
   - Cần bật Docker khi chạy bộ kiểm thử Integration Tests (Testcontainers tự động kích hoạt PostgreSQL container).
   - Có thể dùng Docker để chạy cả SQL Server và PostgreSQL local nếu máy chưa cài trực tiếp.

---

## 3. Cài Đặt & Khởi Tạo Cơ Sở Dữ Liệu

### 3.1. Chạy CSDL Bằng Docker (Nhanh nhất)

Nếu máy chưa cài sẵn SQL Server và PostgreSQL, bạn có thể khởi chạy nhanh bằng 2 lệnh Docker:

```bash
# 1. Khởi chạy PostgreSQL (Port 5432) cho LinguaBridge:
docker run -d --name appinhouse-postgres \
  -e POSTGRES_DB=LinguaBridge \
  -e POSTGRES_USER=postgres \
  -e POSTGRES_PASSWORD=postgres \
  -p 5432:5432 postgres:17-alpine

# 2. Khởi chạy SQL Server (Port 1433) cho Identity:
docker run -d --name appinhouse-sqlserver \
  -e "ACCEPT_EULA=Y" \
  -e "MSSQL_SA_PASSWORD=Abc@1234" \
  -p 1433:1433 mcr.microsoft.com/mssql/server:2022-latest
```

#### Hoặc cài trực tiếp trên Windows (không cần Docker)

- **PostgreSQL 16**: `winget install PostgreSQL.PostgreSQL.16 --override "--mode unattended --superpassword postgres --serverport 5432"`, rồi tạo database: `createdb -U postgres LinguaBridge` (công cụ nằm ở `C:\Program Files\PostgreSQL\16\bin`). Mật khẩu `postgres` khớp với chuỗi kết nối mặc định bên dưới.
- **SQL Server**: dùng SQL Server Developer/Express đã cài (`sa` / `Abc@1234` theo chuỗi kết nối mặc định). Database `AppInHouse.Identity` do STS tự tạo.
- Chuyển dữ liệu từ một PostgreSQL cũ (ví dụ container): `pg_dump -U postgres -d LinguaBridge -Fc -f lb.dump`, rồi `pg_restore -U postgres -d LinguaBridge --no-owner lb.dump` trên database mới.

### 3.2. Cấu Hình Chuỗi Kết Nối (Connection Strings)

- **Identity**: Kiểm tra file `repos/identity/src/Presentation/AppInHouse.Identity.STS/appsettings.json`:
  ```json
  "ConnectionStrings": {
    "IdentityDb": "Server=localhost;Database=AppInHouse.Identity;User Id=sa;Password=Abc@1234;MultipleActiveResultSets=true;TrustServerCertificate=True"
  }
  ```
- **LinguaBridge**: Kiểm tra file `repos/linguabridge/src/Presentation/LinguaBridge.Api/appsettings.Development.json`:
  ```json
  "ConnectionStrings": {
    "LinguaBridgeDb": "Host=localhost;Port=5432;Database=LinguaBridge;Username=postgres;Password=postgres"
  }
  ```

### 3.3. Áp Dụng Migration Vào Database

#### A. Database Identity (SQL Server)
Identity đã tích hợp sẵn cơ chế **Auto-Migration & Seeding** khi khởi chạy. Bạn chỉ cần khởi chạy `AppInHouse.Identity.STS`, hệ thống sẽ tự động tạo bảng và nạp dữ liệu mẫu (Admin account, Default Scopes, Clients) theo mục `Seeding` trong `STS/appsettings.Development.json` (gồm client `linguabridge-app`, `identity_admin_api`, `admin-portal` và các redirect URI `http://localhost:5000/...`).

> Seeder **chỉ tạo client khi chưa có, không cập nhật client đã tồn tại**. Nếu bạn sửa mục `Seeding` sau khi database đã được tạo, thay đổi sẽ không có hiệu lực: sửa trực tiếp bảng `OpenIddictApplications` hoặc xoá database `AppInHouse.Identity` (mất mọi dữ liệu Identity, gồm tài khoản đã tạo) rồi khởi động lại STS.

#### B. Database LinguaBridge (PostgreSQL)
Chạy lệnh EF Core Migration để tạo 5 schemas (`content`, `learning`, `srs`, `ai`, `billing`) và 18 bảng:

```bash
dotnet ef database update \
  --project repos/linguabridge/src/Infrastructure/LinguaBridge.Infrastructure.Persistence \
  --startup-project repos/linguabridge/src/Infrastructure/LinguaBridge.Infrastructure.Persistence
```

---

## 4. Thiết Lập Cấu Hình Bí Mật (Secrets & Configuration)

Trong môi trường Development, các cấu hình dev mặc định đã được thiết lập sẵn trong `appsettings.Development.json`. Nếu chạy môi trường Production hoặc muốn kết nối dịch vụ thật, hãy cấu hình theo bảng sau:

### 4.1. Cấu Hình Tích Hợp Identity STS ↔ LinguaBridge
Hai dịch vụ kết nối với nhau qua cơ chế OpenIddict Introspection. Secret được đồng bộ như sau:
- **Tại STS** (`repos/identity/.../STS/appsettings.Development.json`):
  ```json
  "Seeding": {
    "LinguaBridgeApiClientSecret": "dev-linguabridge-api-secret-change-me"
  }
  ```
- **Tại LinguaBridge** (`repos/linguabridge/.../Api/appsettings.Development.json`):
  ```json
  "Authentication": {
    "Authority": "http://localhost:5001",
    "ClientId": "linguabridge_api",
    "ClientSecret": "dev-linguabridge-api-secret-change-me",
    "Audience": "linguabridge_api"
  }
  ```

### 4.2. Cấu Hình AI Tutor (Module AiTutor)
Đặt trong `repos/linguabridge/src/Presentation/LinguaBridge.Api/appsettings.json` (hoặc dùng `dotnet user-secrets`):
```json
"OpenAI": {
  "ApiKey": "sk-proj-YOUR_OPENAI_API_KEY",
  "Model": "gpt-4o-mini",
  "MaxOutputTokens": 600,
  "TimeoutSeconds": 30
},
"AiTutor": {
  "FreeDailyRequests": 10
}
```

### 4.3. Cấu Hình Cổng Thanh Toán (Module Billing Webhooks)
Đặt trong `repos/linguabridge/src/Presentation/LinguaBridge.Api/appsettings.Development.json`:
```json
"Billing": {
  "Webhooks": {
    "VNPay": {
      "SecretKey": "dev-vnpay-hash-secret-change-me"
    },
    "Stripe": {
      "SecretKey": "whsec_dev_change_me",
      "ToleranceSeconds": 300
    }
  }
}
```

---

## 5. Hướng Dẫn Khởi Chạy Ứng Dụng (Run & Launch)

### Cách 1: Khởi Chạy Bằng Visual Studio (Thuận Tiện Nhất)

1. Mở file solution gốc [AppInHouse.sln](../../AppInHouse.sln) bằng Visual Studio 2022 / 2025.
2. Chuột phải vào **Solution 'AppInHouse'** trên cửa sổ Solution Explorer → Chọn **Configure Startup Projects...**
3. Chọn tùy chọn **Multiple startup projects**:
   - `AppInHouse.Identity.STS`: Chọn **Start**
   - `AppInHouse.Identity.Admin.Web`: Chọn **Start**
   - `LinguaBridge.Api`: Chọn **Start**
   - `LinguaBridge.Web.Host` (thư mục solution `LinguaBridge.Web`): Chọn **Start** (web học viên tại `http://localhost:3000`)
   - `AppInHouse.Identity.Admin.Api` để **None** (Admin Web không gọi nó); chỉ bật **Start** khi cần Swagger hay ClientSdk của Admin API.
4. Nhấn **F5** (hoặc **Ctrl + F5** để chạy không debug). Cả 4 ứng dụng sẽ khởi chạy theo thứ tự trong danh sách, **STS phải đứng đầu**. Visual Studio không đợi STS xong: STS cần vài giây để migrate và seed, nếu lần đầu đăng nhập lỗi thì đợi rồi tải lại trang.

Profile này được lưu trong `AppInHouse.slnLaunch.user` (bị `.gitignore` bỏ qua, chỉ có trên máy bạn).

---

### Cách 2: Khởi Chạy Bằng Terminal / Command Line

Mở các cửa sổ Terminal riêng biệt tại thư mục gốc workspace `e:\Projects\AppInHouse`:

#### Terminal 1: Khởi Chạy Identity STS
```bash
dotnet run --project repos/identity/src/Presentation/AppInHouse.Identity.STS --launch-profile https
```
> Trực quan hóa tại: `https://localhost:44310`  
> Discovery doc: `https://localhost:44310/.well-known/openid-configuration`

#### Terminal 2: Khởi Chạy Identity Admin Web Portal
```bash
dotnet run --project repos/identity/src/Presentation/AppInHouse.Identity.Admin.Web --launch-profile https
```
> Giao diện quản trị: `https://localhost:44303`  
> Tài khoản mặc định (do seeder tạo ở môi trường Development): `admin@appinhouse.local` / `Admin@AppInHouse123!`

#### Terminal 3: Khởi Chạy LinguaBridge API
```bash
dotnet run --project repos/linguabridge/src/Presentation/LinguaBridge.Api --launch-profile https
```
> Healthcheck: `https://localhost:44320/health`  
> Swagger UI: `https://localhost:44320/swagger`

#### Terminal 4: Khởi Chạy Web Học Viên (LinguaBridge Web Host)
```bash
dotnet run --project repos/linguabridge-web/LinguaBridge.Web.Host --launch-profile http
```
> Web: `http://localhost:3000` (đúng `localhost`, cổng 3000, không dùng `127.0.0.1`)
> Proxy: `/sts/*` → `http://localhost:5001`, `/lb/*` → `http://localhost:5020` (đổi bằng `Web__StsUrl`, `Web__ApiUrl`). Không chạy đồng thời với `scripts/start-web.ps1` vì cùng cổng.

> Dù chạy bằng profile `https` hay `http`, các dịch vụ gọi STS qua **`http://localhost:5001`** (STS lắng nghe cả hai cổng khi dùng profile `https`).

---

### Cách 3: Chạy Cụm Identity Bằng Docker Desktop (Docker Compose)

Chạy toàn bộ Identity Platform (SQL Server 2022 + STS + Admin API + Admin Web) trong container, không cần cài SQL Server hay .NET 8 trên máy. Cấu hình nằm ở `docker-compose.identity.yml` (thư mục gốc workspace). LinguaBridge **không** nằm trong compose này, vẫn chạy theo Cách 1 hoặc 2.

> Cách này dùng địa chỉ STS `http://host.docker.internal:5001`, **khác** với Cách 1 và 2 (`http://localhost:5001`). Compose tự đặt `IDENTITY_AUTHORITY` cho các container, còn `appsettings.Development.json` và `config.js` trong repo trỏ `localhost`. Đừng chạy lẫn với STS chạy trên máy (cùng cổng 5001).

**Yêu cầu**: Docker Desktop đang chạy và có kết nối mạng ở lần đầu (tải image SQL Server ~1.6 GB và các image .NET).

#### Chạy, dừng, xoá dữ liệu
```powershell
.\scripts\docker-identity-up.ps1                 # build image, khởi động, đợi tới khi cả 4 service "healthy", rồi in các URL
.\scripts\docker-identity-up.ps1 -NoBuild        # dùng lại image đã build
.\scripts\docker-identity-down.ps1               # dừng, GIỮ dữ liệu (CSDL, key, chứng chỉ) cho lần chạy sau
.\scripts\docker-identity-down.ps1 -RemoveData   # dừng và XOÁ dữ liệu (hỏi xác nhận; thêm -Force để bỏ qua)
.\scripts\verify-identity-e2e.ps1                # kiểm tra toàn trình (xem bên dưới)
```
Lần chạy đầu mất vài phút (build + tải image); các lần sau khoảng nửa phút. Cũng có thể dùng trực tiếp `docker compose -f docker-compose.identity.yml up -d --build` và `down`.

#### Bảng cổng và URL

| Dịch vụ | URL trên máy host | Ghi chú |
| :--- | :--- | :--- |
| **STS** | `http://host.docker.internal:5001` | Discovery: `/.well-known/openid-configuration`. **Luôn dùng tên `host.docker.internal`**, không dùng `localhost` (xem mục dưới) |
| **Admin Web** | `http://localhost:5000` | Giao diện quản trị |
| **Admin API** | `http://localhost:5002/swagger` | Swagger (môi trường Development) |
| **SQL Server** | `localhost,1433` hoặc `localhost,14330` | User `sa`, database `AppInHouse_Identity`. Script tự chuyển sang **14330** nếu cổng 1433 đang bị chiếm (ví dụ SQL Server cài sẵn trên máy). Trong mạng Docker các service luôn nối `identity-db:1433` |

Đổi cổng/địa chỉ bằng biến môi trường (hoặc file `.env` cạnh compose): `IDENTITY_DB_PORT`, `IDENTITY_BIND_ADDRESS`, `SA_PASSWORD`, `ADMIN_PASSWORD`, `ADMIN_API_CLIENT_SECRET`, `IDENTITY_AUTHORITY`, `ADMIN_WEB_URL`. Mọi giá trị mặc định chỉ dành cho phát triển.

#### Tài khoản đăng nhập mẫu
`admin@appinhouse.local` / `Admin@AppInHouse123!` (do seeder tạo; đổi bằng `ADMIN_PASSWORD` trước lần chạy đầu tiên). Mật khẩu này công khai trong repository.

#### Vì sao phải dùng `host.docker.internal` (cơ chế issuer)
Mỗi token do STS cấp mang một **issuer**, và OpenIddict lấy issuer từ *tên máy trong request* (Host header). Admin API xác thực token bằng cách hỏi lại STS ở một địa chỉ cố định và chỉ chấp nhận token có issuer **đúng bằng chuỗi đó**. Trình duyệt, Admin Web, Admin API và người dùng Swagger/Postman đều phải gọi STS bằng *cùng một địa chỉ*. `localhost` không dùng được: bên trong container, `localhost` là chính container đó. Docker Desktop phân giải `host.docker.internal` trỏ về máy host cả trong container lẫn trên host (qua file `hosts`), nên địa chỉ chung là `http://host.docker.internal:5001`.

Hệ quả cần nhớ:
- Khi đăng nhập Admin Web, trình duyệt được chuyển sang `http://host.docker.internal:5001/Account/Login` (không phải `localhost:5001`). Nếu trang này không mở được, xem mục Troubleshooting số 6.
- Lấy token qua `http://localhost:5001/connect/token` thì **Admin API trả 401** vì issuer là `localhost`. Hãy lấy token qua `http://host.docker.internal:5001/connect/token`.
- Lấy token thử bằng dòng lệnh:
  ```powershell
  curl.exe -s -X POST http://host.docker.internal:5001/connect/token -d grant_type=client_credentials -d client_id=internal-service -d client_secret=dev-internal-service-secret-change-me -d scope=identity_admin_api
  ```
  Token dịch vụ này được Admin API chấp nhận nhưng nhận **403** ở các API quản trị (nó không phải người dùng quản trị). Muốn có 200 cần token của người dùng quản trị (luồng authorization code, xem mục 4 của `scripts/verify-identity-e2e.ps1`).

#### Kiểm tra toàn trình
`.\scripts\verify-identity-e2e.ps1` (cần stack đang chạy) tự thực hiện, không cần trình duyệt: discovery + JWKS; token dịch vụ (401 / 401 / 403); đăng nhập Admin Web qua STS (login → `/signin-oidc` → Dashboard → Users, Roles, Clients; sai mật khẩu và truy cập ẩn danh bị chặn); token người dùng quản trị (authorization code + PKCE) gọi Admin API `users`, `roles`, `clients`, `permissions` → 200. Thoát mã 0 nếu mọi mục đạt, mã 1 nếu có mục lỗi và liệt kê các mục lỗi. Script mô phỏng trình duyệt qua HTTP; bạn cũng nên tự thử một lần bằng trình duyệt thật: mở `http://localhost:5000`, đăng nhập bằng tài khoản mẫu, xem Dashboard, Users, Roles, Clients.

#### Dữ liệu được giữ ở đâu
Ba volume Docker: `identity-db-data` (CSDL), `identity-dataprotection-keys` (khoá Data Protection) và `identity-sts-certificates` (chứng chỉ ký/mã hoá token của STS). `down` giữ cả ba; `down -RemoveData` xoá hết, token và phiên đăng nhập cũ không còn hiệu lực.

#### Bảo mật khi chạy bằng Docker
- SQL Server, Admin API và Admin Web chỉ lắng nghe trên `127.0.0.1` của máy bạn (đặt `IDENTITY_BIND_ADDRESS=0.0.0.0` nếu thật sự muốn mở ra mạng).
- **STS (cổng 5001) mở ra cả mạng cục bộ**, vì `host.docker.internal` trỏ tới địa chỉ mạng của máy chứ không phải `127.0.0.1`. Ai cùng mạng có thể thử đăng nhập bằng tài khoản mẫu có mật khẩu công khai. Chỉ chạy trên mạng tin cậy hoặc đổi `ADMIN_PASSWORD`.
- Cấu hình này là **chế độ Development** (Swagger bật, không yêu cầu HTTPS, chứng chỉ phát triển, mật khẩu/secret mẫu). Không dùng cho môi trường thật.

---

## 6. Kiểm Thử Hệ Thống (Testing & Verification)

### 6.1. Chạy Toàn Bộ Unit & Integration Tests

```bash
# 1. Chạy toàn bộ 690 test của LinguaBridge (gồm cả PostgreSQL Testcontainers):
dotnet test repos/linguabridge/LinguaBridge.sln

# Nếu máy không bật Docker, bỏ qua các test cần PostgreSQL container:
dotnet test repos/linguabridge/LinguaBridge.sln --filter "Category!=Integration"

# 2. Chạy toàn bộ 456 test của cụm Identity:
dotnet test repos/identity/AppInHouse.Identity.sln
```

### 6.2. Smoke Test Thủ Công Bằng `curl` Hoặc Browser

1. **Kiểm tra Healthcheck**:
   ```bash
   curl -k https://localhost:44320/health
   # Kết quả mong đợi:
   # {"status":"Healthy","module":"LinguaBridge.Api","timestamp":"..."}
   ```
2. **Kiểm tra cơ chế Deny-By-Default (401 Unauthorized)**:
   ```bash
   curl -k -i https://localhost:44320/api/v1/me
   # Kết quả mong đợi: HTTP/1.1 401 Unauthorized
   ```
3. **Kiểm tra Danh sách Khóa học công khai**:
   ```bash
   curl -k -i https://localhost:44320/api/v1/content/courses
   # Kết quả mong đợi: HTTP/1.1 200 OK (Trả về mảng JSON rỗng [] hoặc danh sách khóa học Published)
   ```
4. **Kiểm tra Danh sách Gói cước thanh toán**:
   ```bash
   curl -k -i https://localhost:44320/api/v1/billing/plans
   # Kết quả mong đợi: HTTP/1.1 200 OK
   ```

---

## 7. Tổng Hợp Các Endpoint API Chính Của LinguaBridge

| Phân hệ | Phương thức | Đường dẫn URL | Quyền hạn | Mục đích sử dụng |
| :--- | :--- | :--- | :--- | :--- |
| **System** | `GET` | `/health` | Anonymous | Kiểm tra trạng thái máy chủ |
| **Auth** | `GET` | `/api/v1/me` | Bearer Token | Lấy thông tin user hiện tại (`userId`, `clientId`) |
| **Content** | `GET` | `/api/v1/content/courses` | Bearer (Learner) | Xem các khóa học đã Publish |
| | `GET` | `/api/v1/content/courses/admin` | `GlobalAdmin` | Xem tất cả khóa học (Draft, Review, Archived) |
| | `POST` | `/api/v1/content/courses` | `GlobalAdmin` | Tạo khóa học mới |
| | `PUT` | `/api/v1/content/courses/{id}/publish` | `GlobalAdmin` | Duyệt xuất bản khóa học |
| | `GET` | `/api/v1/content/lessons/{id}/preview` | `GlobalAdmin` | Xem trước bài học và câu hỏi |
| **Learning** | `GET` | `/api/v1/learning/lessons/{id}/quiz` | Bearer (Learner) | Lấy câu hỏi làm bài (giấu đáp án `isCorrect`) |
| | `POST` | `/api/v1/learning/attempts` | Bearer (Learner) | Bắt đầu bài làm mới |
| | `POST` | `/api/v1/learning/attempts/{id}/responses` | Bearer (Learner) | Gửi câu trả lời & nhận kết quả đúng/sai |
| | `PUT` | `/api/v1/learning/attempts/{id}/complete` | Bearer (Learner) | Hoàn thành bài làm & nhận điểm XP |
| | `GET` | `/api/v1/learning/srs/due` | Bearer (Learner) | Danh sách thẻ từ vựng cần ôn hôm nay |
| | `POST` | `/api/v1/learning/srs/review` | Bearer (Learner) | Đánh giá thẻ SRS (Rating 1 - 4) |
| | `GET` | `/api/v1/learning/progress/{courseId}` | Bearer (Learner) | Xem tiến độ học khóa học |
| **AI Tutor** | `POST` | `/api/v1/ai/sessions` | Bearer (Learner) | Tạo phiên hội thoại với AI Tutor |
| | `POST` | `/api/v1/ai/sessions/{id}/messages` | Bearer (Learner) | Nhắn tin & nhận phản hồi từ AI |
| | `GET` | `/api/v1/ai/sessions/{id}/messages` | Bearer (Learner) | Xem lịch sử trò chuyện |
| | `GET` | `/api/v1/ai/quota` | Bearer (Learner) | Kiểm tra hạn mức token/lượt còn lại hôm nay |
| **Billing** | `GET` | `/api/v1/billing/plans` | Anonymous | Xem bảng giá gói cước |
| | `POST` | `/api/v1/billing/plans` | `GlobalAdmin` | Tạo gói cước mới |
| | `GET` | `/api/v1/billing/entitlement` | Bearer (Learner) | Xem trạng thái VIP & ngày hết hạn |
| | `POST` | `/api/v1/billing/webhooks/{provider}` | Gateway Signature | Nhận Webhook thanh toán an toàn (Idempotent) |

---

## 8. Xử Lý Các Sự Cố Thường Gặp (Troubleshooting)

1. **Lỗi `MSB3027 / MSB3026` (Cannot copy file because it is locked)**:
   - *Nguyên nhân*: Visual Studio hoặc terminal đang chạy tiến trình `AppInHouse.Identity.STS`, khóa file DLL trong thư mục `bin/`.
   - *Khắc phục*: Tắt các ứng dụng đang chạy trước khi chạy lệnh `dotnet build AppInHouse.sln`.
2. **Lỗi `503 Service Unavailable` khi gọi AI Tutor**:
   - *Nguyên nhân*: Chưa cung cấp `OpenAI:ApiKey` trong cấu hình.
   - *Khắc phục*: Đặt key qua biến môi trường `OpenAI__ApiKey=sk-...` hoặc bổ sung vào `appsettings.json`.
3. **Lỗi `500 Server Error (Timeout 35s)` khi gọi API có Bearer Token**:
   - *Nguyên nhân*: Máy chủ Identity STS (`http://localhost:5001`) chưa được bật, khiến `LinguaBridge.Api` không thể kết nối để introspect token.
   - *Khắc phục*: Khởi chạy `AppInHouse.Identity.STS` trước khi gọi các endpoint có yêu cầu xác thực.
4. **Lỗi `429 Too Many Requests (ai.quota_exhausted)`**:
   - *Nguyên nhân*: Học viên tài khoản miễn phí đã dùng hết 10 lượt tương tác AI trong ngày (tính theo UTC).
   - *Khắc phục*: Chờ sang ngày mới hoặc nâng cấp tài khoản lên gói VIP qua Webhook.
5. **Admin API trả `401 Unauthorized` dù token vừa lấy từ STS (chạy bằng Docker)**:
   - *Nguyên nhân*: token được lấy qua `http://localhost:5001`, nên issuer là `localhost`; Admin API chỉ chấp nhận issuer `http://host.docker.internal:5001`.
   - *Khắc phục*: lấy token qua `http://host.docker.internal:5001/connect/token` (xem Cách 3).
6. **Không mở được `http://host.docker.internal:5001` trên trình duyệt**:
   - *Nguyên nhân*: Docker Desktop chưa thêm tên `*.docker.internal` vào file `hosts` của Windows.
   - *Khắc phục*: bật Settings → General → *Add the \*.docker.internal names to the host's /etc/hosts file* rồi khởi động lại Docker Desktop; hoặc thêm dòng `127.0.0.1 host.docker.internal` vào `C:\Windows\System32\drivers\etc\hosts`.
7. **Cổng 1433 đã bị chiếm khi chạy cụm Identity bằng Docker**:
   - *Nguyên nhân*: máy đã có SQL Server cài sẵn. Docker vẫn có thể bind được cổng, khi đó có hai tiến trình cùng nghe 1433 và kết nối `localhost,1433` có thể vào nhầm SQL Server của máy.
   - *Khắc phục*: dùng `docker-identity-up.ps1` (tự chọn cổng 14330) hoặc đặt `IDENTITY_DB_PORT=14330`, rồi kết nối tới `localhost,14330`.
8. **`docker-identity-up.ps1` báo "Not healthy after ... s"**:
   - *Nguyên nhân*: STS đang migrate/seed CSDL hoặc SQL Server chưa sẵn sàng (máy chậm, lần chạy đầu).
   - *Khắc phục*: chạy `docker compose -f docker-compose.identity.yml logs --tail=100` xem service nào lỗi; tăng `-TimeoutSeconds`.
9. **API trả `401` dù đăng nhập thành công (chạy trên máy)**:
   - *Nguyên nhân*: issuer của token không khớp `Authentication:Authority` của API (ví dụ token lấy qua `host.docker.internal` hoặc `127.0.0.1`, trong khi API cấu hình `http://localhost:5001`), hoặc token thiếu scope `linguabridge_api` (log API ghi "token doesn't contain any audience").
   - *Khắc phục*: dùng đúng `http://localhost:5001` ở mọi nơi; xin scope `openid profile email offline_access linguabridge_api`.
10. **Admin Web báo `invalid_request ... redirect_uri is not valid`**:
    - *Nguyên nhân*: client `admin-portal` trong database được tạo từ lúc chưa có redirect `http://localhost:5000/signin-oidc` và seeder không cập nhật client có sẵn.
    - *Khắc phục*: xem ghi chú ở mục 3.3.A (sửa `OpenIddictApplications` hoặc tạo lại database).
11. **Web (`localhost:3000`) không đăng nhập hoặc không gọi được API**:
    - *Nguyên nhân thường gặp*: cổng 3000 đang bị tiến trình khác chiếm (ví dụ `start-web.ps1` còn chạy), trang mở bằng `127.0.0.1` thay vì `localhost`, hoặc STS/API chưa chạy (proxy trả 502 và ghi rõ địa chỉ).
    - *Khắc phục*: tắt tiến trình đang giữ cổng 3000, mở `http://localhost:3000`, khởi động STS và API trước.
