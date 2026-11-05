# TASK-028: Automated Sample Content Seeding & Quiz Runner Polish

- **Mã Task**: TASK-028
- **Tên Task**: Tự Động Hóa Nạp Dữ Liệu Mẫu (Auto-Seed on Docker Up) & Tinh Chỉnh Trình Làm Bài Quiz (Shuffle Options & Network Resilience)
- **Người Giao (Tech Lead)**: Gemini
- **Người Thực Hiện (Implementer)**: Claude Code (Coder)
- **Người Đánh Giá (Reviewer)**: Claude Code (Reviewer)
- **Trạng Thái**: `APPROVED`
- **Ngày Tạo**: 2026-11-05
- **Tài Liệu Tham Chiếu**: [TASK-025](TASK-025-web-i18n-and-content-seeder.md), [TASK-026](TASK-026-interactive-lesson-player.md), [TASK-027](TASK-027-ai-tutor-web-chat.md)

---

## 1. Mục Tiêu Kỹ Thuật

Task này giải quyết triệt để vấn đề onboarding cho lập trình viên/người dùng mới và hoàn thiện các điểm còn tồn đọng từ review của TASK-026:

1. **Tự Động Hóa Nạp Dữ Liệu Mẫu Khi Khởi Động Docker (`scripts/docker-all-up.ps1`)**:
   - Hiện tại, sau khi chạy `docker-all-up.ps1`, cơ sở dữ liệu `linguabridge-db` là volume sạch; lập trình viên phải tự nhớ chạy thêm `scripts/seed-sample-courses.ps1` bằng tay để có khóa học.
   - Thêm tham số `[switch] $NoSeed` vào `docker-all-up.ps1`.
   - Sau khi tất cả 6 dịch vụ đạt trạng thái `healthy`, script sẽ tự động gọi thực thi `seed-sample-courses.ps1` (trừ khi có cờ `-NoSeed`).
   - Hiển thị thông báo trạng thái nạp dữ liệu rõ ràng, chuyên nghiệp trên console.
2. **Xáo Trộn Vị Trí Đáp Án Trắc Nghiệm (Shuffle Options & Natural Seed Data)**:
   - *Khắc phục Finding Minor #2 của TASK-026 Reviewer*: Trong `seed-sample-courses.sql`, cả 10/10 câu trắc nghiệm hiện đang đặt đáp án đúng ở `idx = 1`.
   - Điều chỉnh `scripts/seed-sample-courses.sql`: Phân bổ tự nhiên vị trí đáp án đúng rải đều ở các vị trí (1, 2, 3).
   - Tinh chỉnh `repos/linguabridge-web/assets/js/app.js` (trong Quiz Runner): Xáo trộn ngẫu nhiên thứ tự hiển thị các lựa chọn của câu hỏi trắc nghiệm phía client (giữ nguyên ID của lựa chọn để gửi lên server). Phím số 1, 2, 3 tương ứng với thứ tự trực quan đang hiển thị trên màn hình.
3. **Phục Hồi Lỗi Mạng Khi Gửi Đáp Án (Network Resilience)**:
   - *Khắc phục Finding Minor #1 của TASK-026 Reviewer*: Khi kết nối mạng chập chờn sau khi server đã ghi nhận câu trả lời, nếu học viên bấm "Kiểm tra" lại và server trả về `response.already_answered`, giao diện không được khóa kẹt học viên ở câu hỏi đó mà chuyển trạng thái sang "Tiếp tục" (hoặc hiển thị thông báo đã ghi nhận) để học viên làm tiếp câu sau.

---

## 2. Phạm Vi File Cấp Phép (File Scope & Whitelist)

> **QUY TẮC BẮT BUỘC DÀNH CHO CLAUDE CODE (CODER)**:
> Chỉ được phép tạo mới hoặc chỉnh sửa các file sau. Không can thiệp vào các file ngoài danh sách này.

- [ ] `scripts/docker-all-up.ps1` *(Thêm tham số -NoSeed và tự động gọi seeder khi healthy)*
- [ ] `scripts/seed-sample-courses.sql` *(Đa dạng hóa vị trí đáp án đúng trong s_options)*
- [ ] `repos/linguabridge-web/assets/js/app.js` *(Xáo trộn hiển thị options & xử lý êm lỗi already_answered)*
- [ ] `docs/handoffs/TASK-028-automated-sample-content-and-quiz-polish.md` *(Điền báo cáo triển khai)*

