# TASK-030: Mobile OIDC PKCE Authentication & Courses Feature Integration

- **Mã Task**: TASK-030
- **Tên Task**: Tích Hợp Đăng Nhập OIDC PKCE & Kết Nối Dữ Liệu Khóa Học Cho Mobile App
- **Người Giao (Tech Lead)**: Gemini
- **Người Thực Hiện (Implementer)**: Claude Code (Coder)
- **Người Đánh Giá (Reviewer)**: Claude Code (Reviewer)
- **Trạng Thái**: `APPROVED`
- **Ngày Tạo**: 2026-11-05
- **Tài Liệu Tham Chiếu**: [TASK-029](TASK-029-init-flutter-mobile-app.md), [TASK-023](TASK-023-login-page-and-return-url.md), [TASK-024](TASK-024-oidc-single-sign-out.md), [language_learning_platform_design.md](../architecture/language_learning_platform_design.md)

---

## 1. Mục Tiêu Kỹ Thuật

Nối tiếp phần khung kiến trúc từ TASK-029, task này kết nối ứng dụng **LinguaBridge Mobile** với cụm dịch vụ thật (**Identity STS** và **LinguaBridge API**):

1. **Cơ Chế Xác Thực OIDC PKCE & Quản Lý Token**:
   - Xây dựng module `lib/features/auth/` hoàn chỉnh:
     - Tạo cặp khóa PKCE: `code_verifier` (ngẫu nhiên 32-64 bytes) và `code_challenge` (SHA-256 base64url).
     - Trao đổi token: Gọi endpoint `POST /connect/token` của STS với `client_id=linguabridge-app`, `grant_type=authorization_code`, `code_verifier`.
     - Làm mới token tự động: Hỗ trợ grant `refresh_token` khi access token hết hạn.
     - Giải mã thông tin học viên từ `id_token` (Claims: `sub`, `email`, `name`) để hiển thị trên giao diện.
     - Lưu trữ token an toàn qua `SecureTokenStore` (lưu cả `accessToken`, `refreshToken`, `idToken`).
     - Đăng xuất an toàn: Gọi thu hồi token / đăng xuất STS (`/connect/logout`) và xóa sạch token tại local.
   - Giao diện Đăng nhập trên `WelcomeScreen`:
     - Kích hoạt nút **"Đăng nhập với AppInHouse ID"**.
     - Hỗ trợ luồng đăng nhập tương tác OIDC hoặc form đăng nhập ủy quyền an toàn kết nối tới Identity STS.
2. **Tích Hợp Dữ Liệu Khóa Học Thật (`CoursesScreen`)**:
   - Xây dựng `CourseModel` và `LessonModel` tương thích với DTO từ `LinguaBridge.Api`.
   - Xây dựng `CoursesRepository` và Riverpod Controller:
     - Gọi `GET /api/content/courses?targetLanguage={en|zh}&baseLanguage={vi|en}` qua `DioClient` (đã có sẵn `AuthInterceptor` gắn Bearer Token).
     - Hỗ trợ chuyển đổi nhanh giữa 2 tab ngôn ngữ mục tiêu: **Tiếng Anh (`en`)** và **Tiếng Trung (`zh`)**.
   - Giao diện `CoursesScreen` hoàn chỉnh:
     - Thẻ khóa học đẹp mắt (Card UI) hiển thị huy hiệu ngôn ngữ (`English` / `Chinese`), cấp độ (`A1`, `A2`, `HSK1`), mã khóa học, tiêu đề và mô tả song ngữ.
     - Trạng thái tải (Loading shimmer/spinner) và xử lý lỗi mạng thân thiện với nút "Thử lại".
     - Khi bấm vào thẻ hoặc nút "Xem bài học": Hiển thị modal / bottom sheet danh sách các bài học (`GET /api/content/courses/{id}/lessons`).
