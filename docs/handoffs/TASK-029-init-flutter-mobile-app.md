# TASK-029: Initialize Flutter Mobile App (Cross-Platform) & Core Architecture

- **Mã Task**: TASK-029
- **Tên Task**: Khởi Tạo Dự Án Flutter Mobile App (Cross-Platform) & Thiết Lập Kiến Trúc Nền Tảng (Feature-First Clean Architecture)
- **Người Giao (Tech Lead)**: Gemini
- **Người Thực Hiện (Implementer)**: Claude Code (Coder)
- **Người Đánh Giá (Reviewer)**: Claude Code (Reviewer)
- **Trạng Thái**: `APPROVED`
- **Ngày Tạo**: 2026-11-05
- **Tài Liệu Tham Chiếu**: [language_learning_platform_design.md](../architecture/language_learning_platform_design.md) (Mục 6: Cấu trúc Solution)

---

## 1. Mục Tiêu Kỹ Thuật

Khởi tạo dự án ứng dụng di động đa nền tảng **LinguaBridge Mobile** sử dụng **Flutter 3.x / Dart 3.x** tại thư mục `repos/linguabridge-mobile`, đặt nền móng kiến trúc vững chắc theo mô hình **Feature-First Clean Architecture** kết hợp **State Management chuẩn (Riverpod)** và **GoRouter**:

1. **Khởi tạo dự án Flutter**:
   - Tạo dự án tại `repos/linguabridge-mobile` với package name `com.appinhouse.linguabridge_mobile`.
   - Hỗ trợ đa nền tảng: Android, iOS, Windows, Web (cho phép lập trình viên chạy kiểm thử ngay lập tức trên Windows Desktop hoặc Chrome mà không bắt buộc mở máy ảo Android nặng).
2. **Cấu hình Thư viện cốt lõi (`pubspec.yaml`)**:
   - `flutter_riverpod`: Quản lý state phản ứng (reactive state management) hiện đại, testable.
   - `go_router`: Điều hướng khai báo (declarative routing) hỗ trợ URL sync, redirect guards và nested shell routing (BottomNavigationBar).
   - `dio`: HTTP client chuyên nghiệp hỗ trợ Interceptors (tự động gắn Bearer Token và xử lý lỗi mạng).
   - `shared_preferences` / `flutter_secure_storage`: Lưu trữ cấu hình người dùng và access token an toàn.
3. **Cấu trúc thư mục Feature-First**:
   ```text
   repos/linguabridge-mobile/
   ├── lib/
   │   ├── core/
   │   │   ├── config/          # AppConfig (Địa chỉ API theo nền tảng: 10.0.2.2 cho Android Emulator, localhost cho Windows/Web)
   │   │   ├── network/         # Dio client, AuthInterceptor, ApiExceptions
   │   │   ├── theme/           # LinguaBridge Theme (Primary Blue #2563EB, typography, light/dark mode)
   │   │   └── router/          # AppRouter với ShellRoute (Bottom Navigation)
   │   ├── features/
   │   │   ├── auth/            # Màn hình Splash / Welcome / Login form
   │   │   ├── home/            # Màn hình Dashboard (tiến độ học tập, tổng quan)
   │   │   ├── courses/         # Màn hình Khám phá khóa học (Course List placeholder)
   │   │   ├── srs/             # Màn hình Ôn tập thẻ Flashcards (Deck placeholder)
   │   │   └── ai_tutor/        # Màn hình Gia sư AI (Chat session placeholder)
   │   └── main.dart            # Entry point với ProviderScope
   └── test/                    # Widget tests và Unit tests cơ bản
   ```
4. **Xử lý địa chỉ mạng thích ứng theo nền tảng (Platform-Aware Network Config)**:
   - Android Emulator: Dùng `http://10.0.2.2:5020` để kết nối API và `http://10.0.2.2:5001` để kết nối STS.
   - Windows Desktop / Web: Dùng `http://localhost:5020` (hoặc `http://127.0.0.1:5020`) và `http://localhost:5001`.
   - Cho phép ghi đè cấu hình qua Environment Variables hoặc compile-time flags (`--dart-define`).
5. **Kịch bản khởi chạy tiện lợi**:
   - Tạo script `scripts/start-mobile.ps1` hỗ trợ lập trình viên chạy nhanh app trên Windows (`flutter run -d windows`) hoặc Chrome (`flutter run -d chrome`).

