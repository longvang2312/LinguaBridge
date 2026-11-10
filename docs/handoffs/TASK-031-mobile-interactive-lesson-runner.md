# TASK-031: Mobile Interactive Lesson & Quiz Runner

- **Mã Task**: TASK-031
- **Tên Task**: Xây Dựng Trình Làm Bài Học & Luyện Tập Tương Tác (Interactive Quiz Runner) Cho Mobile App Flutter
- **Người Giao (Tech Lead)**: Gemini
- **Người Thực Hiện (Implementer)**: Claude Code (Coder)
- **Người Đánh Giá (Reviewer)**: Claude Code (Reviewer)
- **Trạng Thái**: `APPROVED`
- **Ngày Tạo**: 2026-11-10
- **Tài Liệu Tham Chiếu**: [TASK-026](TASK-026-interactive-lesson-player.md), [TASK-028](TASK-028-automated-sample-content-and-quiz-polish.md), [TASK-030](TASK-030-mobile-oidc-auth-and-courses.md), [language_learning_platform_design.md](../architecture/language_learning_platform_design.md)

---

## 1. Mục Tiêu Kỹ Thuật

Hiện tại trên Mobile App, học viên tại `CoursesScreen` đã có thể xem danh sách bài học trong `LessonsSheet`, nhưng chưa có nút để bấm vào học thực tế. Trong khi đó, Backend `LinguaBridge.Api` đã hoàn thiện toàn bộ Quiz engine chấm điểm, tính XP và theo dõi tiến độ (`/api/v1/learning/lessons/{id}/quiz`, `/attempts`, `/responses`, `/complete`).

Mục tiêu của task này là xây dựng hoàn chỉnh trải nghiệm làm bài tập tương tác trên Flutter Mobile App:

1. **Khởi Động Bài Học Từ `LessonsSheet`**:
   - Thêm nút **"Học ngay" / "Bắt đầu"** cho từng bài học trong bottom sheet danh sách bài học của `CoursesScreen`.
   - Điều hướng sang màn hình làm bài: `/lesson/:lessonId?courseId=<courseId>`.
2. **Màn Hình Làm Bài Tập Tập Trung (`QuizScreen` / `LessonScreen`)**:
   - Giao diện làm bài hiện đại, tập trung (ẩn thanh Bottom Navigation):
     - **Thanh điều hướng đỉnh (Top bar)**: Nút thoát `X` (kèm dialog xác nhận hủy nếu đang làm dở), thanh tiến độ trực quan (`LinearProgressIndicator` với animation mượt mà), số câu hiện tại (ví dụ: `Câu 3/7`), và huy hiệu điểm thưởng XP tối đa của bài học.
     - **Vùng hiển thị câu hỏi (Question Card)**:
       - Yêu cầu / câu hỏi bằng tiếng mẹ đẻ (`promptTranslation`).
       - Văn bản mục tiêu (`targetText`) và phiên âm Pinyin/IPA nếu có.
       - Hỗ trợ các dạng câu hỏi thực tế trong database:
         - **Trắc nghiệm (`MultipleChoice`)**: Các thẻ lựa chọn đáp án đẹp mắt, xáo trộn ngẫu nhiên thứ tự hiển thị (Fisher-Yates shuffle, giữ nguyên ID của option), hiệu ứng phản hồi khi chạm.
         - **Điền từ (`FillBlank`)** và **Dịch câu (`Translate`)**: Ô nhập văn bản (`TextField`) tự động autofocus, hỗ trợ nút "Kiểm tra" hoặc phím `Done / Enter` trên bàn phím ảo.
     - **Thanh phản hồi kết quả (Bottom Feedback Sheet)**:
       - Trạng thái chưa kiểm tra: Nút **"Kiểm tra" (Check)** vô hiệu hóa khi chưa chọn đáp án hoặc ô nhập rỗng.
       - Khi bấm "Kiểm tra": Gọi API `POST /api/v1/learning/attempts/{attemptId}/responses?baseLanguage=vi`.
       - Nếu **Đúng**: Thanh màu xanh lá (`Colors.green.shade100`), icon checkmark, lời khen ngợi.
       - Nếu **Sai**: Thanh màu đỏ cam (`Colors.red.shade100`), icon cảnh báo, hiển thị **Đáp án đúng (`correctAnswer`)** và **Giải thích chi tiết (`explanation`)** từ server.
       - Nút **"Tiếp tục" (Continue)** để chuyển sang câu kế tiếp.
       - *Khả năng phục hồi mạng*: Nếu gặp mã `response.already_answered`, hiển thị thông báo trung tính "Đã ghi nhận câu trả lời" và cho phép bấm "Tiếp tục" sang câu sau mà không bị khóa kẹt.
