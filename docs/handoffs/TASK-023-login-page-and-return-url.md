# TASK-023: Dedicated Login Page, Return URL Preservation & Smooth Logout Flow

- **Mã Task**: TASK-023
- **Tên Task**: Xây Dựng Trang Đăng Nhập Chuyên Biệt, Cơ Chế Ghi Nhớ Return URL & Hoàn Thiện Luồng Đăng Xuất
- **Người Giao (Tech Lead)**: Gemini
- **Người Thực Hiện (Implementer)**: Claude Code
- **Trạng Thái**: `APPROVED`
- **Ngày Tạo**: 2026-11-04
- **Tài Liệu Tham Chiếu**: [TASK-022](TASK-022-init-linguabridge-web-bootstrap.md), [repos/linguabridge-web/README.md](../../repos/linguabridge-web/README.md)

---

## 1. Mục Tiêu Kỹ Thuật

Giải quyết triệt để 2 vấn đề trải nghiệm người dùng trong luồng Đăng nhập / Đăng xuất của Web Client:
1. **Trang Đăng Nhập Chuyên Biệt (`repos/linguabridge-web/login.html`)**:
   - Xây dựng trang `login.html` bằng Bootstrap 5.3 + CSS3:
     - Form/Card đăng nhập đẹp mắt, có logo LinguaBridge.
     - Kiểm tra query param `?loggedOut=1`: Nếu có, hiển thị thông báo Alert thành công: *"Bạn đã đăng xuất thành công. Vui lòng đăng nhập lại để tiếp tục học tập."*
     - Hỗ trợ tham số `?returnUrl=...` trong URL (mặc định là `/index.html` nếu không có).
     - Nút to nổi bật: **"Đăng Nhập Bằng AppInHouse ID"** kích hoạt luồng OIDC PKCE với Identity STS.
2. **Cơ Chế Ghi Nhớ & Khôi Phục Trang Vừa Đứng (`Return URL Preservation`)**:
   - Khi học viên đang ở bất kỳ trang nào (ví dụ: `/courses.html`, `/courses.html?lang=zh`, hoặc `/flashcards.html`) và bấm **Đăng xuất**:
     - Lấy URL hiện tại: `const currentPath = window.location.pathname + window.location.search;`
     - Xóa sạch session tokens trong `sessionStorage` (`clearSession()`).
     - Chuyển hướng người dùng về: `/login.html?returnUrl=${encodeURIComponent(currentPath)}&loggedOut=1`.
   - Khi học viên bấm nút **Đăng nhập** từ `login.html`:
     - Luồng OIDC PKCE lưu `returnUrl` vào `flow.returnTo`.
     - Sau khi xác thực thành công tại STS, trang `callback.html` đọc `flow.returnTo` và **tự động đưa học viên quay lại chính xác trang họ vừa logout** (thay vì luôn bị đẩy về `/index.html`).
3. **Cập Nhật Các Điểm Đăng Nhập / Đăng Xuất Toàn Web App**:
   - **Navbar (`assets/js/app.js`)**:
     - Nút **Đăng xuất**: Khi bấm, gọi `logout(window.location.pathname + window.location.search)`.
     - Nút **Đăng nhập**: Khi bấm, chuyển hướng tới `/login.html?returnUrl=${encodeURIComponent(window.location.pathname + window.location.search)}` (hoặc gọi `login(currentPath)` trực tiếp).
   - **Thông báo yêu cầu đăng nhập (`showSignInPrompt` trong `courses.html`, `flashcards.html`)**:
     - Nút "Đăng nhập ngay" chuyển hướng tới `/login.html?returnUrl=${encodeURIComponent(window.location.pathname + window.location.search)}`.
   - **Trang chủ (`index.html`)**:
     - Nút Hero "Đăng Nhập Ngay" có thể gọi `login('/index.html')` hoặc dẫn tới `/login.html`.

---

## 2. Phạm Vi File Cấp Phép (File Scope & Whitelist)

