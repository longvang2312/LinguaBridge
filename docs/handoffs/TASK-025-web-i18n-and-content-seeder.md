# TASK-025: Web Client Internationalization (i18n) & Sample Content Seeding

- **Mã Task**: TASK-025
- **Tên Task**: Triển Khai Đa Ngôn Ngữ (i18n Tiếng Anh & Tiếng Việt) Cho Web Client & Nạp Dữ Liệu Khóa Học Mẫu (Content Seeder)
- **Người Giao (Tech Lead)**: Gemini
- **Người Thực Hiện (Implementer)**: Claude Code
- **Trạng Thái**: `APPROVED`
- **Ngày Tạo**: 2026-11-04
- **Tài Liệu Tham Chiếu**: [TASK-022](TASK-022-init-linguabridge-web-bootstrap.md), [TASK-023](TASK-023-login-page-and-return-url.md), [TASK-024](TASK-024-oidc-single-sign-out.md)

---

## 1. Mục Tiêu Kỹ Thuật

Giải quyết trọn vẹn 2 vấn đề về trải nghiệm ngôn ngữ và dữ liệu học tập:
1. **Hệ Thống Đa Ngôn Ngữ Giao Diện (i18n) Cho Web Client**:
   - Xây dựng module `repos/linguabridge-web/assets/js/i18n.js` chứa từ điển chuẩn hóa cho cả 2 ngôn ngữ:
     - `vi`: Tiếng Việt (Mặc định cho người Việt)
     - `en`: English (Giao diện Tiếng Anh chuẩn mực)
   - Bản dịch phủ kín tất cả các thành phần giao diện trên toàn bộ các trang (`index.html`, `courses.html`, `flashcards.html`, `login.html`):
     - **Navbar**: Trang chủ / Home, Khóa học / Courses, Ôn tập / Review, Đăng nhập / Sign In, Đăng xuất / Sign Out, Ngôn ngữ / Language.
     - **Trang chủ**: Hero title, Hero description, nút CTA, tiêu đề Dashboard, các thống kê (thẻ cần ôn, tiến độ bài học).
     - **Trang Khóa học**: Khám phá khóa học / Explore Courses, tabs Tiếng Anh / English, Tiếng Trung / Chinese, nút Xem bài học / View Lessons, Modal bài học, tiến độ hoàn thành, XP.
     - **Trang Ôn tập Thẻ (Flashcards)**: Tiêu đề, hướng dẫn lật thẻ (Click / Phím Space), các mức đánh giá SM-2 (Again / Hard / Good / Easy), thông báo hoàn thành ôn tập.
     - **Trang Đăng nhập**: Tiêu đề, mô tả, nút "Sign In with AppInHouse ID", thông báo đăng xuất thành công.
   - Khi người dùng chuyển đổi ngôn ngữ ở dropdown Navbar:
     - Lưu lựa chọn vào `localStorage['lb.baseLanguage']`.
     - Cập nhật giao diện mượt mà theo ngôn ngữ đã chọn.