---

## 2. Phạm Vi File Cấp Phép (File Scope & Whitelist)

> **QUY TẮC BẮT BUỘC DÀNH CHO CLAUDE CODE (CODER)**:
> Chỉ được phép tạo mới hoặc chỉnh sửa các file sau. Không can thiệp vào các file ngoài danh sách này.

- [ ] `repos/linguabridge-mobile/**` *(Toàn bộ mã nguồn dự án Flutter)*
- [ ] `scripts/start-mobile.ps1` *(Script hỗ trợ chạy nhanh Flutter app)*
- [ ] `docs/handoffs/TASK-029-init-flutter-mobile-app.md` *(Điền báo cáo triển khai)*

---

## 3. Tiêu Chí Nghiệm Thu (Acceptance Criteria)

1. **Khởi tạo và biên dịch thành công**:
   - Dự án Flutter tại `repos/linguabridge-mobile` biên dịch thành công không phát sinh lỗi hay warning nghiêm trọng.
   - Lệnh `flutter test` chạy vượt qua 100% tests.
2. **Kiến trúc & UI Shell hoạt động mượt mà**:
   - Khi chạy ứng dụng (trên Windows Desktop hoặc Chrome):
     - Giao diện LinguaBridge hiển thị chuẩn nhận diện thương hiệu (Màu xanh chủ đạo `#2563EB`).
     - Thanh điều hướng dưới đáy (Bottom Navigation Bar) hoặc Side Navigation có 4 tab: **Trang chủ (Home)**, **Khóa học (Courses)**, **Ôn tập (SRS Review)**, **Gia sư AI (AI Tutor)**.
     - Chuyển đổi qua lại giữa các tab mượt mà, giữ nguyên trạng thái nhờ `ShellRoute` của `go_router`.
3. **Mạng & Cấu hình môi trường**:
   - Module `AppConfig` tự động nhận biết chạy trên Android Emulator (`10.0.2.2`) hay Desktop/Web (`localhost`).
   - Mock/HTTP Client `Dio` được cấu hình sẵn sàng tích hợp với `LinguaBridge.Api`.
4. **Script khởi chạy tiện dụng**:
   - Chạy `./scripts/start-mobile.ps1` (hoặc `./scripts/start-mobile.ps1 -Device chrome`) khởi chạy ứng dụng thành công.

---

## 4. Báo Cáo Triển Khai (Dành cho Claude Code Coder)

*(Claude Code điền vào phần này sau khi hoàn thành)*

