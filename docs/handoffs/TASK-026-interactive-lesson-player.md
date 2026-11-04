# TASK-026: Web Client Interactive Lesson & Quiz Player

- **Mã Task**: TASK-026
- **Tên Task**: Xây Dựng Trình Làm Bài Học & Luyện Tập Tương Tác (Interactive Quiz Player) Trên Web Client
- **Người Giao (Tech Lead)**: Gemini
- **Người Thực Hiện (Implementer)**: Claude Code (Coder)
- **Người Đánh Giá (Reviewer)**: Claude Code (Reviewer)
- **Trạng Thái**: `APPROVED`
- **Ngày Tạo**: 2026-11-04
- **Tài Liệu Tham Chiếu**: [TASK-022](TASK-022-init-linguabridge-web-bootstrap.md), [TASK-025](TASK-025-web-i18n-and-content-seeder.md), [language_learning_platform_design.md](../architecture/language_learning_platform_design.md)

---

## 1. Mục Tiêu Kỹ Thuật

Hiện tại, người dùng tại `courses.html` chỉ có thể mở modal xem danh sách bài học mà chưa thể bấm vào học thực tế. Trong khi đó, Backend `LinguaBridge.Api` đã có đầy đủ hệ thống Quiz engine và theo dõi tiến độ (`/api/v1/learning/lessons/{id}/quiz`, `/attempts`, `/responses`, `/complete`).

Mục tiêu của task này là xây dựng hoàn chỉnh trải nghiệm làm bài học tương tác trên Web Client:
1. **Nút "Bắt đầu học" (Start Lesson)** tại modal danh sách bài học của `courses.html`, chuyển hướng sang `lesson.html?lessonId=<guid>&courseId=<guid>`.
2. **Trang làm bài học `lesson.html`**:
   - Giao diện làm bài tập tập trung, hiện đại (tương tự trải nghiệm Duolingo/Busuu).
   - Thanh điều hướng trên cùng (Top bar): Nút thoát (kèm xác nhận hủy lượt làm bài nếu chưa xong), thanh tiến độ (Progress bar: ví dụ `Câu 3/7`), điểm XP tối đa của bài.
   - Vùng hiển thị câu hỏi (Question Card):
     - Hiển thị yêu cầu/câu hỏi được dịch theo `baseLanguage` (`promptTranslation`).
     - Hiển thị văn bản mục tiêu (`targetText`) và phiên âm/Pinyin/IPA (`pinyinOrIpa`) nếu có.
     - Hỗ trợ các dạng câu hỏi thực tế trong database:
       - **Trắc nghiệm (`MultipleChoice`)**: Các thẻ lựa chọn đáp án đẹp mắt, hỗ trợ click chọn hoặc phím tắt (1, 2, 3, 4).
       - **Điền từ (`FillBlank`)** và **Dịch câu (`Translate`)**: Ô nhập văn bản (text input) có autofocus, hỗ trợ nhấn phím `Enter` để kiểm tra.
   - Thanh phản hồi kết quả tức thì (Bottom Feedback Sheet):
     - Khi bấm **"Kiểm tra" (Check Answer)**:
       - Gọi API `POST /api/v1/learning/attempts/{attemptId}/responses?baseLanguage={vi|en}`.
       - Nếu **Đúng**: Thanh màu xanh lá (`bg-success-subtle`), icon checkmark, lời chúc mừng.
       - Nếu **Sai**: Thanh màu đỏ cam (`bg-danger-subtle`), icon cảnh báo, hiển thị **Đáp án chính xác** và **Giải thích chi tiết (`explanation`)** từ server.
     - Nút **"Tiếp tục" (Continue)** để chuyển sang câu hỏi kế tiếp.
   - Màn hình hoàn thành bài học (Lesson Completed Screen):
     - Gọi API `PUT /api/v1/learning/attempts/{attemptId}/complete`.
     - Hiển thị kết quả: Phần trăm chính xác (`score %`), số XP thực tế đạt được (`xpEarned`), huy hiệu chúc mừng.
     - Nút **"Về danh sách bài học"** và **"Ôn tập Flashcards"**.
3. **Mở rộng API Client & Đa ngôn ngữ**:
   - Cập nhật `assets/js/api.js`: Thêm các hàm `quiz`, `startAttempt`, `submitResponse`, `completeAttempt`.
   - Cập nhật `assets/js/i18n.js`: Cung cấp đầy đủ nhãn song ngữ `vi` và `en` cho toàn bộ luồng làm bài.
   - Cập nhật `assets/css/style.css`: Tạo style mượt mà cho quiz options, feedback banner và progress bar.