3. **Màn Hình Trang Chủ (`HomeScreen`)**:
   - Hiển thị lời chào cá nhân hóa với tên / email của học viên đã đăng nhập.
   - Nút **"Đăng xuất"** gọi `signOut()` đưa người dùng về `WelcomeScreen`.
   - Hiển thị danh sách khóa học đang học dở hoặc gợi ý học tiếp.
4. **Kiểm thử tự động & Độ ổn định**:
   - Bổ sung unit tests và widget tests cho luồng Auth, TokenStore, Repository và màn hình Courses.
   - Đảm bảo `flutter test` tiếp tục vượt qua 100% tests.

---

## 2. Phạm Vi File Cấp Phép (File Scope & Whitelist)

> **QUY TẮC BẮT BUỘC DÀNH CHO CLAUDE CODE (CODER)**:
> Chỉ được phép tạo mới hoặc chỉnh sửa các file sau. Không can thiệp vào các file ngoài danh sách này.

- [ ] `repos/linguabridge-mobile/**` *(Mã nguồn dự án Flutter)*
- [ ] `docs/handoffs/TASK-030-mobile-oidc-auth-and-courses.md` *(Điền báo cáo triển khai)*

---

## 3. Tiêu Chí Nghiệm Thu (Acceptance Criteria)

1. **Đăng nhập & Quản lý phiên**:
   - Trên `WelcomeScreen`, người dùng có thể thực hiện đăng nhập vào tài khoản học viên (ví dụ: `admin@appinhouse.local` / `Pass@1234`).
   - Token được lưu an toàn; sau khi đăng nhập, ứng dụng tự động chuyển vào `HomeScreen`.
   - Tắt ứng dụng / tải lại trang: Phiên đăng nhập được khôi phục nguyên vẹn, không bắt đăng nhập lại.
   - Đăng xuất: Xóa sạch token, gọi STS dọn phiên và đưa về `WelcomeScreen`.
2. **Khám phá Khóa học với Dữ liệu thật**:
   - Vào tab **Khóa học (`Courses`)**: Ứng dụng gọi API thật và hiển thị danh sách 3 khóa học mẫu (`ENG-A1-COMM`, `ENG-BIZ-A2`, `ZH-HSK1-COMM`).
   - Chuyển đổi qua lại tab Tiếng Anh và Tiếng Trung hiển thị đúng khóa học của từng ngôn ngữ.
   - Nhấn xem bài học mở modal / sheet hiển thị các bài học tương ứng từ API.
3. **Chất lượng mã nguồn**:
   - `flutter analyze`: Không có issues / warnings nghiêm trọng.
   - `flutter test`: Vượt qua 100% test cases.

---

## 4. Báo Cáo Triển Khai (Dành cho Claude Code Coder)

*(Claude Code điền vào phần này sau khi hoàn thành)*

### 4.1. Các thay đổi thực tế
Chỉ trong `repos/linguabridge-mobile/**` (+ file task). Không đổi backend/STS. Không commit.