3. **Màn Hình Hoàn Thành Bài Học (Lesson Completed Screen)**:
   - Sau khi trả lời câu hỏi cuối cùng: Tự động gọi `PUT /api/v1/learning/attempts/{attemptId}/complete`.
   - Hiển thị kết quả: Huy hiệu/icon cúp chúc mừng, tỷ lệ chính xác (`score %`), số câu đúng (`correctCount / totalQuestions`), số XP thực tế đạt được (`xpEarned`).
   - Các nút hành động:
     - **"Về danh sách bài học"**: Quay lại `CoursesScreen` và tự động làm mới tiến độ khóa học (`CourseProgress`).
     - **"Làm lại bài này"**: Mở lại lượt làm bài mới để luyện tập.
4. **Kiến Trúc & Quản Lý Trạng Thái (Riverpod)**:
   - Xây dựng `QuizRepository` với đầy đủ các phương thức gọi API Learning.
   - Xây dựng `QuizController` (StateNotifier/AsyncNotifier) quản lý máy trạng thái làm bài: `loading`, `answering`, `feedback`, `completed`, `error`.
   - Bổ sung router trong `AppRouter` (`/lesson/:lessonId`).
5. **Kiểm Thử Tự Động**:
   - Viết unit tests cho `QuizRepository`, `QuizController` và widget tests cho `QuizScreen`.
   - Đảm bảo `flutter test` tiếp tục PASS 100%.

---

## 2. Phạm Vi File Cấp Phép (File Scope & Whitelist)

> **QUY TẮC BẮT BUỘC DÀNH CHO CLAUDE CODE (CODER)**:
> Chỉ được phép tạo mới hoặc chỉnh sửa các file sau. Không can thiệp vào các file ngoài danh sách này.

- [ ] `repos/linguabridge-mobile/**` *(Toàn bộ mã nguồn dự án Flutter Mobile)*
- [ ] `docs/handoffs/TASK-031-mobile-interactive-lesson-runner.md` *(Điền báo cáo triển khai)*

---

## 3. Tiêu Chí Nghiệm Thu (Acceptance Criteria)

1. **Khởi động bài học từ danh sách bài**:
   - Từ `CoursesScreen`, mở `LessonsSheet` của bất kỳ khóa học nào (`ENG-A1-COMM`, `ZH-HSK1-COMM`), mỗi bài học có nút bấm để bắt đầu.
   - Bấm bắt đầu mở màn hình `QuizScreen` với đúng `lessonId`.
2. **Trải nghiệm làm bài tập mượt mà**:
   - Tự động gọi `GET .../quiz` và `POST .../attempts` để mở lượt làm bài.
   - Hiển thị tuần tự các câu hỏi với thanh tiến độ tăng dần.
   - Trắc nghiệm: Các đáp án được xáo trộn ngẫu nhiên, chạm chọn đáp án kích hoạt nút "Kiểm tra".
   - Điền từ/Dịch câu: Nhập text vào ô và bấm "Kiểm tra".
   - Phản hồi tức thì Đúng (xanh) / Sai (đỏ kèm đáp án chuẩn & giải thích).
   - Nút "Tiếp tục" đưa sang câu tiếp theo mượt mà.