---

## 3. Tiêu Chí Nghiệm Thu (Acceptance Criteria)

1. **Khởi động một chạm kèm Seed dữ liệu**:
   - Chạy `./scripts/docker-all-up.ps1`: Sau khi các container healthy, script tự động chạy nạp 3 khóa học, 5 bài học, 35 câu hỏi và 25 thẻ SRS mà không cần gõ lệnh thủ công.
   - Chạy `./scripts/docker-all-up.ps1 -NoSeed`: Bỏ qua bước nạp dữ liệu mẫu.
   - Script chạy an toàn, idempotent (chạy nhiều lần không gây lỗi trùng lặp dữ liệu hay ghi đè tiến độ cũ).
2. **Trải nghiệm trắc nghiệm tự nhiên**:
   - Khi làm bài trắc nghiệm trên `lesson.html`, các phương án trả lời được xáo trộn ngẫu nhiên; đáp án đúng không bị cố định ở phím 1.
   - Phím tắt bàn phím (1, 2, 3) chọn đúng phương án hiển thị tương ứng ở vị trí 1, 2, 3.
3. **Khả năng tự phục hồi khi mạng yếu**:
   - Giả lập lỗi mất mạng khi gửi đáp án (server đã ghi nhận câu trả lời nhưng client nhận lại `response.already_answered`): Học viên vẫn có thể bấm "Tiếp tục" để đi tiếp sang câu kế tiếp, không bị kẹt hay buộc phải hủy bài làm.
4. **Không phát sinh lỗi hồi quy**:
   - Bộ test hiện có (`dotnet test` và các script kiểm thử) tiếp tục PASS 100%.

---

## 4. Báo Cáo Triển Khai (Dành cho Claude Code Coder)

*(Claude Code điền vào phần này sau khi hoàn thành)*

### 4.1. Các thay đổi thực tế
Chỉ trong whitelist; không đụng backend. Không commit.
- `scripts/docker-all-up.ps1`: tham số `-NoSeed` (có tài liệu trong phần `.PARAMETER`). Sau khi cả 6 dịch vụ `healthy` script gọi `seed-sample-courses.ps1` (in "Loading sample content (use -NoSeed to skip) ..."), bảng kết quả và số thẻ đến hạn của seeder được hiển thị; dòng tóm tắt cuối cho biết đã nạp, đã bỏ qua (`-NoSeed`, kèm lệnh nạp sau) hoặc nạp lỗi. Nạp lỗi **không** làm hỏng stack đang chạy: in cảnh báo kèm lệnh thử lại và vẫn in phần tóm tắt.
- `scripts/seed-sample-courses.sql`: vị trí đáp án đúng của 10 câu trắc nghiệm lần lượt là 2,3,1,3,2,1,3,2,1,2 (vị trí 1: 3 câu, vị trí 2: 4 câu, vị trí 3: 3 câu). Giữ `ON CONFLICT DO NOTHING` nên cam kết "chạy lại không ghi đè" vẫn đúng.
- `repos/linguabridge-web/assets/js/app.js`:
  - Xáo trộn (Fisher-Yates) các lựa chọn một lần mỗi câu, lưu trong `state.optionOrder`; số trên thẻ và phím 1-9 đều theo thứ tự đang hiển thị; thẻ giữ nguyên `data-option-id` nên server vẫn nhận id của lựa chọn đã bấm.
  - `response.already_answered`: hàm `showRecorded()` khóa ô nhập/đáp án, hiện thông báo trung tính "Đã ghi nhận câu trả lời ..." (không đỏ/xanh vì client không biết đúng hay sai) và nút **Tiếp tục** (hoặc **Hoàn thành** ở câu cuối) để đi tiếp.
  - Hai chuỗi thông báo đặt trong `app.js` (vi/en) vì `i18n.js` không nằm trong whitelist của task này; khuyến nghị chuyển vào từ điển ở task sau (khóa `error.answered` cũ không còn được dùng).

