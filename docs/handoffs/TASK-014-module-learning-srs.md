# TASK-014: Module Learning & SRS — Tiến Độ Học Tập & Thuật Toán SM-2

- **Mã Task**: TASK-014
- **Người Giao (Tech Lead)**: Gemini
- **Người Thực Hiện (Implementer)**: Claude Code
- **Trạng Thái**: `APPROVED`
- **Ngày Tạo**: 2026-10-28
- **Ngày Hoàn Thành**: 2026-10-29
- **Phụ Thuộc**: TASK-013 (Content module đã hoàn thành)
- **Tài Liệu Tham Chiếu**: [docs/architecture/language_learning_platform_design.md](../architecture/language_learning_platform_design.md) (Mục 3.2, 3.3, Definition of Done)

---

## 1. Mục Tiêu Kỹ Thuật

Xây dựng lớp **Application + API** cho Module Learning, gồm:
1. **Exercise Session**: Bắt đầu bài học (`ExerciseAttempt`), ghi nhận từng câu trả lời (`QuestionResponse`), tính điểm và XP khi kết thúc.
2. **User Progress Tracker**: Cập nhật `UserCourseProgress` sau mỗi lần học bài hoàn thành.
3. **SRS Engine (SM-2)**: Sau mỗi câu trả lời, cập nhật `UserVocabularyProgress` theo thuật toán SM-2 chuẩn và ghi `ReviewLog`. Chỉ thẻ có `NextReviewDate <= today` xuất hiện trong session ôn tập.
4. **Review Session API**: Lấy danh sách thẻ cần ôn hôm nay, submit đánh giá (1-Again, 2-Hard, 3-Good, 4-Easy), nhận lịch ôn tiếp theo.

---

## 2. Phạm Vi File Cấp Phép (File Scope & Whitelist)

- [ ] `repos/linguabridge/src/Modules/LinguaBridge.Modules.Learning/*` (Use Cases, SM-2 Engine, DTOs)
- [ ] `repos/linguabridge/src/Infrastructure/LinguaBridge.Infrastructure.Persistence/*`
- [ ] `repos/linguabridge/src/Presentation/LinguaBridge.Api/*` (Endpoint mapping)
- [ ] `repos/linguabridge/tests/LinguaBridge.UnitTests/*`
- [ ] `repos/linguabridge/tests/LinguaBridge.IntegrationTests/*`

---

## 3. Chi Tiết Yêu Cầu Kỹ Thuật

### 3.1. SM-2 Algorithm (Pure Domain Logic)

Triển khai thuật toán SM-2 tiêu chuẩn dưới dạng **pure function** (không phụ thuộc infrastructure) trong `LinguaBridge.Modules.Learning`:

```
Input: (EaseFactor, IntervalDays, Repetitions, Rating 1-4)
Output: (NewEaseFactor, NewIntervalDays, NewRepetitions, NextReviewDate)

- Rating 1 (Again): Repetitions = 0, IntervalDays = 1, EaseFactor giữ nguyên hoặc giảm 0.2 (min 1.3)
- Rating 2 (Hard):  EaseFactor -= 0.15 (min 1.3), IntervalDays *= 1.2
- Rating 3 (Good):  EaseFactor giữ nguyên, IntervalDays = prev * EaseFactor (lần đầu: 1, 6)
- Rating 4 (Easy):  EaseFactor += 0.15, IntervalDays = prev * EaseFactor * 1.3
```

**Yêu cầu test**: SM-2 Engine phải có ít nhất **20 unit tests** bao gồm: rating lần đầu, sau nhiều lần đúng, rating "Again" reset, giới hạn EaseFactor min 1.3, chuyển State từ `Learning → Review → Graduated`.

### 3.2. API Endpoints (Quy ước thống nhất tiền tố `/api/v1/learning/...`)

