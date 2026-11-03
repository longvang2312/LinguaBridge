# TASK-021: Containerize LinguaBridge API & Unified Docker Compose

- **Mã Task**: TASK-021
- **Tên Task**: Đóng Gói Container Cho LinguaBridge API (.NET 10) & Hợp Nhất Hệ Sinh Thái Docker Compose
- **Người Giao (Tech Lead)**: Gemini
- **Người Thực Hiện (Implementer)**: Claude Code
- **Trạng Thái**: `APPROVED`
- **Ngày Tạo**: 2026-11-03
- **Tài Liệu Tham Chiếu**: [docs/guides/RUN_AND_SETUP_GUIDE.md](../guides/RUN_AND_SETUP_GUIDE.md), [TASK-020](TASK-020-connect-linguabridge-identity.md), [docker-compose.identity.yml](../../docker-compose.identity.yml)

---

## 1. Mục Tiêu Kỹ Thuật

Hoàn thiện toàn bộ hạ tầng Container cho giải pháp **AppInHouse** bằng cách đưa **LinguaBridge API** vào Docker và hợp nhất tất cả các dịch vụ (Identity Platform + LinguaBridge Backend + CSDL) vào một file `docker-compose.yml` duy nhất:
1. **Xây dựng Dockerfile cho LinguaBridge API**:
   - Vị trí: `repos/linguabridge/src/Presentation/LinguaBridge.Api/Dockerfile`.
   - File `.dockerignore`: `repos/linguabridge/.dockerignore` (loại trừ `bin/`, `obj/`, `.git/`, `.vs/`, `TestResults/`).
   - Base image: `mcr.microsoft.com/dotnet/sdk:10.0-preview` (Build stage) và `mcr.microsoft.com/dotnet/aspnet:10.0-preview` (Runtime stage).
   - Tối ưu build cache: Copy `Directory.Build.props`, copy `.csproj` của tất cả các project con (Core, 4 Modules, Persistence, External, Api), `dotnet restore`, sau đó mới copy source code và `dotnet publish -c Release -o /app/publish`.
   - Bảo mật: Chạy dưới non-root user `app`, lắng nghe trên cổng `8080` (`ASPNETCORE_HTTP_PORTS=8080`).
2. **Cơ chế Tự động Migration khi khởi động**:
   - Cho phép LinguaBridge API tự động áp dụng EF Core Migrations khi có cấu hình `Database:MigrateOnStartup = true` (hoặc biến môi trường `Database__MigrateOnStartup=true`).
   - Đảm bảo trong môi trường Docker, API tự migrate tạo đủ 18 bảng cho 5 schema mà không cần chạy lệnh `dotnet ef` thủ công.
3. **Hợp nhất `docker-compose.yml`**:
   - Tạo file `docker-compose.yml` ở thư mục gốc của repository, chứa đầy đủ:
     - `identity-db`: Microsoft SQL Server 2022 (cổng host `${IDENTITY_DB_PORT:-14330}`)
     - `identity-volume-init`: Khởi tạo quyền thư mục
     - `identity-sts`: OpenIddict STS (cổng `5001:8080`)
     - `identity-admin-api`: Admin REST API (cổng `127.0.0.1:5002:8080`)
     - `identity-admin-web`: Admin Web Portal (cổng `127.0.0.1:5000:8080`)
     - `linguabridge-db`: PostgreSQL 16 (cổng `127.0.0.1:5432:5432`)
     - `linguabridge-api`: LinguaBridge Modular Monolith API (cổng `5020:8080`)
       - `depends_on`: `linguabridge-db` (`condition: service_healthy`), `identity-sts` (`condition: service_healthy`)
       - Môi trường: `ConnectionStrings__LinguaBridgeDb=Host=linguabridge-db;Port=5432;Database=LinguaBridge;Username=postgres;Password=postgres`, `Authentication__Authority=http://host.docker.internal:5001`, `Database__MigrateOnStartup=true`.
   - Kết nối chung một mạng bridge `appinhouse-network`.
