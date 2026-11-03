# TASK-017: Containerize Identity Services (Dockerfiles & .dockerignore)

- **Mã Task**: TASK-017
- **Tên Task**: Xây dựng Dockerfile chuẩn multi-stage build cho STS, Admin API, Admin Web và file .dockerignore
- **Người Giao (Tech Lead)**: Gemini
- **Người Thực Hiện (Implementer)**: Claude Code
- **Trạng Thái**: `APPROVED`
- **Ngày Tạo**: 2026-11-03
- **Tài Liệu Tham Chiếu**: [repos/identity/README.md](../../repos/identity/README.md), [docs/handoffs/TASK-008-align-ports-with-skoruba.md](TASK-008-align-ports-with-skoruba.md)

---

## 1. Mục Tiêu Kỹ Thuật

Khởi tạo cấu hình Docker hóa (Containerization) cho toàn bộ 3 dịch vụ của **Identity Platform** (`repos/identity`):
1. **Tạo `.dockerignore`**: Đặt tại `repos/identity/.dockerignore` nhằm loại bỏ `bin/`, `obj/`, `.vs/`, `.git/`, `TestResults/` giúp giảm thời gian gửi build context.
2. **Tạo 3 Dockerfile multi-stage build**:
   - `repos/identity/src/Presentation/AppInHouse.Identity.STS/Dockerfile`
   - `repos/identity/src/Presentation/AppInHouse.Identity.Admin.Api/Dockerfile`
   - `repos/identity/src/Presentation/AppInHouse.Identity.Admin.Web/Dockerfile`
3. **Yêu cầu Dockerfile**:
   - **Build image**: `mcr.microsoft.com/dotnet/sdk:8.0`
   - **Runtime image**: `mcr.microsoft.com/dotnet/aspnet:8.0`
   - **Tối ưu layer caching**: Copy file `.csproj` và `Directory.Build.props` trước để `dotnet restore` riêng biệt, sau đó mới copy toàn bộ source code để `dotnet publish -c Release -o /app/publish`.
   - **Cấu hình cổng container**: Mặc định ASP.NET Core 8 trong container lắng nghe trên cổng `8080` (HTTP) hoặc cấu hình `ASPNETCORE_URLS=http://+:8080`.
4. **Kiểm thử build image**:
   - Chạy lệnh build thử cả 3 images thành công trên Docker Desktop:
     - `docker build -f repos/identity/src/Presentation/AppInHouse.Identity.STS/Dockerfile -t appinhouse-identity-sts:local repos/identity`
     - `docker build -f repos/identity/src/Presentation/AppInHouse.Identity.Admin.Api/Dockerfile -t appinhouse-identity-admin-api:local repos/identity`
     - `docker build -f repos/identity/src/Presentation/AppInHouse.Identity.Admin.Web/Dockerfile -t appinhouse-identity-admin-web:local repos/identity`
   - Kiểm tra `docker images` xác nhận 3 image được tạo với trạng thái hợp lệ, kích thước hợp lý.

---

## 2. Phạm Vi File Cấp Phép (File Scope & Whitelist)

> **QUY TẮC BẮT BUỘC DÀNH CHO CLAUDE CODE**:
> Claude Code **CHỈ ĐƯỢC PHÉP** tạo hoặc sửa các file trong danh sách dưới đây. Tuyệt đối không sửa `AGENTS.md`, `CLAUDE.md`, thư mục `repos/linguabridge/` hoặc `docs/architecture/`.

- [ ] `repos/identity/.dockerignore`
- [ ] `repos/identity/src/Presentation/AppInHouse.Identity.STS/Dockerfile`
- [ ] `repos/identity/src/Presentation/AppInHouse.Identity.Admin.Api/Dockerfile`
- [ ] `repos/identity/src/Presentation/AppInHouse.Identity.Admin.Web/Dockerfile`
- [ ] `docs/handoffs/TASK-017-containerize-identity-services.md` (Điền báo cáo triển khai)

---

## 3. Chi Tiết Kỹ Thuật

### 3.1. Build Context
- Build context cho cả 3 Dockerfile được khuyến nghị đặt tại **thư mục gốc của repo identity**: `repos/identity/`.
- Lý do: Các project trong `src/Presentation/` tham chiếu chéo tới `src/Core/`, `src/Infrastructure/` và dùng chung file `Directory.Build.props` ở cấp `repos/identity/`.

