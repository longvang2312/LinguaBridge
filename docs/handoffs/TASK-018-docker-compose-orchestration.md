# TASK-018: Docker Compose Orchestration & Database Healthcheck

- **Mã Task**: TASK-018
- **Tên Task**: Thiết lập Docker Compose Orchestration cho Identity Services & SQL Server Healthcheck
- **Người Giao (Tech Lead)**: Gemini
- **Người Thực Hiện (Implementer)**: Claude Code
- **Trạng Thái**: `APPROVED`
- **Ngày Tạo**: 2026-11-03
- **Tài Liệu Tham Chiếu**: [docs/guides/RUN_AND_SETUP_GUIDE.md](../guides/RUN_AND_SETUP_GUIDE.md), [TASK-017](TASK-017-containerize-identity-services.md), [plan_deploy_identity_docker](../../../.gemini/antigravity/brain/9a3122ec-9f5c-4dda-b5a6-94acf22e7587/plan_deploy_identity_docker.md)

---

## 1. Mục Tiêu Kỹ Thuật

Xây dựng file `docker-compose.identity.yml` để dàn xếp (orchestrate) toàn bộ cụm dịch vụ **Identity Platform** cùng với cơ sở dữ liệu **Microsoft SQL Server 2022** trên Docker Desktop:
1. **Container SQL Server (`identity-db`)**:
   - Sử dụng image chính thức: `mcr.microsoft.com/mssql/server:2022-latest`.
   - Cổng expose: `1433:1433`.
   - Cấu hình Volume `identity-db-data` để duy trì dữ liệu bền vững (persistence).
   - Thiết lập `healthcheck` chuẩn xác (sử dụng `sqlcmd` với `-C` TrustServerCertificate) để các service phụ thuộc chỉ khởi động khi SQL Server đã thực sự sẵn sàng nhận kết nối (`service_healthy`).
2. **Container STS (`identity-sts`)**:
   - Sử dụng Dockerfile đã tạo ở TASK-017 (`repos/identity/src/Presentation/AppInHouse.Identity.STS/Dockerfile`).
   - Cổng ánh xạ: `5001:8080`.
   - Biến môi trường: `ASPNETCORE_ENVIRONMENT=Development`, chuỗi kết nối `ConnectionStrings__IdentityDb` trỏ tới `identity-db,1433`.
   - Volume: mount `identity-dataprotection-keys:/home/app/.aspnet/DataProtection-Keys` để chống mất encryption keys khi container restart.
   - `depends_on`: `identity-db` với điều kiện `condition: service_healthy`.
3. **Container Admin API (`identity-admin-api`)**:
   - Dockerfile: `repos/identity/src/Presentation/AppInHouse.Identity.Admin.Api/Dockerfile`.
   - Cổng ánh xạ: `5002:8080`.
   - Biến môi trường: `ASPNETCORE_ENVIRONMENT=Development`, chuỗi kết nối DB trỏ tới `identity-db`.
   - Volume: mount `identity-dataprotection-keys:/home/app/.aspnet/DataProtection-Keys`.
   - Cấu hình OIDC/Introspection trỏ tới STS phù hợp (chú ý host name / authority).
   - `depends_on`: `identity-db` (`condition: service_healthy`), `identity-sts`.
4. **Container Admin Web (`identity-admin-web`)**:
   - Dockerfile: `repos/identity/src/Presentation/AppInHouse.Identity.Admin.Web/Dockerfile`.
   - Cổng ánh xạ: `5000:8080`.
   - Biến môi trường: `ASPNETCORE_ENVIRONMENT=Development`, chuỗi kết nối DB trỏ tới `identity-db`.
   - Volume: mount `identity-dataprotection-keys:/home/app/.aspnet/DataProtection-Keys`.
   - Cấu hình OIDC Client trỏ tới STS và Admin API.
   - `depends_on`: `identity-db` (`condition: service_healthy`), `identity-sts`, `identity-admin-api`.
