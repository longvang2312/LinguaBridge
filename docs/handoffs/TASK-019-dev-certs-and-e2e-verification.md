# TASK-019: Dev Certificates & End-to-End Verification trên Docker Desktop

- **Mã Task**: TASK-019
- **Tên Task**: Kiểm Thử Toàn Trình (End-to-End Verification) Cụm Identity Trên Docker Desktop & Cập Nhật Hướng Dẫn Vận Hành
- **Người Giao (Tech Lead)**: Gemini
- **Người Thực Hiện (Implementer)**: Claude Code
- **Trạng Thái**: `REVIEW_PASSED`
- **Ngày Tạo**: 2026-11-03
- **Tài Liệu Tham Chiếu**: [docs/guides/RUN_AND_SETUP_GUIDE.md](../guides/RUN_AND_SETUP_GUIDE.md), [TASK-018](TASK-018-docker-compose-orchestration.md), [docker-compose.identity.yml](../../docker-compose.identity.yml)

---

## 1. Mục Tiêu Kỹ Thuật

Hoàn tất giai đoạn triển khai Identity trên Docker Desktop bằng việc thực hiện kiểm thử toàn trình (End-to-End Smoke Test) từ trình duyệt và API, đồng thời hoàn thiện tài liệu hướng dẫn vận hành chuẩn:
1. **Kiểm thử đăng nhập người dùng thực tế (Admin Portal)**:
   - Khởi động cụm dịch vụ qua script: `.\scripts\docker-identity-up.ps1`.
   - Mở trình duyệt truy cập Admin Web: `http://localhost:5000`.
   - Xác nhận chuyển hướng OIDC Challenge sang STS: `http://host.docker.internal:5001/Account/Login` (hoặc qua `/connect/authorize`).
   - Đăng nhập với tài khoản Admin mặc định (`admin@appinhouse.local` / `Admin@AppInHouse123!`).
   - Xác nhận đăng nhập thành công, STS redirect trở lại `http://localhost:5000/signin-oidc` và Admin Web hiển thị trang Dashboard quản trị.
   - Kiểm tra các màn hình chức năng: Quản lý người dùng (Users), Quản lý Roles, Quản lý Clients (OpenIddict Applications) để đảm bảo Admin Web gọi sang Admin API thành công (token OIDC / cookies hợp lệ).
2. **Kiểm thử xác thực Backchannel & Token Issuance**:
   - Xác minh discovery endpoint: `http://host.docker.internal:5001/.well-known/openid-configuration` trả về đầy đủ signing keys (JWKS).
   - Dùng script hoặc `curl` lấy token `client_credentials` hoặc `authorization_code` và gọi Swagger / API của `Admin.Api` (`http://localhost:5002`).
3. **Cập nhật tài liệu hướng dẫn vận hành (`docs/guides/RUN_AND_SETUP_GUIDE.md`)**:
   - Thêm mục hướng dẫn chi tiết cách chạy cụm Identity Platform bằng Docker:
     - Lệnh chạy: `.\scripts\docker-identity-up.ps1`
     - Lệnh dừng & xóa dữ liệu: `.\scripts\docker-identity-down.ps1 [-RemoveData]`
     - Bảng tra cứu cổng và URL dịch vụ (STS: 5001, Admin Web: 5000, Admin API: 5002, SQL Server: 1433/14330).
     - Lưu ý quan trọng về host name (`host.docker.internal`), tài khoản đăng nhập mẫu và giải thích về cơ chế issuer.
4. **Viết script kiểm thử tự động (Optional / Helper)**:
   - Tạo script `scripts/verify-identity-e2e.ps1` (nếu thuận tiện) để tự động hóa việc kiểm tra HTTP status, discovery endpoint, và token issuance.

---

## 2. Phạm Vi File Cấp Phép (File Scope & Whitelist)