### 3.2. Cấu Trúc Dockerfile Mẫu (Gợi ý)

```dockerfile
# Stage 1: Build & Publish
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Copy Directory.Build.props
COPY Directory.Build.props ./

# Copy csproj files for cache optimization
COPY src/Core/AppInHouse.Identity.SharedKernel/*.csproj src/Core/AppInHouse.Identity.SharedKernel/
COPY src/Core/AppInHouse.Identity.Domain/*.csproj src/Core/AppInHouse.Identity.Domain/
COPY src/Core/AppInHouse.Identity.Application/*.csproj src/Core/AppInHouse.Identity.Application/
COPY src/Infrastructure/AppInHouse.Identity.Infrastructure.Identity/*.csproj src/Infrastructure/AppInHouse.Identity.Infrastructure.Identity/
COPY src/Infrastructure/AppInHouse.Identity.Infrastructure.Persistence/*.csproj src/Infrastructure/AppInHouse.Identity.Infrastructure.Persistence/
COPY src/Infrastructure/AppInHouse.Identity.Infrastructure.Caching/*.csproj src/Infrastructure/AppInHouse.Identity.Infrastructure.Caching/
COPY src/Infrastructure/AppInHouse.Identity.Infrastructure.Logging/*.csproj src/Infrastructure/AppInHouse.Identity.Infrastructure.Logging/
COPY src/Presentation/AppInHouse.Identity.STS/*.csproj src/Presentation/AppInHouse.Identity.STS/

# Restore dependencies
RUN dotnet restore src/Presentation/AppInHouse.Identity.STS/AppInHouse.Identity.STS.csproj

# Copy the rest of the source code
COPY src/ src/

# Publish
WORKDIR /src/src/Presentation/AppInHouse.Identity.STS
RUN dotnet publish -c Release -o /app/publish /p:UseAppHost=false

# Stage 2: Runtime
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app
EXPOSE 8080
ENV ASPNETCORE_HTTP_PORTS=8080

COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "AppInHouse.Identity.STS.dll"]
```

*(Áp dụng tương tự cho `Admin.Api` và `Admin.Web` với các project dependencies tương ứng).*

---

## 4. Tiêu Chí Nghiệm Thu (Acceptance Criteria)

- [ ] File `repos/identity/.dockerignore` được tạo đầy đủ (chặn `bin`, `obj`, `.git`, `.vs`, `TestResults`).
- [ ] 3 file `Dockerfile` cho `STS`, `Admin.Api`, `Admin.Web` được tạo đúng vị trí.
- [ ] Lệnh `docker build` thành công cho cả 3 project với build context tại `repos/identity/`:
  - `appinhouse-identity-sts:local`
  - `appinhouse-identity-admin-api:local`
  - `appinhouse-identity-admin-web:local`
- [ ] Không làm ảnh hưởng đến mã nguồn hiện tại của Identity (`dotnet build repos/identity/AppInHouse.Identity.sln` và `dotnet test` vẫn PASS 100%).
- [ ] Không sửa file ngoài whitelist.

---

## 5. Báo Cáo Triển Khai (Claude Code điền khi hoàn thành)

### a. Danh sách file đã tác động
Chỉ 4 file mới trong whitelist + file task này; **không sửa file nào có sẵn** (mã nguồn Identity, `AGENTS.md`, `CLAUDE.md`, `repos/linguabridge/`, `docs/architecture/` đều không đổi). Không commit.
- `repos/identity/.dockerignore`
- `repos/identity/src/Presentation/AppInHouse.Identity.STS/Dockerfile`
- `repos/identity/src/Presentation/AppInHouse.Identity.Admin.Api/Dockerfile`
- `repos/identity/src/Presentation/AppInHouse.Identity.Admin.Web/Dockerfile`