5. **Đồng bộ mạng (Network) & Host Resolution**:
   - Tạo custom bridge network `identity-network`.
   - Cấu hình `extra_hosts` nếu cần để container và trình duyệt host đều phân giải được URL authority.

---

## 2. Phạm Vi File Cấp Phép (File Scope & Whitelist)

> **QUY TẮC BẮT BUỘC DÀNH CHO CLAUDE CODE**:
> Claude Code **CHỈ ĐƯỢC PHÉP** tạo hoặc sửa các file trong danh sách dưới đây. Tuyệt đối không sửa mã nguồn C# trong `repos/`, không sửa `AGENTS.md`, `CLAUDE.md`, hoặc `docs/architecture/`.

- [ ] `docker-compose.identity.yml`
- [ ] `scripts/docker-identity-up.ps1` (Tùy chọn: script helper khởi động)
- [ ] `scripts/docker-identity-down.ps1` (Tùy chọn: script helper dừng & dọn dẹp)
- [ ] `docs/handoffs/TASK-018-docker-compose-orchestration.md` (Điền báo cáo triển khai)

---

## 3. Chi Tiết Kỹ Thuật Cần Lưu Ý

### 3.1. SQL Server Healthcheck
Trên SQL Server 2022 container, công cụ `sqlcmd` nằm ở `/opt/mssql-tools18/bin/sqlcmd`. Lưu ý cần cờ `-C` (Trust Server Certificate) vì SQL Server 2022 yêu cầu mã hóa kết nối mặc định:
```yaml
healthcheck:
  test: ["CMD-SHELL", "/opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -P \"$${SA_PASSWORD}\" -C -Q \"SELECT 1\" || exit 1"]
  interval: 10s
  timeout: 5s
  retries: 5
  start_period: 15s
```

### 3.2. Cấu Hình Connection String qua Environment Variables
Trong ASP.NET Core, dấu hai chấm `:` trong file appsettings được thay thế bằng hai dấu gạch dưới `__` trong biến môi trường Docker:
- `ConnectionStrings__IdentityDb=Server=identity-db,1433;Database=AppInHouse_Identity;User Id=sa;Password=Abc@1234Password!;TrustServerCertificate=True;MultipleActiveResultSets=true`
*(Sử dụng mật khẩu SA đủ mạnh theo chính sách SQL Server, ví dụ `Abc@1234Password!` hoặc khớp với cấu hình dev hiện tại).*

### 3.3. Volume cho Data Protection
Khai báo named volume ở cuối file compose:
```yaml
volumes:
  identity-db-data:
  identity-dataprotection-keys:
```
Và mount vào từng service:
```yaml
volumes:
  - identity-dataprotection-keys:/home/app/.aspnet/DataProtection-Keys
```

### 3.4. Kiểm Thử Khởi Động
Coder cần chạy thử:
```bash
docker compose -f docker-compose.identity.yml up -d --build
docker compose -f docker-compose.identity.yml ps
docker compose -f docker-compose.identity.yml logs --tail=50
```
Xác nhận:
- Container SQL Server đạt trạng thái `healthy`.
- STS khởi động, seeder chạy thành công và lắng nghe trên port 8080 (map ra 5001 trên host).
- Admin API và Admin Web khởi động thành công, không crash lặp vòng.

---

## 4. Tiêu Chí Nghiệm Thu (Acceptance Criteria)

- [ ] File `docker-compose.identity.yml` được tạo hợp lệ cú pháp (`docker compose -f docker-compose.identity.yml config` không có lỗi).
- [ ] SQL Server 2022 có healthcheck chuẩn xác và đạt trạng thái `healthy`.
- [ ] Cả 3 service Identity (`identity-sts`, `identity-admin-api`, `identity-admin-web`) đều khởi động thành công với `depends_on: condition: service_healthy` trỏ tới `identity-db`.
- [ ] Dữ liệu SQL Server và Data Protection keys được mount volume bền vững.
- [ ] Mở trình duyệt trên host hoặc dùng `curl` kiểm tra được:
  - `http://localhost:5001/.well-known/openid-configuration` (STS discovery endpoint trả JSON hợp lệ 200 OK)
  - `http://localhost:5002/api/health` hoặc tương đương
  - `http://localhost:5000` (Admin Web giao diện trả 200 OK hoặc redirect)
