# TASK-027: AI Tutor Web Chat & Roleplay Interface

- **Mã Task**: TASK-027
- **Tên Task**: Xây Dựng Giao Diện Hội Thoại AI Tutor & Thực Hành Tình Huống (`tutor.html`) Trên Web Client
- **Người Giao (Tech Lead)**: Gemini
- **Người Thực Hiện (Implementer)**: Claude Code (Coder)
- **Người Đánh Giá (Reviewer)**: Claude Code (Reviewer)
- **Trạng Thái**: `APPROVED`
- **Ngày Tạo**: 2026-11-04
- **Tài Liệu Tham Chiếu**: [TASK-015](TASK-015-module-ai-tutor-v1.md), [TASK-022](TASK-022-init-linguabridge-web-bootstrap.md), [TASK-025](TASK-025-web-i18n-and-content-seeder.md), [TASK-026](TASK-026-interactive-lesson-player.md)

---

## 1. Mục Tiêu Kỹ Thuật

Backend `LinguaBridge.Api` đã có phân hệ AI Tutor hoàn chỉnh (`/api/v1/ai/sessions`, `/messages`, `/quota`) hỗ trợ chấm điểm sửa lỗi ngữ pháp, gợi ý giao tiếp và kiểm soát hạn mức quota hàng ngày. Task này xây dựng giao diện đàm thoại thực tế trên Web Client để người học có thể luyện nói/chat với gia sư AI theo từng kịch bản chuyên biệt:

1. **Thanh điều hướng (Navbar)**:
   - Bổ sung mục **"AI Tutor"** vào thanh menu trên toàn bộ các trang (`index.html`, `courses.html`, `flashcards.html`, `tutor.html`).
2. **Màn hình chọn kịch bản & Quota (`tutor.html` - Trạng thái chưa vào phòng chat)**:
   - Hiển thị thông tin hạn mức Quota hôm nay (`GET /api/v1/ai/quota`): số lượt chat còn lại, gói tài khoản (`Free` / `Premium`), thời gian reset quota.
   - Thẻ chọn ngôn ngữ thực hành: **Tiếng Anh (`en`)** hoặc **Tiếng Trung (`zh`)**.
   - Thẻ chọn tình huống giao tiếp (3 Scenarios chính thức từ backend):
     - 💼 **Phỏng vấn tuyển dụng (`JobInterview`)**: Luyện trả lời phỏng vấn chuyên nghiệp, trả lời câu hỏi tình huống công sở.
     - 📧 **Email & Trao đổi thương mại (`BusinessEmail`)**: Soạn thảo, đàm phán và trao đổi nghiệp vụ văn phòng.
     - ☕ **Hội thoại tự do (`FreeTalk`)**: Trò chuyện hàng ngày, kết hợp sửa lỗi phát âm và từ vựng.
   - Nút **"Bắt đầu hội thoại"**: Gọi `POST /api/v1/ai/sessions` với `{ targetLanguage, scenarioCode, title }`.
3. **Màn hình Chat tương tác (`tutor.html` - Trạng thái trong phòng chat)**:
   - Header phiên chat: Tên kịch bản, ngôn ngữ, nút **"Kết thúc phiên" (End Session)** gọi `DELETE /api/v1/ai/sessions/{id}` để quay lại chọn kịch bản mới.
   - Khung tin nhắn cuộn tự động (Chat History):
     - Lời chào khởi đầu thân thiện từ AI Tutor phù hợp với kịch bản đã chọn.
     - Bong bóng tin nhắn học viên (bên phải, màu chủ đạo).
     - Bong bóng tin nhắn AI Tutor (bên trái, nền sáng):
       - Nội dung câu trả lời (`replyText`).
       - Phiên âm Pinyin (đối với tiếng Trung `zh`) hoặc IPA (đối với tiếng Anh `en`) nếu có.
       - Bản dịch nghĩa sang ngôn ngữ mẹ đẻ (`baseLanguage` - Việt hoặc Anh).
       - Khung **Gợi ý & Sửa lỗi ngữ pháp (`grammarTips`)**: Hiển thị nổi bật nếu AI phát hiện lỗi diễn đạt hoặc gợi ý cách dùng từ tự nhiên hơn của người bản xứ.
   - Khung nhập tin nhắn (Input Bar):
     - Ô nhập nội dung hỗ trợ gõ nhiều dòng hoặc nhấn `Enter` để gửi, `Shift+Enter` xuống dòng.
     - Nút "Gửi" có trạng thái loading spinner ("AI đang soạn câu trả lời...").
     - Chống gửi tin nhắn rỗng hoặc gửi trùng khi AI đang xử lý.
