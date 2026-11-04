# TASK-022: Xây Dựng LinguaBridge Web Client (HTML5, CSS3, Bootstrap 5 & OIDC PKCE)

- **Mã Task**: TASK-022
- **Tên Task**: Xây dựng Giao diện Web Học Viên LinguaBridge bằng HTML5, CSS3 & Bootstrap 5, Tích Hợp Đăng Nhập OIDC PKCE & Kết Nối Backend API
- **Người Giao (Tech Lead)**: Gemini
- **Người Thực Hiện (Implementer)**: Claude Code
- **Trạng Thái**: `APPROVED`
- **Ngày Tạo**: 2026-11-03
- **Tài Liệu Tham Chiếu**: [TASK-020](TASK-020-connect-linguabridge-identity.md), [TASK-021](TASK-021-containerize-linguabridge-api.md), [docs/architecture/language_learning_platform_design.md](../architecture/language_learning_platform_design.md)

---

## 1. Mục Tiêu Kỹ Thuật

Xây dựng ứng dụng **Web Client dành cho học viên** đặt tại thư mục `repos/linguabridge-web` sử dụng công nghệ web chuẩn (**HTML5, CSS3, Bootstrap 5 & Vanilla JavaScript**), kết nối trực tiếp với **Identity STS** để đăng nhập và gọi **LinguaBridge API** để học tập:
1. **Kiến trúc & Công nghệ Web**:
   - Sử dụng **Bootstrap 5.3 (qua CDN)**: Hệ thống Grid responsive, Navbar, Cards, Modals, Badges và Form controls.
   - **Bootstrap Icons (hoặc FontAwesome/Lucide qua CDN)**: Icon trực quan, sinh động.
   - **CSS3 hiện đại**: Thiết kế hiệu ứng thẻ lật 3D (3D Flashcard Flip), thanh điểm kinh nghiệm (XP bar), animation chuyển cảnh mượt mà.
   - **Vanilla JavaScript (ES6 Modules)**: Không phụ thuộc bundler phức tạp, chạy trực tiếp trên trình duyệt.
2. **Cơ chế Xác thực OIDC PKCE**:
   - Tích hợp với **Identity STS** (`http://host.docker.internal:5001` hoặc `http://localhost:5001`).
   - Sử dụng Client ID: `linguabridge-app` (đã được seed sẵn trong STS với Authorization Code Flow + PKCE).
   - Redirect URI chuẩn: `http://localhost:3000/callback.html` (cổng 3000 đã được seed sẵn trong STS).
   - Triển khai chuẩn PKCE bằng Web Crypto API chuẩn (`crypto.subtle` tạo `code_verifier`, SHA-256 `code_challenge`, `state`).
   - Xử lý nhận mã tại `callback.html`, đổi Authorization Code lấy Access Token (có scope `linguabridge_api`), lưu an toàn trong `sessionStorage`.
3. **Các Màn Hình Chức Năng (Features)**:
   - **Trang chủ (`index.html`)**:
     - *Khi chưa đăng nhập*: Hero section giới thiệu khóa học Tiếng Anh & Tiếng Trung giao tiếp, nút **"Đăng Nhập Ngay"**.
     - *Khi đã đăng nhập*: Navbar hiển thị thông tin học viên (`admin@appinhouse.local`), nút Đăng xuất; Dashboard hiển thị thẻ thông tin tiến độ, số từ vựng cần ôn tập hôm nay, và danh sách các khóa học từ API LinguaBridge.
   - **Màn hình Khám phá Khóa học & Bài học (`courses.html`)**:
     - Gọi `GET /api/content/courses` và hiển thị danh sách các khóa học (Title, Level, Description, Tags).
     - Bấm vào khóa học để xem danh sách bài học (Lessons).
   - **Màn hình Ôn tập Từ vựng SRS Flashcard (`flashcards.html`)**:
     - Giao diện thẻ Flashcard với hiệu ứng lật 3D CSS3 (mặt trước: từ vựng/ví dụ; mặt sau: phiên âm, nghĩa tiếng Việt/tiếng Anh).
     - 4 nút chấm điểm độ khó thuật toán SM-2 (Again: 0, Hard: 2, Good: 4, Easy: 5) gửi phản hồi về API.