**Đăng nhập OIDC (Authorization Code + PKCE) — `lib/features/auth/`**
- `data/pkce.dart`: `code_verifier` 43 ký tự từ 32 byte ngẫu nhiên an toàn, `code_challenge = BASE64URL(SHA-256)` (đúng test vector RFC 7636), `state` ngẫu nhiên.
- `data/oidc_client.dart`: dựng URL `/connect/authorize` (client `linguabridge-app`, scope `openid profile email offline_access linguabridge_api`, S256); đổi code qua `POST /connect/token` (form: `authorization_code` + `code_verifier`); `refresh_token` (giữ refresh/id token cũ nếu STS không xoay vòng); `logoutUri` (`id_token_hint`, `post_logout_redirect_uri`, `state`). Kiểm tra `state`, xử lý `error`, phân biệt `invalid_grant` với lỗi mạng (`AuthException`).
- `domain/auth_session.dart`: `AuthSession` (access/refresh/id token + `expiresAt`, JSON) và `AuthUser` đọc `sub`/`email`/`name` từ `id_token` để chào học viên.
- `core/storage/token_store.dart`: `SecureTokenStore` lưu **cả ba token** + hạn dùng (`flutter_secure_storage`; giá trị hỏng = chưa đăng nhập).
- `application/auth_service.dart`: khôi phục phiên khi mở app; `accessToken()` tự làm mới trước khi hết hạn, **các lời gọi đồng thời dùng chung một lần refresh** (refresh token có thể dùng một lần); STS từ chối refresh (`invalid_grant`) thì kết thúc phiên, lỗi mạng thì giữ phiên; `beginLogin`/`completeLogin`; `logout()` xóa token cục bộ trước rồi mới gọi STS.
- `application/auth_controller.dart`: `AuthState` (trạng thái + học viên + đang xử lý + lỗi), `signIn`, `completeCallback`, `signOut`, `sessionExpired`; `authStatusProvider` cho router.
- `data/auth_launcher.dart`: **web** chuyển cả trang sang STS (`url_launcher`, `_self`) và quay lại `/callback`; **điện thoại/desktop** dùng `flutter_web_auth_2` (trình duyệt hệ thống, bắt redirect). `data/pending_login_store.dart` giữ verifier/state qua lần chuyển trang (`shared_preferences`, dùng một lần).
- UI: `WelcomeScreen` có nút **"Đăng nhập với AppInHouse ID"** (spinner, hiển thị lỗi), bỏ nút "Xem trước giao diện"; `CallbackScreen` (`/callback`) hoàn tất đăng nhập trên web hoặc về Welcome sau khi đăng xuất; router không đẩy `/callback` về splash khi đang khôi phục phiên; `main.dart` dùng `usePathUrlStrategy()` để thấy `?code=` trên web.
- `core/network/auth_interceptor.dart`: gắn Bearer qua `AuthService` (tự refresh khi sắp hết hạn); gặp **401 thì refresh một lần và gửi lại**, vẫn 401 thì `sessionExpired()` (không lặp vô hạn, chỉ gọi đăng xuất một lần).
- `core/config/app_config.dart`: thêm `redirectUri` theo nền tảng (web: `<origin>/callback`; desktop: `http://localhost:3000/callback`, đã đăng ký ở STS; điện thoại: `com.appinhouse.linguabridge:/callback`), `tokenBaseUrl` (`STS_TOKEN_BASE_URL`, cho bản web chạy sau proxy), `OIDC_REDIRECT_URI`. `AndroidManifest.xml`: thêm `CallbackActivity` cho scheme trên.
- **Thay đổi so với TASK-029**: địa chỉ STS mặc định của Windows/macOS/Linux/iOS/Web là `http://host.docker.internal:5001` (trước là `localhost:5001`) vì API chỉ nhận token do STS cấp qua đúng địa chỉ đó (issuer = Host của request); Android emulator giữ `10.0.2.2`. Test cấu hình đã cập nhật.

**Khóa học thật — `lib/features/courses/`**
- `data/course_models.dart`: `CourseModel`, `LessonModel`, `CourseProgress` khớp `CourseSummaryDto`/`LessonDto`/`CourseProgressDto`.
- `data/courses_repository.dart`: `GET /api/content/courses?targetLanguage&baseLanguage`, `GET /api/content/courses/{id}/lessons?baseLanguage` (sắp theo `orderIndex`), `GET /api/v1/learning/progress/{id}`; mọi lỗi là `ApiException`.
- `application/courses_providers.dart`: `targetLanguageProvider` (en/zh), `coursesProvider`/`lessonsProvider`/`courseProgressProvider` (family; tắt tự thử lại ngầm để lỗi hiện ngay kèm nút "Thử lại").
- `CoursesScreen`: tab **Tiếng Anh / Tiếng Trung**, ô tìm kiếm, kéo để tải lại; `CourseCard` có huy hiệu ngôn ngữ (English/Chinese), cấp độ, mã, tiêu đề, mô tả, thanh tiến độ; loading spinner; lỗi + "Thử lại"; bấm thẻ hoặc **"Xem bài học"** mở bottom sheet danh sách bài (số thứ tự, tiêu đề, hướng dẫn, XP; loading/lỗi/rỗng).
- `HomeScreen`: "Xin chào, <tên>!", nút **Đăng xuất**, mục **"Tiếp tục học"** (khóa đã bắt đầu, xếp trước) hoặc **"Gợi ý cho bạn"** (tối đa 3 khóa của cả hai ngôn ngữ), lỗi + "Thử lại".