4. **Xử lý các tình huống biên & phản hồi lỗi**:
   - Nếu tài khoản hết lượt (HTTP 429 `ai.quota_exhausted`): Hiển thị thông báo thân thiện nhắc nhở hạn mức và thời gian đếm ngược reset.
   - Nếu server chưa cấu hình OpenAI API Key (HTTP 503 `ai.provider_unavailable`): Hiển thị alert rõ ràng, lịch sự hướng dẫn cấu hình `OpenAI:ApiKey` trong môi trường thay vì báo lỗi hệ thống chung chung.
   - Hỗ trợ đầy đủ song ngữ giao diện (`vi` / `en`) qua `assets/js/i18n.js`.

---

## 2. Phạm Vi File Cấp Phép (File Scope & Whitelist)

> **QUY TẮC BẮT BUỘC DÀNH CHO CLAUDE CODE (CODER)**:
> Chỉ được phép tạo mới hoặc chỉnh sửa các file sau. Không can thiệp vào các file ngoài danh sách này.

- [ ] `repos/linguabridge-web/tutor.html` *(Tạo mới)*
- [ ] `repos/linguabridge-web/assets/js/api.js` *(Bổ sung các phương thức gọi API AI Tutor)*
- [ ] `repos/linguabridge-web/assets/js/app.js` *(Thêm router `initTutor` và cập nhật link navbar)*
- [ ] `repos/linguabridge-web/assets/js/i18n.js` *(Bổ sung từ điển `vi` và `en` cho AI Tutor)*
- [ ] `repos/linguabridge-web/assets/css/style.css` *(Thêm styles cho giao diện chat, grammar tips, quota card)*
- [ ] `repos/linguabridge-web/index.html` *(Cập nhật menu navbar)*
- [ ] `repos/linguabridge-web/courses.html` *(Cập nhật menu navbar)*
- [ ] `repos/linguabridge-web/flashcards.html` *(Cập nhật menu navbar)*
- [ ] `docs/handoffs/TASK-027-ai-tutor-web-chat.md` *(Điền báo cáo triển khai)*

---

## 3. Tiêu Chí Nghiệm Thu (Acceptance Criteria)

1. **Điều hướng & Xác thực**:
   - Menu Navbar trên tất cả các trang có mục **"AI Tutor"** (icon `bi-robot` hoặc `bi-chat-dots`).
   - Truy cập `/tutor.html` khi chưa đăng nhập: Hiển thị prompt đăng nhập kèm `returnUrl` bảo toàn.
2. **Khởi tạo kịch bản & Thông tin Quota**:
   - Đăng nhập thành công vào `/tutor.html`: Hiển thị thanh thông tin Quota hôm nay (số tin nhắn đã dùng / giới hạn).
   - Cho phép chọn ngôn ngữ mục tiêu (`en` / `zh`) và kịch bản (`JobInterview`, `BusinessEmail`, `FreeTalk`).
   - Bấm bắt đầu: Gọi `POST /api/v1/ai/sessions`, khởi tạo thành công và chuyển sang giao diện chat.
3. **Trải nghiệm đàm thoại 2 chiều**:
   - Gửi tin nhắn: Gọi `POST /api/v1/ai/sessions/{id}/messages?baseLanguage={vi|en}`.
   - Tin nhắn hiển thị mượt mà; tin nhắn AI có đầy đủ các thành phần phân tích: text câu trả lời, phiên âm Pinyin/IPA, dịch nghĩa và khung góp ý ngữ pháp (`grammarTips`).
   - Nút "Kết thúc phiên" gọi `DELETE /api/v1/ai/sessions/{id}` và đưa học viên về màn hình chọn kịch bản.