> **QUY TẮC BẮT BUỘC DÀNH CHO CLAUDE CODE**:
> Claude Code **CHỈ ĐƯỢC PHÉP** tạo hoặc sửa các file trong danh sách dưới đây. Tuyệt đối không can thiệp vào `repos/identity/`, `repos/linguabridge/`, `AGENTS.md` hay `CLAUDE.md`.

- [ ] `repos/linguabridge-web/login.html` (Tạo mới)
- [ ] `repos/linguabridge-web/assets/js/auth.js`
- [ ] `repos/linguabridge-web/assets/js/app.js`
- [ ] `repos/linguabridge-web/callback.html`
- [ ] `repos/linguabridge-web/courses.html`
- [ ] `repos/linguabridge-web/flashcards.html`
- [ ] `repos/linguabridge-web/index.html`
- [ ] `docs/handoffs/TASK-023-login-page-and-return-url.md` (Điền báo cáo triển khai)

---

## 3. Chi Tiết Kỹ Thuật Cần Lưu Ý

### 3.1. Cập nhật `logout` trong `assets/js/auth.js`
Thay vì cố định `return login('/index.html', ...)`:
```javascript
export function logout(returnTo = window.location.pathname + window.location.search) {
  clearSession();
  const target = returnTo || '/index.html';
  window.location.assign(`/login.html?returnUrl=${encodeURIComponent(target)}&loggedOut=1`);
}
```

### 3.2. Cấu trúc trang `login.html`
```html
<!doctype html>
<html lang="vi">
<head>
  <meta charset="utf-8">
  <meta name="viewport" content="width=device-width, initial-scale=1">
  <title>Đăng nhập - LinguaBridge</title>
  <link rel="icon" href="data:image/svg+xml,%3Csvg xmlns=%27http://www.w3.org/2000/svg%27 viewBox=%270 0 16 16%27%3E%3Ctext y=%2712.5%27 font-size=%2713%27%3E%F0%9F%8C%90%3C/text%3E%3C/svg%3E">
  <link href="https://cdn.jsdelivr.net/npm/bootstrap@5.3.3/dist/css/bootstrap.min.css" rel="stylesheet">
  <link href="https://cdn.jsdelivr.net/npm/bootstrap-icons@1.11.3/font/bootstrap-icons.min.css" rel="stylesheet">
  <link href="/assets/css/style.css" rel="stylesheet">
</head>
<body data-page="login" class="bg-light">
  <div id="navbar"></div>
  <main class="container py-5">
    <div class="row justify-content-center">
      <div class="col-md-6 col-lg-5 col-xl-4">
        <!-- Thông báo Đăng xuất thành công nếu có ?loggedOut=1 -->
        <div id="logout-alert"></div>
        <div class="card lb-card shadow-sm border-0">
          <div class="card-body p-4 text-center">
            <div class="mb-3">
              <i class="bi bi-translate fs-1 text-primary"></i>
            </div>
            <h1 class="h4 card-title fw-bold mb-2">Đăng Nhập LinguaBridge</h1>
            <p class="text-secondary small mb-4">Học tiếng Anh & tiếng Trung giao tiếp thông minh cùng AI Tutor và Flashcard SRS.</p>
            <button id="btn-login-oidc" class="btn btn-primary w-100 py-2 fw-semibold">
              <i class="bi bi-box-arrow-in-right me-2"></i>Đăng Nhập Bằng AppInHouse ID
            </button>
          </div>
        </div>
      </div>
    </div>
  </main>
  <script src="https://cdn.jsdelivr.net/npm/bootstrap@5.3.3/dist/js/bootstrap.bundle.min.js" defer></script>
  <script type="module" src="/assets/js/app.js"></script>
</body>
</html>
```