4. **API Service Module (`assets/js/api.js`)**:
   - Tự động gắn header `Authorization: Bearer <access_token>` khi gọi các endpoint của LinguaBridge API (`http://localhost:5020`).
   - Tự động xử lý lỗi 401 (chuyển hướng đăng nhập lại nếu token hết hạn).
5. **Script Chạy Web Server Cục Bộ**:
   - Viết script `scripts/start-web.ps1`: Khởi động một HTTP server đơn giản lắng nghe trên cổng `3000` (sử dụng `npx serve`, `python -m http.server 3000`, hoặc PowerShell HTTP listener) và tự động mở trình duyệt tới `http://localhost:3000`.

---

## 2. Phạm Vi File Cấp Phép (File Scope & Whitelist)

> **QUY TẮC BẮT BUỘC DÀNH CHO CLAUDE CODE**:
> Claude Code **CHỈ ĐƯỢC PHÉP** tạo hoặc sửa các file trong danh sách dưới đây. Tuyệt đối không can thiệp vào `repos/identity/`, `repos/linguabridge/`, `AGENTS.md` hay `CLAUDE.md`.

- [ ] `repos/linguabridge-web/*` (Toàn bộ thư mục dự án web mới)
- [ ] `scripts/start-web.ps1` (Script chạy web server cục bộ port 3000)
- [ ] `docs/handoffs/TASK-022-init-linguabridge-web-bootstrap.md` (Điền báo cáo triển khai)

---

## 3. Cấu Trúc Thư Mục Dự Kiến (`repos/linguabridge-web`)

```text
repos/linguabridge-web/
├── index.html            # Trang chủ & Dashboard học viên
├── callback.html         # Xử lý OIDC Redirect & đổi token PKCE
├── courses.html          # Khám phá khóa học & bài học
├── flashcards.html       # Ôn tập Flashcard SRS với hiệu ứng lật thẻ 3D
├── assets/
│   ├── css/
│   │   └── style.css     # CSS3 tùy biến (3D flip card, XP bar, theme)
│   └── js/
│       ├── auth.js       # Triển khai OIDC PKCE Login/Logout/Token Management
│       ├── api.js        # Giao tiếp HTTP với LinguaBridge API (port 5020)
│       └── app.js        # Logic điều khiển giao diện chính
└── README.md             # Hướng dẫn chạy & tài liệu kỹ thuật
```

---

## 4. Tiêu Chí Nghiệm Thu (Acceptance Criteria)

- [ ] Toàn bộ thư mục `repos/linguabridge-web` được khởi tạo đầy đủ mã nguồn HTML5, CSS3, JS chuẩn, không lỗi cú pháp.
- [ ] Giao diện được xây dựng đẹp mắt, hiện đại bằng **Bootstrap 5.3**, hiển thị tốt trên cả Desktop và Mobile (Responsive).
- [ ] Luồng đăng nhập OIDC PKCE hoạt động chuẩn xác:
  - Bấm "Đăng Nhập" ➡️ Chuyển sang STS (`http://host.docker.internal:5001/Account/Login`) ➡️ Nhập tài khoản ➡️ Redirect về `http://localhost:3000/callback.html` ➡️ Lấy token thành công và lưu vào `sessionStorage`.
- [ ] Trang Dashboard gọi được API `/api/v1/me` và hiển thị thông tin học viên thực tế.
- [ ] Màn hình Flashcard có hiệu ứng lật thẻ 3D mượt mà bằng CSS3.
- [ ] Script `scripts/start-web.ps1` chạy được trên Windows PowerShell và mở web tại cổng 3000.
- [ ] Không chỉnh sửa bất kỳ file mã nguồn backend C# nào.

---

## 5. Báo Cáo Triển Khai (Claude Code điền khi hoàn thành)

### a. Danh sách file đã tác động
Chỉ trong whitelist; không đụng `repos/identity/`, `repos/linguabridge/`, `AGENTS.md`, `CLAUDE.md`. Không commit.
- `repos/linguabridge-web/` (**mới**): `index.html`, `callback.html`, `courses.html`, `flashcards.html`, `assets/css/style.css`, `assets/js/{config,auth,api,app}.js`, `README.md`.
- `scripts/start-web.ps1` (**mới**): web server cục bộ cổng 3000 (HttpListener nhúng bằng `Add-Type`, không cần cài gì), tự mở trình duyệt (`-NoBrowser` để tắt), báo rõ nếu cổng bận.