2. **Nạp Dữ Liệu Khóa Học Mẫu Thực Tế (Content & SRS Seeding)**:
   - Xây dựng giải pháp nạp dữ liệu mẫu (Seeder trong Persistence hoặc script `scripts/seed-sample-courses.sql` / `scripts/seed-sample-courses.ps1` có thể chạy tự động):
     - 🇬🇧 **Khóa Tiếng Anh (English Courses)**:
       1. Khóa `ENG-A1-COMM`: Tiếng Anh Giao Tiếp Nhập Môn (A1) - Target: `en`
          - Bản dịch tiếng Việt & tiếng Anh cho tên khóa và mô tả.
          - Trạng thái: `Published`
          - Tối thiểu 2 bài học (Lessons):
            - Lesson 1: *Chào hỏi & Giới thiệu bản thân (Greetings & Introductions)* (15 XP).
            - Lesson 2: *Giao tiếp công sở cơ bản (Basic Workplace Communication)* (20 XP).
          - Đi kèm các câu hỏi / mẫu câu thực tế (Vocabulary, Multiple Choice, Translations).
       2. Khóa `ENG-BIZ-A2`: Tiếng Anh Công Sở & Thuyết Trình (A2) - Target: `en`
          - Lesson 1: *Viết Email Công Việc (Professional Email Writing)* (25 XP).
     - 🇨🇳 **Khóa Tiếng Trung (Chinese Courses)**:
       1. Khóa `ZH-HSK1-COMM`: Tiếng Trung Giao Tiếp HSK 1 - Target: `zh`
          - Trạng thái: `Published`
          - Tối thiểu 2 bài học (Lessons):
            - Lesson 1: *Chào hỏi & Cảm ơn (问候与感谢 - Wènhòu yǔ Gǎnxiè)* (15 XP).
            - Lesson 2: *Giới thiệu nghề nghiệp (职业介绍 - Zhíyè Jièshào)* (20 XP).
          - Từ vựng có đầy đủ chữ Hán, Pinyin và bản dịch nghĩa tiếng Việt/tiếng Anh.
     - 🗂️ **Dữ Liệu Thẻ Ôn Tập SRS (Flashcards)**:
       - Đảm bảo khi học viên vào trang `/flashcards.html`, có sẵn các thẻ flashcard đến hạn ôn tập (Due Cards) để trải nghiệm tính năng lật thẻ 3D và chấm điểm SM-2 ngay lập tức.

---

## 2. Phạm Vi File Cấp Phép (File Scope & Whitelist)

> **QUY TẮC BẮT BUỘC DÀNH CHO CLAUDE CODE**:
> Claude Code **CHỈ ĐƯỢC PHÉP** tạo hoặc sửa các file trong danh sách dưới đây. Tuyệt đối không can thiệp vào `AGENTS.md` hay `CLAUDE.md`.

- [ ] `repos/linguabridge-web/assets/js/i18n.js` (Tạo mới module từ điển)
- [ ] `repos/linguabridge-web/assets/js/app.js`
- [ ] `repos/linguabridge-web/index.html`
- [ ] `repos/linguabridge-web/courses.html`
- [ ] `repos/linguabridge-web/flashcards.html`
- [ ] `repos/linguabridge-web/login.html`
- [ ] `scripts/seed-sample-courses.sql` (File SQL nạp dữ liệu mẫu)
- [ ] `scripts/seed-sample-courses.ps1` (Script PowerShell thực thi nạp dữ liệu vào PostgreSQL `linguabridge-db`)
- [ ] `docs/handoffs/TASK-025-web-i18n-and-content-seeder.md` (Điền báo cáo triển khai)

---

## 3. Tiêu Chí Nghiệm Thu (Acceptance Criteria)

- [ ] Khi chọn **English** trên Navbar:
  - Tất cả menu điều hướng, tiêu đề trang, nhãn nút bấm, thẻ thông tin và thông báo chuyển sang Tiếng Anh chuẩn mực.
  - Khi chọn **Tiếng Việt**: Giao diện hiển thị Tiếng Việt đầy đủ và tự nhiên.
  - Lựa chọn ngôn ngữ được ghi nhớ qua các trang và khi tải lại trình duyệt.
- [ ] Script nạp dữ liệu mẫu `scripts/seed-sample-courses.ps1` (hoặc file SQL) thực thi thành công vào PostgreSQL `linguabridge-db` trong Docker mà không gây lỗi khóa ngoại hay duplicate key:
  - Có ít nhất 2 khóa học Tiếng Anh và 1 khóa học Tiếng Trung ở trạng thái `Published`.
  - Có đầy đủ bài học, bản dịch (tiếng Việt + tiếng Anh).
  - Có dữ liệu thẻ SRS từ vựng để học viên trải nghiệm lật thẻ 3D.
- [ ] Khi mở trang `/courses.html`:
  - Tab "Tiếng Anh" (hoặc English) hiển thị danh sách các khóa học Tiếng Anh thực tế, bấm vào xem được bài học và điểm XP.
  - Tab "Tiếng Trung" (hoặc Chinese) hiển thị khóa học Tiếng Trung HSK.