### b. Kiểm thử thực tế (Commands & Results)
```bash
# Build 3 image (đúng lệnh trong task, Docker Desktop đang chạy) — cả 3 thành công:
docker build -f repos/identity/src/Presentation/AppInHouse.Identity.STS/Dockerfile -t appinhouse-identity-sts:local repos/identity
docker build -f repos/identity/src/Presentation/AppInHouse.Identity.Admin.Api/Dockerfile -t appinhouse-identity-admin-api:local repos/identity
docker build -f repos/identity/src/Presentation/AppInHouse.Identity.Admin.Web/Dockerfile -t appinhouse-identity-admin-web:local repos/identity

docker images | grep appinhouse-identity
# appinhouse-identity-admin-api:local   358MB (nén 101MB)
# appinhouse-identity-admin-web:local   363MB (nén 102MB)
# appinhouse-identity-sts:local         353MB (nén 100MB)
```
Kiểm tra thêm (ngoài yêu cầu build):
- **Cache layer**: build lại STS khi không đổi gì → bước `dotnet restore` và các bước sau đều `CACHED`. `.csproj` + `Directory.Build.props` được copy trước, source copy sau, nên sửa code không làm restore lại package.
- **Chạy thử container** (`docker run`): cả 3 chạy bằng user **`app`** (không phải root). Không có cấu hình thì mỗi service dừng với đúng lỗi fail-fast của chính nó (STS: thiếu `OpenIddict:*CertificateThumbprint` ngoài Development; Admin.Api/Admin.Web: thiếu `Authentication:Authority`) — chứng tỏ host .NET khởi động được trong image. Với `Authentication__Authority/ClientId/ClientSecret` giả, **Admin.Api và Admin.Web ghi log `Now listening on: http://[::]:8080`**.
- **STS ở `ASPNETCORE_ENVIRONMENT=Development`**: container chạy nhưng chưa trả lời trên 8080 vì khi khởi động nó chạy `DbSeeder` và chờ SQL Server (chuỗi kết nối mặc định `Server=localhost`, trong container không có). Đây là phụ thuộc runtime (cần DB trong compose), không phải lỗi image; **chưa kiểm chứng STS thực sự phục vụ HTTP** vì cần SQL Server.
- **Identity không bị ảnh hưởng**: `dotnet build repos/identity/AppInHouse.Identity.sln` → 0 warning, 0 error; `dotnet test` → 170/170 unit + 286/286 integration pass.

### c. Rủi ro, đánh đổi kỹ thuật hoặc lưu ý cho Tech Lead
1. **Chỉ copy các `.csproj` thuộc bao đóng phụ thuộc của từng service**, không copy cả 7 project như mẫu §3.2: STS không cần Caching, Admin.Web không cần Logging (mẫu gợi ý của task thiếu `Infrastructure.Identity` cho Admin.Api/Web, trong khi `Infrastructure.Persistence` tham chiếu nó nên phải có, nếu thiếu thì restore lỗi). Hệ quả: thêm `ProjectReference` mới vào một service thì phải thêm dòng `COPY *.csproj` tương ứng, nếu không `dotnet restore` báo thiếu project.
2. **Chạy bằng user không phải root (`USER app`)** — ngoài yêu cầu, nhưng image `aspnet:8.0` có sẵn user này và cổng 8080 không cần quyền root. Đổi lại: STS dùng `AddDevelopmentEncryptionCertificate/SigningCertificate` (chỉ Development) và Data Protection ghi key vào `/home/app/.aspnet/DataProtection-Keys` — khi dùng compose cần **mount volume** cho key Data Protection (nếu không, mỗi lần tạo lại container sẽ mất key, cookie/token cũ không giải mã được; nếu chạy nhiều bản của một service thì phải dùng chung key). Log cảnh báo này đã xuất hiện khi chạy thử.
3. **Ngoài Development, STS bắt buộc cấu hình thumbprint chứng chỉ** ký/mã hoá và Admin.Api/Web bắt buộc `Authentication:*`; chứng chỉ phải được đưa vào container (mount + cấu hình store) — chưa nằm trong task này. Cũng cần `OpenIddict:DisableTransportSecurityRequirement=true` nếu STS chạy HTTP sau reverse proxy; việc này thuộc compose/cấu hình, không đặt trong Dockerfile.
4. **Image chỉ lắng nghe HTTP:8080** (`ASPNETCORE_HTTP_PORTS=8080`, `EXPOSE 8080`). TLS do reverse proxy đảm nhiệm. `ASPNETCORE_HTTP_PORTS` là biến .NET 8 (tên cũ `ASPNETCORE_URLS` vẫn thắng nếu ai đó đặt thêm).
5. **`.dockerignore`** loại `bin/ obj/ TestResults/ .vs/ .git/` như yêu cầu, và thêm `*.user`, `.env*`, `secrets.json`, `node_modules`, `*.md`, `Dockerfile*` để secret cục bộ không lọt vào layer. Dockerfile nằm trong `src/Presentation/...` nên cũng bị loại khỏi context (Docker vẫn đọc được vì `-f` chỉ định riêng). `obj/` bị loại là bắt buộc: nếu lọt vào sẽ làm hỏng kết quả `restore` đã cache.
6. **Phiên bản base image chỉ ghim ở mức `8.0`** (tag trôi theo bản vá, đúng yêu cầu task). Muốn build tái lập hoàn toàn thì ghim digest hoặc `8.0.x`; chưa làm.
7. **Không có `HEALTHCHECK`** vì image `aspnet` không có `curl`/`wget`; healthcheck nên khai báo ở compose (hoặc thêm endpoint health + kiểm bằng TCP) — để task sau.
8. Kích thước ~100MB nén mỗi image, nằm trong mức bình thường cho ASP.NET Core 8 (chưa dùng `chiseled`/trim). Không dùng `--no-restore` sai chỗ: publish dùng `--no-restore` nên nếu ai sửa Dockerfile mà bỏ bước restore riêng thì publish sẽ lỗi (cố ý, để không restore trùng).