---

## 2. Phạm Vi File Cấp Phép (File Scope & Whitelist)

> **QUY TẮC BẮT BUỘC DÀNH CHO CLAUDE CODE (CODER)**:
> Chỉ được phép tạo mới hoặc chỉnh sửa các file sau. Không chạm vào các file nằm ngoài phạm vi này.

- [ ] `repos/linguabridge-web/lesson.html` *(Tạo mới)*
- [ ] `repos/linguabridge-web/assets/js/api.js` *(Bổ sung endpoint API learning/attempts)*
- [ ] `repos/linguabridge-web/assets/js/app.js` *(Thêm luồng `initLesson` và gắn link nút Start trong `openLessons`)*
- [ ] `repos/linguabridge-web/assets/js/i18n.js` *(Bổ sung từ vựng song ngữ cho luồng học)*
- [ ] `repos/linguabridge-web/assets/css/style.css` *(Thêm styles cho quiz player & feedback sheet)*
- [ ] `docs/handoffs/TASK-026-interactive-lesson-player.md` *(Ghi chép báo cáo triển khai)*

---

## 3. Tiêu Chí Nghiệm Thu (Acceptance Criteria)

1. **Khởi tạo bài học**:
   - Đăng nhập vào `courses.html`, mở modal xem bài học của bất kỳ khóa nào (`ENG-A1-COMM`, `ENG-BIZ-A2`, `ZH-HSK1-COMM`).
   - Mỗi bài học có nút bấm **"Học ngay" / "Bắt đầu"**. Khi click, điều hướng mượt sang `lesson.html?lessonId=...`.
2. **Luồng làm bài tương tác**:
   - Khi vào `lesson.html`:
     - Nếu chưa đăng nhập: chuyển hướng sang trang đăng nhập với `returnUrl` bảo toàn.
     - Nếu đã đăng nhập: tự động gọi `POST /api/v1/learning/attempts` để mở lượt làm bài và `GET /api/v1/learning/lessons/{id}/quiz?baseLanguage=...` để lấy danh sách câu hỏi.
   - Hiển thị từng câu hỏi một theo thứ tự `orderIndex`.
   - Chọn đáp án trắc nghiệm hoặc nhập từ điền khuyết -> bấm "Kiểm tra" -> hiển thị đúng/sai tức thì kèm giải nghĩa.
   - Bấm "Tiếp tục" chuyển câu kế tiếp, thanh tiến độ tăng dần.
3. **Hoàn tất bài học & Tích lũy XP**:
   - Sau khi trả lời câu hỏi cuối cùng, tự động gọi `PUT /api/v1/learning/attempts/{id}/complete`.
   - Màn hình tổng kết hiển thị điểm số và số XP đạt được.
   - Quay lại trang `courses.html` hoặc `index.html`: Thanh tiến độ của khóa học được cập nhật phản ánh bài học đã hoàn thành.
4. **Đa ngôn ngữ & Chất lượng mã nguồn**:
   - Toàn bộ text (nút, thanh tiến độ, lời khen, thông báo lỗi, giải thích) hỗ trợ đầy đủ `vi` và `en` khi chuyển ngôn ngữ.
   - Code không có lỗi console, giao diện responsive mượt mà trên cả Mobile và Desktop.

---

## 4. Báo Cáo Triển Khai (Dành cho Claude Code Coder)

*(Claude Code điền vào phần này sau khi hoàn thành)*