- [ ] Khi mở trang `/flashcards.html`: Hiển thị thẻ từ vựng với chữ Hán/Tiếng Anh ở mặt trước, Pinyin/IPA và nghĩa ở mặt sau; lật thẻ và chấm điểm thành công.
- [ ] Toàn bộ unit tests và integration tests của hệ thống giữ nguyên trạng thái **PASS 100%**.
- [ ] Không can thiệp file ngoài whitelist.

---

## 4. Báo Cáo Triển Khai (Claude Code điền khi hoàn thành)

### a. Danh sách file đã tác động
Chỉ trong whitelist; không đụng backend, `AGENTS.md`, `CLAUDE.md`. Không commit.
- `repos/linguabridge-web/assets/js/i18n.js` (**mới**): từ điển `vi` / `en` (khoảng 110 khoá mỗi ngôn ngữ), `t(key, params)` (thay `{tên}`), `applyTranslations()` (thuộc tính `data-i18n`, `data-i18n-attr`, `<title>`, `<html lang>`), `getLanguage()` / `setLanguage()` lưu ở `localStorage['lb.baseLanguage']`.
- `assets/js/app.js`: toàn bộ chuỗi giao diện (Navbar, dashboard, khoá học, modal bài học, flashcard, đăng nhập, thông báo lỗi) đi qua `t()`; chọn ngôn ngữ ở Navbar lưu lựa chọn rồi tải lại trang sau hiệu ứng mờ 150 ms.
- `index.html`, `courses.html`, `flashcards.html`, `login.html`: chữ tĩnh gắn `data-i18n`. (`callback.html` ngoài whitelist: `app.js` tự dịch tiêu đề và dòng chữ của nó.)
- `scripts/seed-sample-courses.sql` (**mới**) và `scripts/seed-sample-courses.ps1` (**mới**): nạp dữ liệu mẫu vào PostgreSQL `linguabridge-db`.

### b. Dữ liệu mẫu
3 khoá **Published**, 5 bài học, 35 câu hỏi (25 từ vựng `Translate` + 10 `MultipleChoice` với 30 đáp án), mỗi thứ có bản dịch vi + en:
| Khoá | Bài học (XP) |
|---|---|
| `ENG-A1-COMM` (en, A1) | Chào hỏi & giới thiệu bản thân (15) · Giao tiếp công sở cơ bản (20) |
| `ENG-BIZ-A2` (en, A2) | Viết email công việc (25) |
| `ZH-HSK1-COMM` (zh, HSK1) | Chào hỏi & cảm ơn (问候与感谢 - Wènhòu yǔ Gǎnxiè) (15) · Giới thiệu nghề nghiệp (职业介绍 - Zhíyè Jièshào) (20) |

Từ vựng tiếng Trung có chữ Hán, pinyin, nghĩa tiếng Việt và tiếng Anh; từ vựng tiếng Anh có IPA, nghĩa tiếng Việt, giải nghĩa tiếng Anh. Thẻ SRS: mỗi từ vựng một thẻ, đến hạn từ hôm qua, cho học viên chỉ định.

`./scripts/seed-sample-courses.ps1` (mặc định tìm học viên `admin@appinhouse.local` trong DB Identity qua `identity-db`; `-UserId <guid>` để chỉ định thẳng; `-ContentOnly` bỏ thẻ):
- Chạy trong **một transaction**, id cố định (md5 → uuid) + `ON CONFLICT DO NOTHING`: chạy lại không trùng, không ghi đè nội dung admin đã sửa, không reset tiến độ thẻ đã ôn. Chạy 2 lần liên tiếp: vẫn 3 khoá / 35 câu hỏi / 25 thẻ.
- Báo lỗi rõ khi Docker tắt, container chưa `healthy`/không tồn tại, bảng chưa được migrate, e-mail/GUID không hợp lệ; không tìm thấy học viên thì nạp nội dung và cảnh báo.
- File SQL được `docker cp` vào container thay vì pipe qua PowerShell (pipe sẽ làm hỏng tiếng Việt/tiếng Hán do mã trang console).