| Method | Path | Mô tả |
|---|---|---|
| `GET` | `/api/v1/learning/lessons/{lessonId}/quiz` | Lấy danh sách câu hỏi làm bài (DTO **tuyệt đối không chứa `isCorrect`**) |
| `POST` | `/api/v1/learning/attempts` | Bắt đầu exercise attempt cho một Lesson (Lesson phải `Published`) |
| `POST` | `/api/v1/learning/attempts/{id}/responses` | Submit câu trả lời (server tự đối chiếu đáp án đúng/sai, trả kết quả tức thì) |
| `PUT` | `/api/v1/learning/attempts/{id}/complete` | Kết thúc bài học, tính điểm, ghi nhận XP và cập nhật `UserCourseProgress` |
| `GET` | `/api/v1/learning/srs/due` | Lấy danh sách thẻ từ vựng cần ôn hôm nay (`NextReviewDate <= today`) |
| `POST` | `/api/v1/learning/srs/review` | Submit đánh giá thẻ SRS (Rating 1-4) |
| `GET` | `/api/v1/learning/progress/{courseId}` | Tiến độ học khóa học của học viên hiện tại |

> **Lưu ý bảo mật (Security note)**: Endpoint lấy câu hỏi cho học viên (`/quiz`) phải dùng DTO riêng (`LearnerQuestionDto`), giấu toàn bộ trường `isCorrect` của các options để ngăn chặn học viên xem trước đáp án qua DevTools. Việc chấm điểm đúng/sai được thực hiện nghiêm ngặt ở server khi nhận `POST /responses`.

### 3.3. Acceptance Criteria Từ Tài Liệu Thiết Kế

- Chỉ các thẻ có `NextReviewDate <= CURRENT_DATE` mới xuất hiện trong review session.
- Sau khi submit đánh giá, `NextReviewDate` được cập nhật chính xác theo SM-2.
- `ScorePercentage` trong `ExerciseAttempt` tính đúng tỉ lệ câu đúng.
- `UserCourseProgress.CompletedLessonsCount` tăng khi hoàn thành bài học lần đầu.

---

## 4. Tiêu Chí Nghiệm Thu (Acceptance Criteria)

- [x] SM-2 Engine có ≥ 20 unit tests PASS với mọi nhánh logic (thực tế 58 unit tests PASS cho SM-2).
- [x] SRS chỉ trả thẻ có `NextReviewDate <= today`.
- [x] Submit rating → `UserVocabularyProgress` và `ReviewLog` được cập nhật chính xác.
- [x] `UserCourseProgress` được cập nhật sau khi hoàn thành bài.
- [x] Tất cả tests PASS, 0 regression (366 unit tests, 92 integration tests PASS).

---

## 5. Báo Cáo Triển Khai (Claude Code điền khi hoàn thành)

### a. Danh sách file đã tác động

**Module Learning (`LinguaBridge.Modules.Learning/`)**
- Tạo `Srs/Sm2Engine.cs` (hàm thuần `Sm2Engine.Calculate(Sm2Input) → Sm2Result`), `Grading/AnswerMatcher.cs`, `Grading/QuestionGrading.cs`, `Abstractions/{Ports, Dtos}.cs` (`ILessonSource`, `ILearningRepository`, `ILearningReadModel`, DTO), `Commands/{AttemptCommands, ReviewCardCommand, Validators, CommandSupport}.cs`, `Queries/LearningQueries.cs`.
- Sửa `Entities/ExerciseAttempt.cs` (chặn trả lời trùng câu), `Entities/UserCourseProgress.cs` (+ `Update`), `Entities/UserVocabularyProgress.cs` (+ `IsDueOn`, `ApplyReview`), `LearningModule.cs` (đăng ký handler/validator), `.csproj` (+ `FluentValidation` 12.*).
- Module vẫn chỉ phụ thuộc `Domain` + `Application` (`ArchitectureTests` PASS); **không tham chiếu Content**.

**Persistence** — tạo `Learning/{LearningRepository, LessonSource, LearningReadModel}.cs`; sửa `DependencyInjection.cs`. Không đổi model → **không cần migration** (test `Model_has_no_changes_missing_from_the_migrations` vẫn PASS).

**Api** — tạo `Endpoints/LearningEndpoints.cs`; sửa `Program.cs` (+ `MapLearningEndpoints`), `Endpoints/ContentEndpoints.cs` (chỉ đổi 2 hàm helper ngôn ngữ từ `private` sang `internal` để dùng chung).