### 4.1. Các thay đổi thực tế
Chỉ trong whitelist (`repos/linguabridge-mobile/**`, `scripts/start-mobile.ps1`, file task). Không commit.
- **Dự án**: `flutter create --org com.appinhouse --project-name linguabridge_mobile --platforms android,ios,windows,web` (Flutter 3.47.7 / Dart 3.13.5). `applicationId` và `namespace` Android là `com.appinhouse.linguabridge_mobile`. Thư viện: `flutter_riverpod` 3.4, `go_router` 18.1, `dio` 5.11, `shared_preferences` 2.5, `flutter_secure_storage` 11.2.
- **`lib/core/config/app_config.dart`**: `AppConfig.resolve(isWeb, platform, apiOverride, stsOverride)` (hàm thuần, kiểm thử được trên mọi máy) và `AppConfig.fromEnvironment()`. Android emulator dùng `10.0.2.2`; Windows/macOS/Linux/iOS/Web dùng `localhost` (web luôn là `localhost` kể cả khi trình duyệt chạy trên Android); API cổng 5020, STS cổng 5001. Ghi đè bằng `--dart-define=API_BASE_URL=...` / `STS_BASE_URL=...` (giá trị rỗng = không đặt; bỏ dấu `/` cuối).
- **`lib/core/network/`**: `ApiException` (đọc problem-details `detail`/`title`/`code`, giữ cả body để lấy `resetsAt`; phân biệt lỗi mạng/timeout), `AuthInterceptor` (gắn `Authorization: Bearer`, không đè header do nơi gọi đặt, đổi mọi lỗi thành `ApiException`, gọi `onUnauthorized` khi 401), `dioProvider` (baseUrl theo `AppConfig`, timeout 10 s/20 s) và `appConfigProvider`.
- **`lib/core/storage/token_store.dart`**: `TokenStore` + `SecureTokenStore` (`flutter_secure_storage`; lỗi đọc coi như chưa có token) + `MemoryTokenStore` cho test. `shared_preferences` đã khai báo trong `pubspec.yaml` theo yêu cầu nhưng chưa có chỗ dùng.
- **`lib/core/theme/app_theme.dart`**: Material 3, màu chủ đạo `#2563EB` (app bar, nút, `primary` của light theme), có dark theme.
- **`lib/core/router/`**: `GoRouter` với `redirectFor(status, location)` (hàm thuần): `unknown` → splash, `signedOut` → welcome, `signedIn` bỏ qua splash/welcome. Bốn tab `/home`, `/courses`, `/srs`, `/tutor` nằm trong `StatefulShellRoute.indexedStack` (mỗi tab một navigator, giữ nguyên trạng thái khi đổi tab; đây là dạng giữ state của `ShellRoute` trong go_router). `AppShell`: `NavigationBar` dưới đáy khi rộng < 720 px, `NavigationRail` bên cạnh khi ≥ 720 px (mở rộng có chữ ≥ 1100 px); bấm lại tab đang mở sẽ về màn hình đầu của tab.
- **`lib/features/`** (Feature-First): `auth` (`AuthController` + Splash + Welcome), `home` (dashboard giữ chỗ + địa chỉ API/STS đang dùng + nút đăng xuất), `courses` (ô tìm kiếm + chip lọc ngôn ngữ thật, danh sách giữ chỗ), `srs` (bộ thẻ giữ chỗ), `ai_tutor` (khung chat giữ chỗ). `main.dart`: `ProviderScope` + `MaterialApp.router`, sáng/tối theo hệ thống.
- **Đăng nhập chưa làm** (task sau, OIDC Code + PKCE): màn hình Welcome có nút "Đăng nhập (sắp có)" bị vô hiệu và nút **"Xem trước giao diện"** (`startPreview`) để nhà phát triển vào khung ứng dụng không cần token. Các API gọi đi sẽ không có Bearer cho đến khi có đăng nhập thật.
- **`scripts/start-mobile.ps1`**: `-Device` (mặc định `chrome`; `windows`, `android`, id từ `flutter devices`...), `-ApiUrl`, `-StsUrl` (chuyển thành `--dart-define`), `-NoPubGet`, `-Release`; kiểm tra có `flutter` trên PATH, chạy `flutter pub get` rồi `flutter run`, báo lỗi theo mã thoát. Mặc định chọn `chrome` thay vì `windows` vì Windows desktop cần Visual Studio C++ (xem 4.3).

### 4.2. Kiểm thử thực tế
- `flutter analyze`: **No issues found**.
- `flutter test`: **30/30 PASS**:
  - `app_config_test.dart` (8): Android `10.0.2.2`; Windows/macOS/Linux/iOS `localhost`; web `localhost`; ghi đè, dấu `/` cuối, giá trị rỗng/khoảng trắng, ghi đè một địa chỉ không đổi địa chỉ kia.
  - `network_test.dart` (9): gắn Bearer, không gắn khi chưa đăng nhập, không đè header có sẵn, problem-details → `ApiException` (status, `code`, `detail`, `resetsAt`), body không phải JSON, 401 gọi `onUnauthorized` đúng một lần, 403 thì không, lỗi mạng, và `dioProvider` thật: baseUrl đúng, 401 đăng xuất và xóa token.
  - `app_test.dart` (13): `redirectFor` (3), theme (2), chưa có phiên → Welcome, có token → Home, bốn tab mở đúng màn hình, **tab giữ nguyên trạng thái** (gõ chữ và chọn chip ở Khóa học, sang Ôn tập rồi quay lại vẫn còn), màn hình rộng dùng NavigationRail, xem trước → vào app → đăng xuất → về Welcome, Home hiển thị địa chỉ API/STS (trong `flutter_test` nền tảng mặc định là Android nên là `10.0.2.2`).
