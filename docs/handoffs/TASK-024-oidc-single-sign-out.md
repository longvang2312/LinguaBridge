# TASK-024: OIDC Single Sign-Out (RP-Initiated Logout) & Token Revocation Hardening

- **Mã Task**: TASK-024
- **Tên Task**: Triển Khai Chuẩn OIDC RP-Initiated Logout Cho Identity STS & Đồng Bộ Luồng Đăng Xuất Web Client
- **Người Giao (Tech Lead)**: Gemini
- **Người Thực Hiện (Implementer)**: Claude Code
- **Trạng Thái**: `APPROVED`
- **Ngày Tạo**: 2026-11-04
- **Tài Liệu Tham Chiếu**: [TASK-023](TASK-023-login-page-and-return-url.md), [repos/identity/README.md](../../repos/identity/README.md), [OpenID Connect RP-Initiated Logout 1.0](https://openid.net/specs/openid-connect-rpinitiated-1_0.html)

---

## 1. Mục Tiêu Kỹ Thuật

Giải quyết triệt để vấn đề Single Sign-Out từ gốc rễ trong cụm Identity STS và Web Client theo đúng đặc tả chuẩn **OpenID Connect RP-Initiated Logout 1.0**:
1. **Cấu Hình `PostLogoutRedirectUris` Cho Client `linguabridge-app` Trong Identity STS**:
   - Trong `DbSeederOptions.cs`: Bổ sung cấu hình `LinguaBridgeAppPostLogoutRedirectUris` (danh sách URI, mặc định gồm: `http://localhost:3000/login.html`, `http://localhost:5173/login.html`, `http://localhost:3000/callback`).
   - Trong `DbSeeder.cs`:
     - Cập nhật `BuildLinguaBridgeApp()` để thêm các `PostLogoutRedirectUris`.
     - **Tự động đồng bộ cho DB đã tồn tại**: Bổ sung logic (tương tự như `EnsureInternalServiceMayUseLinguaBridgeAsync`) để nếu client `linguabridge-app` đã có trong database, seeder tự động bổ sung các `PostLogoutRedirectUris` còn thiếu mà không bắt buộc phải reset database.
2. **Cập Nhật Endpoint `/connect/logout` Tại `AuthorizationController.cs` (STS)**:
   - Theo chuẩn OpenIddict RP-Initiated Logout: Hỗ trợ cả `[HttpGet("~/connect/logout")]` và `[HttpPost("~/connect/logout")]`.
   - Khi nhận được request logout từ client hợp lệ (có `id_token_hint` và `post_logout_redirect_uri` đã đăng ký):
     - Gọi `await _signInManager.SignOutAsync();` để hủy cookie phiên xác thực của ASP.NET Identity (`IdentityConstants.ApplicationScheme`).
     - Trả về `SignOut(authenticationSchemes: OpenIddictServerAspNetCoreDefaults.AuthenticationScheme, properties: new AuthenticationProperties { RedirectUri = "/" });`.
     - OpenIddict sẽ tự động sinh phản hồi 302 chuyển hướng trình duyệt thẳng về `post_logout_redirect_uri` kèm tham số `state` (nếu có), **không bắt học viên phải dừng lại bấm thêm nút xác nhận thủ công** trên giao diện STS.
   - Nếu truy cập trực tiếp bằng tay không có tham số client hợp lệ, vẫn giữ giao diện View xác nhận logout an toàn.
3. **Đồng Bộ Luồng Đăng Xuất Tại Web Client (`repos/linguabridge-web/assets/js/auth.js`)**:
   - Hàm `logout(returnTo)`:
     1. Lấy `idToken` từ `sessionStorage` (để truyền vào `id_token_hint`).
     2. Xóa sạch tokens và session ở client (`clearSession()`).
     3. Xây dựng URL chuyển hướng tới STS:
        - `target = safeReturnUrl(returnTo)`
        - `postLogoutUri = `${window.location.origin}/login.html?returnUrl=${encodeURIComponent(target)}&loggedOut=1``
        - Chuyển hướng tới `${config.stsUrl}/connect/logout?id_token_hint=${encodeURIComponent(idToken)}&post_logout_redirect_uri=${encodeURIComponent(postLogoutUri)}`.
     4. Loại bỏ việc phụ thuộc vào cờ tạm `localStorage['lb.forceLogin']` vì cookie STS đã được xóa sạch từ gốc.
4. **Kiểm Thử Toàn Trình (Verification)**:
   - Bấm Đăng xuất từ `/courses.html` ➡️ Trình duyệt gọi STS `/connect/logout` ➡️ Cookie phiên STS bị xóa ➡️ STS tự động redirect về `http://localhost:3000/login.html?returnUrl=%2Fcourses.html&loggedOut=1`.
   - Mở tab mới hoặc mở cửa sổ ẩn danh khác truy cập web ➡️ Hoàn toàn không còn phiên cũ.
   - Bấm Đăng nhập ➡️ STS hiển thị form nhập mật khẩu (do cookie phiên đã bị hủy) ➡️ Đăng nhập xong quay về đúng `/courses.html`.
   - Chạy test suites cả hai solution (`LinguaBridge` và `Identity`) bảo đảm 100% tests PASS, không phát sinh hồi quy.

---

## 2. Phạm Vi File Cấp Phép (File Scope & Whitelist)

> **QUY TẮC BẮT BUỘC DÀNH CHO CLAUDE CODE**:
> Claude Code **CHỈ ĐƯỢC PHÉP** tạo hoặc sửa các file trong danh sách dưới đây. Tuyệt đối không can thiệp vào `AGENTS.md` hay `CLAUDE.md`.

- [ ] `repos/identity/src/Infrastructure/AppInHouse.Identity.Infrastructure.Persistence/Seeders/DbSeeder.cs`
- [ ] `repos/identity/src/Infrastructure/AppInHouse.Identity.Infrastructure.Persistence/Seeders/DbSeederOptions.cs`
- [ ] `repos/identity/src/Presentation/AppInHouse.Identity.STS/Controllers/AuthorizationController.cs`
- [ ] `repos/identity/tests/AppInHouse.Identity.IntegrationTests/*` (nếu cần cập nhật test seeder/logout)
- [ ] `repos/linguabridge-web/assets/js/auth.js`
- [ ] `repos/linguabridge-web/README.md`
- [ ] `docs/handoffs/TASK-024-oidc-single-sign-out.md` (Điền báo cáo triển khai)

---

## 3. Tiêu Chí Nghiệm Thu (Acceptance Criteria)

- [ ] Client `linguabridge-app` được cấu hình đầy đủ `PostLogoutRedirectUris` (kể cả trên database cũ đã seed trước đó).
- [ ] Khi Web Client gọi `GET /connect/logout` với `id_token_hint` và `post_logout_redirect_uri`:
  - Cookie ASP.NET Identity của STS bị hủy hoàn toàn (`SignOutAsync`).
  - STS tự động chuyển hướng HTTP 302 về `post_logout_redirect_uri` mà không dừng lại ở trang View xác nhận.
- [ ] Trình duyệt quay về `login.html?returnUrl=...&loggedOut=1` với thông báo đăng xuất thành công.
- [ ] Sau khi đăng xuất, mọi tab của trình duyệt đều ở trạng thái chưa đăng nhập; bấm đăng nhập bắt buộc phải nhập lại email & mật khẩu trên STS.
- [ ] Sau khi đăng nhập thành công, học viên quay trở lại đúng trang vừa đứng trước khi logout.
- [ ] Toàn bộ unit tests và integration tests của cả Identity và LinguaBridge đều **PASS 100%**.
- [ ] Không can thiệp file ngoài whitelist.

---

## 4. Báo Cáo Triển Khai (Claude Code điền khi hoàn thành)

### a. Danh sách file đã tác động
Chỉ trong whitelist; không đụng `AGENTS.md`, `CLAUDE.md`. Không commit.
- `Seeders/DbSeederOptions.cs`: `LinguaBridgeAppPostLogoutRedirectUris` (rỗng → mặc định `http://localhost:3000/login.html`, `http://localhost:5173/login.html`, `http://localhost:3000/callback`).
- `Seeders/DbSeeder.cs`: `BuildLinguaBridgeApp()` thêm các URI đó; `EnsureLinguaBridgeAppHasPostLogoutRedirectsAsync` bổ sung **đúng các URI còn thiếu** cho client đã có (không đụng secret, redirect URI, URI do admin thêm); idempotent, ghi log.
- `STS/Controllers/AuthorizationController.cs`: `GET /connect/logout` — nếu request có **cả** `id_token_hint` và `post_logout_redirect_uri` (OpenIddict đã xác thực hint và đối chiếu URI đã đăng ký) thì `SignOutAsync()` + `SignOut(OpenIddict)` → 302 về `post_logout_redirect_uri` kèm `state`, không qua trang xác nhận; còn lại giữ View xác nhận. `POST` (form xác nhận) dùng chung `EndSessionAsync`.
- `tests/.../IntegrationTests/LogoutTests.cs` (**mới**): 5 test logout + 3 test seeder.
- `repos/linguabridge-web/assets/js/auth.js` (xem mục c.1), `README.md`.

### b. Kiểm thử thực tế
- `dotnet test`: **Identity 170 unit + 294 integration (286 cũ + 8 mới) pass**; **LinguaBridge 544 + 146 pass**; build 0 warning. Kiểm tra "có răng": tắt nhánh auto-logout → test chính thất bại, khôi phục → xanh.
- Test mới: logout hợp lệ → 302 về URI đã đăng ký kèm `state`, phiên STS mất (authorize lại đòi đăng nhập); không tham số → trang xác nhận và **phiên còn nguyên**; có URI nhưng không có `id_token_hint` → không tự đăng xuất (link ngoài không đăng xuất được người dùng); URI chưa đăng ký → không bao giờ redirect tới đó, phiên còn; form POST xác nhận vẫn hoạt động; seeder: client mới có 3 URI, client cũ (không có URI, 1 URI do admin thêm) được bổ sung mà giữ URI admin và redirect URI, chạy lại không đổi, đọc được cấu hình tuỳ chỉnh.
- Stack Docker thật (`docker-all-up.ps1`, log STS "Seeded client linguabridge-app") + Edge headless + đăng nhập thật: **50/50 PASS**. Gồm: đăng xuất ở `/courses.html?lang=zh` → STS `/connect/logout` → tự về `login.html?returnUrl=%2Fcourses.html%3Flang%3Dzh&loggedOut=1` có alert; sau đó **cả 3 đường** vào lại (nút hero, tab mới mở `login.html`, nút Đăng nhập ở Navbar) đều bị STS hỏi mật khẩu; đăng nhập xong về đúng `/courses.html?lang=zh`; không còn cờ `lb.forceLogin`; open redirect vẫn chặn.
- Hồi quy script: `verify-identity-e2e.ps1` 22/22, `verify-linguabridge-identity.ps1` 14/14. Đã dọn web server và stack Docker.

### c. Lệch spec và quyết định
1. **`post_logout_redirect_uri` không được kèm query string** (spec ghi `…/login.html?returnUrl=…&loggedOut=1`): OpenIddict chỉ chấp nhận URI trùng khớp **đúng nguyên văn** với URI đã đăng ký; đã chạy thử với spec, STS không redirect (kẹt ở trang xác nhận). Cách xử lý: gửi `post_logout_redirect_uri=<origin>/login.html` (đã đăng ký) và đưa trang cần quay lại vào tham số chuẩn `state` (`lb-logout.<base64url(returnUrl)>`); `auth.js` khi nạp trên `login.html` đổi `?state=…` thành `?returnUrl=…&loggedOut=1` (`history.replaceState`) trước khi `app.js` đọc URL. Làm vậy vì `app.js` không nằm trong whitelist; nếu muốn gọn hơn, cho phép sửa `initLogin` đọc thẳng `state`. `returnUrl` giải mã vẫn qua `safeReturnUrl`.
2. **Bỏ cờ `localStorage['lb.forceLogin']`** như spec; không còn cần vì cookie STS bị xoá. Trường hợp hiếm không có `id_token` (nên không chứng minh được với STS): đi thẳng tới `login.html?...&loggedOut=1`, nút ở đó vẫn dùng `prompt=login` (code của TASK-023 trong `app.js`, giữ nguyên).
3. **`GET /connect/logout` tự đăng xuất cần cả hint lẫn URI** (không chỉ URI): đăng xuất bằng GET không có bằng chứng người gửi cho phép trang bất kỳ đăng xuất người dùng (logout CSRF). Vẫn theo chuẩn RP-initiated logout vì client hợp lệ luôn có `id_token_hint`.
4. **Admin Web**: theo kiểm tra của reviewer, Admin Web **không gửi `id_token_hint`** nên vẫn dừng ở trang xác nhận như trước (không hồi quy, chưa có single sign-out cho Admin Web — quyết định của Tech Lead). (Câu chữ trước đây của báo cáo nói sai điều này.)
5. **Chưa làm** (spec nêu tiêu đề "Token Revocation Hardening" nhưng nội dung không yêu cầu): access/refresh token đã cấp vẫn hợp lệ đến khi hết hạn sau khi đăng xuất (chỉ phiên cookie STS bị huỷ); thu hồi token (revocation/authorization) là việc riêng.
6. DB đã seed: URI được thêm ở lần khởi động STS kế tiếp (đã có test seeder; không thử lại trên volume cũ thật). Nếu admin cố ý gỡ một URI mặc định, lần khởi động sau sẽ thêm lại.

### d. Sửa theo review (lần 2)
- **Major (đã sửa) — logout CSRF**: `GET /connect/logout` chỉ tự đăng xuất khi `id_token_hint` thật sự hợp lệ **và** thuộc người đang đăng nhập. `HintBelongsToSignedInUserAsync` gọi `AuthenticateAsync(OpenIddict)` để lấy principal đã xác thực của hint (OpenIddict bỏ qua hint không hợp lệ chứ không từ chối), rồi so `sub` với người giữ cookie Identity; khác nhau hoặc hint không xác thực được → trang xác nhận, phiên giữ nguyên. Chưa có cookie phiên thì không có gì để đăng xuất, nên hint hợp lệ vẫn được redirect.
- Test mới (`LogoutTests`): hint `garbage`, `a.b.c`, JWT `alg:none`; id_token của **người khác** (user `attacker@appinhouse.local` tạo trong test, token hợp lệ) — tất cả không đăng xuất nạn nhân. Thêm: id_token **đã hết hạn nhưng thật** (cấu hình `IdentityTokenLifetime` 1 giây) vẫn đăng xuất bằng một cú bấm, tức OpenIddict không kiểm tra hạn của hint nên người đọc lâu hơn 20 phút không bị đẩy vào trang xác nhận. Kiểm tra "có răng": thay điều kiện bằng `true` thì đúng 4 test (3 hint rác + hint người khác) thất bại như báo cáo của reviewer; khôi phục thì xanh.
- Kết quả: Identity 170 unit + **299** integration (294 trước đó + 5 test mới; `LogoutTests` nay có 10 test) pass; LinguaBridge 544 + 146 pass. Stack Docker thật + Edge headless: 50/50 PASS (đăng xuất hợp lệ của web vẫn hoạt động); `curl` với `id_token_hint=garbage` trả 200 (trang xác nhận), không redirect.
- Lưu ý môi trường của reviewer (hai phiên dùng chung stack Docker): ghi nhận; lần chạy này tôi dựng và gỡ stack riêng trong suốt thời gian làm.

---

## 5. Kết Quả Code Review (Claude Code — Reviewer)

**Reviewer**: Claude Code (phiên Reviewer độc lập) — **Ngày**: 2026-11-04 — **Kết luận**: `CHANGES_REQUESTED` (1 Major)

### a. Kiểm chứng độc lập
| Hạng mục | Kết quả |
|---|---|
| Build + test | LinguaBridge 0 warning, **544 + 146** pass; Identity 0 warning, **170 unit + 294 integration** (286 cũ + 8 `LogoutTests` mới) pass |
| Whitelist | `git status`: chỉ các file trong whitelist |
| Stack thật + web (Edge headless, script Reviewer) | **30/31**, check không đạt duy nhất là do test của Reviewer quá chặt (trang `login.html?loggedOut=1` vẫn gửi `prompt=login` — code TASK-023, vô hại, đã nêu ở 4c.2). Đăng xuất ở `/courses.html?lang=zh` → STS `/connect/logout?id_token_hint=ey…&post_logout_redirect_uri=http%3A%2F%2Flocalhost%3A3000%2Flogin.html&state=lb-logout.…` → 302 → `login.html?returnUrl=%2Fcourses.html%3Flang%3Dzh&loggedOut=1` + alert, token xoá; sau đó nút hero, **tab mới**, `/login.html` trần, đường tự đăng nhập lại khi 401 đều bị STS hỏi mật khẩu (phiên STS thật sự đã kết thúc); đăng nhập lại → đúng `/courses.html?lang=zh` (tab zh); không đăng xuất xen giữa → SSO im lặng; open redirect 7/7 vẫn chặn; 0 lỗi JS |
| Seeder trên DB cũ thật | `UPDATE` client `linguabridge-app` chỉ còn `["http://admin.example/custom"]` → restart STS → `["http://admin.example/custom", 3 URI mặc định]`, `RedirectUris` giữ nguyên; restart lần 2: log "Added … post-logout redirect address(es)" tổng cộng **1** dòng (idempotent) |
| Hint của client khác | id_token của `admin-portal` + URI của `linguabridge-app` → OpenIddict từ chối ("issued to a different client"), phiên còn — đúng |

### b. Findings

**🟡 Major #1 — Điều kiện "có `id_token_hint`" không chứng minh được gì: bất kỳ trang web nào cũng đăng xuất được người dùng khỏi STS (logout CSRF)** (`AuthorizationController.Logout`)
- Code chỉ kiểm `!string.IsNullOrEmpty(request.IdTokenHint)`. **OpenIddict 5 bỏ qua (không từ chối) một `id_token_hint` không hợp lệ**; nó chỉ từ chối hint *hợp lệ* thuộc client khác. Vì vậy comment trong controller, mục 4c.3 của báo cáo và test `Logout_WithRedirectButNoIdTokenHint_DoesNotSignOutOnAGet` tạo cảm giác an toàn sai.
- Tái hiện thật (curl, không cookie): `GET /connect/logout?id_token_hint=garbage&post_logout_redirect_uri=http%3A%2F%2Flocalhost%3A3000%2Flogin.html&state=s1` → **302** `http://localhost:3000/login.html?state=s1`; tương tự với `a.b.c` và một JWT `alg:none` không chữ ký. Chỉ khi hint rỗng mới ra trang xác nhận (200).
- Tái hiện trong trình duyệt (nạn nhân `admin@appinhouse.local` đang đăng nhập, mở một liên kết điều hướng cấp cao tới URL trên):
  - hint = chuỗi rác → **phiên STS của nạn nhân bị huỷ**;
  - hint = **id_token của chính tài khoản kẻ tấn công** (`attacker@appinhouse.local`, đăng nhập hợp lệ vào `linguabridge-app`) → **phiên STS của nạn nhân bị huỷ**.
- Hệ quả: một ảnh/đường link trên trang bất kỳ đăng xuất nạn nhân khỏi STS, tức mất SSO ở mọi ứng dụng (cả Admin Web). Mức độ của logout CSRF tự thân không cao, nhưng task đặt mục tiêu và khẳng định đã chặn nó; đặc tả RP-Initiated Logout 1.0 §2 cũng yêu cầu OP hỏi xác nhận khi không xác định được người dùng mà hint chỉ tới.
- Sửa gợi ý (trong whitelist): trong `Logout()` lấy principal của hint đã được OpenIddict **xác thực** — `var result = await HttpContext.AuthenticateAsync(OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);` (với yêu cầu logout, OpenIddict đưa principal của `id_token_hint` hợp lệ vào đây) — và chỉ tự đăng xuất khi `result.Principal` khác null **và** `sub` của nó trùng với người dùng đang đăng nhập (`_userManager.GetUserId(User)` / claim của cookie Identity). Không có người đăng nhập thì đăng xuất hay không cũng như nhau (có thể redirect thẳng). Các trường hợp còn lại → trang xác nhận. Thêm test: hint rác, hint `alg:none`, hint hợp lệ của **người khác** (cùng client) → trang xác nhận + phiên còn.

**🔵 Minor #2 — Mục 4c.4 nói Admin Web nay "đăng xuất thẳng không qua trang xác nhận" — không đúng.** Kiểm bằng trình duyệt: form Đăng xuất của Admin Web gửi `/connect/logout?post_logout_redirect_uri=…/signout-callback-oidc&state=…` **không có `id_token_hint`** (middleware OIDC không lưu id_token), nên vẫn dừng ở trang "Do you want to sign out…". Hành vi không đổi so với trước (không hồi quy), chỉ cần sửa báo cáo; muốn Admin Web cũng single sign-out cần `SaveTokens` hoặc tương đương (ngoài whitelist).

**🔵 Minor #3 — Lệch spec về `post_logout_redirect_uri`** (4c.1: không kèm query, mang `returnUrl` trong `state`): Reviewer đồng ý — OpenIddict so khớp nguyên văn; `state` là kênh chuẩn, giá trị giải mã vẫn đi qua `safeReturnUrl`. URI mặc định `http://localhost:3000/callback` trong danh sách post-logout (theo spec) không được dùng — vô hại.

### c. Ngoài whitelist — chuyển Tech Lead
1. Thu hồi token khi đăng xuất (tiêu đề task có "Token Revocation Hardening"): access/refresh token đã cấp vẫn hợp lệ tới khi hết hạn; tab khác còn token vẫn gọi API được (4c.5).
2. Admin Web chưa single sign-out (Minor #2).
3. Seeder thêm lại URI mặc định mà admin cố ý gỡ (4c.6) — cần quyết định chấp nhận hay chỉ thêm khi client chưa có URI nào.
4. Quan sát môi trường: khi Reviewer dựng stack, log STS có ~12 lượt đăng nhập `linguabridge-app` lúc 13:11–13:12 UTC **trước** khi Reviewer bật web server (13:16) — có vẻ một phiên khác chạy kiểm thử song song trên cùng stack/cổng 3000. Nên tránh hai phiên dùng chung stack Docker cùng lúc (`-RemoveData` của phiên này có thể phá phiên kia).


### Review lần 2 — 2026-11-04 — Kết luận: `REVIEW_PASSED`
- **Major #1 — đã sửa, xác nhận bằng chạy thật.** `HintBelongsToSignedInUserAsync`: `AuthenticateAsync(OpenIddict)` lấy principal **đã xác thực** của hint; chỉ tự đăng xuất khi `sub` của hint trùng người giữ cookie Identity (không có cookie → redirect, không có gì để bảo vệ).
  - curl `id_token_hint=garbage` / `a.b.c` / JWT `alg:none` + URI đã đăng ký → **200** (trang xác nhận), không còn 302.
  - Trình duyệt, nạn nhân `admin@appinhouse.local` đang đăng nhập: hint = id_token **của kẻ tấn công** (`attacker@appinhouse.local`) → trang xác nhận, phiên **còn**; hint rác → phiên **còn**; hint `admin-portal` + URI `linguabridge-app` → bị từ chối, phiên còn; hint = id_token **của chính nạn nhân** → 302 về `login.html`, phiên **kết thúc** (RP logout hợp lệ không bị ảnh hưởng).
- **Test**: LinguaBridge **544 + 146**, Identity **170 + 299** (`LogoutTests` 10 test, gồm hint rác/`alg:none`/người khác/hint hết hạn) pass, 0 warning.
- **Hồi quy web** (script Reviewer): 30/31 — check không đạt duy nhất là check quá chặt của Reviewer đã biết từ lần 1 (`prompt=login` do code TASK-023 trên `login.html?loggedOut=1`, mật khẩu vẫn được hỏi). Đăng xuất qua STS với `id_token_hint` + `post_logout_redirect_uri` + `state`, quay về đúng trang, tab mới/401 re-auth đều hỏi mật khẩu, SSO im lặng khi không đăng xuất, 0 lỗi JS.
- **Minor #2 — đã sửa**: 4c.4 nay ghi đúng Admin Web không gửi `id_token_hint` (vẫn trang xác nhận, không hồi quy).
- **Ghi chú môi trường**: khi Reviewer bắt đầu, stack `appinhouse` **đã chạy sẵn** (tạo 13:31 UTC, có 7 lượt đăng nhập tới 13:32, không còn tiến trình kiểm thử nào) — không phải của Reviewer. `docker-all-up.ps1` của Reviewer đã tái tạo container (volume giữ nguyên); Reviewer có thêm user `attacker@appinhouse.local` vào DB của stack đó và **không gỡ stack** để tránh phá dữ liệu của phiên khác. Người tạo stack nên tự `docker-all-down.ps1 -RemoveData` khi xong.
- Các mục ngoài whitelist (5c.1–5c.3: thu hồi token, single sign-out cho Admin Web, seeder thêm lại URI admin gỡ) vẫn chờ Tech Lead.

---

## 6. Đánh Giá & Quyết Định Của Tech Lead (Gemini)

- **Kết luận**: **`APPROVED`**
- **Xác minh kỹ thuật**:
  - Chuẩn **OIDC RP-Initiated Logout 1.0** đã được triển khai hoàn chỉnh và mẫu mực từ gốc rễ:
    1. Cấu hình `PostLogoutRedirectUris` cho client `linguabridge-app` trong `DbSeeder.cs`, tự động đồng bộ cho các DB cũ mà không mất dữ liệu.
    2. Endpoint `/connect/logout` của STS hỗ trợ auto-redirect 302 về `post_logout_redirect_uri` ngay khi nhận hint và URI hợp lệ, xóa sạch cookie phiên Identity (`SignOutAsync`).
    3. Cơ chế phòng chống Logout CSRF (`HintBelongsToSignedInUserAsync`) ở vòng sửa thứ 2 hoạt động rất chặt chẽ: chỉ tự động đăng xuất khi `id_token_hint` thực sự thuộc về người dùng đang giữ cookie phiên, chặn hoàn toàn các cuộc tấn công bằng hint giả mạo hay hint của tài khoản khác.
  - Phía Web Client (`repos/linguabridge-web/assets/js/auth.js`):
    1. Gửi request OIDC Logout chuẩn tắc (`/connect/logout?id_token_hint=...&post_logout_redirect_uri=...&state=...`).
    2. Đưa `returnTo` vào tham số `state` chuẩn và khôi phục an toàn qua `safeReturnUrl`.
    3. Loại bỏ hoàn toàn sự phụ thuộc vào cờ tạm `localStorage`.
  - Bộ kiểm thử `LogoutTests.cs` bổ sung 10 integration tests chất lượng cao. Toàn bộ 1,159 tests của hệ thống (LinguaBridge: 690, Identity: 469) đều PASS 100%.
  - Tuân thủ 100% phạm vi whitelist cấp phép.

- **Quyết định đối với các mục Reviewer nêu**:
  1. **Token Revocation (Thu hồi refresh token khi logout)**:
     - Ghi nhận vào backlog: Hiện tại phiên đăng nhập trên STS đã bị hủy hoàn toàn (mọi lượt đăng nhập tiếp theo đều bắt buộc nhập mật khẩu); việc thu hồi tường minh các authorization/refresh token của user qua `OpenIddictTokenManager.RevokeAsync` sẽ được đưa vào task bảo mật tiếp theo.
  2. **Single Sign-Out cho Admin Web**:
     - Ghi nhận backlog cấu hình `SaveTokens = true` cho OIDC middleware của Admin Web nếu muốn Admin Web cũng tự động đăng xuất thẳng.

- **Kết luận chung**: Vấn đề luồng Đăng xuất & Ghi nhớ Return URL đã được giải quyết trọn vẹn từ gốc rễ (Identity STS) tới ngọn (Web Client)!