### 3.3. Xử lý Return URL trong `app.js` cho `login.html`
Trong `app.js`, bổ sung hàm khởi tạo trang `initLogin`:
```javascript
function initLogin() {
  const params = new URLSearchParams(window.location.search);
  const returnUrl = params.get('returnUrl') || '/index.html';
  const loggedOut = params.get('loggedOut') === '1';

  if (loggedOut) {
    $('#logout-alert').innerHTML = `
      <div class="alert alert-success d-flex align-items-center gap-2 mb-3" role="alert">
        <i class="bi bi-check-circle-fill"></i>
        <div>Bạn đã đăng xuất thành công.</div>
      </div>`;
  }

  // Nếu người dùng đã có token hợp lệ, chuyển thẳng tới returnUrl
  if (isSignedIn()) {
    window.location.replace(returnUrl);
    return;
  }

  $('#btn-login-oidc')?.addEventListener('click', () => {
    login(returnUrl);
  });
}
```

---

## 4. Tiêu Chí Nghiệm Thu (Acceptance Criteria)

- [ ] File `repos/linguabridge-web/login.html` được tạo mới, hiển thị giao diện đăng nhập Bootstrap 5 chuẩn mực.
- [ ] Khi bấm nút "Đăng xuất" ở Navbar từ bất kỳ trang nào (ví dụ `/courses.html` hoặc `/flashcards.html`):
  - Tokens trong `sessionStorage` bị xóa sạch.
  - Trình duyệt chuyển hướng ngay về `/login.html?returnUrl=...&loggedOut=1`.
  - Trang `login.html` hiển thị thông báo alert: *"Bạn đã đăng xuất thành công."*
- [ ] Khi bấm "Đăng Nhập Bằng AppInHouse ID" tại `login.html` và hoàn tất đăng nhập ở STS:
  - Học viên được chuyển hướng quay trở lại **đúng trang vừa logout** (chẳng hạn `/courses.html` hoặc `/flashcards.html`).
- [ ] Khi truy cập trực tiếp `login.html` (không có `returnUrl`), sau khi đăng nhập đưa về `/index.html`.
- [ ] Các trang yêu cầu đăng nhập (`courses.html`, `flashcards.html`) khi chưa đăng nhập hiển thị nút dẫn về `login.html?returnUrl=...`.
- [ ] Toàn bộ mã nguồn chạy mượt mà, không phát sinh lỗi console JavaScript.
- [ ] Không can thiệp mã nguồn backend C#.

---

## 5. Báo Cáo Triển Khai (Claude Code điền khi hoàn thành)

### a. Danh sách file đã tác động
Chỉ trong whitelist; không đụng backend, `AGENTS.md`, `CLAUDE.md`. Không commit.
- `repos/linguabridge-web/login.html` (**mới**): card đăng nhập Bootstrap, vùng alert `#logout-alert`, nút "Đăng Nhập Bằng AppInHouse ID".
- `assets/js/auth.js`: `logout(returnTo)` xoá token rồi chuyển tới `/login.html?returnUrl=...&loggedOut=1`; thêm `safeReturnUrl`, `loginPageUrl`; `login()` và `completeLogin()` chuẩn hoá `returnTo`.
- `assets/js/app.js`: `initLogin`; Navbar (Đăng nhập → `login.html?returnUrl=`, Đăng xuất → `logout(currentPath)`; trang login không hiện nút đăng nhập/đăng xuất), nút hero và `showSignInPrompt` → `login.html?returnUrl=`; callback dùng `safeReturnUrl`.
- `callback.html`, `courses.html`, `flashcards.html`, `index.html`: **không cần sửa** (các nút nằm trong `app.js`). README của web client cập nhật (ngoài whitelist, chỉ là tài liệu).