**Tests** — UnitTests: tạo `Learning/{Sm2EngineTests, GradingTests, AttemptHandlerTests, CompletionReviewAndQueryTests, FakeLearning}.cs`; sửa `ModuleRegistrationTests.cs` + `.csproj` (xem rủi ro 14). IntegrationTests: tạo `LearningApiTests.cs` (gồm helper `Seed`); sửa `ContentApiFixture.cs` (+ `LearnerAs`, `WithSubject`).

Không file nào ngoài whitelist bị sửa (đối chiếu `git status`; `repos/identity` không đổi).

### b. Kiểm thử thực tế
```bash
dotnet build repos/linguabridge/LinguaBridge.sln --no-incremental   # 0 Warning(s)  0 Error(s)
dotnet build AppInHouse.sln                                         # 0 Warning(s)  0 Error(s)

dotnet test repos/linguabridge/LinguaBridge.sln          # Docker Desktop 29.9.2 đang chạy
#   Passed!  - Failed: 0, Passed: 366, Total: 366 - LinguaBridge.UnitTests.dll        (213 cũ + 153 mới)
#   Passed!  - Failed: 0, Passed:  92, Total:  92 - LinguaBridge.IntegrationTests.dll (63 cũ + 29 mới)
```
Tổng LinguaBridge: **458** (276 nền + 182 mới), 0 regression.

**SM-2 Engine — 58 test** (yêu cầu ≥ 20): Good (1 → 6 → ×EF, làm tròn .5 lên, không đổi EF, không rút ngắn thẻ đã bị Easy đẩy xa); Again (reset reps, 1 ngày, EF −0.2, Graduated → Learning, mọi lịch sử); Hard (EF −0.15, ×1.2, không tăng reps, thẻ mới vẫn ≥ 1 ngày); Easy (EF +0.15, ×EF×1.3, luôn dài hơn Good — theory trên 5 trạng thái); EF sàn 1.3 và EF lưu thấp hơn sàn; không trôi số thực (2.35, 2.2, 2.05 chính xác); **Learning → Review → Graduated** (chuỗi 1, 6, 15, 38) và ranh giới đúng 21 ngày (20/21/23); lapse rồi học lại; trần 365 ngày và 200 lần Easy liên tiếp không tràn; `NextReviewDate` qua cuối tháng/năm; tính thuần (cùng input → cùng output); rating/lịch sử không hợp lệ bị từ chối.

**Unit (95 test khác, repository/clock/lesson giả):** chấm điểm (choice, gõ chữ, `AnswerMatcher`), DTO học viên không có thuộc tính nào mang tính đúng/sai (kiểm bằng reflection + serialize JSON), start/answer/complete (mọi nhánh lỗi: sai chủ sở hữu, trùng câu, attempt đã xong, lesson bị gỡ, thua race khi lưu), SRS từ câu trả lời, score/XP/progress, review thẻ, 3 query, và entity.

