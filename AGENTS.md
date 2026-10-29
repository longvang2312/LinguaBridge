# Multi-Agent Workflow: AppInHouse

Tài liệu này xác định vai trò, nguyên tắc làm việc và quy trình phối hợp giữa các Agent trong repository **AppInHouse**.

---

## 1. Phân Công Vai Trò

| Agent / Thực thể | Vai Trò Chính | Trách Nhiệm Cụ Thể |
| :--- | :--- | :--- |
| **User (Bạn)** | Project Owner / Dispatcher | Định hướng yêu cầu tổng quan, làm cầu nối trung chuyển trạng thái và thông báo kết quả giữa các Agent session. |
| **Gemini** | Orchestrator / Tech Lead | Lập kiến trúc, phân rã công việc thành task nguyên tử, chỉ định phạm vi file, quyết định các vấn đề kiến trúc do Reviewer nêu, nghiệm thu cuối (`APPROVED`). |
| **Claude Code (Coder)** | Coder / Implementer | Đọc task từ `docs/handoffs/`, viết code và unit test đúng phạm vi file được giao, chạy test và báo cáo kết quả. |
| **Claude Code (Reviewer)** | Code Reviewer | Phiên Claude Code riêng, **chỉ đọc**: tự chạy lại build/test, kiểm tra phạm vi file, review correctness/bảo mật/kiến trúc, ghi findings vào file task. Hướng dẫn chi tiết: [docs/roles/CODE_REVIEWER.md](docs/roles/CODE_REVIEWER.md). |

> Coder và Reviewer **phải là hai phiên khác nhau** — không phiên nào tự review code do chính mình viết.

---

## 2. Nguyên Tắc Phối Hợp & Khóa File (File Locking)

1. **Tuyệt đối không sửa file đồng thời**:
   - Khi một task đang mở cho Claude Code (`IN_PROGRESS`), Gemini **không được phép** sửa bất kỳ file nào trong danh sách được cấp phép của task đó.
   - Claude Code (Coder) **chỉ được phép** tạo/sửa các file được liệt kê trong mục `File Scope` của task handoff.
   - Reviewer **không sửa** bất kỳ file code/test/cấu hình nào; chỉ ghi mục "Kết Quả Code Review" trong file task đang review.
2. **Quyền sở hữu tài liệu**:
   - Gemini phụ trách và có toàn quyền cập nhật: `AGENTS.md`, `docs/architecture/*`, `docs/roles/*`, và tạo mới các file bàn giao tại `docs/handoffs/TASK-*.md`.
   - Claude Code (Coder) phụ trách cập nhật source code tại `src/` (theo task) và điền phần Báo cáo triển khai tại phiếu task handoff.
   - Claude Code (Reviewer) phụ trách mục "Kết Quả Code Review" tại phiếu task handoff.
3. **Vòng đời trạng thái task**:
   `PENDING` → `IN_PROGRESS` → `READY_FOR_REVIEW` → (`CHANGES_REQUESTED` → `READY_FOR_REVIEW` …) → `REVIEW_PASSED` → `APPROVED`
   - Coder đặt `IN_PROGRESS`, `READY_FOR_REVIEW`.
   - Reviewer đặt `REVIEW_PASSED` hoặc `CHANGES_REQUESTED`.
   - Chỉ Gemini đặt `APPROVED` (hoặc trả về `CHANGES_REQUESTED` kèm lý do).
4. **Tiêu chuẩn nghiệm thu (Definition of Done)**:
   - Code phải có unit test hoặc script kiểm thử đi kèm.
   - Code chạy không phát sinh lỗi cú pháp hay linter warning.
   - Coder cung cấp lệnh kiểm thử và output thực tế.
   - Reviewer tự chạy lại build/test, không còn finding 🔴 Blocker nào mở.
   - Gemini Tech Lead xem kết quả review và đưa ra kết luận cuối: `APPROVED` hoặc `CHANGES_REQUESTED`.

---

## 3. Quy Trình Vòng Lặp Task (Handoff Loop)

```
[1. Gemini] Tạo task tại docs/handoffs/TASK-xxx-<name>.md
     │
     ▼
[2. User] Thông báo Claude Code: "Thực hiện task tại docs/handoffs/TASK-xxx-<name>.md"
     │
     ▼
[3. Claude Code – Coder] Đọc task -> Viết code -> Chạy test -> Báo cáo vào file handoff -> READY_FOR_REVIEW
     │
     ▼
[4. User] Thông báo Reviewer: "Review task tại docs/handoffs/TASK-xxx-<name>.md"
     │
     ▼
[5. Claude Code – Reviewer] Chạy lại build/test, kiểm tra phạm vi file, review code -> Ghi findings
     │                                     │
     │ REVIEW_PASSED                       │ CHANGES_REQUESTED
     │                                     └──► quay lại [3] (Coder sửa theo findings)
     ▼
[6. User] Cung cấp kết quả review cho Gemini
     │
     ▼
[7. Gemini] Xem review + quyết định các mục "Cần Gemini quyết định" -> APPROVED hoặc CHANGES_REQUESTED
```