### 4.1. Các thay đổi thực tế
Chỉ trong whitelist; không đụng backend. Không commit.
- `lesson.html` (**mới**): bố cục tập trung (không có Navbar): thanh trên cùng (nút thoát, thanh tiến độ, "Câu n/N", "Tối đa X XP"), thẻ câu hỏi, thanh phản hồi cố định ở đáy (nút Kiểm tra/Tiếp tục), màn hình hoàn thành, hộp xác nhận thoát.
- `assets/js/api.js`: `quiz`, `startAttempt`, `submitResponse`, `completeAttempt`, thêm `course` (lấy ngôn ngữ đích của khóa để quay về đúng tab).
- `assets/js/app.js`: nút **Học ngay** ở mỗi bài trong modal (`lesson.html?lessonId=…&courseId=…`) và `initLesson`:
  - Chưa đăng nhập → `login.html?returnUrl=<đúng bài này>`; đã đăng nhập → `GET …/quiz?baseLanguage=` rồi `POST …/attempts` (gọi quiz trước để bài không tồn tại không tạo lượt làm bài), câu hỏi sắp theo `orderIndex`.
  - Trắc nghiệm: thẻ đáp án có số, click hoặc phím 1–9; Điền từ / Dịch / Gõ lại: ô nhập tự focus, `Enter` để kiểm tra. Câu gõ chữ: chỉ hiện nghĩa (`promptTranslation`) vì `targetText` chính là đáp án; trắc nghiệm hiện `targetText`, phiên âm và gợi ý bản dịch.
  - Kiểm tra → `POST …/responses?baseLanguage=`: đúng = thanh xanh + lời khen; sai = thanh đỏ + **Đáp án đúng** + **Giải thích** + lưu ý ngữ pháp (từ server), tô xanh đáp án đúng/đỏ đáp án đã chọn. Câu cuối → `PUT …/complete` → màn hình kết quả (điểm %, số câu đúng, XP, tiến độ khóa; nút Về danh sách bài học, Ôn tập Flashcards, Làm lại bài này).
  - Thoát (X): hỏi xác nhận nếu chưa xong; lượt làm bài dở bị bỏ, không tính tiến độ.
- `assets/js/i18n.js`: ~55 khóa mới `vi` + `en` cho cả luồng (nhãn, lời khen, lỗi).
- `assets/css/style.css`: style cho thanh trên cùng, thẻ câu hỏi, đáp án, ô nhập, thanh phản hồi (`bg-success-subtle`/`bg-danger-subtle`), màn hình hoàn thành; tôn trọng `prefers-reduced-motion`.

### 4.2. Kiểm thử thực tế
Stack Docker thật + `start-web.ps1` + Edge headless, đăng nhập thật, dữ liệu từ `seed-sample-courses.ps1`: **41/41 PASS**:
- Chưa đăng nhập vào `lesson.html?...` → login với `returnUrl` đúng → đăng nhập xong vào thẳng bài; mở bài chỉ gọi `quiz` rồi `startAttempt`.
- Bài `ENG-A1-COMM` #1 (7 câu): "Câu 1/7", "Tối đa 15 XP"; câu gõ không lộ đáp án; ô nhập autofocus; Kiểm tra bị khóa khi ô trống; trả lời sai → thanh đỏ có "Đáp án đúng: Hello" và giải thích; Enter trên nút Tiếp tục chỉ sang đúng 1 câu; **Enter hai lần liên tiếp chỉ gửi 1 câu trả lời**; trắc nghiệm phím 2 rồi 1 đổi lựa chọn, đáp án đúng xanh và bị khóa; chọn sai → đáp án sai đỏ, đáp án đúng xanh; nút cuối là "Hoàn thành".
- Kết quả 5/7: màn hình hiển thị **71 %, 5/7, 11 XP**; DB khớp (`score 71, xp 11`), `PUT /complete` gọi 1 lần.
- Quay lại danh sách: modal mở lại đúng khóa (tab English) với "Đã hoàn thành 1/2 bài học"; dashboard "1/5". Mỗi bài có nút Học ngay.
- Làm lại bài: 100 % nhưng **0 XP** kèm giải thích (server chỉ tính XP lần đầu). Thoát: hộp xác nhận, "Ở lại" giữ nguyên, "Thoát" về khóa học, lượt dở còn `completed_at` rỗng trong DB.
- Lỗi: `lessonId` sai định dạng và bài không tồn tại (404) đều có thông báo + nút quay lại.
- Bài tiếng Trung `ZH-HSK1-COMM` #1: gõ chữ Hán đúng cả 7 câu → 100 %, 15 XP; trắc nghiệm hiện chữ Hán + pinyin; link quay lại dùng tab `lang=zh`.
- Giao diện English: "Question 1/7", "Up to 25 XP", "Check", "Not quite / Correct answer / Explanation", title đổi. Mobile 390 px: không cuộn ngang (kể cả khi thẻ trượt vào), ô nhập nằm trên thanh đáy. Không lỗi JS.
- LinguaBridge `dotnet test`: 544 + 146 PASS (không đổi backend).
- Lỗi tự tìm ra khi kiểm thử: (1) `const isChoice` dùng trước khi khai báo làm trang trắng → đổi thành hàm; (2) hiệu ứng thẻ trượt từ phải làm trang rộng thêm 12 px và trình duyệt di động giữ nguyên độ rộng đó → đổi sang trượt dọc.