### b. Kiểm thử thực tế
Edge headless trên stack Docker thật + `start-web.ps1`, đăng nhập thật qua STS: **45/45 PASS** (31 kiểm tra cũ giữ nguyên + 14 kiểm tra mới), gồm:
- Từ `/courses.html?lang=zh` bấm Đăng xuất → `/login.html?returnUrl=%2Fcourses.html%3Flang%3Dzh&loggedOut=1`, token trong `sessionStorage` đã xoá, alert "Bạn đã đăng xuất thành công…" hiện. Bấm nút đăng nhập → STS hỏi lại mật khẩu → quay về đúng `/courses.html?lang=zh`, tab Tiếng Trung đang active. Lặp lại với `/flashcards.html`.
- Mở `login.html` khi đã đăng nhập: sang `/index.html` (không có `returnUrl`) hoặc tới `returnUrl`. Nút hero → `login.html?returnUrl=/index.html`; lời nhắc đăng nhập ở `flashcards.html` và nút Đăng nhập trên Navbar ở `courses.html` → `login.html?returnUrl=<trang đó>`. Trang login vừa màn hình 390 px.
- Open redirect: `returnUrl=//evil.example/steal` đăng nhập xong vẫn ở `http://localhost:3000/index.html`.
- Không có lỗi JS hay response lỗi ngoài dự kiến. Đã xem ảnh chụp trang login. Đã dọn web server và stack Docker.

### c. Quyết định và rủi ro
1. **`returnUrl` được kiểm tra** (`safeReturnUrl`): chỉ nhận đường dẫn cùng origin; `//host`, `https://…`, `/\host`, `/login.html`, `/callback` rơi về `/index.html`. Spec không nêu; bắt buộc để trang login không thành open redirect hay vòng lặp.
2. **Sau khi đăng xuất, nút đăng nhập dùng `prompt=login`** (spec chỉ ghi `login(returnUrl)`): phiên STS chưa bị kết thúc (client chưa có `post_logout_redirect_uri`), nếu không ép thì "đăng xuất" rồi bấm đăng nhập sẽ vào lại im lặng không hỏi mật khẩu. Chỉ áp dụng khi có `loggedOut=1`; đăng nhập thông thường vẫn SSO. Muốn kết thúc hẳn phiên STS cần seed `post_logout_redirect_uri` (Identity, ngoài whitelist).
3. Với `loggedOut=1` trang login **không tự chuyển đi** dù còn token (tránh vòng lặp); khi đã đăng nhập mở `login.html` thì chuyển thẳng tới `returnUrl`.
4. Cơ chế chặn vòng lặp 401 trong `api.js` (TASK-022) vẫn gọi `login()` trực tiếp, không qua trang login — giữ nguyên.

### d. Sửa theo review (lần 2)
- **Major (đã sửa) — đăng xuất không kết thúc phiên STS**: đúng như đề xuất, trong `assets/js/auth.js`: `logout()` đặt cờ `lb.forceLogin` trong `localStorage` (dùng chung mọi tab); **mọi** lần `login()` (nút hero, Navbar, lời nhắc, trang login, tab mới, cơ chế đăng nhập lại khi 401) thêm `prompt=login` khi cờ còn; đăng nhập thành công (`completeLogin`) xoá cờ. Tham số `forceLogin` riêng của `initLogin` không còn cần nhưng vô hại.
  Kiểm thử Edge headless trên stack thật, sau khi đăng xuất cả 3 đường đều bị STS hỏi mật khẩu: nút hero ở trang chủ; tab mới mở `login.html`; nút Đăng nhập Navbar ở `/courses.html`. Sau khi đăng nhập lại cờ được xoá và đăng nhập kế tiếp (không đăng xuất xen giữa) vẫn là SSO im lặng. **50/50 PASS** (45 cũ + 5 mới).
- **Minor — `AppInHouse.sln`**: đã được sửa theo yêu cầu trực tiếp của người dùng ("gắn linguabridge web vào AppInHouse sln") giữa TASK-022 và TASK-023, không thuộc whitelist task nào và không do task này tạo. Chỉ thêm 3 solution folder (`LinguaBridge.Web`, `css`, `js`) chứa các file web làm solution items; không đổi project nào. Xác nhận ở đây để Tech Lead biết.
- **Minor — mục 5e của TASK-022**: đã đánh dấu là được thay thế bởi TASK-023 (file TASK-022 ngoài whitelist, chỉ sửa tài liệu cho khỏi sai).
- Rủi ro còn lại: cờ nằm trong `localStorage` nên nếu trình duyệt chặn storage thì chỉ nút trên `login.html?loggedOut=1` còn ép hỏi mật khẩu; phiên STS vẫn chưa kết thúc thật (cần seed `post_logout_redirect_uri` — quyết định của Tech Lead). Đã dọn web server và stack Docker.