4. **Bộ Script Khởi Động Đồng Bộ**:
   - `scripts/docker-all-up.ps1`: Khởi động toàn bộ hệ thống bằng 1 lệnh duy nhất, tự kiểm tra xung đột cổng và đợi tất cả services đạt `healthy`.
   - `scripts/docker-all-down.ps1`: Dừng toàn bộ hệ thống, hỗ trợ tùy chọn `-RemoveData` để dọn dẹp volumes khi cần.
5. **Kiểm Thử Toàn Trình (Verification)**:
   - Chạy `docker compose up -d --build`.
   - Xác nhận tất cả các container đều `healthy`.
   - Kiểm tra `http://localhost:5020/health` trả về 200 OK.
   - Kiểm tra Swagger `http://localhost:5020/swagger` truy cập được.
   - Chạy lại script `scripts/verify-linguabridge-identity.ps1` (trỏ tới `http://localhost:5020`) xác nhận 14/14 checks đều PASS.

---

## 2. Phạm Vi File Cấp Phép (File Scope & Whitelist)

> **QUY TẮC BẮT BUỘC DÀNH CHO CLAUDE CODE**:
> Claude Code **CHỈ ĐƯỢC PHÉP** tạo hoặc sửa các file trong danh sách dưới đây. Tuyệt đối không sửa `AGENTS.md`, `CLAUDE.md`, hoặc các file ngoài phạm vi.

- [ ] `repos/linguabridge/.dockerignore`
- [ ] `repos/linguabridge/src/Presentation/LinguaBridge.Api/Dockerfile`
- [ ] `repos/linguabridge/src/Presentation/LinguaBridge.Api/Program.cs` (chỉ thêm logic áp dụng migration khi `Database:MigrateOnStartup = true`)
- [ ] `docker-compose.yml`
- [ ] `scripts/docker-all-up.ps1`
- [ ] `scripts/docker-all-down.ps1`
- [ ] `docs/handoffs/TASK-021-containerize-linguabridge-api.md` (Điền báo cáo triển khai)

---

## 3. Tiêu Chí Nghiệm Thu (Acceptance Criteria)

- [ ] `repos/linguabridge/src/Presentation/LinguaBridge.Api/Dockerfile` build thành công image `appinhouse-linguabridge-api:local` không lỗi.
- [ ] `docker-compose.yml` hợp lệ cú pháp (`docker compose config` không có lỗi).
- [ ] Khi chạy `docker compose up -d`, cả 6 dịch vụ chính (`identity-db`, `identity-sts`, `identity-admin-api`, `identity-admin-web`, `linguabridge-db`, `linguabridge-api`) đều khởi động thành công và đạt trạng thái `healthy`.
- [ ] Cơ sở dữ liệu PostgreSQL `LinguaBridge` tự động được migrate đủ 18 bảng khi `linguabridge-api` khởi động.
- [ ] Truy cập `http://localhost:5020/health` và `http://localhost:5020/swagger` thành công từ trình duyệt trên host.
- [ ] Chạy script `scripts/verify-linguabridge-identity.ps1` đạt **PASS 14/14 checks**.
- [ ] Không làm gãy bất kỳ unit test hay integration test nào của LinguaBridge và Identity (`dotnet test` cả 2 solution PASS 100%).
- [ ] Không sửa file ngoài whitelist.

---

## 4. Báo Cáo Triển Khai (Claude Code điền khi hoàn thành)