### b. Kiểm thử thực tế
Stack Docker thật (`docker-all-up.ps1`) + `start-web.ps1` + **Microsoft Edge headless điều khiển bằng puppeteer-core**: đăng nhập thật trên trang STS rồi dùng chính app. Script kiểm thử nằm ở thư mục scratchpad (ngoài whitelist, không đưa vào repo). Chạy từ DB sạch: **31/31 PASS**, gồm:
- Chưa đăng nhập: hero hiện, dashboard ẩn. Bấm "Đăng Nhập Ngay" → STS (`host.docker.internal:5001/Account/Login`), URL authorize có `code_challenge` S256, `client_id=linguabridge-app`, `redirect_uri=http://localhost:3000/callback`. Nhập tài khoản → về lại trang chủ; access/id/refresh token nằm trong `sessionStorage` (không có trong `localStorage`).
- Dashboard hiển thị email thật; `userId` lấy từ `/api/v1/me` **bằng `sub` của id_token**.
- Dữ liệu mẫu tạo qua chính API client của app (khóa học en/A1 + bản dịch vi + bài học 15 XP + 3 câu hỏi, publish; làm sai để sinh 3 thẻ SRS rồi `UPDATE next_review_date` để đến hạn): trang chủ "3 thẻ cần ôn", danh sách khóa học (tiêu đề tiếng Việt), `courses.html` (title/level/description/tag + modal danh sách bài học + XP), tab tiếng Trung rỗng đúng.
- Flashcard: 3 thẻ; nút chấm ẩn đến khi lật; lật 3D cho `transform: matrix3d(...)`; mặt sau có phiên âm + nghĩa; bấm chuột `Good` và phím `Space`+`2`, `Space`+`4` gửi đúng `rating` `[3,2,4]` + `cardId` tới `POST /srs/review`; 6 dòng `review_logs`; màn hình hoàn thành.
- Token: hết hạn → tự refresh im lặng; token bị API từ chối (401) → 1 lần refresh thất bại → đăng nhập lại (im lặng nhờ phiên STS) và quay về trang đã đăng nhập với token mới.
- Mobile 390 px: không cuộn ngang, navbar thu gọn. Đăng xuất: xoá token, chuyển tới `/connect/logout?id_token_hint=...`, trang chủ về trạng thái chưa đăng nhập. Không có lỗi JS và không có response lỗi ngoài dự kiến.
- `start-web.ps1`: phục vụ file, `/callback` → callback.html, chặn `../` (404), proxy `/lb/*` và `/sts/*` hoạt động; cổng bận → lỗi rõ ràng. Cú pháp PowerShell hợp lệ.
- Đã xem ảnh chụp màn hình (home, courses + modal, flashcard mặt sau, mobile). Đã dọn: tắt web server, `docker-all-down.ps1 -RemoveData -Force`.
- Không sửa file C# nào; không cần chạy lại `dotnet test` (không đổi backend).

Lỗi thật tìm ra nhờ kiểm thử: proxy ban đầu biến 401 của API thành 502 (HttpListener không cho gán `WWW-Authenticate` qua indexer) nên app không bao giờ thấy 401 → đã sửa (`AddHeader`), kèm kiểm thử lại.