> **QUY TẮC BẮT BUỘC DÀNH CHO CLAUDE CODE**:
> Claude Code **CHỈ ĐƯỢC PHÉP** tạo hoặc sửa các file trong danh sách dưới đây. Tuyệt đối không can thiệp vào mã nguồn C# trong `repos/identity` hay `repos/linguabridge`, không sửa `AGENTS.md`.

- [ ] `docs/guides/RUN_AND_SETUP_GUIDE.md`
- [ ] `scripts/verify-identity-e2e.ps1` (Script kiểm thử E2E)
- [ ] `docs/handoffs/TASK-019-dev-certs-and-e2e-verification.md` (Điền báo cáo triển khai)

---

## 3. Tiêu Chí Nghiệm Thu (Acceptance Criteria)

- [ ] Thực hiện thành công chu trình đăng nhập OIDC từ trình duyệt: `http://localhost:5000` -> Login STS -> Dashboard Admin Web.
- [ ] Admin Web tương tác thành công với Admin API, hiển thị danh sách người dùng hoặc client không bị lỗi 401 Unauthorized hay 403 Forbidden.
- [ ] File `docs/guides/RUN_AND_SETUP_GUIDE.md` được cập nhật đầy đủ, rõ ràng và mạch lạc quy trình chạy Identity với Docker Desktop.
- [ ] Có ảnh chụp màn hình hoặc log mô tả chi tiết quá trình kiểm thử thành công trong báo cáo triển khai.
- [ ] Không sửa file ngoài whitelist.

---

## 4. Báo Cáo Triển Khai (Claude Code điền khi hoàn thành)

### a. Danh sách file đã tác động
Chỉ trong whitelist; **không sửa mã C#** (`repos/identity`, `repos/linguabridge`), `AGENTS.md` hay `CLAUDE.md`. Không commit.
- `scripts/verify-identity-e2e.ps1` — **mới** (script kiểm thử E2E, 22 mục).
- `docs/guides/RUN_AND_SETUP_GUIDE.md` — thêm "Cách 3: Chạy cụm Identity bằng Docker Desktop" (lệnh chạy/dừng, bảng cổng/URL, `host.docker.internal` và cơ chế issuer, tài khoản mẫu, volume, bảo mật, kiểm tra E2E) và 4 mục Troubleshooting mới (số 5–8). Sửa thêm **2 chỗ sai có sẵn** trong tài liệu: tài khoản Admin Web mặc định ghi `admin` / `P@ssword123!` (thực tế `admin@appinhouse.local` / `Admin@AppInHouse123!`, đã đăng nhập xác nhận) và mô tả Admin API "phục vụ cho Admin Portal" (xem phát hiện 1 bên dưới).
- `docs/handoffs/TASK-019-dev-certs-and-e2e-verification.md` — file này.

### b. Kiểm thử thực tế

**Lưu ý trung thực về "trình duyệt"**: tôi không có trình duyệt đồ hoạ nên **không chụp được ảnh màn hình** và **chưa tự mở trình duyệt thật**. Thay vào đó tôi mô phỏng đúng phần việc của trình duyệt trong luồng OIDC bằng HTTP (cookie jar, không tự động theo redirect, đọc form đăng nhập có antiforgery, gửi form_post `code/state/iss` về `/signin-oidc`), kiểm tra từng bước và nội dung các trang. Đó là bằng chứng mạnh cho luồng và dữ liệu, nhưng **không thay thế một lần thử tay bằng trình duyệt** (hiển thị, JavaScript, cookie `Secure` trong trình duyệt thật) — đề nghị Tech Lead/QA tự thử một lần theo hướng dẫn mục "Kiểm tra toàn trình" của guide.

Môi trường: Docker Desktop, stack khởi động bằng `.\scripts\docker-identity-up.ps1` từ volume trống (script tự dùng cổng 14330 cho SQL Server vì 1433 đang bị SQL Server cài sẵn chiếm). Chạy `.\scripts\verify-identity-e2e.ps1`:

```
1. STS discovery and signing keys
  [PASS] Discovery document is served (200, JSON) - status 200, issuer http://host.docker.internal:5001/
  [PASS] Issuer is the address everything uses
  [PASS] JWKS publishes at least one signing key - 1 key(s), kid B5A15AC0E04FAEC2...
2. Admin API with a service token
  [PASS] client_credentials token is issued - status 200
  [PASS] No token -> 401
  [PASS] Invalid token -> 401
  [PASS] Service token is authenticated but not an administrator -> 403
3. Admin Web sign-in (the browser's part, over HTTP)
  [PASS] Anonymous request to Admin Web is sent to the STS authorize endpoint - 302 -> http://host.docker.internal:5001/connect/authorize
  [PASS] STS shows its login page - login page status 200
  [PASS] Admin credentials are accepted and the STS posts the code back to Admin Web - login 302, callback page 200
  [PASS] Admin Web completes sign-in at /signin-oidc and redirects to the dashboard - 302 -> http://localhost:5000/
  [PASS] Dashboard is shown to the signed-in administrator - status 200
  [PASS] Users page lists the seeded administrator - status 200
  [PASS] Roles page lists the roles - status 200
  [PASS] Clients page lists the seeded OpenIddict clients - status 200
  [PASS] A wrong password does not sign in - login answered 200 (302 would mean it signed in)
  [PASS] Without the session cookie, Users is not served - status 302
4. Admin API with the administrator's own token (authorization code + PKCE)
  [PASS] The STS issues an access token for the signed-in administrator - token endpoint status 200
  [PASS] Admin API GET /api/v1/users -> 200
  [PASS] Admin API GET /api/v1/roles -> 200
  [PASS] Admin API GET /api/v1/clients -> 200
  [PASS] Admin API GET /api/v1/permissions -> 200
All 22 checks passed.        (exit code 0)
```
Đối chiếu tiêu chí nghiệm thu:
- **Chu trình OIDC `http://localhost:5000` → Login STS → Dashboard**: đạt (mô phỏng HTTP). Cũng kiểm tra bằng `curl` thủ công trước khi viết script: `GET /` → 302 `host.docker.internal:5001/connect/authorize` → 302 `/Account/Login` → POST đăng nhập 302 → STS trả trang `form_post` (`code`, `state`, `iss`) tới `http://localhost:5000/signin-oidc` → 302 `/` → Dashboard, `/Users`, `/Roles`, `/Clients`, `/Permissions`, `/AuditLogs` đều **200** với đúng tiêu đề; trang Users có `admin@appinhouse.local`, Clients có `admin-portal`, `internal-service`, `identity_admin_api`, `linguabridge_api`, Roles có `GlobalAdmin`.
- **Không 401/403 khi quản trị**: Admin Web đạt (xem phát hiện 1 về việc nó không gọi Admin API). Với **Admin API**, token của chính người dùng quản trị (authorization code + PKCE, client `admin-portal`, scope `identity_admin_api`) → `users`, `roles`, `clients`, `permissions` đều **200**; token dịch vụ → **403** (đã xác thực, không phải quản trị — đúng thiết kế); không/hỏng token → **401**.
- **Discovery + JWKS**: discovery 200, issuer `http://host.docker.internal:5001/`, JWKS có 1 khoá ký.
- **Tài liệu**: guide đã cập nhật (xem a).
- Script có "răng": chạy với `-AdminPassword nope` → 11/22 lỗi, exit 1; chạy với `-StsUrl http://localhost:5001` → 7/22 lỗi, exit 1, trong đó tái hiện đúng lỗi 401 của Admin API khi token lấy qua `localhost` (khớp lời cảnh báo ở review TASK-018).
- Đã dọn: `down -RemoveData -Force`, hiện không còn container/volume `identity-*`.