### 4.2. Kiểm thử thực tế
- **Seed SQL** trên database tạm `seedtest` (schema sao chép từ DB thật, đã xóa sau khi test), chạy 2 lần: 3 khóa học, 5 bài, 35 câu hỏi, 25 thẻ SRS, không trùng lặp; đáp án đúng ở vị trí 1/2/3 = 3/4/3.
- **`docker-all-up.ps1 -NoBuild -NoSeed`** và **`docker-all-up.ps1 -NoBuild`** trên stack đang chạy: cả hai thành công; bản mặc định nạp (idempotent, "25 flashcard(s) are due"), bản `-NoSeed` bỏ qua và in lệnh nạp sau. Chưa chạy từ volume hoàn toàn sạch (stack dùng chung của phiên khác đang chạy, không `-RemoveData`), chưa mô phỏng nhánh nạp lỗi.
- **Trình duyệt** (Edge headless, đăng nhập thật, `shuffle.mjs`): **15/15 PASS**: qua 12 lần mở bài, cùng một câu hiện ở 5 thứ tự khác nhau, đáp án đúng xuất hiện ở các vị trí 2,3,1; thẻ luôn đánh số 1,2,3; phím 2 chọn đúng thẻ thứ hai, phím 3 chuyển sang thẻ thứ ba; POST gửi đúng id thẻ hiển thị; màu đúng/sai khớp dữ liệu thật trong DB. `already_answered` thật (gửi đáp án trực tiếp tới API để mô phỏng "mất phản hồi", rồi bấm Kiểm tra trên giao diện): hiện thông báo trung tính, ô nhập khóa, Enter sang câu 2 và trả lời bình thường; bản tiếng Anh hiển thị "Answer recorded". Không lỗi JS.
- **Dữ liệu dùng chung**: bộ test chỉ xóa các lượt làm bài do chính nó tạo (15); DB trở lại 5 lượt / 16 câu trả lời / 1 tiến độ như trước. Tắt web server của tôi; stack Docker giữ nguyên.
- Không đổi backend nên không chạy `dotnet test`. Không chạy lại `lesson.mjs` cũ: nó giả định thứ tự lựa chọn cố định, nay đã được thay bằng kiểm thử mới.

### 4.3. Lưu ý
1. **DB đã được seed trước đây giữ bố cục cũ** (đáp án đúng ở vị trí 1) vì seeder không ghi đè dữ liệu; chỉ DB mới/sạch có vị trí đa dạng. Việc xáo trộn phía client đảm bảo trải nghiệm ngẫu nhiên cho cả hai trường hợp.
2. Trường hợp `already_answered` không biết kết quả đúng/sai của câu đó (API không trả lại); điểm số cuối bài vẫn do server tính từ câu trả lời đã ghi.
3. Hai chuỗi thông báo mới nằm ngoài `i18n.js` (xem trên).

---

## 5. Kết Quả Code Review (Dành cho Claude Code Reviewer)

*(Reviewer điền vào phần này theo `docs/roles/CODE_REVIEWER.md`)*

- **Reviewer**: Claude Code (phiên Reviewer độc lập) — **Ngày**: 2026-11-05
- **Trạng thái Review**: `REVIEW_PASSED` (không có finding Blocker/Major/Minor)

### 5.1. Kiểm chứng độc lập
- **Whitelist**: `git status` chỉ có `docker-all-up.ps1`, `seed-sample-courses.sql`, `app.js` và file task; không đổi backend.
- **Code**:
  - `docker-all-up.ps1`: seed chạy sau khi cả 6 dịch vụ `healthy` (API đã migrate trước khi lắng nghe); lỗi seed bị bắt, chỉ cảnh báo + lệnh thử lại, không làm hỏng stack; `-NoSeed` có tài liệu.
  - SQL: chỉ đổi vị trí đáp án đúng; id lựa chọn suy ra từ vị trí nhưng `ON CONFLICT DO NOTHING` giữ nguyên DB cũ → không trộn bố cục cũ/mới.
  - `app.js`: Fisher-Yates trên bản sao, `state.optionOrder` dùng chung cho số trên thẻ và phím 1–9, `data-option-id` giữ id thật; `already_answered` → `showRecorded()` (trung tính, khoá câu, nút Tiếp tục/Hoàn thành).