### c. Lệch spec và quyết định (cần Tech Lead biết)
1. **Redirect URI là `http://localhost:3000/callback`, không phải `callback.html`**: STS chỉ seed `…/callback` (và `:5173/callback`) cho `linguabridge-app` (xem `DbSeeder`), trái với mô tả task. Không được sửa Identity, nên `start-web.ps1` phục vụ `callback.html` tại `/callback`. Server tĩnh khác (không phải script này) phải làm tương tự, hoặc seed thêm redirect URI (`Seeding__LinguaBridgeAppRedirectUris`).
2. **Backend không có CORS** (STS và API đều không gửi header CORS) → trình duyệt ở `localhost:3000` không gọi trực tiếp được `/connect/token` lẫn API. Vì thế `start-web.ps1` kiêm proxy cùng origin: `/sts/*` → STS (token endpoint), `/lb/*` → `http://localhost:5020`. Chỉ hai đích cố định, không chuyển cookie. Nếu muốn dùng server tĩnh bất kỳ hoặc deploy thật, cần task backend bật CORS cho origin này (rồi đổi `apiBaseUrl`/`tokenEndpoint` trong `config.js`).
3. **Endpoint khác spec**: `GET /api/content/courses` **bắt buộc `targetLanguage` (en|zh)** nên trang có tab Anh/Trung (dashboard gọi cả hai). Chấm điểm là `rating` **1–4 (Again 1, Hard 2, Good 3, Easy 4)**, không phải 0/2/4/5 như spec; API từ chối giá trị khác.
4. **Khóa học không có "Tags"** trong API → hiển thị mã khóa học và phiên bản làm thẻ. **XP**: API chưa trả tổng XP của học viên, nên "thanh XP" ở dashboard thể hiện tiến độ bài học đã hoàn thành (tổng và theo khóa).
5. **Logout** gọi `/connect/logout` của STS (không có `post_logout_redirect_uri` vì client chưa seed URI này) → học viên dừng ở trang STS sau khi đăng xuất, phải tự quay lại.
6. **Phải mở bằng `http://localhost:3000`** (không `127.0.0.1`), và STS phải là `host.docker.internal:5001` (cần Docker Desktop hoặc mục hosts tương ứng).
7. Bootstrap/Bootstrap Icons từ CDN jsDelivr không kèm SRI (cần internet lần đầu). Token ở `sessionStorage` (chuẩn cho SPA không backend; XSS vẫn đọc được — đã escape toàn bộ text từ API).
8. `start-web.ps1` có thể bị chặn bởi execution policy mặc định của Windows PowerShell; chạy `powershell -ExecutionPolicy Bypass -File scripts/start-web.ps1` (đã ghi trong README).
9. Chưa có test tự động nằm trong repo cho web (không có thư mục test trong whitelist); kiểm thử E2E ở trên chạy ngoài repo. Không có màn hình làm bài quiz / AI Tutor (ngoài phạm vi task).

### d. Sửa theo review (lần 2)
- **Major (đã sửa) — vòng lặp đăng nhập vô hạn**: `assets/js/api.js` nay chỉ cho tự đăng nhập lại **một lần mỗi phút** (dấu `lb.reauth` trong `sessionStorage`). Nếu sau lần đó API vẫn trả 401 cho token vừa cấp, ném `AuthLoopError` và trang hiện thông báo "API từ chối phiên đăng nhập vừa được cấp. Có thể API và STS cấu hình không khớp…" thay vì chuyển hướng tiếp. Mọi lần gọi API thành công xoá dấu; nhiều lệnh gọi song song trong cùng một trang dùng chung một lần chuyển hướng (cờ `leaving`).
  Kiểm chứng bằng Edge headless trên stack thật, chặn mọi `/lb/api/*` trả 401 trong 15 giây: **1** request `/connect/authorize` (trước đây 372), 3 token request, 4 lần điều hướng, trang ổn định, hiện thông báo lỗi; khi API chấp nhận lại thì dấu được xoá (5/5 PASS). Hồi quy toàn bộ kịch bản cũ: **31/31 PASS**.
- **Minor (đã sửa)**: README và comment trong `auth.js` nay mô tả đúng việc đăng xuất: STS hiện trang xác nhận, phiên STS chỉ kết thúc khi bấm "Sign out" ở đó. Dashboard hiện số thẻ đến hạn chính xác (không còn "100+"), với tooltip nói mỗi lượt hiển thị tối đa 100 thẻ.
- Không đổi: các quyết định ngoài whitelist (CORS, post_logout_redirect_uri, SRI, refresh token trong `sessionStorage`, test tự động cho web) chờ Tech Lead. Đã dọn web server và stack Docker.

### e. (Đã được thay thế bởi TASK-023)
Mục này từng mô tả một thay đổi đăng xuất làm theo yêu cầu của người dùng sau TASK-022 (đăng xuất → thẳng trang đăng nhập STS với `prompt=login`, chưa qua review). Hành vi đó **không còn đúng**: luồng đăng xuất hiện do [TASK-023](TASK-023-login-page-and-return-url.md) quy định (trang `login.html`, ghi nhớ return URL, cờ buộc nhập lại mật khẩu).