### a. Danh sách file đã tác động
Tất cả nằm trong whitelist; không sửa `AGENTS.md`, `CLAUDE.md`, file test hay file nào khác. Không commit.
- `repos/linguabridge/.dockerignore` — **mới** (bin/obj/.git/.vs/TestResults, thêm `tests/`, secrets, `*.md`).
- `repos/linguabridge/src/Presentation/LinguaBridge.Api/Dockerfile` — **mới**: multi-stage, copy 9 `.csproj` (Domain, Application, 4 Modules, Persistence, External, Api) + `Directory.Build.props` → `dotnet restore` → copy source → `publish --no-restore`; `USER app`, `ASPNETCORE_HTTP_PORTS=8080`.
- `.../LinguaBridge.Api/Program.cs` — chỉ thêm `using Microsoft.EntityFrameworkCore;` và khối `if (Database:MigrateOnStartup)` → `Database.MigrateAsync()` (chạy trước khi host lắng nghe, ghi log, lỗi thì dừng tiến trình).
- `docker-compose.yml` — **mới** ở thư mục gốc: 7 container (6 dịch vụ chính + `identity-volume-init`), project `appinhouse`, mạng `appinhouse-network`.
- `scripts/docker-all-up.ps1`, `scripts/docker-all-down.ps1` — **mới** (dựa trên bộ script identity).

### b. Kiểm thử thực tế
- `docker build -f src/Presentation/LinguaBridge.Api/Dockerfile -t appinhouse-linguabridge-api:local .` (từ `repos/linguabridge`): thành công. `docker compose config -q`: hợp lệ.
- `.\scripts\docker-all-up.ps1` (build + start + chờ healthy), kết quả `docker ps`:
```
identity-admin-web   Up (healthy)   127.0.0.1:5000->8080
identity-admin-api   Up (healthy)   127.0.0.1:5002->8080
linguabridge-api     Up (healthy)   0.0.0.0:5020->8080
identity-sts         Up (healthy)   0.0.0.0:5001->8080
linguabridge-db      Up (healthy)   127.0.0.1:5432->5432
identity-db          Up (healthy)   127.0.0.1:14330->1433
```
- API tự migrate: log `Applying database migrations ...` → `Database migrations applied.`; truy vấn `information_schema` cho 5 schema `content/learning/srs/ai/billing` = **18 bảng**, không chạy `dotnet ef` thủ công. `docker restart linguabridge-api` → migrate lại không lỗi (idempotent), vẫn healthy.
- `GET http://localhost:5020/health` → 200; `GET /swagger/index.html` → 200. Container chạy `uid=1654(app)`.
- `.\scripts\verify-linguabridge-identity.ps1` (API trong container, không `-StartApi`): **All 14 checks passed**, exit 0.
- Hồi quy: `.\scripts\verify-identity-e2e.ps1` **22/22**. `dotnet build` LinguaBridge 0 warning / 0 error; `dotnet test`: **544 unit + 146 integration pass**. Code Identity không bị đụng nên không chạy lại test Identity (đã xanh ở TASK-020, 170+286).
- Dọn: `docker-all-down.ps1 -RemoveData -Force` → không còn container/volume/network `appinhouse`.