4. **Bền vững & Xử lý lỗi**:
   - Khi gặp 429: Thông báo hết quota kèm thời gian reset.
   - Khi gặp 503: Thông báo dịch vụ AI tạm thời chưa khả dụng (hoặc chưa cấu hình API key).
   - Hỗ trợ đổi ngôn ngữ giao diện `vi` / `en` tức thì.
   - Giao diện chat responsive, hiển thị tốt trên cả màn hình di động (390px) và máy tính.

---

## 4. Báo Cáo Triển Khai (Dành cho Claude Code Coder)

*(Claude Code điền vào phần này sau khi hoàn thành)*

### 4.1. Các thay đổi thực tế
Chỉ trong whitelist; không đụng backend. Không commit.
- `tutor.html` (**mới**): một trang, ba vùng: lời nhắc đăng nhập, màn hình chọn kịch bản (thẻ Quota + chọn ngôn ngữ + chọn tình huống + nút Bắt đầu hội thoại) và phòng chat (header có nút Kết thúc phiên, khung tin nhắn, ô nhập).
- `assets/js/api.js`: `tutorQuota`, `createTutorSession`, `tutorMessages`, `sendTutorMessage`, `endTutorSession` (đúng các endpoint `/api/v1/ai/*` trong `AiTutorEndpoints.cs`).
- `assets/js/app.js`: mục **Gia sư AI** (`bi-robot`) trong navbar và `initTutor`:
  - Chưa đăng nhập: dùng lại `showSignInPrompt` → `login.html?returnUrl=/tutor.html`.
  - Thẻ Quota từ `GET /quota`: gói Free tính theo lượt chat, gói Paid tính theo token (API trả giới hạn còn lại là `null`), thanh tiến độ (vàng khi ≥80 %, đỏ khi hết), đếm ngược thời gian reset cập nhật mỗi 30 giây. Hết hạn mức: cảnh báo kèm thời gian reset và khóa nút Bắt đầu. Không tải được quota thì chỉ cảnh báo, không chặn (API vẫn là nơi áp quota).
  - Chọn `en`/`zh` × `JobInterview`/`BusinessEmail`/`FreeTalk` (mặc định English + Phỏng vấn); `POST /sessions` với `{targetLanguage, scenarioCode, title}` (title theo ngôn ngữ giao diện, ví dụ "Email & trao đổi thương mại · Tiếng Trung").
  - Phòng chat: lời chào theo kịch bản và ngôn ngữ luyện (hiển thị phía client, có pinyin cho tiếng Trung, bản dịch theo ngôn ngữ giao diện); bong bóng học viên bên phải, AI bên trái gồm `replyText`, phiên âm Pinyin/IPA, bản dịch và khung vàng **Gợi ý & sửa lỗi** (`grammarTips`). Mọi chuỗi từ API đều qua `esc()`, giữ xuống dòng bằng CSS `pre-wrap`.
  - Ô nhập: `Enter` gửi, `Shift+Enter` xuống dòng, `Enter` đang chọn chữ trong IME (`isComposing`/keyCode 229) không gửi; tự co giãn đến 8rem; `maxlength=1000` (mặc định `AiTutor:MaxMessageLength`), bộ đếm hiện từ 800 ký tự. Chặn gửi rỗng và gửi trùng khi AI đang soạn (cờ `busy` + nút Gửi hiện spinner + bong bóng "AI đang soạn câu trả lời...").
  - Lỗi khi gửi: tin nhắn của học viên được gỡ khỏi khung và trả lại vào ô nhập để gửi lại. **429** `ai.quota_exhausted`: thông báo kèm đếm ngược từ `resetsAt`, khóa ô nhập đến lúc reset; **503** `ai.provider_unavailable`: cảnh báo hướng dẫn kiểm tra cấu hình `OpenAI:ApiKey` (biến môi trường `OpenAI__ApiKey`) rồi khởi động lại API; **409** `session.ended` / **404**: thông báo + nút "Phiên mới"; **400**: tin quá dài; lỗi khác: thông báo chung.
  - **Kết thúc phiên**: `DELETE /sessions/{id}` (404 coi như đã kết thúc) → về màn hình chọn kịch bản và tải lại quota.
  - **Khôi phục phòng chat sau khi tải lại trang**: id phiên lưu trong `sessionStorage` (`lb.tutor.session`); nếu có thì mở lại phòng và nạp lịch sử qua `GET /sessions/{id}/messages` (phân trang 200). Nhờ vậy đổi ngôn ngữ giao diện (tải lại trang) không làm mất cuộc hội thoại.