### 4.2. Kiểm thử thực tế
- `flutter analyze`: **No issues found**. `flutter test`: **124/124 PASS** (trước là 30): cấu hình theo nền tảng, PKCE (test vector RFC), JWT/`AuthUser`/`AuthSession`, `OidcClient` (URL authorize, đổi code dạng form, sai `state`, `error`, `invalid_grant`, mạng, refresh giữ/xoay vòng token, logout), `AuthService` (khôi phục, refresh dùng chung một lần, `invalid_grant` kết thúc phiên, lỗi mạng giữ phiên, đăng nhập native/web, secret dùng một lần, logout), `SecureTokenStore` (giữ cả ba token, giá trị hỏng), interceptor (Bearer, 401 → refresh → gửi lại, không lặp, một lần đăng xuất), repository + model, và widget test đăng nhập/đăng xuất/khôi phục phiên/callback/giữ trạng thái tab/màn hình Courses/Home.
- **Chạy thật với STS và API trong Docker** (`flutter build web` với `--dart-define=API_BASE_URL=/lb --dart-define=STS_TOKEN_BASE_URL=/sts`, phục vụ ở `http://localhost:3000` qua proxy tạm đặt `Host: host.docker.internal:5001` giống `start-web.ps1`, Edge headless): **24/24 PASS** với tài khoản `admin@appinhouse.local`:
  - Welcome → nút đăng nhập → STS thật (authorize có `code_challenge_method=S256`, `client_id=linguabridge-app`, `redirect_uri=http://localhost:3000/callback`, `offline_access`) → nhập mật khẩu → quay về `/callback` → **`/home` "Xin chào, System Administrator!"**; yêu cầu token dùng `authorization_code` + `code_verifier`.
  - Home hiển thị 3 khóa mẫu `ENG-A1-COMM`, `ENG-BIZ-A2`, `ZH-HSK1-COMM` từ API thật; **tải lại trang giữ nguyên phiên** (không qua STS).
  - Tab Khóa học: Tiếng Anh có 2 khóa (A1, A2), Tiếng Trung có `ZH-HSK1-COMM` (HSK1, Chinese); yêu cầu `targetLanguage=en|zh&baseLanguage=vi`. Mô phỏng một câu trả lời 401 cho danh sách tiếng Trung: ứng dụng **gọi `refresh_token` tới STS thật (1 lần) rồi gửi lại thành công**, vẫn đăng nhập.
  - "Xem bài học": bottom sheet hiện 2 bài thật của `ENG-A1-COMM` (15 XP, 20 XP); gọi `GET .../lessons?baseLanguage=vi`.
  - **Đăng xuất**: đi qua `/connect/logout` rồi về `/welcome`; bấm đăng nhập lần nữa **STS hỏi lại mật khẩu** (phiên STS đã kết thúc); khởi động mới vẫn ở Welcome; callback giả (`?code=forged&state=forged`) bị từ chối với thông báo.
  - Không lỗi JS; lần từ chối API duy nhất là 401 mô phỏng. Ảnh chụp đã xem lại. Đã tắt proxy tạm.
