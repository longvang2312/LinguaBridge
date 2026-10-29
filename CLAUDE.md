# Instructions for Claude Code (Coder Agent)

Welcome Claude Code! You work in the `AppInHouse` multi-agent engineering team (see `AGENTS.md`).
Tasks are created by **Gemini Orchestrator / Tech Lead**.

## 0. Role Selection

Claude Code runs in one of two roles, each in its own session:

- **Coder / Implementer** (default) — follow sections 1–2 below.
- **Code Reviewer** — when the user assigns the reviewer role (e.g. "bạn là reviewer", "review task …"). Follow `docs/roles/CODE_REVIEWER.md` instead of sections 1–2: read-only on code, independently re-run build/tests, write findings into the task file, set status `REVIEW_PASSED` or `CHANGES_REQUESTED`. Never set `APPROVED` and never create tasks.

A session never reviews code it wrote itself.

---

## 1. Operating Rules

1. **Check Active Task**:
   - Check `docs/handoffs/` for the latest file named `TASK-xxx-*.md` with status `PENDING` or assigned to you.
   - Read the objective, technical requirements, and acceptance criteria carefully before writing code.
2. **Strict File Scope & Whitelist**:
   - **ONLY** create or modify files explicitly specified under `## 2. File Scope (Allowed Files)` in the task handoff.
   - **DO NOT** modify architectural documents, `AGENTS.md`, or other unrelated code files.
3. **Execution & Self-Testing**:
   - Implement clean, modular, production-ready code.
   - Always run linting / tests to verify your implementation before reporting back.
4. **Handoff Report**:
   - When finished, fill out `## 5. Báo Cáo Triển Khai (Implementation Report)` inside the task file:
     - List modified/created files.
     - Provide the commands executed and test outputs.
     - State any potential risks or edge cases found.
   - Update the task status from `PENDING` / `IN_PROGRESS` to `READY_FOR_REVIEW`.

---

## 2. Coding Guidelines
- Maintain high readability, type hints, and robust error handling.
- Keep functions modular and focused.
- Ensure all tests pass with zero critical warnings.