---

## 6. Kết Quả Code Review (Claude Code — Reviewer)

- **Kết luận**: `REVIEW_PASSED` — 5/5 acceptance criteria đạt và đã xác minh. 1 🟡 Major **không nằm trong phạm vi file của task này** (cấu hình chứa thông tin đăng nhập bị đóng vào image); đề xuất Gemini xử lý **trước khi push image lên registry hoặc deploy**. Không có Blocker.
- **Ngày review**: 2026-11-03 — **Lần review**: 1

### Xác minh độc lập
```bash
# Build lại cả 3 image với đúng build context repos/identity (tag :review để không ghi đè :local của Coder)
docker build -f repos/identity/src/Presentation/AppInHouse.Identity.STS/Dockerfile       -t appinhouse-identity-sts:review       repos/identity   # OK
docker build -f repos/identity/src/Presentation/AppInHouse.Identity.Admin.Api/Dockerfile -t appinhouse-identity-admin-api:review repos/identity   # OK
docker build -f repos/identity/src/Presentation/AppInHouse.Identity.Admin.Web/Dockerfile -t appinhouse-identity-admin-web:review repos/identity   # OK
#   sts 353MB, admin-api 358MB, admin-web 363MB — khớp báo cáo
docker image inspect -f '{{.Config.User}}' ...   # cả 3: app (không phải root)

dotnet build repos/identity/AppInHouse.Identity.sln   # 0 Warning(s), 0 Error(s)
dotnet test  (IntegrationTests 286/286, UnitTests 170/170)   # PASS

# Đối chiếu bao đóng ProjectReference (script trong scratchpad) với các dòng COPY *.csproj:
#   STS: need=7 copied=7 | Admin.Api: need=8 copied=8 | Admin.Web: need=7 copied=7 — không thiếu, không thừa
```
- **Phạm vi file**: chỉ 4 file mới + file task (`git status`). Không file nguồn nào bị sửa.

### Điểm tốt
- **Restore được cache đúng cách**: chỉ copy `.csproj` + `Directory.Build.props` trước `dotnet restore`; publish dùng `--no-restore` nên không restore lại lần hai.
- **Danh sách `.csproj` chính xác theo từng service** (đã kiểm bằng script): đúng hơn mẫu §3.2 của task — mẫu thiếu `Infrastructure.Identity`, mà `Infrastructure.Persistence` lại tham chiếu tới nó.
- **Chạy bằng user `app`** thay vì root; HTTP 8080, TLS để reverse proxy lo.
- `.dockerignore` loại `obj/` (tránh làm hỏng layer restore), `.env*`, `secrets.json`.
- Báo cáo trung thực về phần **chưa** kiểm chứng: STS chưa phục vụ HTTP thật vì cần SQL Server.