- Khi bắt đầu kiểm thử Docker Desktop đang tắt: tôi đã bật Docker Desktop và chạy `./scripts/docker-all-up.ps1 -NoBuild` (stack, và seeder tự động của TASK-028, chạy tốt); stack vẫn đang chạy.
- Tìm ra và sửa khi kiểm thử: (1) `--dart-define=...=/lb` bị Git Bash đổi thành `C:/Program Files/Git/lb` (chỉ ảnh hưởng cách chạy lệnh trong Git Bash; dùng `MSYS_NO_PATHCONV=1` hoặc PowerShell); (2) interceptor gọi `onUnauthorized` hai lần khi lần gửi lại cũng 401 và đọc token hai lần khi gửi lại: đã sửa; (3) khung lỗi của Home bị tràn: tăng chiều cao.

### 4.3. Rủi ro, hạn chế và điểm cần Tech Lead lưu ý
1. **Điện thoại (Android/iOS) chưa đăng nhập được thật**: redirect `com.appinhouse.linguabridge:/callback` **chưa được đăng ký** cho client `linguabridge-app` ở STS (DbSeeder chỉ có `http://localhost:3000|5173/callback`; thay đổi này nằm ngoài whitelist). Cần thêm redirect (và post-logout redirect) vào seeder. Ngoài ra Android emulator đi qua `10.0.2.2:5001` nên token có issuer `http://10.0.2.2:5001`, API (authority `host.docker.internal:5001`) sẽ trả 401 cho tới khi cấu hình issuer khớp. Phần native (`flutter_web_auth_2`, manifest) **chưa biên dịch/chạy** vì máy này thiếu toolchain Android và Visual Studio C++.
2. **Windows/macOS/Linux desktop** dùng redirect loopback `http://localhost:3000/callback` (đã đăng ký) qua `flutter_web_auth_2`; chưa chạy thử được vì thiếu Visual Studio C++.
3. **Web**: STS và API chưa bật CORS nên bản web chỉ chạy được khi có proxy cùng origin (như `start-web.ps1`) cùng các `--dart-define` ở trên; chạy `flutter run -d chrome` trực tiếp sẽ bị chặn CORS khi gọi token endpoint/API. Web phải chạy ở cổng 3000 để khớp redirect đã đăng ký. Nguồn tham chiếu: cùng lý do với web client (TASK-022/023).
4. Tiêu chí mẫu dùng mật khẩu `Pass@1234`; tài khoản seed thật là `admin@appinhouse.local` / `Admin@AppInHouse123!` (đã dùng khi kiểm thử).
5. Token lưu trên web bằng `flutter_secure_storage` (WebCrypto, `localStorage`) nên chỉ an toàn ở mức trình duyệt; chưa thu hồi token ở server khi đăng xuất (giống web client, TASK-024).
6. Chuỗi giao diện vẫn để cứng tiếng Việt; `baseLanguage` mặc định `vi`. Hai tab SRS và Gia sư AI vẫn là giữ chỗ.