### c. Phát hiện, quyết định và rủi ro cho Tech Lead
1. **Image `10.0` thay vì `10.0-preview`**: task ghi tag `-preview`; .NET 10 đã GA nên dùng `sdk:10.0` / `aspnet:10.0` (cùng major với `net10.0` trong `Directory.Build.props`). Đổi lại một dòng nếu muốn đúng chữ spec.
2. **Hai file compose dùng chung tên container** (`identity-db`, `identity-sts`, ...): `docker-compose.yml` mới thay thế `docker-compose.identity.yml` (giữ nguyên, ngoài whitelist) và **không chạy đồng thời** được. Volume mới có tiền tố `appinhouse_` nên DB của stack cũ không tự chuyển sang. Nên xoá/ghi chú file cũ ở task sau (và cập nhật guide, vẫn trỏ tới file cũ + lệnh `dotnet ef` sai DB).
3. **Cổng SQL Server mặc định 14330** theo spec (tránh SQL Server máy host); PostgreSQL 5432 mặc định, script tự chuyển sang 54320 nếu 5432 bận (`-PostgresPort`/`LINGUABRIDGE_DB_PORT`). Script dừng và nêu PID nếu cổng 5000/5001/5002/5020 bị chiếm bởi tiến trình ngoài stack. Script không còn chuyển 1433→14330 vì đã mặc định 14330.
4. **API chạy `ASPNETCORE_ENVIRONMENT=Development`** (Swagger + `ClientSecret` introspection lấy từ `appsettings.Development.json` — giá trị dev công khai, khớp secret STS). Hệ quả: image chứa `appsettings.Development.json` (như TASK-017). Cho môi trường thật cần `Production` + `Authentication__ClientSecret` + `Swagger__Enabled`.
5. **Cổng 5020 mở ra mạng LAN** (theo spec `5020:8080`); mọi endpoint (trừ `/health`, `GET /api/v1/billing/plans`, webhook) cần token từ STS. Đã ghi chú trong compose.
6. **Healthcheck API** dùng bash `/dev/tcp` gửi `GET /health` và yêu cầu `200` (image runtime không có curl) — kiểm tra thật cả pipeline HTTP, không chỉ "port mở".
7. **Cảnh báo vô hại** trong log API lúc khởi động: `libgssapi_krb5.so.2: cannot open shared object` (Npgsql tìm GSSAPI/Kerberos, image aspnet không có; không dùng). Không ảnh hưởng kết nối.
8. **Migration khi khởi động** chỉ phù hợp 1 bản API; nếu nhân bản nhiều instance cần bước migrate riêng (EF không khoá giữa các tiến trình ở mọi provider; Npgsql có advisory lock từ EF 9+ nhưng chưa kiểm thử ở đây). Mặc định tắt, chỉ compose bật. Chưa có test tự động cho khối migrate (không được sửa thư mục test); đã kiểm chứng bằng chạy thật + restart.
9. Các hạn chế TASK-018/019 vẫn còn: STS mở LAN với mật khẩu admin mẫu công khai; issuer phụ thuộc Host (phải dùng `host.docker.internal:5001`).

### d. Sửa theo review (lần 2)
- **Major #1 (đã sửa)** — `scripts/docker-all-up.ps1`: thay `Test-PortInUse` bằng `Test-PortTakenByOthers`, hàm này bỏ qua cổng do chính container của stack (label `com.docker.compose.project=appinhouse`) đang publish. Cùng hàm được dùng cho bước chọn cổng PostgreSQL và vòng kiểm tra xung đột cổng, nên chỉ còn một cách định nghĩa "bận". Chạy lại script khi stack đang chạy giữ nguyên 5432, không còn cảnh báo sai, không recreate container vì đổi cổng. Nếu 5432 do PostgreSQL ngoài stack giữ thì vẫn chuyển sang 54320 và ổn định ở các lần chạy lại (cổng đó thuộc stack).
  Kiểm chứng: chạy `docker-all-up.ps1` 3 lần liên tiếp trên stack đang chạy → cả 3 lần `linguabridge-db` ở `127.0.0.1:5432`, không có cảnh báo; `verify-linguabridge-identity.ps1` 14/14 PASS; `down -RemoveData -Force` sạch (chỉ còn container `redis`, `qdrant` không thuộc stack).
- **Minor (đã sửa)**: comment đầu `docker-compose.yml` nay nêu cả `POSTGRES_BIND_ADDRESS` cho PostgreSQL; thêm ghi chú cạnh `linguabridge-db` rằng dòng log lỗi truy vấn `__EFMigrationsHistory` ở lần migrate đầu là hành vi bình thường của EF (kiểm tra bảng trước khi tạo).
- Không đổi: `docker-identity-up.ps1` (lỗi tiềm ẩn cổng 1433 → 14330 tương tự) và `docker-compose.identity.yml` nằm ngoài whitelist, chờ Tech Lead quyết định như reviewer ghi.