- [ ] Không chỉnh sửa bất kỳ file mã nguồn C# nào.

---

## 5. Báo Cáo Triển Khai (Claude Code điền khi hoàn thành)

### a. Danh sách file đã tác động
Chỉ file mới trong whitelist; **không sửa mã C# hay file có sẵn nào**. Không commit.
- `docker-compose.identity.yml`
- `scripts/docker-identity-up.ps1`, `scripts/docker-identity-down.ps1` (helper tuỳ chọn, đã làm)
- `docs/handoffs/TASK-018-docker-compose-orchestration.md` (file này)

### b. Kiểm thử thực tế
Chạy trên Docker Desktop, bắt đầu từ volume trống (`up -d --build`), dùng `IDENTITY_DB_PORT=14330` vì cổng 1433 của máy đã có SQL Server cài sẵn (xem rủi ro 1).

| Tiêu chí | Kết quả |
|---|---|
| `docker compose -f docker-compose.identity.yml config` | hợp lệ, không lỗi |
| SQL Server 2022 `healthy` (sqlcmd `-C`) | `identity-db ... (healthy)` sau ~17s |
| 3 service Identity khởi động, `depends_on: service_healthy` | cả 4 container `healthy`, không crash lặp; thứ tự db → volume-init → sts → admin-api → admin-web |
| `http://localhost:5001/.well-known/openid-configuration` | **200** JSON hợp lệ |
| Admin API | `http://localhost:5002/swagger/v1/swagger.json` **200** (API không có `/api/health`; Swagger là "tương đương", xem rủi ro 7) |
| `http://localhost:5000` (Admin Web) | **302** sang `http://host.docker.internal:5001/connect/authorize?...` (đúng, chưa đăng nhập); theo redirect → **302 `/Account/Login`** |
| Dữ liệu & key bền vững | `down` (giữ volume) rồi `up`: DB vẫn 23 bảng / 1 user / 4 client, **cùng 2 chứng chỉ ký/mã hoá STS, cùng 1 file Data Protection key** (không sinh lại), discovery vẫn 200 |

Kiểm tra sâu hơn (ngoài yêu cầu):
- **Chuỗi xác thực giữa các container**: lấy token `client_credentials` (client `internal-service`, scope `identity_admin_api`) qua `host.docker.internal:5001`, gọi Admin API: không token → 401, token rác → 401, token hợp lệ → **qua xác thực** (bị 403 "requires a global administrator" — đúng, vì đó là token dịch vụ không có user). Tức introspection Admin API → STS qua mạng compose hoạt động. **Chưa thử đăng nhập người dùng đầy đủ bằng trình duyệt** (cần tương tác); chỉ chứng minh tới trang Login.
- **Redirect URI được seed đúng**: `redirect_uri=http://localhost:5000/signin-oidc` → 302 sang Login; `redirect_uri` lạ → **400**.
- **Script**: `docker-identity-down.ps1` / `docker-identity-up.ps1` chạy thật trên Windows PowerShell 5.1 (down giữ dữ liệu → up → cảnh báo cổng 1433 và dùng 14330 → đợi healthy → in URL → `down -RemoveData -Force` xoá sạch volume). Đã dọn: hiện không còn container/volume `identity-*` nào đang chạy.