### Findings
| # | Mức độ | Vị trí | Vấn đề | Đề xuất |
|---|---|---|---|---|
| 1 | 🟡 Major → Gemini | Image đã build (`/app/appsettings*.json`); gốc rễ ở `src/Presentation/*/appsettings*.json` (ngoài whitelist) | Image đóng gói nguyên cấu hình chứa thông tin đăng nhập: **cả 3 image** có `ConnectionStrings:IdentityDb` mặc định `User Id=sa;Password=Abc@1234`; **image STS** có thêm `appsettings.Development.json` với `AdminPassword: "Admin@AppInHouse123!"`, `InternalServiceSecret`, `LinguaBridgeApiClientSecret`. Hệ quả: (a) ai pull được image là đọc được; (b) container nào quên ghi đè connection string sẽ đăng nhập DB bằng `sa`; (c) container chạy với `ASPNETCORE_ENVIRONMENT=Development` (thường gặp ở staging/demo) sẽ seed admin với **mật khẩu ai cũng biết**. | Không chặn task này: giá trị đã có sẵn trong git từ trước và nằm ngoài whitelist. Đề xuất một task cấu hình: (1) xóa mật khẩu khỏi `appsettings.json` (để trống + fail-fast khi thiếu, như cách LinguaBridge làm với `Authentication`), dev dùng `user-secrets`; (2) trong Dockerfile, không đưa `appsettings.Development.json` vào runtime image (ví dụ `RUN rm -f /app/publish/appsettings.Development.json` ở stage build). Mục (2) chỉ một dòng, có thể giao lại cho task này nếu Gemini muốn. |

### Cần Gemini quyết định
1. **Finding #1**: giao vòng 2 cho TASK-017 (riêng phần Dockerfile) hay mở task cấu hình/secret riêng. Bắt buộc xong trước khi push image lên registry.
2. **Data Protection keys** (5.c.2): cần volume (hoặc lưu key vào DB/Redis) khi viết compose, nếu không mỗi lần tạo lại container sẽ làm mất hiệu lực cookie và token đã phát.
3. **Chứng chỉ ký/mã hóa của STS ngoài Development** (5.c.3): cần cơ chế đưa chứng chỉ vào container (mount + store, hoặc load từ file) — STS hiện chỉ đọc theo thumbprint trong certificate store, vốn không tự nhiên trên Linux container.
4. **Ghim digest base image, HEALTHCHECK, chiseled image** (5.c.6–8): backlog cho task compose/CI.

---

## 7. Đánh Giá & Quyết Định Của Tech Lead (Gemini)

- **Kết luận**: **`APPROVED`**
- **Xác minh kỹ thuật**:
  - Cả 3 Dockerfile được tổ chức multi-stage build tối ưu cache layer tốt: restore dependencies độc lập dựa trên `.csproj` và `Directory.Build.props`, build publish tách biệt với runtime.
  - Sử dụng non-root user `app` mặc định của .NET 8 runtime image, bảo đảm an toàn container.
  - Kích thước image chuẩn mực (~100MB compressed), build context gọn nhẹ nhờ `.dockerignore`.
  - Toàn bộ 456 tests của Identity (170 unit + 286 integration) giữ nguyên trạng thái PASS 100%.
  - Tuân thủ 100% phạm vi file cấp phép, không can thiệp mã nguồn ngoài whitelist.

- **Quyết định đối với các mục Reviewer & Coder nêu**:
  1. **Finding #1 (Secrets trong image & appsettings.Development.json)**:
     - Chấp thuận không chặn TASK-017 vì mục tiêu trước mắt là triển khai môi trường Docker Desktop cục bộ cho Dev.
     - Tạo backlog task cho Production Hardening: loại bỏ secrets tĩnh trong appsettings, load từ secrets vault / environment variables, loại trừ `appsettings.Development.json` trong production build stage trước khi phát hành public/private registry.
  2. **Data Protection Key Persistence**:
     - Đồng ý với đề xuất của Coder/Reviewer. Trong TASK-018 (Docker Compose), sẽ khai báo persistent volume mount vào `/home/app/.aspnet/DataProtection-Keys` cho cả 3 container để duy trì key consistency và chống mất session khi container restart.
  3. **Chứng chỉ ngoài môi trường Development**:
     - Giữ nguyên cấu hình `Development` cho môi trường docker compose cục bộ hiện tại (`AddDevelopmentEncryptionCertificate` / `AddDevelopmentSigningCertificate`). Sẽ có ADR và task riêng khi triển khai môi trường Staging/Prod (chuyển sang mount X.509 cert hoặc Key Vault).
  4. **Healthcheck & Base Image**:
     - Healthcheck cho các container Identity và SQL Server sẽ được thiết lập trực tiếp trong `docker-compose.yml` ở TASK-018.

- **Bước tiếp theo**: Tạo `TASK-018: Docker Compose Orchestration & Database Healthcheck` để liên kết mạng nội bộ, khởi tạo SQL Server và kết nối 3 services STS, Admin API, Admin Web.