---

## 6. Kết Quả Code Review (Claude Code — Reviewer)

**Reviewer**: Claude Code (phiên Reviewer độc lập) — **Ngày**: 2026-11-03 — **Kết luận**: `CHANGES_REQUESTED` (1 Major)

### a. Kiểm chứng độc lập
Stack thật (`docker-all-up.ps1`, DB sạch) + `start-web.ps1 -NoBrowser` chạy **không quyền admin** + script E2E **tự viết** (puppeteer-core 23 + Edge headless, scratchpad của Reviewer, không dùng script của Coder ngoài công thức seed dữ liệu). Không có file C# nào bị sửa (`git status`: chỉ 3 mục trong whitelist).

| Hạng mục | Kết quả |
|---|---|
| Đối chiếu endpoint/DTO với backend | đường dẫn (`/api/content/courses`, `/api/v1/learning/{srs/due,srs/review,progress/{id}}`, `/api/v1/billing/entitlement`, `/api/v1/me`) và tên field (`CourseSummaryDto`, `LessonDto`, `DueCardDto`, `CourseProgressDto`, `EntitlementDto`, `MeResponse`) khớp; `limit` ≤ `MaxLimit` 100 |
| Luồng đăng nhập | hero → STS `host.docker.internal:5001/Account/Login`; authorize có `client_id=linguabridge-app`, `code_challenge` 43 ký tự + `S256`, `state`, `nonce`, `redirect_uri=http://localhost:3000/callback` → về `index.html`; access/id/refresh token chỉ trong `sessionStorage` (localStorage trống) |
| Dashboard | `/api/v1/me` `userId` **==** `sub` của id_token; navbar `admin@appinhouse.local`; "2 thẻ cần ôn" khớp dữ liệu seed |
| Callback giả mạo / phát lại | `/callback?code=abc&state=forged` khi chưa có flow → báo lỗi, không lưu token; phát lại callback khi đã đăng nhập → token cũ không bị thay |
| XSS | tiêu đề khoá `<img src=x onerror=...>`, mô tả `<script>`, tiêu đề bài `Bài <b>1</b>` → hiển thị dạng text, không thực thi (dashboard, courses, modal) |
| Courses + modal | danh sách bài học, `15 XP`, tiến độ `0/1` |
| Flashcard | nút chấm ẩn đến khi lật; lật → `matrix3d(-1,…)`; mặt sau có IPA + nghĩa; click `Good` → `rating 3`, phím `1` khi chưa lật bị bỏ qua, `Space`+`4` → `rating 4`; DB `review_logs` có đúng 2 dòng rating 3/4 (2 dòng rating 1 còn lại do bài làm sai lúc seed) |
| Mobile 390 px | không cuộn ngang |
| Logout | xoá token, tới `/connect/logout?id_token_hint=…`; STS hiện trang xác nhận, bấm "Sign out" → phiên STS kết thúc (đăng nhập lại hỏi mật khẩu) |
| Lỗi JS | 0 |
| Proxy `start-web.ps1` | `/`, `/callback`, assets 200; `..%2f`, `..%5c` → 403, `%2e%2e` → 404 (không thoát web root); `Host: evil.example` → 400 (chặn DNS rebinding); `/lb/api/v1/me` không token → 401 **kèm** `WWW-Authenticate: Bearer`; `/sts/.well-known/...` → issuer `http://host.docker.internal:5001/`; `127.0.0.1:3000` chạy được không cần admin |

### b. Findings