- `flutter build web`: thành công.
- **Chạy bằng chính script**: `./scripts/start-mobile.ps1 -Device web-server -NoPubGet` biên dịch và phục vụ app (log: "API http://localhost:5020, STS http://localhost:5001"); mở bằng Edge headless: Welcome → "Xem trước giao diện" → `/#/home` → bấm lần lượt Khóa học, Ôn tập, Gia sư AI, URL đổi đúng (`#/tutor` cuối cùng), không lỗi JS/console; ảnh chụp đã xem (app bar xanh `#2563EB`, rail 4 tab ở 1100 px). Đã tắt tiến trình sau khi kiểm tra.
- Không đổi backend nên không chạy `dotnet test`.

### 4.3. Rủi ro, hạn chế và điểm cần Tech Lead lưu ý
1. **Windows desktop và Android chưa chạy được trên máy này**: `flutter doctor` báo thiếu workload "Desktop development with C++" của Visual Studio (nên `flutter run -d windows` chưa thử được) và thiếu Android cmdline-tools/licenses. Mã nguồn chỉ dùng API đa nền tảng, đã kiểm thử bằng test, Chrome/web-server và `flutter build web`; bản dựng Windows/Android chưa được biên dịch. Script mặc định `chrome` vì lý do này (`-Device windows` dùng được sau khi cài đủ workload).
2. **Đăng nhập chỉ là khung**: chưa có OIDC, chưa có refresh token; `AuthInterceptor` mới đăng xuất khi 401. Task sau cần lưu ý STS cấp token theo issuer `http://host.docker.internal:5001`, nên Android emulator (`10.0.2.2:5001`) hay Windows (`localhost:5001`) đều sẽ bị API từ chối token nếu không điều chỉnh issuer/Host (đã ghi chú trong `app_config.dart`). Web cần CORS (API/STS hiện chưa bật) hoặc proxy như `start-web.ps1`.
3. Địa chỉ Android emulator chưa được thử trên emulator thật; chỉ kiểm thử đơn vị.
4. Chuỗi giao diện đang để cứng tiếng Việt (chưa có i18n như web).
5. Màn hình tính năng đều là giữ chỗ, chưa gọi API.

---

## 5. Kết Quả Code Review (Dành cho Claude Code Reviewer)

*(Reviewer điền vào phần này theo `docs/roles/CODE_REVIEWER.md`)*

- **Reviewer**: Claude Code (phiên Reviewer độc lập) — **Ngày**: 2026-11-05
- **Trạng thái Review**: `REVIEW_PASSED` (không Blocker/Major; 2 Minor)

### 5.1. Kiểm chứng độc lập
- **Whitelist / git**: chỉ `repos/linguabridge-mobile/**`, `scripts/start-mobile.ps1`, file task. `.gitignore` của dự án loại `build/` (206 MB), `.dart_tool/` (84 MB), `.idea/`, `*.iml` → chỉ 109 file nguồn sẽ vào git.
- **Toolchain** (`flutter doctor`, Flutter 3.47.7): xác nhận đúng như báo cáo 4.3.1 — thiếu workload "Desktop development with C++" và Android cmdline-tools/licenses → bản Windows/Android **không build được trên máy này** (cả Coder lẫn Reviewer); kiểm chứng trên web.
- `flutter analyze`: **No issues found**. `flutter test`: **30/30 pass** (đã đọc test giữ trạng thái tab: gõ "business" + chọn chip → đổi tab → quay lại → khẳng định cả hai còn).
- **Code**: `AppConfig.resolve` thuần (Android không phải web → `10.0.2.2`, còn lại `localhost`; override qua `--dart-define`, bỏ `/` cuối); `AuthInterceptor` không đè header có sẵn, mọi lỗi → `ApiException`, 401 → `onUnauthorized`; `AuthController` xử lý race đọc token vs. bấm vào app; `SecureTokenStore` nuốt lỗi keystore; router `redirectFor` thuần + `StatefulShellRoute.indexedStack`; script kiểm tra `flutter` trên PATH, mã thoát rõ ràng.
- **Chạy bằng chính script** `./scripts/start-mobile.ps1 -Device web-server` (in "API http://localhost:5020, STS http://localhost:5001") + Edge headless qua cây semantics của Flutter: 14 check, **12 đạt ngay**, 2 không đạt do lỗi test của Reviewer và **đạt khi kiểm lại đúng cách** ( — `goto` chỉ đổi `#hash` không tải lại trang, và gõ phím vào ô text Flutter web trong headless — đã kiểm lại đúng cách):
  - Không token → `#/welcome`; nút "Đăng nhập (sắp có)" có mặt; "Xem trước giao diện" → `#/home`.
  - 4 tab Trang chủ / Khóa học / Ôn tập / Gia sư AI → `#/home`, `#/courses`, `#/srs`, `#/tutor`.
  - App bar đúng màu **`#2563EB`** (đọc pixel ảnh chụp).
  - 390 px: thanh điều hướng ở đáy (y = 764); 1280 px: NavigationRail bên trái.
  - Giữ trạng thái tab: chọn chip "Tiếng Trung" ở Khóa học → sang Ôn tập → quay lại: chip vẫn được chọn.
  - Tải mới `#/courses` khi chưa có phiên → `#/welcome`. 0 lỗi JS/console.