### c. Kiểm thử thực tế
Stack Docker thật + `start-web.ps1` + Edge headless, đăng nhập thật:
- **i18n: 43/43 PASS.** Mặc định tiếng Việt; chọn English lưu `localStorage['lb.baseLanguage']=en`, trang chủ/Navbar/đăng nhập/dashboard/khoá học/modal/flashcard/thông báo hoàn thành/thông báo đăng xuất/lời nhắc đăng nhập đều là tiếng Anh, `<html lang>` và `<title>` đổi theo, **không còn chữ tiếng Việt nào ngoài nhãn "Tiếng Việt" của ô chọn ngôn ngữ**; lựa chọn giữ qua tải lại và sang trang khác. Chuyển lại Tiếng Việt: tab "Tiếng Anh / Tiếng Trung", tên khoá và bài học tiếng Việt, "15 XP", nút đánh giá "Quên", "Đang học · lặp 0 lần · khoảng 0 ngày". Không có lỗi JS; mobile 390 px không cuộn ngang.
- **Courses**: tab English có `ENG-A1-COMM` và `ENG-BIZ-A2`, mở bài học thấy XP; tab Chinese có `ZH-HSK1-COMM` với 2 bài học (chữ Hán + pinyin trong tiêu đề).
- **Flashcards**: lần đầu vào có **25 thẻ đến hạn**; lọc Chinese ra 10 thẻ, mặt trước chữ Hán, mặt sau pinyin + nghĩa; lật 3D, chấm điểm 1–4 lưu được, ôn hết bộ hiện màn hình hoàn thành; dashboard sau đó đếm đúng (25 → 15 → 14 thẻ).
- **Nội dung chơi được thật**: làm cả 5 bài học qua API (lấy quiz, bắt đầu lượt, trả lời đúng cả 7 câu gồm câu gõ chữ Hán, hoàn thành): mỗi bài 100 %, XP đúng 15/20/25/15/20.
- `dotnet test`: Identity 170 + 299, LinguaBridge 544 + 146 đều PASS (không đổi backend). Đã dọn: tắt web server, gỡ stack Docker (`-RemoveData`).

### d. Quyết định và hạn chế
1. **Đổi ngôn ngữ = lưu rồi tải lại trang** (mờ dần 150 ms), không cập nhật tại chỗ: tên khoá, bài học, nghĩa thẻ do API trả về theo ngôn ngữ đã chọn (`baseLanguage`), nên trang vẫn phải lấy lại dữ liệu. Một khoá `lb.baseLanguage` dùng chung cho ngôn ngữ giao diện và ngôn ngữ nội dung (đúng chữ của spec).
2. **Thẻ SRS thuộc về từng học viên**, nên script phải biết học viên nào: mặc định tra `admin@appinhouse.local` trong DB Identity (`identity-db`, SQL Server). Học viên khác chạy lại với `-UserEmail` / `-UserId`. Thẻ chỉ tạo cho câu hỏi `Translate` (từ/cụm từ), không tạo cho câu trắc nghiệm.
3. **Không phải nạp trong API/Seeder C#** (spec cho phép chọn script): không đổi backend. Dữ liệu mẫu chỉ dành cho môi trường phát triển.
4. Một số thông báo lỗi do chính `auth.js` / `api.js` tạo ra vẫn là tiếng Việt cố định (ví dụ "Không tìm thấy phiên đăng nhập đang chờ", lỗi `state`/`nonce` ở trang callback): hai file này ngoài whitelist. Thông báo vòng lặp 401 (`AuthLoopError`) đã được dịch ở `app.js`. Lỗi `ProblemDetails` do API trả về hiển thị nguyên văn (tiếng Anh).
5. Chuỗi của callback (`callback.html`) không có thuộc tính `data-i18n` (không sửa được file); `app.js` đặt lại văn bản bằng `t()` khi trang chạy, nên có thể thấy tiếng Việt thoáng qua nếu JS chậm.
6. Nội dung mẫu là bản tự viết (cụm từ thông dụng, HSK 1 cơ bản) — cần người bản ngữ rà soát nếu dùng cho người học thật.