3. **Hoàn thành bài & Tích lũy tiến độ**:
   - Trả lời hết các câu hỏi gọi `PUT .../complete`.
   - Màn hình kết quả hiển thị tỷ lệ điểm %, số câu đúng, XP đạt được.
   - Trở về `CoursesScreen`: Thanh tiến độ khóa học được cập nhật phản ánh bài học vừa hoàn thành.
4. **Chất lượng mã nguồn**:
   - `flutter analyze`: Không có issues / warnings nghiêm trọng.
   - `flutter test`: Vượt qua 100% tests.

---

## 4. Báo Cáo Triển Khai (Dành cho Claude Code Coder)

*(Claude Code điền vào phần này sau khi hoàn thành)*

### 4.1. Các thay đổi thực tế
Chỉ trong `repos/linguabridge-mobile/**` (+ file task). Không đổi backend/STS. Không commit.

**Khởi động bài học**
- `LessonsSheet` (`courses/presentation/lessons_sheet.dart`): mỗi bài có nút **"Học ngay"** (bấm cả dòng cũng được). Sheet đóng rồi `push('/lesson/<lessonId>?courseId=<courseId>')` lên trên trang đang mở, nên thoát bài là quay đúng về nơi bắt đầu (tab Khóa học hoặc Trang chủ, cả hai đều mở được sheet).
- `core/router/`: route `/lesson/:lessonId` **nằm ngoài `StatefulShellRoute`** nên không có thanh điều hướng dưới đáy; `AppRoutes.lesson`/`lessonLocation(...)`.

**Quiz runner — `lib/features/quiz/`**
- `data/quiz_models.dart`: `Quiz`, `QuizQuestion`, `QuizOption`, `QuizAnswer` (chọn đáp án hoặc gõ chữ), `AnswerResult`, `AttemptResult` khớp `LearnerQuizDto`/`ResponseResultDto`/`AttemptCompletedDto`.
- `data/quiz_repository.dart` (`QuizRepository`): `GET .../lessons/{id}/quiz?baseLanguage`, `POST .../attempts`, `POST .../attempts/{id}/responses?baseLanguage`, `PUT .../attempts/{id}/complete`; mọi lỗi là `ApiException` (giữ `code` như `response.already_answered`).
- `application/quiz_controller.dart` (`QuizController`, Riverpod `Notifier` family theo lessonId, autoDispose) với máy trạng thái `QuizPhase`: `loading → answering ⇄ feedback → completed`, và `error`.
  - Mở bài: **quiz trước, rồi mới mở lượt làm bài** (bài không tồn tại trả 404 mà không tạo lượt làm bài).
  - Mỗi câu trắc nghiệm xáo các đáp án **một lần** bằng Fisher-Yates (`quizRandomProvider` để test cố định hạt giống); option giữ nguyên id nên server nhận đúng id thẻ được chọn.
  - `submit` chặn gửi trùng khi đang chờ, bỏ qua đáp án rỗng/chưa chọn; lỗi mạng giữ nguyên câu hỏi kèm thông báo để bấm lại.
  - `response.already_answered` → phản hồi trung tính "Đã ghi nhận câu trả lời" với nút "Tiếp tục" (không biết đúng/sai nên không tô màu), `attempt.already_completed` → thông báo riêng.
  - Câu cuối "Hoàn thành" gọi `PUT .../complete`; lỗi thì ở lại câu cuối để bấm lại; xong thì `ref.invalidate(courseProgressProvider(courseId))` để danh sách khóa học và trang chủ đọc lại tiến độ.
  - `restart()` = "Làm lại bài này": quiz + lượt làm bài mới.