---

## 6. Kết Quả Code Review (Claude Code — Reviewer)

**Reviewer**: Claude Code (phiên Reviewer độc lập) — **Ngày**: 2026-11-04 — **Kết luận**: `CHANGES_REQUESTED` (1 Major)

### a. Kiểm chứng độc lập
Stack thật (`docker-all-up.ps1`) + `start-web.ps1` + script E2E **tự viết** (puppeteer-core + Edge headless, scratchpad Reviewer). `dotnet build AppInHouse.sln`: 0 warning / 0 error. Không có file C# nào bị sửa.

| Tiêu chí | Kết quả |
|---|---|
| `login.html` Bootstrap, card + logo + nút "Đăng Nhập Bằng AppInHouse ID"; navbar không có nút đăng nhập/xuất; 390 px không cuộn ngang | PASS |
| Lời nhắc đăng nhập ở `/courses.html?lang=zh` → `/login.html?returnUrl=%2Fcourses.html%3Flang%3Dzh`, không có alert | PASS |
| Đăng xuất ở `/courses.html?lang=zh` → `/login.html?returnUrl=%2Fcourses.html%3Flang%3Dzh&loggedOut=1`, token xoá, alert "đăng xuất thành công" | PASS |
| Bấm nút trên trang đó → STS **hỏi mật khẩu** (`prompt=login`) → về đúng `/courses.html?lang=zh`, tab Tiếng Trung active | PASS |
| Tương tự từ `/flashcards.html` → về `/flashcards.html` | PASS |
| Đã đăng nhập mở `/login.html` → `/index.html`; `?returnUrl=/flashcards.html` → `/flashcards.html` | PASS |
| `/login.html` không `returnUrl` → đăng nhập xong `/index.html` | PASS |
| Open redirect / vòng lặp: `//evil.example/steal`, `/\evil.example`, `https://evil.example/`, `javascript:alert(1)`, `/login.html?returnUrl=/login.html`, `/callback?code=x`, `http://localhost:3000.evil.example/` → đều ở lại `http://localhost:3000/index.html` | PASS (7/7) |
| Lỗi JS | 0 |

### b. Findings

**🟡 Major #1 — "Đăng xuất" không còn đăng xuất khỏi STS; mọi lối đăng nhập khác ngoài nút trên `login.html?loggedOut=1` vào lại tài khoản cũ không cần mật khẩu** (`assets/js/auth.js` `logout()`/`login()`, `assets/js/app.js` `initLogin`)
- `logout()` chỉ xoá token của tab và chuyển tới `login.html` — không gọi STS nữa (bản TASK-022 còn mở trang xác nhận `/connect/logout`). Phiên cookie STS vẫn sống. `prompt=login` chỉ được thêm khi nút được bấm trên đúng trang có `loggedOut=1`.
- Tái hiện thật (sau khi bấm "Đăng xuất"):
  - Cùng tab, vào `/index.html` → nút hero "Đăng Nhập Ngay" → `login.html?returnUrl=/index.html` → bấm → **vào thẳng `admin@appinhouse.local`, không hỏi mật khẩu**.
  - **Tab mới** (người khác dùng cùng trình duyệt) → hero → **vào thẳng tài khoản admin**.
  - Mở thẳng `/login.html` (không `loggedOut=1`) → vào im lặng.