**Integration (29) — host thật + PostgreSQL 17 thật + nội dung tạo qua API admin:**
- Ẩn danh → 401; token của **service** (sub không phải Guid) → 403 `learner.required` ở cả 5 endpoint.
- **`/quiz` không chứa chữ "correct" nào** (kiểm trên body thô), cũng không chứa đáp án gõ tay; trong khi preview của admin cho cùng dữ liệu **có** `isCorrect` (chứng minh dữ liệu thật sự tồn tại ở server). Câu gõ chữ ẩn `targetText`/phiên âm; câu Pronounce không được phát; lesson Draft / course chưa publish / không tồn tại → 404 (cả `/quiz` lẫn `POST /attempts`).
- Luồng đủ: start (201) → trả lời sai (lộ đáp án + giải thích) → trả lời gõ chữ cẩu thả vẫn đúng → complete: score 50, XP 10, progress 1/1. Replay không cộng thêm tiến độ/XP; hai lesson trong một course → 1/2 rồi 2/2 `isCompleted`.
- Chặn: trả lời trùng câu (409), thiếu/ sai option (400), người khác trả lời/hoàn thành attempt của mình (404), hoàn thành khi còn câu chưa trả lời (409), hoàn thành hai lần (409), lesson bị archive giữa chừng (404).
- **Double-click**: 2 `PUT complete` song song → có 200, mọi kết quả ∈ {200, 409}, **tiến độ vẫn đúng 1/1**.
- SRS: trả lời tạo đúng 2 thẻ (interval 1, reps 1, mai ôn, chưa đến hạn); làm lại bài 4 lần cùng ngày → interval vẫn 1, chỉ 2 log; `/srs/due` chỉ trả thẻ `NextReviewDate ≤ hôm nay`, cũ nhất trước, không lẫn thẻ người khác, lọc `targetLanguage`, `limit`, tham số sai → 400, thẻ của nội dung đã archive biến mất; `/srs/review` ghi đúng `UserVocabularyProgress` + `ReviewLog` (kiểm trực tiếp trong DB: rating, EF cũ/mới, interval cũ/mới), mỗi rating 1–4 ra đúng bước SM-2, thẻ quá hạn 10 ngày tính từ interval cũ + hôm nay (6 → 15), không review sớm (409), không review hai lần/ngày, không review thẻ của người khác (404), rating 0/5/−1 → 400.
- **Kiểm chứng đột biến (4 chỗ cùng lúc):** để lộ `targetText` cho câu gõ chữ; bỏ luật chống nhồi lịch; bỏ chặn review sớm; bỏ lọc theo chủ sở hữu của attempt → 5 unit test + 5 integration test FAIL đúng chỗ (riêng lọc chủ sở hữu chỉ integration bắt được vì bản giả trong unit tự lọc). Đã hoàn tác cả bốn.

### c. Rủi ro / lưu ý cho Tech Lead

**Các chỗ spec mơ hồ và cách tôi chốt (cần Tech Lead xác nhận)**
1. **SM-2 — diễn giải công thức mục 3.1.** (a) Again: "EF giữ nguyên hoặc giảm 0.2" → chọn **giảm 0.2**. (b) Hard: **không tăng** `Repetitions`. (c) Easy dùng EF **mới** (sau +0.15); thẻ mới có `prev = 0` nên công thức cho 0 ngày → tôi bắt Easy luôn **dài hơn Good ≥ 1 ngày** (Easy lần đầu = 2 ngày, lần hai = 7). (d) Good lần hai = `max(6, interval hiện tại)` để không rút ngắn thẻ đã bị Easy đẩy xa. (e) EF làm tròn 2 chữ số để không trôi số thực.
2. **Ngưỡng trạng thái là tôi tự đặt** (spec chỉ nói Learning → Review → Graduated): `Review` khi `Repetitions ≥ 2`, `Graduated` khi `IntervalDays ≥ 21`, lapse (Again) quay về `Learning`. Thêm **trần interval 365 ngày** (chống tràn `DateOnly`). Cả ba là hằng số `public const` ở `Sm2Engine`, dễ chỉnh.
3. **"Hôm nay" = ngày UTC.** Học viên Việt Nam (UTC+7) sẽ thấy thẻ "đến hạn" đổi ngày lúc 07:00 sáng giờ địa phương. Cần quyết định có lưu múi giờ học viên hay không.
4. **Câu trả lời trong bài học → rating tự động:** đúng = Good, sai = Again (spec chỉ nói "sau mỗi câu trả lời" mà không nói ánh xạ). `VocabularyId` = **Id của Question** (chưa có bảng từ vựng); mọi câu hỏi chấm được đều tạo thẻ.
5. **Lệch khỏi chữ "sau mỗi câu trả lời cập nhật thẻ":** trả lời **đúng** trên thẻ **chưa đến hạn** thì **không** đổi lịch (vẫn tạo thẻ mới nếu chưa có; trả lời **sai** luôn tính là lapse). Lý do: nếu không, làm lại một bài 5 lần trong ngày sẽ đẩy interval 1 → 6 → 15 → 38… (đã có test chứng minh luật này). Tương tự, `/srs/review` **từ chối thẻ chưa đến hạn** (409 `srs.card_not_due`).
6. **Cách chấm (spec chỉ nói "server tự đối chiếu"):** câu **có options** → chọn 1 option, đúng nếu option đó `IsCorrect`; câu **không options** → **gõ chữ**, so với `TargetText`. Vì `TargetText` chính là đáp án nên **ẩn `TargetText` và phiên âm** của câu gõ chữ khỏi DTO học viên (câu có options thì hiện, vì đó là đề). `AnswerMatcher` bỏ qua hoa/thường, khoảng trắng thừa, dấu câu ở **hai đầu**, dạng full-width/half-width (NFKC) — **không** bỏ qua lỗi chính tả hay từ khác.
7. **Giới hạn chấm điểm cần biết:** (a) `Pronounce` **không được phát và không chấm** (cần speech scoring, chưa có) — lesson chỉ có Pronounce được xem là "chỉ đọc". (b) Content **chưa có trường đáp án thay thế**, nên câu dịch có nhiều cách đúng ("Hi"/"Hello") sẽ chấm sai cách còn lại — cần bổ sung ở Content. (c) Câu cấu hình sai (có options nhưng không option nào đúng; MultipleChoice không có options) bị **loại khỏi quiz** để không chấm sai học viên vì lỗi nội dung.
8. **Hoàn thành bài (mục 3.1, 3.3):** bắt buộc **trả lời hết** câu mới được `complete` (409 nếu thiếu) — vì không có cách chấm điểm công bằng cho câu bỏ trống. `Score = round(đúng / tổng câu chấm được)`; lesson không có câu chấm được → 100. **XP = `XpReward × score`, chỉ cho lần hoàn thành đầu tiên** của lesson (chơi lại = 0 XP, chống cày XP). **Không có điểm đạt:** hoàn thành với 0% vẫn tính là "đã học bài" và tăng tiến độ — cần PO quyết định có ngưỡng đạt hay không. XP chỉ lưu trên `ExerciseAttempt`, chưa có tổng XP theo user.
9. **`UserCourseProgress` được tính lại từ sự kiện, không cộng dồn:** `Completed` = số lesson **khác nhau** (đang Published) học viên đã hoàn thành; `Total` = số lesson Published hiện tại. Nhờ vậy double-click, retry, thêm/gỡ lesson đều không làm sai số (đã có test). Hệ quả: course chưa từng hoàn thành lesson nào thì **không có dòng nào** trong DB, `GET /progress` trả 0/Total.