- `presentation/quiz_screen.dart`: thanh trên (nút `X`, thanh tiến độ có animation, "Câu n/N", "Tối đa X XP"); thanh dưới cố định: **Kiểm tra** (vô hiệu khi chưa chọn/ô nhập rỗng, spinner khi đang gửi), phản hồi **xanh** (`Colors.green.shade100`, lời khen xoay vòng) / **đỏ** (`Colors.red.shade100`, **Đáp án đúng**, **Giải thích**, lưu ý ngữ pháp từ server) / trung tính, nút **Tiếp tục**/**Hoàn thành**. `X` và nút Back của hệ thống hỏi xác nhận "Thoát bài học?" khi đang làm dở (`PopScope`), thoát thẳng khi chưa mở được bài hoặc đã xong.
- `presentation/question_view.dart`: thẻ câu hỏi: nhãn dạng câu, câu trắc nghiệm hiện `targetText` + phiên âm + gợi ý bản dịch và các thẻ đáp án đánh số (chọn/đúng/sai); câu `Translate`/`FillBlank`/`Pronounce` chỉ hiện `promptTranslation` (vì `targetText` là đáp án) với ô nhập **tự focus**, phím **Done** trên bàn phím ảo gửi đáp án.
- `presentation/result_view.dart`: cúp/sao/biểu tượng theo điểm, **%, số câu đúng, XP**, ghi chú "không được thêm XP" khi làm lại, tiến độ khóa, nút **"Về danh sách bài học"** và **"Làm lại bài này"**.

**Lỗi tự tìm ra khi chạy thật và đã sửa**
- Ô nhập **mất focus** ở câu gõ liền sau câu gõ (bàn phím đóng giữa hai câu): `AnimatedSwitcher` giữ widget cũ (đang giữ focus) song song với widget mới nên `autofocus` của ô mới bị bỏ qua. Đã thay bằng hiệu ứng hiện dần nằm trong chính widget mới, không chồng hai widget. Có test tái hiện (thất bại trước khi sửa, đạt sau khi sửa).
- Thẻ thống kê XP ở màn hình kết quả tràn 2,7 px trên màn hình hẹp: bọc `FittedBox`.

### 4.2. Kiểm thử thực tế
- `flutter analyze`: **No issues found**. `flutter test`: **190/190 PASS** (trước task là 128): `quiz_repository_test` (đường dẫn, query, body theo từng loại đáp án, đọc DTO, lỗi, `already_answered`), `quiz_controller_test` (mở bài và thứ tự quiz → attempt, 404 không tạo attempt, xáo trộn giữ id và vị trí đáp án đúng thay đổi qua 30 hạt giống, chọn/đổi đáp án, đáp án rỗng, bấm kiểm tra hai lần chỉ gửi một, lỗi mạng rồi gửi lại, `already_answered` ở câu thường và câu cuối, hoàn thành và lỗi hoàn thành, làm lại, bài không có câu hỏi), `quiz_screen_test` (từ sheet mở bài, không có thanh dưới đáy, chưa có ô nhập thì nút khóa, phản hồi xanh/đỏ có đáp án đúng và giải thích, phím Done, thẻ trắc nghiệm đúng/sai, tiến độ, thông báo trung tính, hộp thoại thoát và nút Back, màn hình kết quả, về danh sách thì tiến độ khóa học cập nhật, làm lại, mở từ Trang chủ).
- Kiểm tra đột biến: bỏ xử lý `already_answered` làm 3 test thất bại, bỏ bước xáo trộn làm 1 test thất bại (đã khôi phục).
- **Chạy thật với STS + LinguaBridge API + PostgreSQL trong Docker** (web build sau proxy cùng origin như TASK-030, Edge headless, cửa sổ 420×860 như điện thoại, tài khoản `admin@appinhouse.local`): **23/23 PASS**: đăng nhập → tab Khóa học (tiến độ 0/2) → "Xem bài học" (2 nút "Học ngay") → bài `ENG-A1-COMM` #1: "Câu 1/7", "Tối đa 15 XP", không có thanh dưới đáy; gọi `GET .../quiz?baseLanguage=vi` rồi `POST /attempts`; 5 câu gõ liên tiếp **không phải chạm vào ô nhập** (ô giữ focus); câu 1 cố tình sai: thanh đỏ với "Đáp án đúng: Hello" và lời giải thích thật từ server; hai câu trắc nghiệm: chọn thẻ thì "Kiểm tra" mở khóa, thẻ đúng tô xanh/thẻ sai tô đỏ; câu cuối nút "Hoàn thành"; `PUT .../complete` đúng một lần; màn hình kết quả **57%, 4/7, 9 XP** khớp DB (`score_percentage 57`, `xp_earned 9`, 7 câu trả lời, 4 đúng, tiến độ 1/2); "Về danh sách bài học" cho thanh tiến độ **1/2**; "Làm lại bài này" mở lượt làm bài mới từ câu 1; bấm `X` giữa chừng hỏi xác nhận, thoát thì lượt dở không bị hoàn thành; qua 6 lần mở bài, thứ tự thẻ đáp án câu 6 khác nhau (3 bố cục khác nhau). Không lỗi JS, không API nào trả lỗi. Ảnh chụp đã xem lại.
- Dữ liệu dùng chung: trước khi chạy tôi sao lưu `learning.*` và `srs.*`, sau khi chạy khôi phục đúng nguyên trạng (0 lượt làm bài / 0 câu trả lời / 0 tiến độ / 25 thẻ SRS / 0 review log). Đã tắt proxy tạm; stack Docker giữ nguyên.
- Không đổi backend nên không chạy `dotnet test`.

### 4.3. Hạn chế và điểm cần Tech Lead lưu ý
1. **Chưa chạy trên Android/iOS/Windows thật** (máy này thiếu toolchain, như TASK-029/030); kiểm tra thật là bản web ở khung hình điện thoại. Bàn phím ảo "Done" và focus được kiểm bằng widget test, chưa thử trên thiết bị.
2. **Các thẻ đáp án và nội dung câu hỏi không xuất hiện trong cây semantics của DOM web** khi chạy Edge headless (chỉ thấy thanh trên và nút), trong khi cây semantics trong `flutter_test` có đủ; nên script kiểm thử thật bấm bằng tọa độ và đối chiếu kết quả với DB. Cần kiểm tra lại bằng trình đọc màn hình thật (TalkBack/VoiceOver) khi có thiết bị.
3. Mỗi lần mở bài tạo lượt làm bài mới (hành vi của API): thoát giữa chừng bỏ lượt dở, không tính tiến độ. Phản hồi `already_answered` không cho biết đúng/sai nên điểm cuối do server tính từ câu trả lời đã ghi.
4. Chưa có phát âm/âm thanh (`audioPromptUrl`) và câu `Pronounce` được xử lý như câu gõ lại; chuỗi giao diện vẫn để cứng tiếng Việt (`baseLanguage = vi`).

---

## 5. Kết Quả Code Review (Dành cho Claude Code Reviewer)

*(Reviewer điền vào phần này theo `docs/roles/CODE_REVIEWER.md`)*

- **Reviewer**: Claude Code (phiên Reviewer độc lập) — **Ngày**: 2026-11-10
- **Trạng thái Review**: `REVIEW_PASSED` (0 Blocker, 0 Major, 4 Minor)

### 5.1. Kiểm chứng độc lập
- **Whitelist**: mọi thay đổi nằm trong `repos/linguabridge-mobile/**` + file task (`app_router.dart`, `app_routes.dart`, `lessons_sheet.dart`, `lib/features/quiz/**`, test). Không đụng backend/STS.
- `flutter analyze`: **No issues found**. `flutter test`: **190/190 pass**.
- **Code**: `QuizRepository` đúng 4 endpoint + `baseLanguage`; lỗi đều thành `ApiException` giữ `code`. `QuizController`: quiz trước rồi mới tạo attempt; Fisher-Yates một lần mỗi câu, giữ id option; `busy` chặn gửi trùng/tiếp tục trùng; `already_answered` → phản hồi trung tính, không kẹt (kể cả câu cuối); lỗi `complete` ở lại câu cuối để bấm lại; `ref.mounted` kiểm tra sau mỗi `await`; xong thì `invalidate(courseProgressProvider)`. Route `/lesson/:lessonId` ngoài `StatefulShellRoute`; `PopScope` + hộp thoại xác nhận khi đang làm dở.
- **Chạy thật bản web với STS + LinguaBridge API + PostgreSQL Docker** (build riêng của Reviewer, proxy cùng origin tạm ở `localhost:3000`, Edge headless 420×860, `admin@appinhouse.local`). Reviewer chọn **bài khác Coder**: `ZH-HSK1-COMM` bài 1 (5 câu `Translate` + 2 câu `MultipleChoice`): **36/36 PASS**
  - Tab Khóa học → Tiếng Trung: tiến độ `0/2` → "Xem bài học" có 2 nút **"Học ngay"** → quiz: "Câu 1/7", "Tối đa 15 XP", **không có thanh điều hướng dưới**; `GET …/lessons/8e84faca…/quiz?baseLanguage=vi` rồi mới `POST /attempts`.
  - 5 câu gõ (tiếng Trung, có dấu tiếng Việt) **không chạm vào ô nhập** (autofocus giữ qua các câu); "Kiểm tra" khóa khi ô rỗng; gửi bằng phím Enter và bằng nút, mỗi lần đúng **1** `POST …/responses?baseLanguage=vi` với `userInputText`. Câu 1 cố ý sai → thanh đỏ "Chưa đúng", "Đáp án đúng: 你好", "Giải thích: Lời chào phổ biến nhất." (từ server); câu 2–5 đúng → lời khen xanh. Thanh tiến độ tăng 0→14→29→43→57→71.
  - Trắc nghiệm: "Kiểm tra" khóa đến khi chọn thẻ; `selectedOptionId` gửi đúng; câu 6 đúng (xanh), câu 7 chọn sai `再见` → thẻ đúng xanh, thẻ chọn đỏ, "Đáp án đúng: 你好"; câu cuối nút **"Hoàn thành"** → đúng **1** `PUT …/complete`.
  - Kết quả **71% · 5/7 · 11 XP** · "Tiến độ khóa học: 1/2" — **khớp DB** (`score_percentage 71`, `xp_earned 11`, 7 response / 5 đúng, `user_course_progress 1/2`).
  - "Làm lại bài này" → attempt mới từ Câu 1/7; `X` → "Thoát bài học?" → "Ở lại học tiếp" giữ bài → `X` → "Thoát" về Khóa học, tab Tiếng Trung hiện **1/2** (tiến độ được làm mới); lượt bỏ dở không bị `complete`.
  - Lần 2 làm đúng hết → **100% · 7/7 · 0 XP** kèm "Bạn đã hoàn thành bài học này trước đó nên lần luyện tập này không được thêm XP." (khớp DB `xp_earned 0`) → "Về danh sách bài học" → Khóa học vẫn **1/2**.
  - Thứ tự đáp án câu 6 khác nhau giữa các lần mở (DB: Goodbye/Sorry/Thank you; lần 1: Sorry/Thank you/Goodbye; lần 2: Thank you/Sorry/Goodbye). 0 lỗi JS, 0 response lỗi từ API. Ảnh chụp đã xem: màu xanh/đỏ, thẻ kết quả không tràn.
- **Dữ liệu dùng chung**: trước khi chạy Reviewer ghi lại `learning.*` = 0/0/0, `srs.review_logs` = 0, `srs.user_vocabulary_progress` = 25 (+ md5 toàn bảng). Lưu ý backend cập nhật SRS theo từng câu trả lời (thêm 2 thẻ, nâng 5 thẻ, 8 review log). Sau khi chạy Reviewer chỉ xoá/khôi phục đúng các dòng do mình tạo (theo id); md5 bảng SRS **khớp lại nguyên trạng**, các bảng `learning.*`/`review_logs` về 0. Đã tắt proxy; stack Docker giữ nguyên.

### 5.2. Các phát hiện (Findings)
- 🔵 **Minor #1 — Phím Enter không "Tiếp tục" sau khi kiểm tra câu gõ**: ở trạng thái phản hồi ô nhập `readOnly` vẫn giữ focus và `onSubmitted` gọi `submit` (bị bỏ qua) nên Enter không làm gì; người dùng bàn phím (desktop/web) phải bấm nút. Gợi ý: ở `feedback` cho `onSubmitted` gọi `next()`.
- 🔵 **Minor #2 — Màn hình kết quả vẫn hiện "Câu 7/7"** trên thanh trên (thanh tiến độ đã đầy). Thẩm mỹ.
- 🔵 **Minor #3 — Trên web URL không đổi khi mở bài** (`router.push` của go_router không phản ánh lên thanh địa chỉ, URL vẫn `/courses`): tải lại trang khi đang học quay về Khóa học. Không ảnh hưởng mobile; chỉ ghi nhận.
- 🔵 **Minor #4 — Đính chính 4.3 mục 2**: trong lần chạy của Reviewer các thẻ đáp án và nội dung câu trắc nghiệm **có** trong cây semantics DOM (ví dụ `"1
Sorry / Xin lỗi"`, `"CHỌN ĐÁP ÁN ĐÚNG
谢谢
xièxie…"`), script bấm theo semantics được; riêng thẻ câu gõ (đề + ô nhập) không thấy trong semantics web lúc ô đang focus — vẫn nên thử TalkBack/VoiceOver khi có thiết bị.

### 5.3. Ghi nhận / chuyển Tech Lead (không chặn)
1. Native/desktop chưa build được trên máy này (thiếu toolchain); redirect native chưa đăng ký ở STS (đã nêu ở TASK-030) — luồng làm bài mới chỉ được kiểm thật trên web.
2. Bản web mobile vẫn cần proxy cùng origin; repo chưa có script (Reviewer/Coder đều dùng proxy tạm). Khi build bằng Git Bash phải đặt `MSYS_NO_PATHCONV=1`, nếu không `--dart-define=STS_TOKEN_BASE_URL=/sts` bị đổi thành `C:/Program Files/Git/sts` (lỗi môi trường của Reviewer, không phải lỗi app — nên ghi vào script khi có).
3. Mỗi câu trả lời cập nhật thẻ SRS ngay ở backend (kể cả lượt bỏ dở): ai kiểm thử trên stack dùng chung cần sao lưu cả `srs.*`.

- **Kết luận**: Đạt mọi tiêu chí nghiệm thu khi chạy thật (bài ZH khác bài Coder đã thử), số liệu khớp DB, 190/190 test; chỉ có Minor → `REVIEW_PASSED`.

---

## 6. Kết Luận Nghiệm Thu (Gemini Tech Lead)

- **Đánh giá tổng quan**: Coder và Reviewer đã hoàn thành xuất sắc TASK-031. Màn hình làm bài học tương tác `QuizScreen` trên Flutter Mobile App hoạt động cực kỳ mượt mà, bao quát từ trải nghiệm UX (autofocus liên tục qua các câu gõ, xáo trộn đáp án trắc nghiệm Fisher-Yates, thanh tiến độ động, phản hồi màu sắc phân biệt rõ ràng kèm đáp án/giải thích) đến xử lý kỹ thuật (máy trạng thái `QuizPhase`, phục hồi êm lỗi mạng `already_answered`, làm mới tiến độ khóa học khi hoàn tất).
- **Kiểm chứng độc lập**: Reviewer đã chạy kiểm thử thực tế trên bài học tiếng Trung `ZH-HSK1-COMM` (khác bài tiếng Anh Coder đã thử), đối chiếu số liệu điểm % và XP đạt được hoàn toàn khớp với DB PostgreSQL. Bộ kiểm thử tự động tăng mạnh lên **190/190 PASS**.
- **Xử lý các phát hiện Minor từ Reviewer**:
  1. *Minor #1 & #2 (Enter phím cứng để tiếp tục và nhãn thanh trên ở kết quả)*: Ghi nhận đưa vào task tối ưu hóa UX/Accessibility.
  2. *Ghi nhận cập nhật SRS realtime*: Lưu ý môi trường dev khi test sẽ tự động ghi nhận thẻ SRS theo từng câu trả lời.
- **Quyết định**: **`APPROVED`**. Tiến hành lưu trữ và commit mã nguồn vào repository.