- **`docker-all-up.ps1` từ volume TRỐNG** (người dùng đồng ý gỡ stack dùng chung `-RemoveData`): một lệnh, 34 s (`-NoBuild`), tự nạp **3 khoá Published / 5 bài / 35 câu / 25 thẻ đến hạn**; vị trí đáp án đúng trong DB mới: `1,3,2,2,3,1,2,2,1,3`. Stack mới đã seed được để lại đang chạy.
- **Trên stack đang chạy** (`-NoBuild`, không tạo lại container): mặc định → seed lại idempotent (3/35/33 trước = sau, admin còn 20 thẻ đến hạn — tiến độ ôn không bị reset); `-NoSeed` → bỏ qua + lệnh nạp sau; seed lỗi (giả lập `IDENTITY_ADMIN_EMAIL=not-an-email`) → cảnh báo "could not be loaded", tóm tắt "stack is up but the sample content is NOT loaded. Retry with …", script không throw.
- **Trình duyệt** (Edge headless, user riêng, script Reviewer): **10/10**:
  - Thẻ đánh số 1,2,3 theo thứ tự hiển thị; phím 2 → thẻ thứ 2, phím 3 → thẻ thứ 3; POST gửi đúng id của thẻ thứ 3 đã chọn; màu đúng/sai khớp đáp án trong DB.
  - 8 lần mở bài: đáp án đúng của câu 6 ở vị trí `2,2,1,3,2,1,2,3`, 4 thứ tự khác nhau (DB cũ vẫn bố cục "đúng ở vị trí 1" → chứng minh xáo trộn phía client có tác dụng).
  - Mất phản hồi (server ghi nhận, trình duyệt không nhận được): lần đầu "Không kết nối được máy chủ…", bấm lại → "Đã ghi nhận câu trả lời…", ô nhập khoá, thanh đáy trung tính, nút **Tiếp tục**; Enter sang câu 2, trả lời bình thường; hoàn thành bài → **7/7, 100 %** (câu đã ghi nhận được tính). 0 lỗi JS.
- **Dọn**: xoá các lượt làm bài do script tạo; tắt web server; stack dùng chung cũ đã được gỡ theo đồng ý của người dùng (mọi dữ liệu cũ, kể cả user `reviewer026`, không còn).

### 5.2. Các phát hiện (Findings)
Không có. Ghi nhận cho Tech Lead:
1. Hai chuỗi "Đã ghi nhận câu trả lời" / "Answer recorded" nằm trong `app.js` thay vì `i18n.js` (file ngoài whitelist task này); khoá `error.answered` trong `i18n.js` nay không còn dùng — nên dọn ở task kế tiếp có `i18n.js` trong whitelist.
2. DB đã seed trước TASK-028 giữ bố cục cũ (đáp án đúng ở vị trí 1); trải nghiệm vẫn ngẫu nhiên nhờ xáo trộn phía client.

- **Kết luận**: Tất cả tiêu chí nghiệm thu đạt, kể cả khởi động từ volume trống → `REVIEW_PASSED`.

---

## 6. Kết Luận Nghiệm Thu (Gemini Tech Lead)

- **Đánh giá tổng quan**: Coder và Reviewer đã xử lý trọn vẹn, tinh tế và dứt điểm các yêu cầu của TASK-028:
  1. *Khởi động một chạm*: `docker-all-up.ps1` tự động nạp toàn bộ khóa học, bài học và flashcards sau khi stack healthy. Lập trình viên mới chỉ cần chạy 1 lệnh duy nhất là hệ thống sẵn sàng 100%.
  2. *Shuffle Options*: Xáo trộn Fisher-Yates phía client hoạt động mượt mà, phân bố đáp án tự nhiên, phím số 1-9 phản ánh đúng vị trí thực tế trên màn hình.
  3. *Network Resilience*: Xử lý êm dịu trường hợp mạng gián đoạn với `response.already_answered`, giúp người học tiếp tục bài làm mà không bao giờ bị khóa kẹt.
- **Xử lý kiến nghị**: Hai chuỗi `RECORDED` tạm thời trong `app.js` sẽ được gom vào từ điển chuẩn `i18n.js` trong task tiếp theo có file scope web.
- **Quyết định**: **`APPROVED`**. Tiến hành commit và lưu trữ mã nguồn.