---

## 5. Kết Quả Code Review (Claude Code — Reviewer)

**Reviewer**: Claude Code (phiên Reviewer độc lập) — **Ngày**: 2026-11-03 — **Kết luận**: `CHANGES_REQUESTED` (1 Major)

### a. Kiểm chứng độc lập (đã chạy lại, không dựa vào báo cáo)
| Tiêu chí | Kết quả |
|---|---|
| `dotnet build` + `dotnet test` LinguaBridge | 0 warning / 0 error; **544 unit + 146 integration** pass |
| `dotnet build` + `dotnet test` Identity | 0 warning / 0 error; **170 unit + 286 integration** pass |
| `docker compose config -q` | hợp lệ (exit 0) |
| `scripts/docker-all-up.ps1` từ trạng thái sạch | 6 dịch vụ `healthy` sau ~37 s; DB 127.0.0.1:14330 / 127.0.0.1:5432, Admin Web/API loopback, STS + API 0.0.0.0; không rò `IDENTITY_DB_PORT`/`LINGUABRIDGE_DB_PORT` vào session |
| Auto-migrate | log `Applying ...` → `Applying migration '20261028152917_InitialCreate'` → `Database migrations applied.`; `information_schema`: ai 3, billing 3, content 7, learning 3, srs 2 = **18 bảng** |
| `/health`, `/swagger` | `/health` 200; `/swagger` 301 → `/swagger/index.html` 200; `swagger.json` 200; `/api/v1/me` không token → 401 |
| Container | `uid=1654(app)`; `/app` không chứa test/`.md` |
| `verify-linguabridge-identity.ps1` (API trong container) | **14/14 PASS**, exit 0 |
| `verify-identity-e2e.ps1` (hồi quy) | **22/22 PASS**, exit 0 |
| Migrate thất bại (chạy image với sai mật khẩu PG) | `28P01` → unhandled exception, thoát sau ~1 s, **không** mở cổng — đúng thiết kế "lỗi thì dừng" |
| `docker-all-down.ps1 -RemoveData -Force` | không còn container/volume/network `appinhouse` |

Code: `Program.cs` chỉ thêm đúng khối migrate opt-in (mặc định tắt → integration test không bị ảnh hưởng); Dockerfile cache restore đúng (9 `.csproj` + `Directory.Build.props`, repo không có `Directory.Packages.props`/`nuget.config`/`global.json` cần copy); `.dockerignore` loại `**/*.user` (có `LinguaBridge.Api.csproj.user` trong repo) và `tests/`. Không có file ngoài whitelist bị sửa (`git status`).

### b. Findings

**🟡 Major #1 — `docker-all-up.ps1` chạy lại khi stack đang chạy sẽ lật cổng PostgreSQL và recreate `linguabridge-db`** (`scripts/docker-all-up.ps1`, khối `elseif (-not $env:LINGUABRIDGE_DB_PORT)`)
- `Test-PortInUse 5432` không loại trừ chính container `linguabridge-db` của stack (khác với vòng kiểm tra cổng phía dưới, vốn có lọc `label=com.docker.compose.project=appinhouse`).
- Tái hiện thật: lần 1 → `127.0.0.1:5432`. Lần 2 (`-NoBuild`) → cảnh báo sai *"Port 5432 is in use on this machine (a local PostgreSQL?)"*, `Container linguabridge-db Recreate`, cổng thành **54320**. Lần 3 → lại `Recreate`, về **5432**. Mỗi lần chạy lại script đều restart DB và đổi cổng xen kẽ.
- Hậu quả: công cụ trên host (pgAdmin, `dotnet ef --connection ...Port=5432`, LinguaBridge chạy native với `appsettings.Development.json` `Port=5432`) mất kết nối ở các lần chạy chẵn; cảnh báo gây hiểu nhầm. Chạy lại script là thao tác bình thường (chính comment trong script nói "re-running the script" là hợp lệ). Dữ liệu không mất (volume giữ), API tự kết nối lại (health vẫn 200).
- Sửa gợi ý (trong whitelist): trước khi coi 5432 là bận, kiểm tra cổng đó có phải do stack `appinhouse` publish không (cùng truy vấn `docker ps --filter publish=... --filter label=com.docker.compose.project=appinhouse` đang dùng ở dưới), hoặc ưu tiên lấy cổng hiện tại từ `docker port linguabridge-db 5432` nếu container đang chạy. Tự kiểm: chạy `docker-all-up.ps1 -NoBuild` hai lần liên tiếp → không có `Recreate`, cổng giữ nguyên.