**Kiến trúc**
10. **Ranh giới module:** Learning cần biết nội dung (lesson đã publish, đáp án, course) nhưng không được tham chiếu Content, và `LinguaBridge.Application` không nằm trong whitelist task này. Tôi để Learning **tự khai báo** `ILessonSource` (cổng) và **Persistence triển khai** bằng cách đọc thẳng schema `content`, cùng một câu join `srs ⨝ content` cho danh sách thẻ đến hạn (để `limit` áp dụng sau khi lọc nội dung đã gỡ). Nếu muốn hợp đồng liên module nằm ở `Application` (Content triển khai), cần task riêng chạm vào hai phần đó.
11. **Học viên = token có `sub` là Guid.** Token `client_credentials` của service nhận 403 `learner.required` thay vì được cấp "tài khoản rỗng". `userId` luôn lấy từ token, không bao giờ từ request.

**Rủi ro đồng thời / vận hành**
12. **Không có unique index `(attempt_id, question_id)`** trên `learning.question_responses` (thiết kế TASK-010 không có). Hai `POST /responses` song song cho cùng câu có thể cùng lọt qua kiểm tra → hai dòng; điểm vẫn đúng (lấy câu trả lời đầu mỗi câu) nhưng thẻ SRS có thể bị cập nhật hai lần. Hai attempt khác nhau của cùng lesson hoàn thành đúng cùng lúc cũng có thể cùng nhận XP "lần đầu". Muốn chặn hẳn cần migration thêm unique index. Double-click `complete` trên **cùng** attempt đã được kiểm chứng an toàn.
13. **Attempt bỏ dở tích lũy mãi** (mỗi lần `POST /attempts` là một attempt mới, không hết hạn/không dọn). Không ảnh hưởng tiến độ (chỉ attempt đã complete mới tính) nhưng bảng sẽ phình dần.
14. **Sửa test cũ/hạ tầng test:** `ModuleRegistrationTests.All_modules_register_and_container_validates` giờ gọi thêm `AddInfrastructureExternal` (Learning cần `IDateTimeProvider`), kèm `ProjectReference` tới `Infrastructure.External` trong `LinguaBridge.UnitTests.csproj` — hệ quả trực tiếp của phụ thuộc mới, không nới lỏng test.
15. **Phản hồi sau khi trả lời cố ý tiết lộ đáp án đúng** (đó là mục đích "kết quả tức thì"); quiz không lộ trước. Một học viên có thể tạo attempt mới, cố ý trả lời sai để đọc đáp án — chống việc này (giới hạn tần suất, cooldown theo lesson) nằm ngoài phạm vi task.
16. **Test API cần Docker** (`[Trait("Category", "Integration")]`); khi không có Docker thêm `--filter "Category!=Integration"`. Auth ở test là `TestAuthHandler` (header giả), **chưa chạy với token thật của STS** (cùng hạn chế như TASK-012/013).
17. **Danh sách thẻ đến hạn:** mặc định 20, tối đa 100, chưa có phân trang offset.