**🟡 Major #1 — Vòng chuyển hướng vô hạn khi API liên tục trả 401** (`assets/js/api.js` `apiFetch`/`reauthenticate`)
- `apiFetch`: 401 → refresh → 401 → `reauthenticate()` → `login()`; STS còn phiên nên đăng nhập im lặng → `callback` → trang lại gọi API → 401 → … Không có gì chặn lần lặp thứ hai.
- Tái hiện thật: `start-web.ps1 -Port 5173 -ApiUrl http://127.0.0.1:5999` (API giả luôn trả `401 WWW-Authenticate: Bearer error="invalid_token"`), đăng nhập một lần rồi để yên **15 giây**: **152** lần chuyển trang, **372** request `/connect/authorize`, **149** lần đổi code lấy token, **746** request API; STS DB tăng **836 token + 374 authorization**. Không dừng cho tới khi đóng tab; người dùng chỉ thấy trang nhấp nháy, không có thông báo lỗi.
- Tình huống thật dẫn tới: API cấu hình sai introspection secret / Authority; `IDENTITY_AUTHORITY` trong compose bị đổi trong khi `config.js` cố định `http://host.docker.internal:5001` (token bị API từ chối do lệch issuer); API restart với cấu hình khác. Tức là một lỗi cấu hình backend biến thành tải liên tục lên STS + phình bảng token.
- Sửa gợi ý (trong whitelist): chỉ cho phép tự đăng nhập lại **một lần** trong một khoảng ngắn — ví dụ lưu `lb.reauthAt` vào `sessionStorage` trước khi `login()`; nếu sau khi đăng nhập xong mà API vẫn 401 trong vòng ~60 giây thì **không** chuyển hướng nữa, ném `ApiError(401)` để trang hiển thị "API từ chối phiên đăng nhập — kiểm tra cấu hình" (kèm nút đăng nhập lại thủ công). Tự kiểm bằng đúng kịch bản API giả ở trên: tối đa 1 vòng đăng nhập lại, sau đó hiện thông báo.

**🔵 Minor #2 — README (mục "How sign-in works" bước 4) và comment `logout()` trong `auth.js` nói logout "ends the STS session"**: thực tế STS hiện trang xác nhận (GET `/connect/logout` chỉ render view, POST mới `SignOutAsync`). Nếu học viên đóng tab ở trang xác nhận, phiên STS vẫn còn và lần "Đăng nhập" sau sẽ vào thẳng không hỏi mật khẩu (đáng lưu ý trên máy dùng chung). Nên sửa câu chữ cho đúng.

**🔵 Minor #3 — Dashboard hiển thị `100+` khi có đúng 100 thẻ đến hạn** (`due.length >= 100` với `limit: 100`). Không đáng kể; xin `limit: 101` hoặc ghi `≥ 100`.

### c. Ngoài whitelist — chuyển Tech Lead quyết định
1. **CORS backend**: proxy cùng origin trong `start-web.ps1` là giải pháp hợp lý cho dev (đã kiểm an toàn: loopback, chặn Host lạ, chỉ 2 đích cố định, không chuyển cookie), nhưng mọi server tĩnh khác/triển khai thật cần task bật CORS cho STS (`/connect/token`) và API.
2. **Redirect URI** thực tế `…/callback` (không phải `callback.html` như spec) — Reviewer đồng ý với cách Coder xử lý; cần cập nhật spec hoặc seed thêm.
3. **`post_logout_redirect_uri` cho `linguabridge-app`** (đã nêu từ TASK-020): học viên kẹt ở trang `/` của STS sau đăng xuất. Cần task Identity seed URI + app truyền tham số.
4. **Spec lệch API**: rating 1–4 (không phải 0/2/4/5), `targetLanguage` bắt buộc, không có Tags/tổng XP — Reviewer xác nhận khớp backend; spec cần cập nhật.
5. CDN không có SRI; token (cả refresh token) trong `sessionStorage` đọc được bởi XSS — chấp nhận cho dev, cân nhắc BFF/CSP khi lên production.
6. Chưa có test tự động trong repo cho web.


### Review lần 2 — 2026-11-03 — Kết luận: `REVIEW_PASSED`
- **Major #1 — đã sửa, xác nhận bằng chạy thật.** `reauthenticate()` chỉ tự đăng nhập lại 1 lần/60 s (`lb.reauth` trong `sessionStorage`), lần tiếp theo ném `AuthLoopError` (thông báo được `describeError` hiển thị trên mọi trang); cờ `leaving` gom các lệnh gọi song song vào một lần chuyển hướng; mọi phản hồi không phải 401 xoá dấu.
  - Đúng kịch bản lần 1 (API giả luôn 401, `start-web.ps1 -Port 5173 -ApiUrl http://127.0.0.1:5999`, 15 s): `/connect/authorize` **3** (trước 372 — gồm đăng nhập gốc + 1 lần thử lại), chuyển trang **6** (trước 152), request API **20** (trước 746), token request 4; trang dừng ở `index.html` với thông báo "API từ chối phiên đăng nhập vừa được cấp…". Bảng token STS chỉ tăng có giới hạn (39 token / 6 authorization trên stack mới, trước: +836 / +374).
  - Phục hồi (API thật, chặn `/lb/api/*` → 401 bằng request interception): hiện thông báo + dấu `lb.reauth` được đặt; bỏ chặn và tải lại → dashboard bình thường, dấu bị xoá, không còn alert.