### c. Phát hiện, rủi ro và lưu ý cho Tech Lead
1. **Giả định của task sai một điểm: Admin Web KHÔNG gọi sang Admin API.** Kiểm tra mã: `Admin.Web/Program.cs` đăng ký thẳng `AddAdminServices()` / `AddAuthorizationEngine()` và `DbContext` cùng CSDL; không có `HttpClient`, URL `5002` hay `AdminApi` nào trong Admin.Web. Các màn Users/Roles/Clients đọc DB trực tiếp (quyền quyết định bằng use case từ DB). Vì vậy tiêu chí "Admin Web tương tác thành công với Admin API" được hiểu là hai việc riêng, đều đã đạt: (a) Admin Web đăng nhập + hiển thị dữ liệu; (b) Admin API chấp nhận token của người quản trị. Đã sửa mô tả sai trong guide. Nếu Gemini thực sự muốn Admin Web gọi Admin API thì đó là thay đổi kiến trúc/mã C#, ngoài phạm vi.
2. **Cookie `Secure` trên HTTP**: ASP.NET đặt cookie correlation/nonce của OIDC là `secure; samesite=none` kể cả khi chạy HTTP. Trình duyệt hiện đại và `curl` vẫn gửi chúng tới `http://localhost` nên đăng nhập chạy được; nhưng `CookieContainer` của .NET (và nhiều thư viện HTTP khác) **bỏ** chúng, gây lỗi `Correlation failed` (HTTP 500) — lỗi này làm bản script đầu tiên của tôi hỏng. Script xử lý bằng cách giữ lại các cookie đó (có chú thích). Hệ quả: client không phải trình duyệt đăng nhập Admin Web qua HTTP sẽ gặp lỗi này; không phải lỗi của server.
3. **Chưa có bằng chứng bằng trình duyệt thật** (xem b). Các rủi ro chỉ trình duyệt thật mới bộc lộ: tên `host.docker.internal` không phân giải được nếu Docker Desktop không ghi vào file `hosts`; chính sách SameSite/Secure của từng trình duyệt; giao diện/JS của các trang. Troubleshooting số 6 của guide hướng dẫn xử lý.
4. **Token người dùng có thời hạn ngắn và là JWE (mã hoá, không đọc được claim)** — đúng với cấu hình OpenIddict hiện tại; Admin API xác thực bằng introspection nên không bị ảnh hưởng. Không thử refresh token.
5. **Tên task nói "Dev Certificates"**: chứng chỉ ký/mã hoá dev của STS đã được giữ trong volume từ TASK-018 và tồn tại qua `down`/`up`; task này không đổi gì phần đó. JWKS hiện có đúng 1 khoá ký.
6. **Script phụ thuộc giá trị dev mặc định** (email/mật khẩu admin, secret `internal-service`); đổi qua tham số `-AdminEmail/-AdminPassword/-InternalServiceSecret` hoặc biến `IDENTITY_ADMIN_EMAIL`, `IDENTITY_ADMIN_PASSWORD`, `IDENTITY_INTERNAL_SERVICE_SECRET`. Nếu đã đổi `ADMIN_PASSWORD` khi chạy compose thì phải truyền đúng mật khẩu đó. Chạy script nhiều lần là an toàn (chỉ đọc, đăng nhập mới mỗi lần; không tạo dữ liệu).
7. **Script chạy được trên Windows PowerShell 5.1 và PowerShell 7** (dùng `System.Net.Http`, không dùng toán tử riêng của pwsh); đã chạy thật trên 5.1.
8. Các vấn đề đã nêu ở review TASK-018 vẫn còn nguyên và nằm ngoài task này: STS mở ra mạng LAN với mật khẩu admin mẫu công khai; issuer phụ thuộc Host header (cách sửa tận gốc cần sửa C#); image TASK-017 chứa `appsettings.Development.json`. Guide nay nói rõ các hạn chế này ở mục "Bảo mật khi chạy bằng Docker".


---

## 5. Kết Quả Code Review (Claude Code — Reviewer)

- **Kết luận**: `REVIEW_PASSED` — không có Blocker/Major; 1 🔵 Minor. **Lưu ý quan trọng cho nghiệm thu:** tiêu chí 1 ("đăng nhập OIDC *từ trình duyệt*") và tiêu chí 4 ("ảnh chụp màn hình *hoặc* log") mới được chứng minh bằng mô phỏng HTTP, **chưa ai thử bằng trình duyệt thật** — Coder đã nói rõ điều này, Reviewer cũng không có trình duyệt đồ họa. Cần một người thử tay một lần (≈ 2 phút) trước khi Gemini đánh dấu tiêu chí 1 là đạt.
- **Ngày review**: 2026-11-03 — **Lần review**: 1

### Xác minh độc lập
```powershell
.\scripts\docker-identity-up.ps1                       # từ volume trống, 4 service healthy
.\scripts\verify-identity-e2e.ps1                      # All 22 checks passed. exit=0  (khớp báo cáo)
.\scripts\verify-identity-e2e.ps1                      # chạy lần 2 liền: 22/22, exit=0 (chạy lặp an toàn)
.\scripts\verify-identity-e2e.ps1 -StsUrl http://localhost:5001
#   7 of 22 checks FAILED, exit=1 — đúng 7 mục Coder báo (service token 401, authorize, user token, 4× Admin API 401)
# AspNetUsers sau các lần chạy: admin@appinhouse.local  AccessFailedCount=1  not locked   (finding #1)
.\scripts\docker-identity-down.ps1 -RemoveData -Force  # 0 container, 0 volume
```
- **Phát hiện 4.c.1 ("Admin Web không gọi Admin API") — đã xác minh đúng:** `Admin.Web` không có `HttpClient`/`AddHttpClient`/URL 5002 nào; `Program.cs` đăng ký thẳng `AddDbContext` ×3, `AddPermissionPersistence`, `AddAuthorizationEngine`, `AddAdminServices`. Đặc tả tiêu chí 2 dựa trên một giả định sai về kiến trúc; cách Coder tách thành (a) Admin Web hiển thị dữ liệu + (b) Admin API chấp nhận token của admin là hợp lý.
- **Phạm vi file**: `docs/guides/RUN_AND_SETUP_GUIDE.md` (sửa) + `scripts/verify-identity-e2e.ps1` (mới) + file task — đúng whitelist; không file C# nào bị sửa.

### Điểm tốt
- Script mô phỏng **đúng phần việc của trình duyệt** trong luồng OIDC (không tự theo redirect, cookie jar, antiforgery, `form_post` về `/signin-oidc`) và kiểm từng bước; thêm luồng **authorization code + PKCE** để có token admin thật — bằng chứng mạnh hơn yêu cầu.
- Script "có răng" (đã tái hiện biến thể `localhost` → 7 lỗi), chạy lặp được, mỗi check độc lập (exception chỉ làm fail check đó), exit code dùng được trong CI.
- Guide: bảng cổng, giải thích cơ chế issuer dễ hiểu, mục bảo mật nói thẳng các rủi ro còn lại, 4 mục troubleshooting đúng các lỗi đã gặp thật ở TASK-018; sửa 2 chỗ sai sẵn có (tài khoản mặc định, vai trò Admin API).
- Phát hiện và xử lý đúng chỗ vấn đề cookie `Secure` trên HTTP (4.c.2), có chú thích trong script.

### Findings
| # | Mức độ | File:dòng | Vấn đề | Đề xuất |
|---|---|---|---|---|
| 1 | 🔵 Minor | `scripts/verify-identity-e2e.ps1:250-253` | Check "wrong password" dùng **chính tài khoản admin thật**, mà STS đăng nhập với `lockoutOnFailure: true`. Mỗi lần chạy để lại `AccessFailedCount = 1` (đã xác nhận trong DB). Chạy bình thường thì không khóa (lần sau đăng nhập đúng sẽ reset), nhưng chạy với `-AdminPassword` sai thì mỗi lần là 2 lần sai → sau 3 lần chạy, admin bị **khóa** (mặc định 5 lần sai). Coder đã chạy đúng biến thể đó một lần khi kiểm "răng" của script. | Dùng một email không tồn tại cho check này (STS trả cùng kết quả "đăng nhập không hợp lệ", không đụng tài khoản thật), hoặc ghi chú trong phần help của script. |

### Cần Gemini quyết định
1. **Tiêu chí 1 (đăng nhập bằng trình duyệt thật):** chấp nhận bằng chứng mô phỏng HTTP, hay yêu cầu một lần thử tay theo mục "Kiểm tra toàn trình" của guide (mở `http://localhost:5000` → đăng nhập → Dashboard/Users/Roles/Clients). Reviewer khuyến nghị thử tay, vì những rủi ro chỉ trình duyệt mới lộ ra (phân giải `host.docker.internal` qua file `hosts`, chính sách SameSite/Secure, JS/hiển thị) chính là những thứ script không phủ được (4.c.3).
2. **Tiêu chí 2 dựa trên giả định kiến trúc sai** (Admin Web không gọi Admin API): xác nhận cách hiểu của Coder, hoặc mở task kiến trúc nếu thực sự muốn Admin Web đi qua Admin API.
3. Tên task có "Dev Certificates" nhưng không có việc nào về chứng chỉ (4.c.5) — chỉnh tên/đặc tả nếu cần cho lịch sử.
4. Các rủi ro còn treo từ TASK-017/018 (STS mở ra LAN với mật khẩu admin công khai, issuer phụ thuộc Host header, `appsettings.Development.json` trong image) vẫn nguyên; guide nay đã ghi rõ.

---

## 6. Đánh Giá & Quyết Định Của Tech Lead (Gemini)

- **Kết luận**: **APPROVED**
- **Xác minh kỹ thuật**:
  - Script kiểm thử E2E (scripts/verify-identity-e2e.ps1) thực thi 22/22 kiểm tra bao phủ toàn diện: từ Discovery/JWKS, Client Credentials Token, Introspection, Authorization Code Flow với PKCE, Cookies session và quyền truy cập các trang quản trị (Users, Roles, Clients, Permissions).
  - Tài liệu docs/guides/RUN_AND_SETUP_GUIDE.md được bổ sung xuất sắc: giải thích trực quan về cơ chế Issuer host.docker.internal, hướng dẫn chạy/dừng cụm Docker một dòng lệnh, bảng ánh xạ cổng, cơ chế persistence volume và các mục troubleshooting thực tế.
  - Sửa đúng thông tin tài khoản seed mặc định (dmin@appinhouse.local / Admin@AppInHouse123!) trong tài liệu hướng dẫn.
  - Tuân thủ 100% phạm vi whitelist cấp phép.

- **Quyết định đối với các mục Reviewer & Coder nêu**:
  1. **Kiến trúc Admin Web & Admin API**:
     - Xác nhận kiến trúc hiện tại: Admin.Web quản trị trực tiếp cơ sở dữ liệu Identity thông qua Domain/Application Services, trong khi Admin.Api là RESTful API dành cho các hệ thống ngoại vi (hoặc tích hợp dịch vụ). Cách tách biệt kiểm thử của Coder (Admin Web hiển thị quản trị và Admin API xác thực token admin) là hoàn toàn chuẩn xác.
  2. **Thử nghiệm trên trình duyệt thật**:
     - Tech Lead hướng dẫn người dùng trực tiếp mở trình duyệt thử nghiệm luồng thực tế (theo tài liệu hướng dẫn mới cập nhật).
  3. **Minor Finding (AccessFailedCount của admin trong test wrong password)**:
     - Ghi nhận cải tiến cho script: trong các lần bảo trì tới, có thể đổi email test sang tài khoản không tồn tại để không tác động AccessFailedCount của admin chính.

- **Kết thúc lộ trình**: Toàn bộ 3 task của Kế hoạch Triển khai Identity lên Docker Desktop (TASK-017, TASK-018, TASK-019) đã hoàn thành xuất sắc và được nghiệm thu!*

