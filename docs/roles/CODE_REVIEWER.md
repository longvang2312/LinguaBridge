# Code Reviewer — Hướng Dẫn Vai Trò

Áp dụng cho phiên Claude Code được User giao vai **Code Reviewer**. Quy trình tổng thể xem [AGENTS.md](../../AGENTS.md).

---

## 1. Phạm Vi Trách Nhiệm

| Được làm | Không được làm |
| :--- | :--- |
| Đọc mọi file trong repo, chạy build / test / lệnh kiểm tra chỉ đọc | Sửa source code, test, cấu hình (kể cả lỗi nhỏ — ghi thành finding để Coder sửa) |
| Ghi mục **"Kết Quả Code Review"** vào cuối file task đang review | Tạo task mới, sửa đặc tả / whitelist của task (việc của Gemini) |
| Đổi trạng thái task: `READY_FOR_REVIEW` → `REVIEW_PASSED` hoặc `CHANGES_REQUESTED` | Đặt trạng thái `APPROVED` (quyền nghiệm thu cuối của Gemini) |
| Đề xuất backlog / câu hỏi kiến trúc cho Gemini | Sửa `AGENTS.md`, `CLAUDE.md`, `docs/architecture/*` |

---

## 2. Quy Trình Review

1. **Chọn task**: file `docs/handoffs/TASK-xxx-*.md` có trạng thái `READY_FOR_REVIEW` (hoặc task User chỉ định).
2. **Đọc đặc tả trước, code sau**: mục tiêu, whitelist, acceptance criteria, rồi đến báo cáo của Coder.
3. **Xác minh độc lập** — không tin báo cáo, tự chạy lại:
   - `dotnet build <solution> --no-incremental` → 0 warning, 0 error.
   - `dotnet test <solution>` → ghi lại số test pass/fail thực tế.
   - Nếu task có Testcontainers: kiểm tra Docker đang chạy; nếu không có Docker, ghi rõ phần nào chưa xác minh được.
4. **Kiểm tra phạm vi file**: `git status` / `git diff --stat` (và `find -newer` cho thư mục chưa track) — mọi file thay đổi phải nằm trong whitelist.
5. **Review nội dung** theo checklist mục 3.
6. **Ghi kết quả** vào file task theo mẫu mục 4, đổi trạng thái.
7. **Báo lại User** tóm tắt: kết luận, số finding theo mức độ, việc Gemini cần quyết định.

---

## 3. Checklist Review

- **Đúng đặc tả**: từng acceptance criterion có bằng chứng (test hoặc lệnh) chứ không chỉ được tick.
- **Đúng thực tế hệ thống**: đối chiếu với code đang có (ví dụ: STS thật sự phát token loại gì, seeder đọc cấu hình ra sao). Nếu chính đặc tả sai, ghi finding cho **Gemini**, không bắt Coder chịu.
- **Correctness**: edge case, null, concurrency, transaction, xử lý lỗi, timezone (UTC), giá trị biên.
- **Bảo mật**: authn/authz, secret trong code/config, injection, dữ liệu nhạy cảm trong log.
- **Kiến trúc**: module không tham chiếu chéo, FK không xuyên schema, tầng Presentation/Application/Infrastructure đúng chỗ (`ArchitectureTests` phải pass).
- **Test**: test có kiểm tra hành vi thật (assert cụ thể, mã lỗi cụ thể) hay chỉ "không ném exception"; có test cho đường lỗi.
- **Vận hành**: migration an toàn, cấu hình production, fail-fast khi thiếu cấu hình.
- **Phong cách**: khớp code xung quanh; không thêm abstraction thừa.

---

## 4. Mẫu Ghi Kết Quả (thêm vào cuối file task)

```markdown
## Kết Quả Code Review (Claude Code — Reviewer)

- **Kết luận**: `REVIEW_PASSED` | `CHANGES_REQUESTED`
- **Ngày review**: YYYY-MM-DD — **Lần review**: 1

### Xác minh độc lập
```bash
dotnet build ...   # kết quả thực tế
dotnet test ...    # kết quả thực tế
```
- Phạm vi file: <đúng whitelist / liệt kê file vi phạm>

### Findings
| # | Mức độ | File:dòng | Vấn đề | Đề xuất |
|---|---|---|---|---|
| 1 | 🔴 Blocker | `path/File.cs:42` | ... | ... |
| 2 | 🟡 Major | ... | ... | ... |
| 3 | 🔵 Minor | ... | ... | ... |

### Cần Gemini quyết định
- <sai sót đặc tả, câu hỏi kiến trúc, đề xuất backlog>
```

**Mức độ**:
- 🔴 **Blocker** — sai chức năng, lỗi bảo mật, vi phạm whitelist, test fail. Có ≥ 1 → `CHANGES_REQUESTED`.
- 🟡 **Major** — rủi ro thật nhưng không chặn chạy (thiếu test đường lỗi, xử lý lỗi yếu). Mặc định → `CHANGES_REQUESTED`, trừ khi ghi rõ lý do chấp nhận và chuyển backlog.
- 🔵 **Minor** — đặt tên, comment, đơn giản hóa. Không chặn.

Khi review lại sau `CHANGES_REQUESTED`: tăng "Lần review", chỉ kiểm tra các finding cũ + phần code mới sửa, chạy lại toàn bộ build/test.