- `assets/js/i18n.js`: ~55 khóa `vi` + `en` (đủ cặp, kiểm tra bằng grep).
- `assets/css/style.css`: card chọn, thẻ Quota, khung chat, bong bóng, khung gợi ý, chấm "đang gõ", thông báo; responsive (≤576 px), `prefers-reduced-motion`.
- `index.html`, `courses.html`, `flashcards.html`: **không cần sửa**. Navbar của mọi trang được `renderNavbar()` trong `app.js` dựng bằng script (các file HTML chỉ có `<div id="navbar">`), nên mục mới tự xuất hiện trên tất cả các trang.

### 4.2. Kiểm thử thực tế
Stack Docker đang chạy sẵn (dùng chung) + `start-web.ps1` + Edge headless, đăng nhập thật qua STS. **49/49 PASS** (`tutor.mjs`):
- Chưa đăng nhập: có lời nhắc đăng nhập, `returnUrl=/tutor.html` được giữ và quay lại đúng trang sau khi đăng nhập. Mục "Gia sư AI" có mặt (đang active ở tutor) trên home, courses, flashcards.
- Quota thật: "Gói miễn phí · Đã dùng 0/10 lượt chat · Còn 10 lượt · Làm mới sau ~12 giờ". Quota giả lập: hết lượt (thẻ đỏ, cảnh báo + đếm ngược, nút Bắt đầu bị khóa) và gói Paid theo token (85.000/100.000, trạng thái "sắp hết", chỉ hiện phút).
- Bắt đầu zh + BusinessEmail: đúng một `POST /sessions`; DB có phiên `zh | BusinessEmail | "Email & trao đổi thương mại · Tiếng Trung"`, chưa kết thúc; lời chào tiếng Trung + pinyin + bản dịch tiếng Việt.
- **503 thật** (API chưa cấu hình khóa AI): thông báo nhắc `OpenAI:ApiKey`, tin nhắn được trả lại ô nhập, `ai_usage_ledger` không để lại dòng nào.
- Trả lời giả lập (chặn request ở trình duyệt vì không có khóa AI thật): bong bóng đủ `replyText`, IPA, dịch nghĩa, khung gợi ý; yêu cầu gửi `baseLanguage=vi` (`en` khi đổi giao diện); gõ rỗng không gửi; `Shift+Enter` xuống dòng; **Enter hai lần chỉ gửi một request**; spinner + chỉ báo "đang soạn" khi chờ; HTML trong tin nhắn/phản hồi chỉ hiển thị dạng văn bản (không chèn phần tử, không chạy script).
- **429** (giả lập): "…sau 3 giờ 2 phút", ô nhập bị khóa, nội dung vẫn giữ. **409**: nút "Phiên mới" quay về màn hình chọn và xóa phiên đã lưu.
- Tải lại trang: mở lại đúng phòng, gọi `GET /sessions/{id}/messages`, thứ tự lời chào → lịch sử đúng; đổi sang English: nhãn, tiêu đề, navbar, thông báo 503 đều tiếng Anh.
- Kết thúc phiên: gọi `DELETE`, DB `ended_at` có giá trị, về màn hình chọn, quota được tải lại.
- Mobile 390 px: không cuộn ngang ở cả hai màn hình, thanh nhập nằm trong khung nhìn, khung chat tự cuộn và nằm ở tin mới nhất. Không có lỗi JS. Ảnh chụp đã xem lại.
- Dọn dữ liệu: bộ test chỉ xóa các phiên do chính nó tạo (so với danh sách id trước khi chạy); DB cuối cùng 0 phiên / 0 tin nhắn / 0 ledger như lúc đầu. Tắt web server của tôi, không đụng stack Docker.
- Không đổi backend nên không chạy lại `dotnet test`.
- Lỗi tự tìm ra khi kiểm thử: không có lỗi ứng dụng; lỗi duy nhất là của bộ test (bong bóng "đang soạn" cũng mang class `msg-ai` nên đếm sai), đã thêm class `msg-typing` để phân biệt.