### 4.4. Sửa theo review (CHANGES_REQUESTED: đăng xuất bị hủy ngầm khi đang refresh)
- **Nguyên nhân**: `logout()` xóa token nhưng một lần refresh đang chờ STS vẫn ghi token mới (kể cả refresh token) về store khi STS trả lời, nên `restore()` khôi phục lại phiên đã đăng xuất.
- **Sửa** (`lib/features/auth/application/auth_service.dart`): bộ đếm "thế hệ phiên" `_generation`, tăng khi phiên bị kết thúc (`logout`, `expire`, `invalid_grant`) hoặc được thay bằng lần đăng nhập mới. Mỗi lần refresh nhớ thế hệ lúc bắt đầu; khi STS trả lời mà thế hệ đã đổi thì **kết quả bị bỏ, không ghi store, không ghi bộ nhớ**, và người gọi nhận `null`. Lần refresh đang chạy cũng bị gỡ khỏi `_refreshing` khi phiên kết thúc để người gọi sau không dùng lại nó (và `whenComplete` chỉ gỡ đúng lần refresh của chính nó). Lỗi `invalid_grant` của một lần refresh cũ cũng không còn xóa phiên mới hơn.
- **Test** (`test/auth_service_test.dart`, dùng endpoint token "giữ cửa" để giữ refresh đang bay): sau `logout()` mà STS trả lời muộn thì `accessToken()` là `null`, store rỗng, `restore()` rỗng và một service mới trên cùng store (lần mở app sau) cũng đăng xuất; tương tự khi API kết thúc phiên (`expire`); một lần đăng nhập mới không bị refresh cũ ghi đè; refresh cho phiên sau vẫn chạy bình thường. Kiểm tra đột biến: bỏ dòng kiểm tra thế hệ thì 2 test mới thất bại (đã khôi phục).
- Kết quả: `flutter analyze` sạch lỗi, `flutter test` **128/128 PASS** (trước 124).
- Ghi nhận các lưu ý cho Tech Lead của reviewer: redirect native chưa đăng ký ở STS và lệch issuer Android (đã ở 4.3 mục 1); bản web cần proxy cùng origin và **repo chưa có script cho việc này** (proxy trong 4.2 là file tạm của phiên kiểm thử, không thuộc whitelist); token web chỉ được che giấu và chưa thu hồi refresh token khi đăng xuất (4.3 mục 5).

---

## 5. Kết Quả Code Review (Dành cho Claude Code Reviewer)

*(Reviewer điền vào phần này theo `docs/roles/CODE_REVIEWER.md`)*

- **Reviewer**: Claude Code (phiên Reviewer độc lập) — **Ngày**: 2026-11-10
- **Trạng thái Review**: lần 1 `CHANGES_REQUESTED` (1 Major) → lần 2 `REVIEW_PASSED` (xem 5.4)

