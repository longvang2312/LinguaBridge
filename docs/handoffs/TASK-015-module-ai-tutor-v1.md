# TASK-015: Module AI Tutor V1 — Text Tutor, Structured JSON Prompt & Token Usage Ledger

- **Mã Task**: TASK-015
- **Người Giao (Tech Lead)**: Gemini
- **Người Thực Hiện (Implementer)**: Claude Code
- **Trạng Thái**: `PENDING`
- **Ngày Tạo**: 2026-10-28
- **Phụ Thuộc**: TASK-012 (Auth đã có), TASK-014 (Learning Progress để lấy trình độ người dùng)
- **Tài Liệu Tham Chiếu**: [docs/architecture/language_learning_platform_design.md](../architecture/language_learning_platform_design.md) (Mục 4.1, AI V1 Text Tutor)

---

## 1. Mục Tiêu Kỹ Thuật

Xây dựng **AI Tutor V1 — Text Mode** (Mốc 2 theo Roadmap):
1. **Prompt Orchestrator**: Xây dựng prompt ngữ cảnh theo trình độ người học (CEFR/HSK), kịch bản (`ScenarioCode`), và `TargetLanguage` (Anh/Trung).
2. **Structured JSON Output**: Ràng buộc model trả về JSON `{reply_text, pinyin_or_ipa, translation, grammar_tips}`.
3. **LLM Provider Abstraction**: Interface `ILlmProvider` với implementation cho `OpenAI` (gpt-4o-mini). Fallback provider `AzureOpenAI` (optional, cấu hình qua `appsettings`).
4. **Token Usage Ledger**: Ghi mọi request AI vào `ai.ai_usage_ledger` (model, token count, estimated cost USD).
5. **Daily Quota Enforcement**: Trước mỗi request AI, kiểm tra `UserId` đã dùng bao nhiêu token hôm nay. Nếu vượt `AiDailyTokenQuota` của plan → trả 429 với message `"AI quota exhausted. Upgrade to VIP or wait until tomorrow."`.
6. **Chat Session Management**: Tạo/đóng `ChatSession`, lưu `ChatMessage` (user + assistant).

---

## 2. Phạm Vi File Cấp Phép (File Scope & Whitelist)

- [ ] `repos/linguabridge/src/Modules/LinguaBridge.Modules.AiTutor/*` (Use Cases, Prompt builder, Quota checker)
- [ ] `repos/linguabridge/src/Infrastructure/LinguaBridge.Infrastructure.External/*` (LLM provider clients)
- [ ] `repos/linguabridge/src/Infrastructure/LinguaBridge.Infrastructure.Persistence/*` (Queries cho Ledger, ChatSession)
- [ ] `repos/linguabridge/src/Presentation/LinguaBridge.Api/*` (AI Tutor endpoints)
- [ ] `repos/linguabridge/src/Presentation/LinguaBridge.Api/appsettings.json`
- [ ] `repos/linguabridge/src/Presentation/LinguaBridge.Api/appsettings.Development.json`
- [ ] `repos/linguabridge/tests/LinguaBridge.UnitTests/*`
- [ ] `repos/linguabridge/tests/LinguaBridge.IntegrationTests/*`

---

## 3. Chi Tiết Yêu Cầu Kỹ Thuật

### 3.1. `ILlmProvider` Abstraction

```csharp
public interface ILlmProvider
{
    Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken ct = default);
}

public record LlmRequest(string SystemPrompt, string UserMessage, string ModelId);
public record LlmResponse(string ReplyText, int PromptTokens, int CompletionTokens, decimal EstimatedCostUsd);
```

Triển khai `OpenAiLlmProvider` trong `LinguaBridge.Infrastructure.External`:
- Dùng `HttpClient` (factory) để gọi OpenAI Chat Completions API.
- Parse JSON response có `pinyin_or_ipa`, `translation`, `grammar_tips`.
- Cấu hình API key từ `appsettings`: `"OpenAI:ApiKey"`, `"OpenAI:Model"` (default: `gpt-4o-mini`).

### 3.2. Prompt Builder

Trong `LinguaBridge.Modules.AiTutor`, tạo `AiTutorPromptBuilder`:
- System prompt phân nhánh theo `TargetLanguage`:
  - Tiếng Anh: "You are an English tutor. The learner is at CEFR {level}. Reply only with vocabulary from that level."
  - Tiếng Trung: "你是一位中文老师。学习者的水平是 HSK {level}。所有汉字请附带拼音。"
- Thêm kịch bản: `JobInterview`, `BusinessEmail`, `FreeTalk`.
- System prompt cuối cùng **bắt buộc** model trả JSON schema `{reply_text, pinyin_or_ipa, translation, grammar_tips}`.

### 3.3. Token Quota Check

Trước mỗi AI request, query `ai.ai_usage_ledger` để tính tổng token của user trong ngày:
```sql
SELECT SUM(total_tokens) FROM ai.ai_usage_ledger
WHERE user_id = @userId AND created_at >= CURRENT_DATE;
```
- Nếu tổng + token ước tính của request mới > quota → throw `AiQuotaExceededException` → API trả 429.
- Quota mặc định Freemium: **10 lượt / ngày** (cấu hình qua `appsettings`).
- Quota VIP: lấy từ `billing.user_entitlements` → `billing.subscription_plans.ai_daily_token_quota`.

### 3.4. API Endpoints

| Method | Path | Mô tả |
|---|---|---|
| `POST` | `/api/ai/sessions` | Tạo ChatSession mới |
| `POST` | `/api/ai/sessions/{id}/messages` | Gửi tin nhắn → nhận phản hồi AI |
| `GET` | `/api/ai/sessions/{id}/messages` | Lịch sử hội thoại |
| `DELETE` | `/api/ai/sessions/{id}` | Kết thúc session |
| `GET` | `/api/ai/quota` | Quota còn lại hôm nay |

### 3.5. Structured JSON Parse & Graceful Fallback

- Nếu model trả JSON hợp lệ → map sang `AiTutorResponse`.
- Nếu model trả text thường (không phải JSON) → fallback: dùng full text làm `reply_text`, để trống các field còn lại. Log warning.

---

## 4. Tiêu Chí Nghiệm Thu (Acceptance Criteria)

- [ ] `ILlmProvider` được mock trong unit test — không gọi API thật.
- [ ] Quota enforcement: vượt quá limit → 429, đúng limit → 200.
- [ ] `AiUsageLedger` được ghi sau mỗi request thành công.
- [ ] Prompt Builder tạo đúng system prompt theo `TargetLanguage` + `ScenarioCode` + trình độ.
- [ ] Test: gọi cùng một prompt 11 lần với user Freemium → lần thứ 11 bị chặn.
- [ ] Tất cả tests PASS, 0 regression.

---

## 5. Báo Cáo Triển Khai (Claude Code điền khi hoàn thành)

### a. Danh sách file đã tác động
### b. Kiểm thử thực tế
### c. Rủi ro / lưu ý cho Tech Lead