- **Minor #2 — đã sửa**: README bước 4 mô tả đúng trang xác nhận đăng xuất của STS. Test của Reviewer xác nhận: đăng xuất mà không bấm "Sign out" thì lần đăng nhập sau vẫn im lặng (đúng như tài liệu mới).
- **Minor #3 — đã sửa**: dashboard hiện số thẻ chính xác, tooltip khi chạm giới hạn 100.
- **Hồi quy** (script E2E của Reviewer, DB LinguaBridge sạch): **28/28 PASS** — PKCE/state/nonce, token chỉ trong `sessionStorage`, `/me` == `sub`, callback giả/phát lại, XSS escape, courses + modal, flashcard 3D + rating 3/4 lưu DB, mobile 390 px, logout, guard + phục hồi, 0 lỗi JS.
- Dọn: tắt 2 web server + API giả, `docker-all-down.ps1 -RemoveData -Force`; cổng 3000/5173/5999 trống, không còn container/volume `appinhouse`.
- Các mục ngoài whitelist (6c.1–6c.6) vẫn chờ Tech Lead quyết định.

---

## 7. Đánh Giá & Quyết Định Của Tech Lead (Gemini)

- **Kết luận**: **`APPROVED`**
- **Xác minh kỹ thuật**:
  - Giao diện Web Client `repos/linguabridge-web` được xây dựng rất bài bản, thẩm mỹ và trực quan bằng Bootstrap 5.3 + CSS3 hiện đại, hoàn toàn không cần các công cụ build phức tạp.
  - Luồng OIDC Authorization Code Flow + PKCE được triển khai chuẩn mực bằng Web Crypto API gốc trên trình duyệt (`code_verifier`, SHA-256 `code_challenge`, `state`, `nonce`), lưu token an toàn trong `sessionStorage`.
  - Hiệu ứng lật thẻ 3D CSS3 (`perspective`, `transform: rotateY`) cho Flashcard SRS hoạt động mượt mà, hỗ trợ cả chuột lẫn phím tắt (`Space`, `1-4`).
  - Script `scripts/start-web.ps1` tích hợp máy chủ HttpListener nhẹ và reverse proxy cùng origin (`/sts`, `/lb`) giải quyết triệt để rào cản CORS trong môi trường dev cục bộ, bảo vệ an toàn loopback và DNS rebinding.
  - Cơ chế phòng chống vòng lặp đăng nhập vô hạn (`AuthLoopError` với cooldown 60s) ở vòng sửa thứ 2 giải quyết triệt để rủi ro phình bảng token khi backend có sự cố.
  - Kiểm thử toàn diện qua headless browser thực tế (31/31 checks Coder, 28/28 checks Reviewer) đạt kết quả xuất sắc.
  - Tuân thủ 100% phạm vi whitelist cấp phép, không can thiệp mã backend C#.

- **Quyết định đối với các mục Reviewer & Coder nêu**:
  1. **CORS Backend & Post Logout Redirect URI**:
     - Đồng ý: Mở task backlog cho backend để cấu hình CORS chính thức cho các domain frontend và seed `post_logout_redirect_uri` cho `linguabridge-app` trong Identity STS.
  2. **Chuẩn hóa thông số đánh giá SM-2 (Rating 1–4)**:
     - Ghi nhận và chuẩn hóa đặc tả nghiệp vụ: Thuật toán SM-2 của LinguaBridge dùng thang điểm 1-4 (Again, Hard, Good, Easy) thay vì 0-5.
  3. **Bước tiếp theo**: Hướng dẫn User khởi chạy toàn bộ hệ sinh thái (Docker backend + Web client) và trải nghiệm học tập thực tế trên trình duyệt.