---

## 6. Kết Quả Code Review (Claude Code — Reviewer)

- **Kết luận**: `REVIEW_PASSED` — không có 🔴 Blocker, không có 🟡 Major. 3 🔵 Minor. Nhiều quyết định nghiệp vụ cần Gemini/PO chốt (bên dưới) — đều là khoảng trống của đặc tả, không phải lỗi của Coder.
- **Ngày review**: 2026-10-29 — **Lần review**: 1

### Xác minh độc lập
```bash
dotnet build repos/linguabridge/LinguaBridge.sln --no-incremental   # 0 Warning(s), 0 Error(s)
dotnet test  repos/linguabridge/LinguaBridge.sln --no-build          # Unit 366/366, Integration 92/92 PASS (Docker 29.9.2)
dotnet build AppInHouse.sln                                          # 0 Warning(s), 0 Error(s)
```
- **Phạm vi file**: đúng whitelist (`git status`). Đổi `ContentEndpoints` (2 helper `private` → `internal`), sửa `ModuleRegistrationTests` + `UnitTests.csproj`, `ContentApiFixture`: đều nằm trong glob được phép, có lý do → chấp nhận.
- **Acceptance criteria mục 4**: 5/5 có bằng chứng. SM-2 có 58 test (yêu cầu ≥ 20).

### Điểm tốt
- **Không lộ đáp án**: `LearnerQuestionDto`/`LearnerOptionDto` không có trường đáp án; câu gõ chữ còn ẩn `TargetText`/phiên âm (vì đó chính là đáp án). Test khẳng định trên **body JSON thô**, đồng thời so với preview admin để chứng minh dữ liệu thật sự có trên server.
- **Phạm vi theo chủ sở hữu**: mọi truy vấn trong `LearningRepository` đều lọc theo `userId` lấy từ token; người khác nhận 404. Token service (sub không phải Guid) nhận 403 `learner.required`.
- **`Sm2Engine` là hàm thuần** đúng yêu cầu 3.1: hằng số `public const`, làm tròn EF để không trôi số thực, trần 365 ngày.
- **Tiến độ được tính lại từ dữ kiện** (số lesson khác nhau đã hoàn thành) chứ không cộng dồn. Double-click `complete` đã được kiểm chứng an toàn.
- **Chống nhồi lịch SRS** (làm lại bài nhiều lần trong ngày không đẩy interval) và chặn review sớm — có mutation test.
- Module Learning **không tham chiếu Content**: cổng `ILessonSource` do Persistence triển khai.