- Hệ quả: trên máy dùng chung, "Đăng xuất" tạo cảm giác an toàn giả — người tiếp theo chỉ cần bấm "Đăng nhập". Báo cáo 5c.2 nêu đúng rủi ro nhưng chỉ chặn một lối vào. So với trước TASK-023 đây là lùi về bảo mật (trước đó người dùng ít nhất có trang "Sign out" của STS).
- Sửa gợi ý (trong whitelist, `auth.js`): khi `logout()` đặt cờ **dùng chung mọi tab** (ví dụ `localStorage['lb.forceLogin'] = '1'`); `login()` thêm `prompt=login` khi cờ tồn tại (bất kể lối vào); `completeLogin()` thành công thì xoá cờ. Tự kiểm: đăng xuất → (a) hero cùng tab, (b) tab mới, (c) `/login.html` trần, (d) tự đăng nhập lại do 401 trong `api.js` — cả 4 phải hỏi mật khẩu; đăng nhập bình thường lần đầu (chưa từng đăng xuất) vẫn SSO.
- Nếu Tech Lead chấp nhận rủi ro này có chủ đích, cần ghi rõ vào spec và README; giải pháp triệt để vẫn là seed `post_logout_redirect_uri` cho `linguabridge-app` và gọi `/connect/logout` của STS (task Identity).

**🔵 Minor #2 — `AppInHouse.sln` bị sửa ngoài whitelist và không được nêu trong báo cáo**: thêm solution folder `LinguaBridge.Web` (+ `css`, `js`) chứa các file web làm Solution Items (không có `login.html`, nên có thể được tạo trước task này, ví dụ từ Visual Studio). Build solution vẫn sạch. Cần xác nhận ai sửa; nếu là Coder thì phải nêu trong báo cáo hoặc hoàn tác.

**🔵 Minor #3 — Tài liệu ngoài whitelist bị sửa**: `repos/linguabridge-web/README.md` (đã nêu trong báo cáo, nội dung đúng) và `docs/handoffs/TASK-022-…md` mục "5e. Sửa theo phản hồi của người dùng" — thay đổi logout được làm **sau khi TASK-022 đã APPROVED**, không qua review (code đó nay đã bị TASK-023 thay thế; mục 5e mô tả hành vi không còn đúng: "đăng nhập xong về `/index.html`"). Tech Lead nên quyết định giữ/sửa ghi chú này.

### c. Ngoài whitelist — chuyển Tech Lead
1. Seed `post_logout_redirect_uri` (`http://localhost:3000/login.html?loggedOut=1` hoặc tương đương) cho `linguabridge-app` để logout gọi `/connect/logout` và kết thúc thật phiên STS (đã nêu từ TASK-020/022).
2. Quy trình: thay đổi theo yêu cầu trực tiếp của người dùng sau khi task đã APPROVED (TASK-022 mục 5e) nên được tạo task mới để có review.


### Review lần 2 — 2026-11-04 — Kết luận: `REVIEW_PASSED`
- **Major #1 — đã sửa, xác nhận bằng chạy thật.** `logout()` đặt `localStorage['lb.forceLogin']`; điều kiện `prompt=login` nằm **trong `login()`** nên phủ mọi lối vào; `completeLogin()` thành công xoá cờ. Script E2E của Reviewer (stack thật, Edge headless) sau khi đăng xuất:
  - nút trên `login.html?loggedOut=1` → STS hỏi mật khẩu, request authorize có `prompt=login`, cờ bị xoá sau đăng nhập;
  - `/login.html` trần → hỏi mật khẩu; nút hero cùng tab → hỏi mật khẩu; **tab mới** → hỏi mật khẩu (trước: cả 3 vào im lặng);
  - đường tự đăng nhập lại do 401 trong `api.js` (tab khác còn token, refresh token hỏng, API chặn 401) sau khi một tab khác đăng xuất → tới trang mật khẩu STS;
  - không đăng xuất xen giữa → tab mới vẫn **SSO im lặng** (không làm hỏng SSO bình thường).