### 4.3. Rủi ro, hạn chế và điểm cần Tech Lead lưu ý
1. **Chưa thử với nhà cung cấp AI thật.** Môi trường không có khóa nên mọi phản hồi thành công đều là giả lập đúng hợp đồng DTO `TutorReplyDto`; luồng 503 thì chạy thật trên API.
2. **Lời chào do client dựng** (API không có endpoint chào) nên không nằm trong lịch sử phiên; câu chào này không được LLM biết tới. Với kịch bản phỏng vấn, người học trả lời "câu hỏi tự giới thiệu" mà LLM chưa hỏi; có thể cần backend lưu lời chào làm tin nhắn đầu tiên.
3. Không có `GET /sessions/{id}` nên phòng chat khôi phục dựa vào `sessionStorage` (mất khi đóng tab); phiên bỏ dở không có cách liệt kê lại ở phía client.
4. Quota gói Paid hiện token, gói Free hiện lượt; tên gói lấy từ `plan` của API (`Free`/`Paid`), không phải "Premium" như mô tả task.
5. Phòng chat chưa có công tắc ngôn ngữ riêng; đổi ngôn ngữ ở navbar tải lại trang nhưng phiên vẫn được khôi phục. Bản dịch/gợi ý của các tin cũ vẫn theo ngôn ngữ lúc gửi.
6. Giới hạn 1000 ký tự được ghi cứng ở client theo mặc định của API; nếu cấu hình `AiTutor:MaxMessageLength` khác, API vẫn là nơi quyết định (400 hiện "tin quá dài").
7. Chưa kiểm thử với IME tiếng Trung thật (puppeteer gõ ký tự trực tiếp).

---

## 5. Kết Quả Code Review (Dành cho Claude Code Reviewer)

*(Reviewer điền vào phần này theo `docs/roles/CODE_REVIEWER.md`)*

- **Reviewer**: Claude Code (phiên Reviewer độc lập) — **Ngày**: 2026-11-05
- **Trạng thái Review**: `REVIEW_PASSED` (không có finding Blocker/Major/Minor)

### 5.1. Kiểm chứng độc lập
- **Whitelist**: `git status` chỉ có `tutor.html`, `api.js`, `app.js`, `i18n.js`, `style.css` và file task; không đổi backend. `index/courses/flashcards.html` không cần sửa (navbar dựng bằng `renderNavbar()`), đã xác nhận mục "AI Tutor" xuất hiện qua script.
- **Hợp đồng API** (đối chiếu code backend, vì phản hồi thành công của Coder là giả lập trong trình duyệt): `CreateSessionRequest {targetLanguage, scenarioCode, title}`, `SendMessageRequest {text}`, `TutorReplyDto` (`replyText`, `pinyinOrIpa`, `translation`, `grammarTips`), `ChatMessageDto` (`sender` = `User`/`Assistant`, `content`, …), `MessagesPageDto.items`, `AiQuotaDto`, 429 extension `resetsAt` — client dùng đúng tất cả. Phiên lưu trong `sessionStorage` được kiểm GUID + ngôn ngữ + kịch bản hợp lệ trước khi dùng; mọi chuỗi từ API qua `esc()`.
- **Chạy thật end-to-end qua backend** (không giả lập ở trình duyệt): server OpenAI giả (`OpenAI:BaseUrl` → `127.0.0.1:5998`) + `LinguaBridge.Api` native ở cổng 5021 (`OpenAI:ApiKey` giả, `AiTutor:FreeDailyRequests=2`) + `start-web.ps1 -Port 5173 -ApiUrl http://localhost:5021`, user riêng `reviewer026@appinhouse.local`; stack Docker dùng chung không bị đụng. Kết quả **22/22** (1 lần chạy đầu sai selector của Reviewer, chạy lại riêng phần đó):
  - Navbar có "AI Tutor" (`bi-robot`, active); chưa đăng nhập → `login.html?returnUrl=/tutor.html` → quay lại đúng trang.
  - Quota thật: "Đã dùng 0/2 lượt chat · Còn 2 lượt · Làm mới sau 11 giờ 48 phút".
  - Chọn `zh` + `BusinessEmail` → 1 `POST /sessions`, DB `zh|BusinessEmail|Email & trao đổi thương mại · Tiếng Trung`, lời chào chữ Hán + pinyin + bản dịch.
  - Gửi tin: Enter hai lần → **đúng 1** `POST …/messages?baseLanguage=vi`; spinner + "đang soạn" khi chờ; LLM giả nhận đúng lịch sử (turns 2 → 4).
  - Phản hồi thật từ API: `replyText` + pinyin + bản dịch + khung **Gợi ý & sửa lỗi** (`grammar_tips` dạng mảng được API nối dòng); giữ xuống dòng.
  - **XSS**: model trả `<img src=x onerror=…>`, `<script>…</script>`, `<b>Tip 2</b>`; tin học viên `<b>bold</b>` → tất cả hiển thị dạng văn bản, 0 phần tử được chèn, không thực thi.
  - DB: 2 tin nhắn + 1 dòng ledger. `Shift+Enter` xuống dòng, tin trắng không gửi.
  - **429 thật** (tin thứ 3 vượt hạn mức 2): "Bạn đã dùng hết hạn mức hôm nay… sau 11 giờ 48 phút", ô nhập khoá, nội dung "Message three" được giữ, bong bóng tạm bị gỡ.
  - Tải lại: phòng chat khôi phục, thứ tự lời chào → U A U A từ `GET …/messages`, tin AI khôi phục vẫn có gợi ý/bản dịch.
  - English: navbar/nút/tiêu đề tiếng Anh. Kết thúc phiên: `DELETE`, DB `ended_at` có giá trị, `sessionStorage` xoá, về màn hình chọn; quota 2/2 → cảnh báo + nút Bắt đầu bị khoá.
  - 390 px không cuộn ngang; 0 lỗi JS.