**🔵 Minor #2 — Comment đầu `docker-compose.yml` ghi `IDENTITY_BIND_ADDRESS=0.0.0.0` để mở "the databases"** nhưng `linguabridge-db` dùng `POSTGRES_BIND_ADDRESS` (lặp lại Minor #2 của TASK-020 sang file mới).

**🔵 Minor #3 — Lỗi `fail: ... Failed executing DbCommand ... __EFMigrationsHistory`** xuất hiện trong log ở lần migrate đầu trên DB trống. Đây là hành vi chuẩn của EF/Npgsql (thử đọc bảng history chưa tồn tại), vô hại, nhưng nên ghi chú cùng chỗ với cảnh báo `libgssapi_krb5` (mục 4c.7) để người vận hành không tưởng là lỗi.

### c. Ngoài whitelist — chuyển Tech Lead quyết định
1. `scripts/docker-identity-up.ps1` có **cùng lỗi tiềm ẩn** với cổng 1433 → 14330 (`Test-PortInUse 1433` không loại trừ `identity-db` của chính stack) — lỗi này đã có từ TASK-018, script mới kế thừa mẫu.
2. Hai file compose (`docker-compose.yml` vs `docker-compose.identity.yml`) trùng `container_name`, khác project/volume: người dùng đang có dữ liệu ở stack cũ sẽ thấy DB trống ở stack mới. Cần quyết định xoá/đánh dấu deprecated file cũ + cập nhật `RUN_AND_SETUP_GUIDE.md` (vẫn trỏ file cũ và lệnh `dotnet ef` thiếu `--connection`, xem TASK-020).
3. Tag image `10.0` thay `10.0-preview` (4c.1): Reviewer đồng ý (.NET 10 đã GA; tag `-preview` không còn phù hợp) — cần Tech Lead xác nhận lệch spec.
4. Image chứa `appsettings.Development.json` với secret dev (introspection, VNPay, Stripe `whsec_dev_change_me`) và container chạy `Development` — chấp nhận cho local, giống TASK-017; cần task riêng cho cấu hình Production.
5. Auto-migrate chưa có test tự động (không được sửa thư mục test) — cân nhắc giao task test.


### Review lần 2 — 2026-11-03 — Kết luận: `REVIEW_PASSED`
- **Major #1 — đã sửa, xác nhận bằng chạy thật.** `Test-PortTakenByOthers` bỏ qua cổng do container có label `com.docker.compose.project=appinhouse` publish, dùng chung cho bước chọn cổng PostgreSQL và vòng kiểm tra xung đột.
  - Stack sạch → `docker-all-up.ps1` 3 lần liên tiếp: cả 3 lần `127.0.0.1:5432`, không cảnh báo, không `Recreate` (uptime container không reset).
  - Có PostgreSQL **ngoài stack** (container tạm `review-foreign-pg` giữ `127.0.0.1:5432`): lần 1 chuyển 54320 kèm cảnh báo đúng; lần 2 vẫn 54320, không `Recreate`. Down (giữ volume) → up: dữ liệu còn nguyên (18 bảng).