**Lưu ý về môi trường**: lúc tôi bật stack đã có sẵn một stack đang chạy từ phiên khác (container "Up 18 minutes", có 4 lượt ôn thẻ của phiên đó), nên tôi **không** `-RemoveData`. Trước khi kiểm thử tôi sao lưu dữ liệu học viên (`srs.*`, `learning.*`), kiểm thử xong khôi phục nguyên trạng (25 thẻ / 4 review log / 0 lượt làm bài, như lúc đầu) và không tắt stack.

### 4.3. Quyết định và hạn chế
1. **Trang bài học không có công tắc ngôn ngữ**: đổi ngôn ngữ tải lại trang, tức là bỏ lượt làm bài đang dở; chọn ngôn ngữ trước khi vào bài (theo lựa chọn chung `lb.baseLanguage`).
2. **Mỗi lần mở trang tạo một lượt làm bài mới** (hành vi của API: "each start is a fresh attempt"); tải lại giữa chừng bắt đầu lại từ câu 1, lượt cũ bị bỏ.
3. **XP chỉ lần hoàn thành đầu** (server): màn hình kết quả nói rõ khi làm lại. Điểm tính theo lần trả lời đầu mỗi câu.
4. **Câu `Pronounce`** (có trong enum, chưa có trong dữ liệu mẫu) được hiển thị như câu gõ lại; chưa có ghi âm/phát âm. `audioPromptUrl` chưa được phát (không có âm thanh trong dữ liệu).
5. **Gõ tiếng Trung bằng IME**: `Enter` khi đang chọn chữ (`isComposing`) không bị tính là "Kiểm tra"; chưa kiểm thử với IME thật (puppeteer gõ ký tự trực tiếp).
6. Chấm câu gõ chữ do server (`AnswerMatcher`); giao diện không chuẩn hóa chuỗi.

---

## 5. Kết Quả Code Review (Dành cho Claude Code Reviewer)

*(Reviewer điền vào phần này theo `docs/roles/CODE_REVIEWER.md`)*

- **Reviewer**: Claude Code (phiên Reviewer độc lập) — **Ngày**: 2026-11-04
- **Trạng thái Review**: `REVIEW_PASSED` (không Blocker/Major; 2 Minor)

### 5.1. Kiểm chứng độc lập
- **Whitelist**: `git status` chỉ có các file trong whitelist; không đổi backend (kết quả `dotnet test` của Reviewer ở TASK-024 vẫn áp dụng).
- **Code**: `lessonId`/`courseId` từ URL được kiểm GUID trước khi ghép vào đường dẫn API; mọi chuỗi từ API vào template đều qua `esc()`; cờ `busy`/`phase` chặn gửi trùng; Enter trên nút không bị xử lý hai lần; `isComposing` cho IME. Backend (`LearnerQuestionDto`) không lộ đáp án: option không có cờ đúng/sai, câu gõ chữ có `targetText` = null — player không dựa vào dữ liệu ẩn nào.
- **Chạy thật** (stack đang chạy sẵn của phiên khác — Reviewer không gỡ; dùng **học viên riêng** `reviewer026@appinhouse.local` tạo qua Admin API để không đụng dữ liệu của admin) + `start-web.ps1` + script E2E tự viết (Edge headless): **28/29**.
  - Chưa đăng nhập → `login.html?returnUrl=/lesson.html?lessonId=…&courseId=…`, không gọi API; đăng nhập xong về đúng bài; mở bài gọi `GET …/quiz` rồi `POST /attempts` (+ `GET course` cho link quay lại).
  - "Câu 1/7", "Tối đa 15 XP"; câu gõ chỉ hiện nghĩa ("Xin chào"), không lộ "Hello"; ô nhập autofocus, Kiểm tra khoá khi trống; **Enter hai lần chỉ gửi 1 câu trả lời**, không nhảy câu; sai → thanh đỏ, "Đáp án đúng: Hello", "Giải thích: …".
  - Thanh tiến độ 57 % ở câu 5; trắc nghiệm phím 2 rồi 1 đổi lựa chọn, đúng → xanh + khoá; sai → đỏ/xanh; nút cuối "Hoàn thành".
  - Kết quả **71 % / 5/7 / 11 XP** khớp DB (`exercise_attempts`: 71 | 11), `PUT /complete` gọi đúng 1 lần; "Về danh sách bài học" → modal mở lại "Đã hoàn thành 1/2", mỗi bài có nút Học ngay.
  - Làm lại: 100 %, 7/7, **0 XP** kèm giải thích. Thoát: hộp xác nhận → về `courses.html?course=…&lang=en`.
  - `lessonId=nope` → thông báo; GUID không tồn tại → "Không tìm thấy bài học…" và **không tạo lượt làm bài** (đếm DB).
  - English: "Question 1/7", "Up to 15 XP", "Not quite / Correct answer / Explanation", không còn chữ tiếng Việt. 390 px: không cuộn ngang, ô nhập nằm trên thanh đáy. 0 lỗi JS.

