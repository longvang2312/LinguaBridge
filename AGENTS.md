# Multi-Agent Workflow: AppInHouse

Tài liệu này xác định vai trò, nguyên tắc làm việc và quy trình phối hợp giữa các Agent trong repository **AppInHouse**.

---

## 1. Phân Công Vai Trò

| Agent / Thực thể | Vai Trò Chính | Trách Nhiệm Cụ Thể |
| :--- | :--- | :--- |
| **User (Bạn)** | Project Owner / Dispatcher | Định hướng yêu cầu tổng quan, làm cầu nối trung chuyển trạng thái và thông báo kết quả giữa các Agent session. |
| **Gemini** | Orchestrator / Tech Lead | Lập kiến trúc, phân rã công việc thành task nguyên tử, chỉ định phạm vi file, review code & diff, nghiệm thu kỹ thuật. |
| **Claude Code** | Coder / Implementer | Đọc task từ `docs/handoffs/`, viết code và unit test đúng phạm vi file được giao, chạy test và báo cáo kết quả. |

---

## 2. Nguyên Tắc Phối Hợp & Khóa File (File Locking)

1. **Tuyệt đối không sửa file đồng thời**:
   - Khi một task đang mở cho Claude Code (`IN_PROGRESS`), Gemini **không được phép** sửa bất kỳ file nào trong danh sách được cấp phép của task đó.
   - Claude Code **chỉ được phép** tạo/sửa các file được liệt kê trong mục `File Scope` của task handoff.
2. **Quyền sở hữu tài liệu**:
   - Gemini phụ trách và có toàn quyền cập nhật: `AGENTS.md`, `docs/architecture/*`, và tạo mới các file bàn giao tại `docs/handoffs/TASK-*.md`.
   - Claude Code phụ trách cập nhật source code tại `src/` (theo task) và điền phần Báo cáo nghiệm thu tại phiếu task handoff.
3. **Tiêu chuẩn nghiệm thu (Definition of Done)**:
   - Code phải có unit test hoặc script kiểm thử đi kèm.
   - Code chạy không phát sinh lỗi cú pháp hay linter warning.
   - Claude Code cung cấp lệnh kiểm thử và output thực tế.
   - Gemini Tech Lead review diff và đưa ra kết luận: `APPROVED` hoặc `CHANGES_REQUESTED`.

---

## 3. Quy Trình Vòng Lặp Task (Handoff Loop)

```
[1. Gemini] Tạo task tại docs/handoffs/TASK-xxx-<name>.md
     │
     ▼
[2. User] Thông báo Claude Code: "Thực hiện task tại docs/handoffs/TASK-xxx-<name>.md"
     │
     ▼
[3. Claude Code] Đọc task -> Viết code -> Chạy test -> Báo cáo vào file handoff
     │
     ▼
[4. User] Cung cấp kết quả từ Claude Code cho Gemini
     │
     ▼
[5. Gemini] Review git diff, kiến trúc, kiểm tra lỗi -> Nghiệm thu (APPROVED) hoặc Yêu cầu sửa đổi (CHANGES_REQUESTED)
```