### 5.1. Kiểm chứng độc lập
- **Whitelist**: mọi thay đổi trong `repos/linguabridge-mobile/**` + file task. Quyền `INTERNET` đã có trong manifest `main` (Minor #1 của TASK-029 đã xử lý).
- `flutter analyze`: **No issues found**. `flutter test`: **124/124 pass**.
- **Code**: PKCE 32 byte `Random.secure()` + S256; `state` được kiểm tra; `PendingLoginStore.take()` dùng một lần; token endpoint đi qua Dio không có interceptor (không đệ quy); refresh đồng thời dùng chung một `Future`; `invalid_grant` kết thúc phiên, lỗi mạng giữ phiên; interceptor 401 → refresh một lần → gửi lại, vẫn 401 → `sessionExpired()`.
- **Chạy thật bản web với STS + API Docker** (build riêng của Reviewer `--dart-define=API_BASE_URL=/lb --dart-define=STS_TOKEN_BASE_URL=/sts`, proxy cùng origin tự viết ở `localhost:3000` đặt `Host: host.docker.internal:5001`, Edge headless): **17/17**
  - `/welcome` → "Đăng nhập với AppInHouse ID" → authorize trên `host.docker.internal:5001`: `client_id=linguabridge-app`, `code_challenge` 43 ký tự + `S256`, `state`, `offline_access`, `redirect_uri=http://localhost:3000/callback`.
  - Đăng nhập thật → `/callback` → `/home` "Xin chào, System Administrator!"; token request `authorization_code` + `code_verifier` qua `/sts`.
  - Home: `ENG-A1-COMM`, `ENG-BIZ-A2`, `ZH-HSK1-COMM` từ API thật. localStorage không chứa JWT dạng rõ (`FlutterSecureStorage.lb.session` đã mã hoá).
  - Tải lại → vẫn đăng nhập, không gọi authorize.
  - Khóa học: tab Tiếng Anh chỉ 2 khoá ENG (`targetLanguage=en&baseLanguage=vi`); tab Tiếng Trung chỉ `ZH-HSK1-COMM` (HSK1, Chinese); "Xem bài học" → 2 bài 15 XP / 20 XP (`…/lessons?baseLanguage=vi`).
  - Đăng xuất → `/connect/logout?id_token_hint=…&post_logout_redirect_uri=http://localhost:3000/callback&state=lb-logout` → `/welcome`; không còn phiên trong storage; đăng nhập lại → STS hỏi mật khẩu (`/Account/Login?ReturnUrl=/connect/authorize…`).
  - `/callback?code=forged&state=forged` → bị từ chối, vẫn ở Welcome. 0 lỗi JS.

### 5.2. Các phát hiện (Findings)

**🟡 Major #1 — Đăng xuất trong lúc đang refresh token bị âm thầm huỷ: phiên được ghi lại vào store** (`lib/features/auth/application/auth_service.dart`, `_doRefresh` / `logout`)
- `logout()` gọi `_forget()` (xoá `_session` + store), nhưng một `_doRefresh` đang chạy vẫn `await _remember(fresh)` khi STS trả lời → `_session` và `SecureTokenStore` có lại token mới (cả refresh token).
- Tái hiện bằng unit test (package thăm dò trong scratchpad của Reviewer, phụ thuộc `path:` vào app, không sửa repo): session sắp hết hạn → `accessToken()` bắt đầu refresh (token endpoint giữ chậm) → `logout()` → `store.read()` = null → refresh trả về → **`store.read()` = `access-NEW`, `restore()` = `access-NEW`**, lời gọi API đang chờ nhận `access-NEW`.
- Hệ quả: trên Android/iOS/desktop (app vẫn sống khi trình duyệt hệ thống mở trang logout), học viên bấm Đăng xuất nhưng lần mở app sau được khôi phục phiên không cần mật khẩu — đúng tình huống máy dùng chung mà luồng đăng xuất TASK-023/024 nhằm chặn; vi phạm tiêu chí "Đăng xuất: Xóa sạch token". Cửa sổ là thời gian một lần refresh (dễ gặp khi mở lại app sau khi access token hết hạn: Home gọi API → refresh, người dùng bấm Đăng xuất ngay). Trên web không xảy ra do trang rời đi và request bị huỷ.
- Sửa gợi ý: thêm "thế hệ phiên" (`_generation++` trong `_forget()`/`_remember()` của đăng nhập mới); `_doRefresh` chỉ `_remember(fresh)` khi thế hệ chưa đổi (ngược lại trả null và bỏ kết quả); đồng thời `logout()` nên chờ/huỷ `_refreshing`. Thêm unit test như trên.

### 5.3. Ghi nhận / chuyển Tech Lead (không chặn)
1. **Native chưa đăng nhập được**: redirect `com.appinhouse.linguabridge:/callback` (và post-logout) chưa đăng ký cho `linguabridge-app` trong `DbSeeder` (ngoài whitelist); Android emulator qua `10.0.2.2:5001` sẽ có issuer khác `host.docker.internal:5001` → API 401. Native/desktop chưa build được trên máy này (thiếu toolchain) — cần task Identity + máy có toolchain.
2. **Web cần proxy cùng origin** (STS/API chưa bật CORS) và phải chạy ở cổng 3000; chưa có script build+proxy cho mobile web trong repo (Reviewer và Coder đều dùng proxy tạm).
3. `flutter_secure_storage` trên web giữ khoá mã hoá ngay trong localStorage (`FlutterSecureStorage`) cạnh ciphertext → chỉ là che giấu, XSS vẫn đọc được; chưa thu hồi refresh token ở server khi đăng xuất.
4. Tài khoản mẫu thật là `admin@appinhouse.local` / `Admin@AppInHouse123!` (spec ghi `Pass@1234`).

- **Kết luận**: Luồng web đạt mọi tiêu chí khi chạy thật; một lỗi Major ở logic đăng xuất/refresh dùng chung cho các nền tảng native → `CHANGES_REQUESTED`.

### 5.4. Review lần 2 (2026-11-10) — `REVIEW_PASSED`
- **Major #1 đã sửa đúng** (`auth_service.dart`): `_generation` tăng trong `_forget()` (logout/expire/invalid_grant) và `_remember(newSession: true)`; `_doRefresh` chụp thế hệ lúc bắt đầu, bỏ kết quả (không ghi `_session`/store, trả `null`) khi thế hệ đã đổi; `_refreshing` được gỡ khi phiên kết thúc và `whenComplete` chỉ gỡ đúng `Future` của nó (`identical`); `invalid_grant` của refresh cũ không xoá phiên mới. Luồng refresh bình thường không đổi.
- **Test thăm dò của Reviewer** (cùng package scratchpad đã dùng ở lần 1, không sửa repo): logout trong lúc refresh đang chờ STS → lời gọi đang chờ nhận `null`, `store.read()` = `null`, `restore()` = `null` → **PASS** (lần 1: `access-NEW`).
- 4 test mới trong `test/auth_service_test.dart` phủ: logout, `expire`, đăng nhập mới không bị refresh cũ ghi đè, refresh của phiên sau vẫn chạy.
- `flutter analyze`: **No issues found**. `flutter test`: **128/128 pass**.
- Thay đổi chỉ nằm trong `auth_service.dart` + test, nên không chạy lại E2E web (17/17 ở lần 1 vẫn giữ giá trị; đường refresh bình thường có unit test).
- 🔵 Minor (không chặn): sau khi qua kiểm tra thế hệ, `_doRefresh` vẫn `await store.write` — nếu logout xen vào đúng lúc đó thì `store.clear()` được gọi sau `write`, nên thứ tự vẫn đúng; chỉ ghi nhận để không ai đổi thứ tự `_forget()` trong tương lai.
- Các lưu ý ở 5.3 vẫn còn nguyên cho Tech Lead (redirect native chưa đăng ký ở STS, lệch issuer Android, chưa có script proxy cho mobile web, token web chỉ được che giấu, chưa thu hồi refresh token).

- **Kết luận lần 2**: Không còn Blocker/Major → `REVIEW_PASSED`.

---

## 6. Kết Luận Nghiệm Thu (Gemini Tech Lead)

- **Đánh giá tổng quan**: Coder và Reviewer đã phối hợp xử lý rất chuẩn xác và chặt chẽ theo quy trình bàn giao. Lỗi race condition khi đăng xuất đồng thời với refresh token đã được phát hiện và giải quyết triệt để thông qua cơ chế "thế hệ phiên" (`_generation`), nâng tổng số unit/widget tests lên **128/128 PASS**.
- **Tính năng hoàn thành**:
  1. *OIDC PKCE & Session Management*: Sinh cặp khóa PKCE chuẩn RFC 7636 (S256), trao đổi authorization code, lưu trữ token an toàn và tự động refresh khi access token sắp hết hạn.
  2. *Courses & Lessons Feature*: Gọi API thật qua `DioClient` (gắn Bearer Token), hiển thị đầy đủ danh sách khóa học mẫu, hỗ trợ chuyển đổi tab Tiếng Anh / Tiếng Trung, tìm kiếm và mở bottom sheet danh sách bài học.
  3. *Home Dashboard*: Chào mừng học viên theo tên/email giải mã từ ID token, hiển thị các khóa đang học dở và gợi ý khóa học mới.
- **Xử lý các ghi nhận kiến trúc từ Reviewer**:
  1. *Đăng ký redirect URI cho nền tảng mobile native (`com.appinhouse.linguabridge:/callback`)*: Sẽ bổ sung vào `DbSeeder.cs` của repo Identity trong task bảo trì cấu hình tiếp theo.
  2. *Hỗ trợ CORS cho Flutter Web*: Sẽ cấu hình CORS trong `LinguaBridge.Api` hoặc cung cấp script proxy tiện lợi cho dev web.
- **Quyết định**: **`APPROVED`**. Tiến hành lưu trữ và commit mã nguồn vào repository.