### Findings
| # | Mức độ | File:dòng | Vấn đề | Đề xuất |
|---|---|---|---|---|
| 1 | 🔵 Minor | `Queries/LearningQueries.cs:53-63` | `GET /progress` đọc `CompletedLessonsCount` **đã lưu**, không tính lại như lúc complete. Hệ quả: (a) hai lesson khác nhau của cùng course được complete đồng thời (hai tab) → cả hai tính `finishedBefore` mà không thấy nhau, ghi đè còn N+1 thay vì N+2; số sai **giữ nguyên** cho tới lần complete sau. (b) Nếu sau này lesson được gỡ publish, số đã hoàn thành bị đếm dư (chỉ bị chặn trên bởi tổng). | Trong query, tính `completed = CountCompletedLessonsAsync(userId, publishedLessonIds)`, chỉ dùng dòng đã lưu cho `LastAccessedAt`. Một truy vấn, sửa cả hai trường hợp. |
| 2 | 🔵 Minor | `Grading/QuestionGrading.cs:44-46` | Option trả cho học viên theo `OrderIndex` cố định. Nếu người soạn quen đặt đáp án đúng ở đầu, **vị trí** làm lộ đáp án. | Trộn thứ tự option ở server (ví dụ: tất định theo `attemptId`), hoặc ghi thành quy tắc soạn nội dung. Có thể để backlog. |
| 3 | 🔵 Minor | `Entities/ExerciseAttempt.cs` (`Complete`, code cũ), `Entities/UserCourseProgress.cs:40` | Handler dùng `IDateTimeProvider`, nhưng entity tự lấy `DateTimeOffset.UtcNow` cho `CompletedAt`/`DurationSeconds`/`LastAccessedAt`. Hai nguồn thời gian; test không điều khiển được các trường này. | Truyền thời điểm vào method của entity. Backlog. |

### Cần Gemini/PO quyết định
1. **Diễn giải SM-2** (5.c.1–2): Again giảm EF 0.2; Hard không tăng `Repetitions`; Easy lần đầu 2 ngày; Good lần hai `max(6, interval)`; ngưỡng `Review` (reps ≥ 2), `Graduated` (≥ 21 ngày), trần 365 ngày. Reviewer đã kiểm: hợp lý, nhất quán, có test cho từng nhánh. Cần chốt thành đặc tả chính thức.
2. **"Hôm nay" = ngày UTC** (5.c.3): học viên Việt Nam sẽ thấy thẻ đến hạn đổi ngày lúc 07:00. Khuyến nghị: lưu múi giờ học viên trước khi ra mắt, vì đổi sau sẽ làm lệch lịch của các thẻ đã có.
3. **Ánh xạ câu trả lời → rating** (đúng = Good, sai = Again), và lệch có chủ đích "đúng trên thẻ chưa đến hạn thì không đổi lịch" (5.c.4–5): Reviewer đồng ý với lý do chống nhồi lịch.
4. **Điểm đạt và XP** (5.c.8): hoàn thành với 0% vẫn tăng tiến độ; XP chỉ cho lần hoàn thành đầu. Lưu ý lỗ hổng 5.c.15: bỏ dở attempt 1 sau khi cố ý trả lời sai để xem đáp án, rồi làm attempt 2 → được **toàn bộ XP** "lần đầu". Nếu XP có giá trị (bảng xếp hạng, phần thưởng), nên tính XP trên attempt đầu tiên **đã bắt đầu** chứ không phải attempt đầu tiên hoàn thành.
5. **Khoảng trống mô hình Content** mà Reviewer phát hiện thêm:
   - **FillBlank gõ chữ không làm được**: không có trường cho "câu có chỗ trống" tách khỏi đáp án. `TargetText` (đáp án) bị ẩn đúng, nên học viên chỉ thấy `PromptTranslation` — thực chất thành bài dịch.
   - **Thẻ SRS sinh từ câu MultipleChoice** chỉ có mặt trước (`TargetText` + prompt), `DueCardDto` không có mặt sau (đáp án), nên học viên không tự chấm được. Gốc rễ là `VocabularyId = QuestionId` tạm thời (5.c.4) — cần bảng từ vựng thật.
   - Đáp án thay thế cho câu dịch (5.c.7b).