- Đã tắt toàn bộ tiến trình `flutter run`. Không đổi backend.

### 5.2. Các phát hiện (Findings)

**🔵 Minor #1 — Bản release Android không có quyền `INTERNET`** (`android/app/src/main/AndroidManifest.xml`)
- Template Flutter chỉ khai báo `android.permission.INTERNET` trong manifest `debug` và `profile` (để hot reload); `main` không có → bản `--release` (và mọi bản phát hành) không mở được socket, Dio sẽ lỗi ở mọi lời gọi API/STS dù bản debug chạy bình thường.
- Không chặn vì màn hình hiện chưa gọi API và Android chưa build được ở đây; nhưng task này dựng nền mạng nên nên thêm `<uses-permission android:name="android.permission.INTERNET"/>` vào manifest `main` ngay (trong whitelist).

**🔵 Minor #2 — Nhận diện bản web/Android còn tên mặc định**: `<title>`/`apple-mobile-web-app-title`, `manifest.json` (`name`, `short_name` = `linguabridge_mobile`, `theme_color`/`background_color` = `#0175C2` xanh Flutter, không phải `#2563EB`), `android:label="linguabridge_mobile"`. Tab trình duyệt và tên app trên máy hiện "linguabridge_mobile". Nên đổi thành "LinguaBridge" và màu thương hiệu.

### 5.3. Ghi nhận / chuyển Tech Lead
1. Bản Windows desktop và Android **chưa được biên dịch** bởi ai (môi trường thiếu toolchain); cần một máy có VS C++ workload + Android cmdline-tools để xác nhận trước khi coi hai nền tảng này là hỗ trợ.
2. Task đăng nhập kế tiếp phải giải quyết issuer (`http://host.docker.internal:5001`) cho `10.0.2.2`/`localhost`, và CORS (API/STS chưa bật) cho bản web — Coder đã nêu (4.3.2).
3. `flutter run -d web-server` lắng nghe trên `0.0.0.0` (mặc định của Flutter) → máy khác trong LAN truy cập được bản dev; có thể thêm `--web-hostname localhost` trong script.
4. `shared_preferences` khai báo nhưng chưa dùng; chuỗi giao diện cứng tiếng Việt (chưa i18n).

- **Kết luận**: Các tiêu chí nghiệm thu đạt trên nền tảng kiểm chứng được (web); hai Minor không chặn → `REVIEW_PASSED`.

---

## 6. Kết Luận Nghiệm Thu (Gemini Tech Lead)

- **Đánh giá tổng quan**: Coder và Reviewer đã thực hiện xuất sắc TASK-029. Dự án Flutter `repos/linguabridge-mobile` được khởi tạo bài bản với cấu trúc Feature-First Clean Architecture, state management Riverpod, routing khai báo GoRouter với `StatefulShellRoute` giữ nguyên trạng thái giữa 4 tab. Module `AppConfig` thông minh tự động tương thích nền tảng (Android `10.0.2.2`, Desktop/Web `localhost`).
- **Đã xử lý dứt điểm các phát hiện từ Reviewer**:
  1. *Minor #1*: Đã thêm quyền `<uses-permission android:name="android.permission.INTERNET"/>` vào `android/app/src/main/AndroidManifest.xml` để đảm bảo bản build release Android kết nối mạng bình thường.
  2. *Minor #2*: Đã cập nhật `android:label="LinguaBridge"` và nhận diện thương hiệu trong `web/manifest.json`, `web/index.html` (tiêu đề "LinguaBridge" và màu thương hiệu `#2563EB`).
- **Quyết định**: **`APPROVED`**. Tiến hành stage và commit mã nguồn dự án Mobile vào git.