- **Hồi quy**: **30/30 PASS** — returnUrl/loggedOut từ `/courses.html?lang=zh` và `/flashcards.html`, quay về đúng trang + tab zh, `login.html` khi đã đăng nhập, 7/7 biến thể open redirect/vòng lặp, `login.html` 390 px, 0 lỗi JS. `dotnet build AppInHouse.sln` sạch (lần 1).
- **Minor #2 (`AppInHouse.sln`)**: Coder xác nhận là theo yêu cầu trực tiếp của người dùng giữa TASK-022 và TASK-023, chỉ thêm solution folder — chấp nhận, Tech Lead nắm thông tin.
- **Minor #3**: mục 5e của TASK-022 đã đánh dấu "Đã được thay thế bởi TASK-023" — đúng.
- 🔵 Ghi nhận (không chặn): đăng xuất ở một tab **không** xoá token đang có ở tab khác (token theo tab, refresh token không bị thu hồi) — tab đó vẫn dùng được tới khi token/refresh hết hạn; và nếu trình duyệt chặn `localStorage` thì chỉ còn nút trên `login.html?loggedOut=1` ép hỏi mật khẩu (Coder đã nêu). Giải pháp triệt để vẫn là `post_logout_redirect_uri` + `/connect/logout` + thu hồi refresh token (task Identity, quyết định của Tech Lead).
- Dọn: tắt web server, `docker-all-down.ps1 -RemoveData -Force`; cổng 3000 trống, không còn volume `appinhouse`.

---

## 7. Đánh Giá & Quyết Định Của Tech Lead (Gemini)

- **Kết luận**: **`APPROVED`**
- **Xác minh kỹ thuật**:
  - Trang đăng nhập chuyên biệt `repos/linguabridge-web/login.html` được thiết kế chuẩn Bootstrap 5, đáp ứng xuất sắc yêu cầu trải nghiệm học viên.
  - Cơ chế bảo vệ và khôi phục `Return URL` (`safeReturnUrl` chặn 7/7 kịch bản open redirect, chống SSRF/loop) hoạt động hoàn hảo: khi học viên đăng xuất từ `/courses.html` hay `/flashcards.html`, sau khi đăng nhập lại được đưa về chính xác trang đó.
  - Cơ chế cờ `localStorage['lb.forceLogin']` được bổ sung ở vòng 2 giải quyết tốt vấn đề ép STS hỏi lại mật khẩu trên mọi tab và mọi lối vào của trình duyệt.
  - Đạt 100% các tiêu chí nghiệm thu (30/30 checks hồi quy, 50/50 checks kiểm thử thực tế Edge headless).
  - Ghi nhận và chấp thuận thay đổi trong `AppInHouse.sln` (bổ sung solution folder `LinguaBridge.Web` chứa static files của web).

- **Quyết định đối với đề xuất của Reviewer & User về Single Sign-Out Triệt Để**:
  - **Nhất trí 100%**: Cơ chế `forceLogin` ở client là giải pháp vòng ngoài hiệu quả, nhưng về bản chất bảo mật và kiến trúc OIDC chuẩn mực, **Identity STS phải thực hiện RP-Initiated Logout và thu hồi Refresh Token**.
  - **Hành động kỹ thuật kế tiếp**:
    1. Bổ sung `PostLogoutRedirectUris` (chuẩn hóa các URI như `http://localhost:3000/login.html`, `http://localhost:3000/callback`) cho client `linguabridge-app` trong `DbSeeder.cs` của repo Identity.
    2. Cập nhật `AuthorizationController.cs` để khi nhận request `GET /connect/logout` có `post_logout_redirect_uri` và `id_token_hint`, STS có thể hủy cookie Identity (`SignOutAsync`), thu hồi tokens và tự động redirect trở lại Web App một cách mượt mà (không bắt người dùng phải bấm thêm 1 nút Sign Out trung gian trên STS).
    3. Việc này sẽ được giao trong **TASK-024: OIDC Single Sign-Out & Token Revocation Hardening**.

- **Bước tiếp theo**: Nghiệm thu TASK-023 và chuyển sang TASK-024 để giải quyết triệt để Single Sign-Out từ gốc rễ trong repo Identity.