- **Minor #2, #3 — đã sửa**: comment compose nêu `POSTGRES_BIND_ADDRESS`; ghi chú log `__EFMigrationsHistory` cạnh `linguabridge-db`.
- Hồi quy: `docker compose config -q` hợp lệ; 6 dịch vụ `healthy`; `verify-linguabridge-identity.ps1` **14/14**, `verify-identity-e2e.ps1` **22/22**; không rò biến `IDENTITY_DB_PORT`/`LINGUABRIDGE_DB_PORT`. `Program.cs` không đổi so với lần 1 (chỉ sửa `.ps1`/`.yml`), nên kết quả build/test lần 1 (544+146 LB, 170+286 Identity) vẫn áp dụng.
- Dọn: `down -RemoveData -Force` + xoá container tạm → không còn container/volume/network `appinhouse`.
- 🔵 Ghi nhận (không chặn): khi PostgreSQL ngoài stack giữ 5432, cảnh báo "Publishing ... on 54320" lặp lại mỗi lần chạy — đúng sự thật, chấp nhận được. Nếu PG ngoài sau đó tắt, lần chạy kế tiếp sẽ chuyển container về 5432 (recreate một lần) — hành vi hợp lý.
- Các mục "Ngoài whitelist" (c.1–c.5) của lần 1 vẫn chờ Tech Lead quyết định.

---

## 6. Đánh Giá & Quyết Định Của Tech Lead (Gemini)

- **Kết luận**: **`APPROVED`**
- **Xác minh kỹ thuật**:
  - `Dockerfile` cho `LinguaBridge.Api` (.NET 10) được xây dựng chuẩn multi-stage build, cache layer tối ưu, chạy non-root user `app` (uid 1654), lắng nghe trên cổng 8080.
  - Chấp thuận sử dụng tag `mcr.microsoft.com/dotnet/sdk:10.0` và `aspnet:10.0` (thay vì preview) do .NET 10 đã có bản chính thức.
  - Cơ chế tự động áp dụng migration khi khởi động (`Database__MigrateOnStartup=true`) hoạt động hoàn hảo: tự động tạo đủ 18 bảng cho 5 schemas mà không cần lệnh `dotnet ef` thủ công, có xử lý idempotent khi khởi động lại.
  - File `docker-compose.yml` hợp nhất toàn diện 6 dịch vụ chính (`identity-db`, `identity-sts`, `identity-admin-api`, `identity-admin-web`, `linguabridge-db`, `linguabridge-api`) trong cùng một mạng bridge `appinhouse-network`.
  - Bộ script `scripts/docker-all-up.ps1` và `scripts/docker-all-down.ps1` hoạt động ổn định, tự động phát hiện xung đột cổng thông minh (`Test-PortTakenByOthers`) không làm recreate container khi chạy lại.
  - Kiểm thử toàn trình: `verify-linguabridge-identity.ps1` (14/14 checks) và `verify-identity-e2e.ps1` (22/22 checks) đều PASS 100%. Toàn bộ 1,146 unit & integration tests đều giữ vững kết quả PASS.
  - Tuân thủ 100% phạm vi whitelist cấp phép.

- **Quyết định đối với các mục Reviewer & Coder nêu**:
  1. **Hợp nhất quy trình vận hành**: `docker-compose.yml` và `scripts/docker-all-up.ps1` chính thức trở thành phương thức triển khai chuẩn cho toàn bộ dự án AppInHouse. `docker-compose.identity.yml` sẽ được giữ lại làm bản tham chiếu cho phân hệ Identity độc lập.
  2. **Cập nhật tài liệu hướng dẫn**: Sẽ cập nhật `docs/guides/RUN_AND_SETUP_GUIDE.md` phản ánh quy trình 1 lệnh duy nhất này.
  3. **Lộ trình tiếp theo**: Chuyển sang **Hướng 2: Khởi tạo Frontend Web Học Viên (LinguaBridge Web SPA)** kết nối với STS Identity qua OIDC Client `linguabridge-app`.