### c. Rủi ro, đánh đổi kỹ thuật hoặc lưu ý cho Tech Lead
1. **Xung đột cổng 1433 trên máy này (quan trọng)**: máy dev đang có SQL Server cài sẵn nghe `0.0.0.0:1433` (process `sqlservr`). Compose mặc định vẫn `1433:1433` đúng spec, và Docker **vẫn bind được** (nó nghe thêm trên `[::]`), nên có **hai listener cùng cổng**: kết nối `localhost,1433` từ SSMS/tool có thể vào **SQL Server của máy chứ không phải container**, rất dễ nhầm. Vì vậy host port có thể đổi qua `IDENTITY_DB_PORT`, và script `up` tự phát hiện cổng bận để dùng `14330` kèm cảnh báo. Các service trong compose luôn nối `identity-db:1433` trong mạng Docker nên không bị ảnh hưởng. Tôi không thử đăng nhập vào cổng 1433 của máy để xác minh instance nào trả lời (không phải của task).
2. **Địa chỉ STS dùng chung `http://host.docker.internal:5001`** (biến `IDENTITY_AUTHORITY`) cho cả trình duyệt lẫn container. Lý do: issuer của OpenIddict lấy theo Host của request, nên mọi bên phải dùng **cùng một chuỗi**, và `localhost` trong container là chính container đó. Docker Desktop tự phân giải tên này trên host (thêm vào file hosts, mặc định bật) và trong container; `extra_hosts: host.docker.internal:host-gateway` thêm cho Linux. Hệ quả: trình duyệt bị chuyển hướng sang `host.docker.internal:5001` (không phải `localhost:5001`) khi đăng nhập; nếu tuỳ chọn "thêm *.docker.internal vào hosts" bị tắt thì trình duyệt không mở được trang đăng nhập.
3. **Cần thêm `identity-volume-init`** (không có trong spec): volume mới thuộc `root` còn service chạy user `app` (uid 1654, theo TASK-017) → đã kiểm chứng ghi vào volume bị `Permission denied`. Một container busybox chạy một lần (`chown 1654:1654`) rồi thoát; 3 service `depends_on ... service_completed_successfully`. Không sửa Dockerfile (ngoài phạm vi).
4. **Thêm volume `identity-sts-certificates`** (`/home/app/.dotnet/corefx/cryptography`): ở Development STS tự sinh chứng chỉ ký/mã hoá lưu trong cert store của user; nếu không giữ, mỗi lần *tạo lại* container sẽ ra chứng chỉ mới và token/phiên cũ mất hiệu lực. Đã kiểm chứng giữ nguyên sau `down`/`up`. Xoá volume này = token cũ vô hiệu.
5. **Cấu hình seed bắt buộc cho đúng luồng** (ngoài spec, nhưng thiếu thì không chạy): `Seeding__AdminApiClientSecret` (STS) phải trùng `Authentication__ClientSecret` (Admin API) — nếu trống, seeder không tạo client `identity_admin_api` và introspection thất bại; `Seeding__AdminPortalRedirectUris__0/1` và `...PostLogout...` thêm `http://localhost:5000/...` (mặc định chỉ có `https://localhost:44303`). Giữ phần tử `__1` là địa chỉ cũ để chạy ngoài Docker vẫn được. Seeder chỉ tạo client **khi chưa có**: đổi redirect URI sau lần chạy đầu cần xoá volume DB hoặc sửa client qua Admin.
6. **Admin Web đặt `Database__MigrateOnStartup=false`**: ở Development nó mặc định tự migrate, cùng lúc với STS (hai bên cùng tạo/migrate một DB) → race. STS là bên sở hữu schema. Ngoài ra thêm **healthcheck TCP** cho 3 service và `depends_on` vào STS/Admin API ở mức `service_healthy` (spec chỉ yêu cầu "started" cho các service này) — mạnh hơn nhưng đúng ý: STS chỉ nghe cổng sau khi migrate + seed xong nên "cổng mở" = "sẵn sàng".
7. **Không có `/api/health`** ở Admin API (chỉ controller + Swagger ở Development); tôi dùng `/swagger/v1/swagger.json`. Healthcheck container dùng TCP (`bash /dev/tcp`) vì image `aspnet` không có `curl`; không phản ánh tình trạng nghiệp vụ. Nếu cần health thật, phải thêm endpoint (sửa C#, ngoài task này).
8. **Chỉ dùng cho dev**: mọi secret mặc định là giá trị dev (`SA_PASSWORD=Abc@1234Password!`, `dev-identity-admin-api-secret-change-me`, và các secret seed `dev-...-change-me` có sẵn trong `appsettings.Development.json`), có thể ghi đè bằng biến môi trường/`.env`. `ASPNETCORE_ENVIRONMENT=Development` (theo spec) bật Swagger, bỏ yêu cầu HTTPS của OpenIddict và dùng chứng chỉ dev. SQL Server chạy `ACCEPT_EULA=Y`, `MSSQL_PID=Developer` (giấy phép Developer chỉ dùng phát triển/thử nghiệm). Chưa có HTTPS: mọi thứ đi HTTP trên máy local.
9. **Tên database** `AppInHouse_Identity` (theo ví dụ spec) khác với `AppInHouse.Identity` ở cấu hình chạy ngoài Docker; hai môi trường không chung dữ liệu.
10. **Log nhiễu có thể gặp lúc khởi động lần đầu**: SQL Server ghi `Login failed for user 'sa' ... Failed to open the explicitly specified database 'AppInHouse_Identity'` — là kết nối đầu tiên của STS trước khi migration tạo database, vô hại. Admin API ghi cảnh báo `missing_token` / 403 chỉ khi bị gọi không có quyền.
11. Chưa nằm trong task: LinguaBridge (PostgreSQL + API) không có trong compose này; `docker compose up` yêu cầu build image .NET nên lần đầu cần mạng để tải base image (~1.6GB cho SQL Server).

---

### d. Sửa theo Review vòng 1

**Major — cổng mở ra mạng LAN: đã sửa** (`docker-compose.identity.yml`).
- SQL Server, Admin API và Admin Web nay publish ở `127.0.0.1` (biến `IDENTITY_BIND_ADDRESS`, mặc định `127.0.0.1`; đặt `0.0.0.0` nếu cố ý mở). Kiểm chứng bằng cách kết nối tới địa chỉ LAN của máy (`192.168.32.1`): cổng 5000, 5002 và 14330 **bị từ chối**, và vẫn mở trên `127.0.0.1`; cả 4 service vẫn `healthy`.
- **STS (5001) vẫn mở ra mạng**, đúng như reviewer đã chứng minh: `host.docker.internal` trỏ tới IP mạng của máy chứ không phải `127.0.0.1`, nên bind loopback làm cả container lẫn trình duyệt không gọi được STS theo tên. Lý do và hệ quả được ghi thẳng bằng comment tại cổng 5001 trong compose và trong thông báo của script `up`. **Rủi ro còn lại**: ai cùng mạng cũng thử được đăng nhập STS bằng tài khoản admin seed (`admin@appinhouse.local`) với mật khẩu công khai trong repo. Giảm thiểu trong phạm vi file: mật khẩu admin seed nay đổi được qua `ADMIN_PASSWORD` (mặc định vẫn là giá trị dev cũ). Cách sửa tận gốc cần Gemini quyết (cấu hình issuer cố định cho STS, sửa C#, ngoài phạm vi task này).

**Minor — script in sai địa chỉ STS: đã sửa** (`scripts/docker-identity-up.ps1`). Script in `http://host.docker.internal:5001/...` và nhắc rõ: dùng địa chỉ này cho trình duyệt, Swagger, Postman; token lấy qua `localhost:5001` bị Admin API từ chối 401. Đã kiểm chứng lại: token qua `host.docker.internal:5001` → Admin API 403 (qua xác thực, thiếu quyền admin, đúng); token qua `localhost:5001` → **401**.

Chạy lại bằng script từ volume trống: `up` (cảnh báo 1433 bận, dùng 14330) → 4 service healthy → Admin Web 302; `down -RemoveData -Force` đã dọn sạch container/volume/network.

## 6. Kết Quả Code Review (Claude Code — Reviewer)

- **Kết luận**: `CHANGES_REQUESTED` — cả 6 acceptance criteria đều đạt (đã tự chạy lại từ volume trống). Yêu cầu sửa 1 🟡 Major trong phạm vi file (cổng mở ra LAN, đã thử nghiệm cách sửa) và nên sửa luôn 1 🔵 Minor (script in sai địa chỉ STS). Cả hai đều chỉ vài dòng.
- **Ngày review**: 2026-11-03 — **Lần review**: 1

### Xác minh độc lập
```bash
docker compose -f docker-compose.identity.yml config -q                       # OK
IDENTITY_DB_PORT=14330 docker compose -f docker-compose.identity.yml up -d --build --wait   # từ volume trống: 4 service healthy sau 30s
#   (cổng 1433 trên máy này đúng là bị SQL Server cài sẵn chiếm — khớp rủi ro 5.c.1)
curl http://localhost:5001/.well-known/openid-configuration      # 200
curl http://localhost:5002/swagger/v1/swagger.json               # 200
curl http://localhost:5002/api/v1/projects                       # 401 (không token)
curl http://localhost:5000                                       # 302 -> http://host.docker.internal:5001/connect/authorize?...

# Chuỗi xác thực giữa các container (client_credentials, internal-service):
#   token lấy qua http://host.docker.internal:5001 -> Admin API 403 (đã xác thực, không phải admin)  ✅
#   token rác -> 401 ✅
#   token lấy qua http://localhost:5001 -> Admin API 401  ⚠ issuer = http://localhost:5001/ không khớp (finding #2)

# Bền vững: down (giữ volume) -> up
#   kid khóa ký, file key Data Protection: giống hệt trước/sau
#   DB: 1 user / 4 client / 23 bảng — giống báo cáo; discovery vẫn 200

# Đã dọn: down -v, không còn container/volume identity-*
```
- **Phạm vi file**: chỉ file mới trong whitelist (`git status`); không file C# nào bị sửa.

### Điểm tốt
- Healthcheck SQL Server đúng (`sqlcmd -C`, `$$` để compose không nội suy); mọi `depends_on` ở mức `service_healthy`; thứ tự khởi động đúng.
- Các lựa chọn lệch spec đều có lý do và đều cần thiết: `identity-volume-init` (volume mới thuộc root, service chạy uid 1654), volume cert STS (giữ khóa ký qua các lần tạo lại container — đã xác nhận `kid` không đổi), `Database__MigrateOnStartup=false` cho Admin Web (tránh hai bên cùng migrate), seed `AdminApiClientSecret` khớp `ClientSecret` của Admin API.
- Một chuỗi authority chung (`host.docker.internal:5001`) cho cả trình duyệt lẫn container — đúng bản chất issuer của OpenIddict.
- Script PowerShell cẩn thận: tự tránh cổng 1433 bận, trả lại `IDENTITY_DB_PORT` cho session, `-RemoveData` hỏi xác nhận.

### Findings
| # | Mức độ | File:dòng | Vấn đề | Đề xuất |
|---|---|---|---|---|
| 1 | 🟡 Major (sửa ở vòng 2) | `docker-compose.identity.yml` — `ports` của cả 4 service | Mọi cổng publish trên **`0.0.0.0`** (đã xác nhận bằng `docker ps`), tức mở ra mọi mạng mà laptop dev đang nối. Nghiêm trọng nhất là **SQL Server với `sa` / `Abc@1234Password!`** (mật khẩu nằm trong repo): ai cùng Wi-Fi cũng có toàn quyền DB Identity (user, client, secret). Admin API/Admin Web cũng mở ra ngoài không cần thiết. | **Đã thử nghiệm bằng file override trong scratchpad:** bind `127.0.0.1:` cho `identity-db`, `identity-admin-api`, `identity-admin-web` → stack vẫn healthy, token dịch vụ vẫn introspect được (403), Admin Web vẫn 302. **Không** bind loopback cho STS: đã thử, máy host không còn gọi được `host.docker.internal:5001` (tên đó trỏ tới IP không phải loopback), làm hỏng thiết kế authority chung. Giữ STS ở `0.0.0.0` và ghi chú lý do trong compose. Lưu ý thêm: STS mở ra LAN ở Development nghĩa là ai cùng mạng cũng thử đăng nhập được bằng admin seed `Admin@AppInHouse123!` — ghi chú trong compose/README. |
| 2 | 🔵 Minor (nên sửa cùng vòng 2) | `scripts/docker-identity-up.ps1:102` | Script in địa chỉ STS là `http://localhost:5001/...`. Token lấy qua địa chỉ đó có issuer `http://localhost:5001/` và **bị Admin API từ chối (401)** — đã tái hiện. Người dùng Swagger/Postman sẽ gặp 401 khó hiểu. | In `http://host.docker.internal:5001/.well-known/openid-configuration` và ghi rõ "lấy token qua địa chỉ này". Gốc rễ (issuer phụ thuộc Host header) xem mục Gemini #1. |
| 3 | 🔵 Minor | `docker-compose.identity.yml` (`mssql/server:2022-latest`, `busybox:1.36`) | Tag trôi. | Backlog cùng việc ghim digest ở TASK-017. |

### Cần Gemini quyết định
1. **Issuer cố định cho STS**: thêm cấu hình `OpenIddict:Issuer` (`options.SetIssuer(...)`, sửa C#, ngoài phạm vi task này) để issuer không còn phụ thuộc Host header. Khi đó có thể bỏ `host.docker.internal` khỏi trình duyệt và bind STS loopback luôn.
2. **Chưa ai thử đăng nhập người dùng đầy đủ bằng trình duyệt** (cả Coder lẫn Reviewer chỉ tới trang Login). Cần một lần smoke test tay: đăng nhập admin → về `localhost:5000/signin-oidc` → Admin Web gọi Admin API.
3. **Finding #1 của TASK-017** (`appsettings.Development.json` và mật khẩu `sa` trong image) giờ đã thực sự được dùng: compose chạy `Development`, nên seed admin với mật khẩu công khai. Càng nên xử lý trước khi compose được dùng ngoài máy cá nhân.
4. `/api/health` cho Admin API (5.c.7) và healthcheck nghiệp vụ thay vì TCP: backlog (cần sửa C#).
5. Tên DB `AppInHouse_Identity` (Docker) khác `AppInHouse.Identity` (chạy ngoài Docker) (5.c.9): chấp nhận hay thống nhất.


### Review lần 2 (2026-11-03)

- **Kết luận**: `REVIEW_PASSED` — Major #1 và Minor #2 đã sửa và được xác minh. Minor #3 (tag trôi) để backlog. Không có finding mới.

#### Xác minh độc lập
```powershell
.\scripts\docker-identity-up.ps1          # từ volume trống: cảnh báo 1433 bận -> dùng 14330, 4 service healthy, in đúng địa chỉ host.docker.internal
docker ps                                 # db/admin-api/admin-web: 127.0.0.1:*   sts: 0.0.0.0:5001
# Kết nối TCP từ host tới từng địa chỉ (loopback + 5 địa chỉ LAN, gồm Wi-Fi thật 192.168.1.3):
#   127.0.0.1      : 5000 OPEN  5001 OPEN  5002 OPEN  14330 OPEN
#   mọi IP LAN     : 5000 refused  5001 OPEN  5002 refused  14330 refused   ✅
# Token client_credentials:
#   qua host.docker.internal:5001 -> Admin API 403 (đã xác thực)   ✅
#   qua localhost:5001            -> Admin API 401 (đúng như script cảnh báo)
# Admin Web 302 -> host.docker.internal:5001/connect/authorize; STS discovery 200
.\scripts\docker-identity-down.ps1 -RemoveData -Force   # 0 container, 0 volume còn lại
```

#### Đánh giá các bản sửa
| # lần 1 | Trạng thái | Nhận xét |
|---|---|---|
| 1 🟡 Cổng mở ra LAN | ✅ Đã sửa | DB, Admin API, Admin Web bind `${IDENTITY_BIND_ADDRESS:-127.0.0.1}` — đóng mặc định, mở có chủ đích. STS giữ `0.0.0.0` với comment giải thích lý do và hệ quả ngay tại cổng. Thêm `ADMIN_PASSWORD` để đổi mật khẩu admin seed. Lưu ý: seeder chỉ tạo admin khi chưa có, nên biến này chỉ có tác dụng với DB mới (hoặc sau `-RemoveData`) — nên ghi thêm câu này vào comment khi chạm lại file. |
| 2 🔵 Script in sai địa chỉ STS | ✅ Đã sửa | In `host.docker.internal:5001` và cảnh báo rõ chuyện token qua `localhost:5001` bị 401 — đã kiểm cả hai luồng. |
| 3 🔵 Tag trôi | ⏸ Backlog | Như lần 1. |

#### Cần Gemini quyết định
Giữ nguyên danh sách lần 1. Ưu tiên cao nhất vẫn là **issuer cố định cho STS** (sửa C#): khi có, STS cũng bind loopback được và rủi ro còn lại (trang đăng nhập STS mở ra LAN với mật khẩu admin seed công khai) biến mất.
---

## 7. Đánh Giá & Quyết Định Của Tech Lead (Gemini)

- **Kết luận**: **APPROVED**
- **Xác minh kỹ thuật**:
  - docker-compose.identity.yml được cấu hình chặt chẽ, cú pháp chuẩn xác (docker compose config hợp lệ 100%).
  - Các cải tiến kỹ thuật thực tế ngoài spec đều rất thỏa đáng và sâu sắc:
    1. Container identity-volume-init xử lý quyền ghi volume (uid 1654 non-root user pp).
    2. Mount volume identity-sts-certificates để duy trì khóa ký/mã hóa qua các lần recreate container.
    3. Healthcheck chuẩn xác cho SQL Server 2022 (/opt/mssql-tools18/bin/sqlcmd với cờ -C) và các service Identity.
    4. Xử lý bind loopback 127.0.0.1 cho các cổng nhạy cảm (SQL Server, Admin API, Admin Web) bảo vệ môi trường dev LAN.
    5. Bộ script helper (scripts/docker-identity-up.ps1 & scripts/docker-identity-down.ps1) tự phát hiện cổng 1433 bận, hiển thị URL hướng dẫn chính xác.
  - Tuân thủ 100% phạm vi file cấp phép, không can thiệp mã nguồn C#.

- **Quyết định đối với các mục Reviewer & Coder nêu**:
  1. **Issuer cố định cho STS (C# SetIssuer)**:
     - Ghi nhận vào backlog kỹ thuật: Sẽ bổ sung cấu hình OpenIddict:Issuer qua code C# hoặc appsettings để cho phép cố định Issuer (tránh phụ thuộc host header host.docker.internal). Khi đó STS có thể bind loopback hoàn toàn.
  2. **Smoke Test End-to-End người dùng đăng nhập**:
     - Đây chính là trọng tâm của **TASK-019** (Dev Certificates & End-to-End Verification trên Docker Desktop) theo đúng kế hoạch đã định.
  3. **Hardening Secrets**:
     - Đồng ý duy trì giá trị dev trong môi trường local hiện tại. Sẽ có task cấu hình Production Secrets trước khi triển khai môi trường bên ngoài.
  4. **Tên DB AppInHouse_Identity**:
     - Chấp thuận giữ tên này trong Docker để tách biệt hoàn toàn với database chạy cục bộ ngoài host, tránh ghi đè dữ liệu thử nghiệm.
  5. **Healthcheck nghiệp vụ & ghim tag digest**:
     - Đưa vào backlog bảo trì hạ tầng container.

- **Bước tiếp theo**: Tạo TASK-019: Dev Certificates & End-to-End Verification trên Docker Desktop để hoàn tất toàn bộ chu trình kiểm thử đăng nhập người dùng và vận hành cụm Identity trên Docker Desktop.*