### 5.2. Các phát hiện (Findings)

**🔵 Minor #1 — Mất phản hồi của `POST /responses` làm học viên kẹt ở câu hỏi** (`app.js` `checkAnswer`)
- Tái hiện: chặn phản hồi đầu tiên sau khi server **đã** ghi nhận câu trả lời (mô phỏng mạng chập chờn/timeout) → "Không kết nối được máy chủ…"; bấm Kiểm tra lại → "Câu hỏi này đã được trả lời trong lượt làm bài hiện tại." — nút vẫn là "Kiểm tra", vẫn "Câu 1/7"; bấm mãi vẫn vậy. Lối ra duy nhất là thoát và làm lại từ đầu (lượt cũ bỏ dở).
- Không chặn vì: cần đúng tình huống mất phản hồi sau khi server xử lý; không hỏng dữ liệu; bài ngắn. Sửa gợi ý: khi nhận `response.already_answered` thì coi câu đã trả lời — chuyển sang trạng thái "Tiếp tục" (kèm thông báo, không cần biết đúng/sai), hoặc lấy lại kết quả nếu API có.

**🔵 Minor #2 — Đáp án đúng luôn là phương án 1 và player không xáo trộn**
- DB: cả **10/10** câu trắc nghiệm của dữ liệu mẫu (TASK-025) có đáp án đúng ở `order_index = 1`; player hiển thị theo `orderIndex` → học viên sớm nhận ra "bấm 1 là đúng".
- Sửa gợi ý: xáo thứ tự hiển thị phía client (id đáp án giữ nguyên, phím 1–9 theo thứ tự đã xáo) và/hoặc sửa dữ liệu mẫu (file seed ngoài whitelist task này).

### 5.3. Ghi nhận / chuyển Tech Lead
1. Mỗi lần mở/tải lại trang tạo một lượt làm bài mới; lượt bỏ dở nằm lại trong `learning.exercise_attempts` với `completed_at` rỗng (4.3.2) — cân nhắc dọn dẹp/giới hạn ở backend.
2. Chưa có phát âm/âm thanh (`Pronounce`, `audioPromptUrl`) và chưa thử IME tiếng Trung thật (4.3.4–5).
3. Môi trường: stack `appinhouse` lúc review đang chạy sẵn từ phiên khác; Reviewer **không** gỡ. Dữ liệu Reviewer để lại trên stack đó: user `reviewer026@appinhouse.local` (Identity) và các lượt làm bài/tiến độ của user này (LinguaBridge) — mất khi stack được `docker-all-down.ps1 -RemoveData`.

- **Kết luận**: Tất cả tiêu chí nghiệm thu đạt và đã được kiểm chứng độc lập; hai Minor không chặn → `REVIEW_PASSED`.

---

## 6. Kết Luận Nghiệm Thu (Gemini Tech Lead)

- **Đánh giá tổng quan**: Coder và Reviewer đã thực hiện xuất sắc task. Luồng làm bài học tương tác `lesson.html` hoạt động trơn tru, chuẩn xác từ khâu mở attempt, trả lời câu hỏi đa dạng (trắc nghiệm, điền từ, dịch câu), phản hồi tức thì giải thích ngữ pháp, cho đến hoàn tất và tích lũy XP vào tiến độ khóa học.
- **Xử lý các kiến nghị từ Reviewer**:
  1. *Minor #1 (Xử lý khi mạng chập chờn gặp `response.already_answered`)*: Ghi nhận đưa vào task tinh chỉnh trải nghiệm mạng yếu.
  2. *Minor #2 (Xáo trộn vị trí đáp án trắc nghiệm)*: Sẽ bổ sung cơ chế shuffle ngẫu nhiên các option phía client trong task hoàn thiện content/seeder tiếp theo.
  3. *Lượt làm bài dở dang*: Sẽ bổ sung background job định kỳ dọn dẹp các attempt dở dang quá hạn.
- **Quyết định**: **`APPROVED`**. Cho phép tiến hành commit mã nguồn.