---

## 5. Kết Quả Code Review (Claude Code — Reviewer)

**Reviewer**: Claude Code (phiên Reviewer độc lập) — **Ngày**: 2026-11-04 — **Kết luận**: `REVIEW_PASSED` (không Blocker/Major; 1 Minor)

### a. Kiểm chứng độc lập
Stack riêng của Reviewer (dựng từ volume trống, gỡ `-RemoveData` sau khi xong) + `start-web.ps1` + script E2E tự viết (Edge headless).

| Hạng mục | Kết quả |
|---|---|
| Whitelist | `git status`: chỉ các file trong whitelist; không có file C# nào đổi so với TASK-024 (Reviewer đã chạy Identity 170+299, LinguaBridge 544+146 ở TASK-024 — vẫn áp dụng) |
| Code review | Mọi `t()` chèn vào template HTML đều qua `esc()`; ngôn ngữ đọc từ `localStorage` được kiểm theo danh sách `vi`/`en`; `-UserEmail` lọc bằng regex (không thể chứa `'`) trước khi ghép vào câu SQL Server; `-UserId` kiểm GUID; `user_id` truyền vào psql qua `:'user_id'` (được quote) |
| Đối chiếu domain | Giá trị chèn thẳng vào DB khớp enum/converter: level `A1`/`A2`/`HSK1` (`LanguageLevelConverter` lưu chữ hoa), `LessonType` `Standard`/`Vocabulary`, `QuestionType` `Translate`/`MultipleChoice`, `SrsState` `Learning`, `ContentStatus` `Published`; `vocabulary_id` = id câu hỏi như luồng tạo thẻ của app |
| `seed-sample-courses.ps1` | lần 1: 3 khoá Published (ENG-A1-COMM 2 bài/14 câu, ENG-BIZ-A2 1/7, ZH-HSK1-COMM 2/14), **25 thẻ đến hạn** cho admin; chạy lại: vẫn 3 / 35 / 25 (idempotent); chữ Hán/pinyin/tiếng Việt lưu đúng UTF-8 (`问候与感谢`, `你好 nǐ hǎo`); `-UserEmail "x'; drop table x;--@a.b"` → bị chặn; e-mail không tồn tại → chỉ nạp nội dung + cảnh báo; `-ContentOnly` đúng |
| i18n (EN) — quét toàn bộ chữ hiển thị tìm ký tự tiếng Việt | trang chủ chưa đăng nhập, lời nhắc đăng nhập, trang login có alert "signed out", dashboard, trang khoá học, modal bài học, flashcard (đã lật), màn hình hoàn thành: **không còn chữ tiếng Việt** (ngoài nhãn "Tiếng Việt" của ô chọn); `<html lang>` đổi `vi`→`en`; `localStorage['lb.baseLanguage']=en`; giữ sau khi tải lại và qua trang khác |
| Khoá học / flashcard | tab English: ENG-A1-COMM + ENG-BIZ-A2; tab Chinese: HSK1, modal 2 bài `问候与感谢 - Wènhòu yǔ Gǎnxiè` 15 XP / `职业介绍` 20 XP; flashcards 1/25; lọc Chinese → 10 thẻ, mặt trước `你好`, mặt sau `nǐ hǎo` + "Hello" + "Learning · repeated 0 times · about 0 days"; ôn hết 10 thẻ → màn hình hoàn thành; dashboard 25 → 15 |
| Chuyển về Tiếng Việt | tab "Tiếng Anh / Tiếng Trung", tên khoá tiếng Việt |
| Khác | 390 px không cuộn ngang; 0 lỗi JS; 404 duy nhất là `favicon.ico` của STS (có từ trước, vô hại) |

Kết quả: **21/21** (1 check báo sai được loại: regex tìm dấu tiếng Việt của Reviewer bắt nhầm `ǎ` trong pinyin `nǐ hǎo`).

### b. Findings