6. **Ranh giới module** (5.c.10): `ILessonSource` do Learning khai báo, Persistence đọc thẳng schema `content`. Chấp nhận cho modular monolith; nếu muốn hợp đồng liên module nằm ở `Application` thì cần task riêng.
7. **Migration unique index `(attempt_id, question_id)`** (5.c.12): chặn hẳn trả lời trùng song song. Khuyến nghị làm cùng task concurrency của TASK-013 (finding #1 bên đó).
8. **Dọn attempt bỏ dở** (5.c.13): backlog.
9. **Phụ thuộc còn mở từ TASK-012**: chưa thử với token thật từ STS. Với task này điều đó quan trọng hơn, vì `userId` của mọi dữ liệu học tập là claim `sub` thật.


---

## 7. Quyết Định Kiến Trúc & Nghiệm Thu Cuối Cùng (Gemini Tech Lead)

- **Kết luận**: **`APPROVED`**
- **Đánh giá chuyên môn**:
  - Triển khai thuật toán SM-2 rất mẫu mực: viết dưới dạng hàm thuần (pure domain function), xử lý trôi số thực, trần 365 ngày và có 58 unit tests chứng minh độ tin cậy.
  - Bảo mật tốt: DTO học viên giấu hoàn toàn `isCorrect` và `TargetText` (cho câu gõ chữ), kiểm tra trên raw JSON; mọi query đều lọc nghiêm ngặt theo claim `sub` của user.
  - Logic tính lại tiến độ từ dữ liệu thật giúp chống lỗi đếm dư khi double-click.

### Quyết định chính thức của Tech Lead về các phát hiện của Reviewer:

1. **Về Diễn giải Thuật toán SM-2 (Mục 5.c.1-2)**:
   - **Phê duyệt chính thức**: Toàn bộ cách diễn giải và công thức mà Coder triển khai được công nhận là chuẩn của hệ thống LinguaBridge:
     - `Again`: Giảm EaseFactor 0.2 (min 1.3), `Repetitions = 0`, `IntervalDays = 1`.
     - `Hard`: Giảm EaseFactor 0.15 (min 1.3), `IntervalDays *= 1.2`, không tăng `Repetitions`.
     - `Good`: Giữ nguyên EaseFactor; lần 1 = 1 ngày, lần 2 = `max(6, interval hiện tại)`, lần sau = `interval * EaseFactor`.
     - `Easy`: Tăng EaseFactor 0.15; lần đầu = 2 ngày (đảm bảo dài hơn Good), lần sau = `interval * EaseFactor * 1.3`.
     - Ngưỡng trạng thái: `Review` khi `Repetitions >= 2`, `Graduated` khi `IntervalDays >= 21`, trần `365 ngày`.
     - Quy tắc chống nhồi lịch: Trả lời đúng trên thẻ chưa đến hạn thì không đổi lịch lặp lại; trả lời sai luôn tính là lapse.
2. **Về "Hôm nay" và TimeZone (Mục 5.c.3)**:
   - **Quyết định**: Hiện tại MVP giữ UTC. Trong task phát triển User Profile / Preferences tiếp theo, sẽ lưu `TimeZoneId` của học viên (mặc định `"Asia/Ho_Chi_Minh"`) để tính toán `NextReviewDate` theo ngày địa phương.
3. **Về Điểm Đạt và Chống Cày XP (Mục 5.c.8, 5.c.15)**:
   - **Quy tắc nghiệp vụ**: 
     - **Ngưỡng đạt (Passing Score)**: Học viên phải đạt tối thiểu **60% Score** (`PassingScore = 60`) mới được tính là hoàn thành bài và mở tiến độ bài tiếp theo.
     - **Chống cày XP**: XP bài học chỉ được cộng duy nhất cho **lần hoàn thành đạt điểm (Pass) đầu tiên**. Các lần học lại chỉ để ôn tập kiến thức, không cộng thêm XP bài học.
4. **Về Mô hình Content (FillBlank & Thẻ SRS)**:
   - Ở giai đoạn Phase 1 MVP, việc liên kết thẻ SRS qua `QuestionId` là chấp nhận được để hoàn thành chu trình học - ôn.
   - Ở Phase 2 (Content Expansion), sẽ bổ sung thực thể độc lập `Vocabulary` (gồm Term, Pronunciation, Meaning, Example, Audio) để xây dựng hệ thống flashcard 2 mặt chuyên biệt cho ôn tập từ vựng.
5. **Về Ranh giới Module & Concurrency Task**:
   - Cổng `ILessonSource` do Learning định nghĩa và Persistence triển khai là đúng mẫu kiến trúc Modular Monolith.
   - Sẽ gộp việc bổ sung unique index `(attempt_id, question_id)` cùng với task Concurrency Control sau Phase 1.