- **503**: không chạy lại (Coder đã chạy thật trên API container không có khoá); đã đọc nhánh xử lý — thông báo riêng, song ngữ, hướng dẫn `OpenAI__ApiKey`.
- **Dọn**: tắt LLM giả, API native và web 5173; xoá các phiên/tin nhắn/ledger AI của user review khỏi DB dùng chung (stack không bị gỡ).

### 5.2. Các phát hiện (Findings)
Không có. Các hạn chế Coder nêu ở 4.3 là quyết định thiết kế/giới hạn backend, chuyển Tech Lead:
1. Lời chào do client dựng, LLM không biết (4.3.2) — nên để backend lưu lời chào làm tin đầu tiên, nhất là kịch bản phỏng vấn.
2. Không liệt kê/khôi phục phiên khi đóng tab (4.3.3); tên gói `Free`/`Paid` theo API thay vì "Premium" (4.3.4).
3. Chưa thử với nhà cung cấp AI thật và IME tiếng Trung thật (4.3.1, 4.3.7) — Reviewer đã thay bằng LLM giả phía server (đúng định dạng JSON của `AiTutorPromptBuilder`), nên hợp đồng client ↔ API đã được kiểm chứng thật.

- **Kết luận**: Tất cả tiêu chí nghiệm thu đạt, đã kiểm chứng end-to-end qua backend thật → `REVIEW_PASSED`.

---

## 6. Kết Luận Nghiệm Thu (Gemini Tech Lead)

- **Đánh giá tổng quan**: Coder và Reviewer đã hoàn thành xuất sắc TASK-027. Giao diện AI Tutor (`tutor.html`) đạt chuẩn thiết kế, trực quan, hỗ trợ chọn 3 kịch bản thực chiến (JobInterview, BusinessEmail, FreeTalk), hiển thị hạn mức Quota rõ ràng, đàm thoại mượt mà với đầy đủ phân tích Pinyin/IPA, dịch nghĩa và khung góp ý ngữ pháp (`grammarTips`). Xử lý lỗi 429 và 503 rất chu đáo, thân thiện với người dùng.
- **Xử lý các kiến nghị kiến trúc từ Reviewer**:
  1. *Lưu lời chào ban đầu vào cơ sở dữ liệu*: Ghi nhận tối ưu trong backend task tiếp theo để mô hình LLM nhận biết đầy đủ ngữ cảnh câu chào khi người học phản hồi lượt đầu tiên.
  2. *Quản lý lịch sử các phiên cũ*: Sẽ bổ sung endpoint `GET /api/v1/ai/sessions` để người dùng có thể xem lại danh sách các cuộc trò chuyện trước đây trên trang cá nhân.
- **Quyết định**: **`APPROVED`**. Tiến hành stage và commit mã nguồn vào repository.