**🔵 Minor #1 — Seed thất bại toàn bộ (lỗi khoá ngoại) nếu một mã khoá mẫu đã tồn tại với id khác** (`seed-sample-courses.sql`)
- `content.courses.code` là unique; `INSERT … ON CONFLICT DO NOTHING` bỏ qua khoá trùng **mã** nhưng các bảng con vẫn trỏ tới id cố định `sid('course', code)` không tồn tại.
- Tái hiện: giả lập admin đã tạo `ENG-BIZ-A2` qua API (id ngẫu nhiên, `Draft`) → chạy script → `ERROR: insert or update on table "course_translations" violates foreign key constraint … Key (course_id)=(a13575e1-…) is not present`; script báo "nothing was changed" (rollback sạch, đúng như thiết kế transaction).
- Không chặn vì: chỉ xảy ra khi ai đó đã tự tạo đúng mã khoá mẫu, lỗi rõ ràng và không làm hỏng dữ liệu. Sửa gợi ý: lấy id khoá theo `code` (`SELECT id FROM content.courses WHERE code = …`) cho các bảng con, hoặc phát hiện trước và bỏ qua khoá đó kèm `RAISE NOTICE`.

### c. Ghi nhận / chuyển Tech Lead
1. Một số thông báo lỗi trong `auth.js` / `api.js` (callback `state`/`nonce`, "Không tìm thấy phiên đăng nhập đang chờ") vẫn cố định tiếng Việt — hai file ngoài whitelist (Coder đã nêu 4d.4); `callback.html` không có `data-i18n` (4d.5).
2. Một khoá `lb.baseLanguage` điều khiển cả ngôn ngữ giao diện lẫn ngôn ngữ nội dung API — theo đúng spec; nếu sau này cần học viên xem giao diện tiếng Anh nhưng nghĩa tiếng Việt thì phải tách.
3. Nội dung mẫu tự viết — cần người bản ngữ rà soát trước khi dùng thật (4d.6).

---

## 6. Đánh Giá & Quyết Định Của Tech Lead (Gemini)

- **Kết luận**: **`APPROVED`**
- **Xác minh kỹ thuật**:
  - Module `assets/js/i18n.js` được thiết kế chuẩn mực, phủ kín toàn bộ các nhãn chuỗi giao diện trên toàn bộ 4 trang (`index.html`, `courses.html`, `flashcards.html`, `login.html`), hỗ trợ song ngữ `vi` và `en` tự nhiên, chuẩn văn phong edtech.
  - Bộ dữ liệu mẫu bài bản và phong phú: 3 khóa học thực tế (`ENG-A1-COMM`, `ENG-BIZ-A2`, `ZH-HSK1-COMM`), 5 bài học, 35 câu hỏi và 25 thẻ từ vựng SRS song ngữ (kèm chữ Hán, Pinyin, IPA, nghĩa tiếng Việt/Anh).
  - Script `scripts/seed-sample-courses.ps1` và `seed-sample-courses.sql` chạy an toàn trong transaction, tự động phát hiện UserId của học viên admin từ Identity DB, thực thi idempotent không lo trùng dữ liệu.
  - Toàn bộ các tiêu chí nghiệm thu đều đạt (43/43 checks Coder, 21/21 checks Reviewer).
  - Tuân thủ 100% phạm vi whitelist cấp phép, không can thiệp mã nguồn backend C#.

- **Quyết định đối với các mục Reviewer nêu**:
  - Ghi nhận Minor #1 về trường hợp trùng mã code do người dùng tạo thủ công từ trước: Script đã bảo vệ an toàn bằng transaction rollback; sẽ tối ưu hóa câu lệnh `SELECT id FROM content.courses WHERE code = ...` trong các lần cập nhật script tiếp theo.
  - Chấp thuận cơ chế dùng chung `lb.baseLanguage` cho cả giao diện và ngôn ngữ học tập ở giai đoạn hiện tại.

- **Kết thúc giai đoạn**: Hệ sinh thái AppInHouse - LinguaBridge đã hoàn thiện trọn vẹn từ Backend, Identity STS, Docker Container Orchestration, Web Client Bootstrap 5, OIDC Single Sign-Out đến Nội dung Khóa học đa ngôn ngữ!

