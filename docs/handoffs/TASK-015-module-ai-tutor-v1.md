# TASK-015: Module AI Tutor V1 — Text Tutor, Structured JSON Prompt & Token Usage Ledger

- **Mã Task**: TASK-015
- **Người Giao (Tech Lead)**: Gemini
- **Người Thực Hiện (Implementer)**: Claude Code
- **Trạng Thái**: `APPROVED`
- **Ngày Tạo**: 2026-10-28
- **Ngày Hoàn Thành**: 2026-10-29
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

### 3.4. API Endpoints (Quy ước tiền tố `/api/v1/ai/...`)

| Method | Path | Mô tả |
|---|---|---|
| `POST` | `/api/v1/ai/sessions` | Tạo ChatSession mới |
| `POST` | `/api/v1/ai/sessions/{id}/messages` | Gửi tin nhắn → nhận phản hồi AI |
| `GET` | `/api/v1/ai/sessions/{id}/messages` | Lịch sử hội thoại của session |
| `DELETE` | `/api/v1/ai/sessions/{id}` | Kết thúc session |
| `GET` | `/api/v1/ai/quota` | Quota token còn lại hôm nay |

> **Lưu ý về Entitlement**: Vì Module Billing sẽ hoàn thiện ở TASK-016, Module AiTutor khai báo port/abstraction để lấy hạn mức của user (ví dụ `IUserEntitlementSource`). Nếu user chưa có bản ghi trong bảng `billing.user_entitlements`, mặc định áp dụng gói **Freemium** (10 lượt tương tác / ngày).

### 3.5. Structured JSON Parse & Graceful Fallback

- Nếu model trả JSON hợp lệ → map sang `AiTutorResponse`.
- Nếu model trả text thường (không phải JSON) → fallback: dùng full text làm `reply_text`, để trống các field còn lại. Log warning.

---

## 4. Tiêu Chí Nghiệm Thu (Acceptance Criteria)

- [x] `ILlmProvider` được mock trong unit test — không gọi API thật.
- [x] Quota enforcement: vượt quá limit → 429, đúng limit → 200 (đã giải quyết triệt để race condition bằng cơ chế giữ chỗ trước và `pg_advisory_xact_lock`).
- [x] `AiUsageLedger` được ghi sau mỗi request thành công (lưu cùng `CancellationToken.None` để không bị thất thoát chi phí khi client ngắt kết nối).
- [x] Prompt Builder tạo đúng system prompt theo `TargetLanguage` + `ScenarioCode` + trình độ.
- [x] Test: gọi cùng một prompt 11 lần với user Freemium → lần thứ 11 bị chặn (kể cả khi gửi song song 30 requests).
- [x] Tất cả tests PASS, 0 regression (473 unit tests, 121 integration tests PASS — tổng cộng 594 tests).

---

## 5. Báo Cáo Triển Khai (Claude Code điền khi hoàn thành)

### a. Danh sách file đã tác động

Tất cả nằm trong whitelist; `repos/identity`, `AGENTS.md`, `CLAUDE.md`, `docs/architecture/*` không bị chạm. Không có migration (bảng `ai.*` đã có từ TASK-010/011). Không commit.

**Module AiTutor** (`src/Modules/LinguaBridge.Modules.AiTutor/`)
- `Abstractions/Llm.cs` — `ILlmProvider`, `LlmRequest`, `LlmResponse`, `LlmTurn`, `LlmProviderException`, `AiQuotaExceededException`.
- `Abstractions/Ports.cs` — `IAiTutorRepository`, `IUserEntitlementSource` (port lấy hạn mức, thay cho `IEntitlementService` của TASK-016), `ILearnerLevelSource`; `Abstractions/Dtos.cs`.
- `Prompting/AiTutorPromptBuilder.cs` (+ `TutorScenarios`), `Responses/AiTutorResponseParser.cs`, `Quota/AiQuotaPolicy.cs`, `AiTutorOptions.cs`.
- `Commands/` (`CreateSession`, `EndSession`, `SendMessage`, `CommandSupport`), `Queries/TutorQueries.cs` (`GetMessages`, `GetQuota`), `AiTutorModule.cs` (đăng ký), `.csproj` (+FluentValidation 12, +Logging.Abstractions).

**Infrastructure.External** — `Llm/ChatCompletionsProvider.cs` (base dùng chung wire format), `OpenAiLlmProvider.cs`, `AzureOpenAiLlmProvider.cs`, `FallbackLlmProvider.cs`, `LlmOptions.cs`; `DependencyInjection.cs` (HttpClient factory + chọn provider); `.csproj` (+Microsoft.Extensions.Http, +ProjectReference AiTutor).

**Infrastructure.Persistence** — `AiTutor/AiTutorRepository.cs`, `AiTutor/UserEntitlementSource.cs` (đọc `billing.user_entitlements` ⨝ `subscription_plans`), `AiTutor/LearnerLevelSource.cs` (đọc `learning.user_course_progress` ⨝ `content.courses`); `DependencyInjection.cs`.

**Api** — `Endpoints/AiTutorEndpoints.cs`; `Program.cs` (`MapAiTutorEndpoints`); `Endpoints/LearningEndpoints.cs` (chỉ đổi `RequireLearner` từ `private` sang `internal` để dùng lại); `appsettings.json` (mục `OpenAI`, `AzureOpenAI`, `AiTutor`; **không** có key thật). `appsettings.Development.json` không cần đổi.

**Tests** — `tests/LinguaBridge.UnitTests/AiTutor/{FakeAiTutor, PromptAndParserTests, QuotaTests, HandlerTests, LlmProviderTests}.cs`; `tests/LinguaBridge.IntegrationTests/AiTutorApiTests.cs`; `ContentApiFixture.cs` (thay `ILlmProvider` bằng `FakeLlm` để không test nào gọi OpenAI thật).

### b. Kiểm thử thực tế

Lệnh (từ `repos/linguabridge`, Docker Desktop đang chạy):
```
dotnet build                       -> 0 Warning(s), 0 Error(s)
dotnet test                        -> Unit: Passed 469/469 | Integration: Passed 118/118
dotnet test --filter "FullyQualifiedName~AiTutor"  -> Unit 103/103 | Integration 26/26
```
Trước task: 366 + 92 = 458; sau: 469 + 118 = 587 (+129 test mới, 0 regression; `ArchitectureTests` vẫn pass — AiTutor chỉ tham chiếu Domain + Application).

Đối chiếu tiêu chí nghiệm thu:
- **`ILlmProvider` mock, không gọi API thật**: unit dùng `ScriptedLlm` + `StubHandler` (HttpMessageHandler giả); integration thay provider bằng `FakeLlm`.
- **Quota 429 / 200**: `A_free_learner_gets_ten_requests_and_the_eleventh_is_blocked` (unit) và `A_free_learner_can_send_ten_messages_and_the_eleventh_is_a_429` (API, PostgreSQL thật): cùng một prompt gọi 11 lần → lần 11 trả **429**, `detail` đúng nguyên văn `"AI quota exhausted. Upgrade to VIP or wait until tomorrow."`, `code=ai.quota_exhausted`, header `Retry-After`, model không bị gọi, DB còn đúng 10 dòng ledger + 20 message. Đúng ngưỡng (lần 10) → 200.
- **Ledger ghi sau mỗi request thành công**: test đọc `ai.ai_usage_ledger` trực tiếp (prompt/completion/total tokens, cost, `OpenAI_gpt-4o-mini`, task `TextTutor`). Request lỗi (provider down / reply rỗng) → không ghi ledger, không lưu message, không tốn lượt.
- **Prompt Builder**: test câu system prompt đúng nguyên văn Anh/Trung theo spec (CEFR B1; HSK 1–6 đổi từ enum 101–106), 3 scenario, 4 field JSON, ngôn ngữ translation, level sai ngôn ngữ / scenario lạ bị từ chối.
- **Structured JSON + fallback**: parser test JSON hợp lệ, fence ```json, `grammar_tips` dạng mảng, field rỗng → null, text thường / JSON hỏng / thiếu `reply_text` → dùng nguyên text làm reply (`isStructured=false`, có log warning).
- **VIP**: hạn mức token lấy từ `billing.subscription_plans.ai_daily_token_quota`; VIP hết hạn quay về Free (test với dữ liệu DB thật).

Kiểm tra "test có răng" (phá code cố ý rồi khôi phục, chạy lại toàn bộ AiTutor tests):
| Phá | Kết quả |
|---|---|
| `<` → `<=` ở `AiQuotaPolicy` (ngưỡng free) | 4 unit + 2 integration fail |
| Bỏ `repository.Add(ledger)` | 8 unit + 5 integration fail |
| Bỏ `&& s.UserId == userId` ở `GetSessionAsync` | 1 integration fail (404 cho session người khác) |
| Bỏ kiểm tra `ExpiresAt` ở `UserEntitlementSource` | 1 integration fail (VIP hết hạn) |

Đã khôi phục cả bốn; chạy lại toàn bộ: xanh. (Lưu ý phương pháp: lần đầu build cache dùng lại output cũ nên kết quả bị lẫn; đã `touch` file và chạy lại — số liệu trên là của lần chạy sạch.)

Cấu hình (khoá appsettings): `OpenAI:ApiKey`, `OpenAI:Model` (mặc định `gpt-4o-mini`), `OpenAI:BaseUrl`, `OpenAI:MaxOutputTokens` (600), `OpenAI:TimeoutSeconds` (30), `OpenAI:InputUsdPerMillion` (0.15) / `OutputUsdPerMillion` (0.60); `AzureOpenAI:{Endpoint,ApiKey,Deployment,ApiVersion}` (chỉ bật fallback khi đủ 3 giá trị); `AiTutor:{FreeDailyRequests=10, HistoryMessages=10, MaxMessageLength=1000, ModelId}`. API key nên đặt qua user-secrets / biến môi trường `OpenAI__ApiKey`, không commit.

### c. Rủi ro / lưu ý cho Tech Lead

Các điểm tôi phải diễn giải hoặc lệch khỏi spec — cần xác nhận:
1. **Đơn vị quota mâu thuẫn trong spec** (§3.3 nói token, Freemium "10 lượt/ngày"). Tôi làm: **Free = 10 request/ngày (`AiTutor:FreeDailyRequests`)**, **VIP = tổng token/ngày theo `ai_daily_token_quota`**. Quota đếm trên toàn bộ ledger của user (mọi task AI), "ngày" = **ngày UTC** (Việt Nam UTC+7 nên reset lúc 07:00 sáng).
2. **Ước lượng token cho VIP** chỉ tính prompt (~4 ký tự/token, gồm system prompt + lịch sử + tin nhắn), chưa tính phần trả lời (chưa biết trước). Hệ quả: VIP có thể vượt quota tối đa khoảng một câu trả lời (≤ `MaxOutputTokens`=600). Free thì đếm lượt nên chính xác.
3. **Race**: kiểm tra quota trước, ghi ledger sau → hai request đồng thời có thể cùng qua cổng, vượt ngưỡng 1–2 lượt. Chưa khoá (cần advisory lock/đếm trong transaction); chấp nhận được vì chi phí nhỏ, nhưng nên biết.
4. **Spec `LlmRequest/LlmResponse` được mở rộng tương thích**: thêm `History` (mặc định rỗng) để AI nhớ hội thoại (spec chỉ có 1 user message nên không có ngữ cảnh), và `ModelProvider` ở `LlmResponse` để ghi vào ledger. Nếu Tech Lead muốn đúng nguyên record trong spec thì mất hai khả năng này.
5. **Trình độ (level)** lấy từ Learning: level cao nhất trong các khoá cùng ngôn ngữ mà user đã hoàn thành ≥ 1 bài; chưa có → A1 / HSK 1. Tính lại ở **mỗi tin nhắn** (không lưu vào session) nên tiến bộ được phản ánh ngay. Đây là suy luận của tôi vì spec chỉ nói "Learning Progress để lấy trình độ".
6. **`IUserEntitlementSource`** đang đọc thẳng bảng Billing (user VIP còn hạn → hạn mức của plan; còn lại → Free). Khi TASK-016 có `IEntitlementService` chỉ cần đổi phần cài đặt trong Persistence.
7. **Thông tin lỗi**: `LlmProviderException` → **503** `ai.provider_unavailable` với message chung (không lộ key/URL/body của provider; chi tiết chỉ nằm trong log). Chưa cấu hình key → cũng 503, host vẫn khởi động bình thường. Spec không nói mã lỗi này; tôi chọn 503.
8. **Fallback Azure** chỉ chuyển sang khi OpenAI ném `LlmProviderException` (HTTP lỗi, timeout, trả rỗng/sai định dạng). Không retry cùng provider.
9. **Chi phí** (`EstimatedCostUsd`) tính theo giá niêm yết gpt-4o-mini cấu hình được; nếu đổi model cần đổi giá, nếu không ledger sẽ ước lượng sai. Nếu provider không trả `usage`, token được ước lượng theo độ dài text để không "miễn phí" với quota.
10. **Prompt injection**: lời người học chỉ nằm ở message `user`, không bao giờ nối vào system prompt (có test); system prompt có câu yêu cầu bỏ qua chỉ dẫn đổi luật. Đây chỉ là giảm thiểu, không phải bảo đảm tuyệt đối. Độ dài tin nhắn giới hạn 1000 ký tự để chặn chi phí.
11. **Một lượt hội thoại** lưu user message + assistant message + ledger trong **một** `SaveChanges`. Hai tin nhắn đồng thời trong cùng session có thể làm `TotalMessagesCount` lệch (không có concurrency token); không ảnh hưởng dữ liệu hội thoại. Thứ tự lịch sử theo `created_at`, hoà thì User trước Assistant.
12. **Chưa làm / ngoài phạm vi**: streaming, retry/backoff, circuit breaker, rate limit theo giây, xoá/ẩn session, danh sách session của user (spec không có endpoint này), cache prompt. `GET /quota` trả cả hai nhóm trường (request/token), nhóm không áp dụng là `null`.
13. Test API dùng header giả (`X-Test-User`), không qua token STS thật; provider thật (OpenAI/Azure) chưa từng được gọi vì không có key — wire format được kiểm bằng HTTP stub theo tài liệu API, **cần smoke test với key thật** trước khi bật production.
14. `LearningEndpoints.RequireLearner` đổi thành `internal` (thay đổi nhỏ, ngoài module AiTutor nhưng nằm trong `Api/*`).


---

### d. Sửa theo Review vòng 1 (Blocker + Major)

**Blocker — quota bị vượt khi gửi song song: đã sửa bằng cách giữ chỗ trước khi gọi model.** (Mục 3 của 5c ở trên — "vượt 1–2 lượt" — là đánh giá sai của tôi và không còn đúng.)
- `IAiTutorRepository.TryReserveAsync(reservation, windowStart, isAllowed)`: trong **một transaction**, lấy `pg_advisory_xact_lock(hashtextextended(userId))`, đếm usage của ngày, nếu `isAllowed` thì chèn dòng ledger giữ chỗ rồi commit. Các request của cùng một user xếp hàng ở bước này (lock chỉ giữ trong transaction ngắn, **không** giữ trong lúc gọi model); user khác không bị chặn nhau.
- Dòng giữ chỗ chính là dòng ledger: `prompt_tokens` = token ước lượng, `model_provider` = `Pending`. Free đếm số dòng, VIP cộng token (kể cả các dòng đang giữ chỗ) nên cả hai đều đúng khi song song.
- `AiQuotaPolicy.EnsureAllowedAsync` thay bằng `ReserveAsync` (ném `AiQuotaExceededException` nếu không đủ). `AiUsageLedger.Settle(...)` ghi số token thật sau khi model trả lời (không cần migration).
- `SendMessageHandler`: giữ chỗ → gọi model → nếu lỗi/rỗng/**client hủy** thì xoá dòng giữ chỗ (trả lại lượt, lưu với `CancellationToken.None`); nếu thành công thì `Settle` + lưu 2 message cùng lúc.

**Major — ngắt kết nối làm mất ledger: đã sửa.** Lần lưu cuối sau khi model đã trả lời dùng `CancellationToken.None` (và bước trả lại lượt cũng vậy), nên client ngắt kết nối không còn làm "OpenAI đã tính tiền mà không ghi sổ".

**Test mới** (unit +4, integration +3; tổng AiTutor: 107 unit + 29 integration):
- Integration, PostgreSQL thật, model giả trả lời sau 1 giây: **30 request song song của user Free → đúng 10 OK, 20 × 429, model bị gọi đúng 10 lần, ledger 10 dòng, 20 message**; VIP quota 3000 token gửi 30 song song → có request bị 429, tổng token ledger ≤ 3000; 15 request song song bị lỗi provider → ledger 0 dòng và sau đó vẫn dùng đủ 10 lượt.
- Unit: quota được giữ trước khi model chạy; dòng giữ chỗ được `Settle` đúng số token thật; client hủy **sau** khi model trả lời vẫn lưu đủ (token lưu không hủy được); client hủy **trong** lúc gọi model thì lượt được trả lại; request bị từ chối không để lại dòng giữ chỗ.
- Phá code để kiểm tra: bỏ advisory lock → test song song Free fail; lưu lần cuối bằng `cancellationToken` → test ngắt kết nối fail; bỏ bước trả lại lượt → 3 unit + 2 integration fail. Đã khôi phục cả ba.

Kết quả: `dotnet build` 0 warning / 0 error; `dotnet test` **473/473 unit, 121/121 integration** (trước đó 469 / 118).

**Lưu ý còn lại sau sửa**
- Nếu tiến trình chết đúng giữa lúc đang gọi model, dòng giữ chỗ (token ước lượng) còn lại và được tính đến hết ngày — nghiêng về phía an toàn cho chi phí. Cũng áp dụng nếu bước trả lại lượt bị lỗi DB (có log error).
- VIP: lần giữ chỗ dùng ước lượng prompt, lần settle dùng số thật, nên tổng có thể vượt quota tối đa bằng phần chênh (≤ khoảng một câu trả lời cho mỗi request đang bay cùng lúc). Free thì chính xác tuyệt đối.
- **Chưa sửa (Minor của reviewer)**: fallback Azure vẫn chờ thêm một timeout đầy đủ sau khi OpenAI timeout (tối đa ~60s), và token OpenAI đã tiêu trước khi chuyển Azure không được ghi sổ (provider không trả usage khi lỗi, nên không có số thật để ghi). Chờ Gemini/Tech Lead quyết có muốn sửa (ví dụ ngân sách thời gian chung) hay không.
- Các câu hỏi cần Gemini quyết (đơn vị quota, múi giờ, vị trí `ILlmProvider`, smoke test key thật, VIP không có `PlanId` coi như Free) vẫn giữ nguyên như reviewer liệt kê.

## 6. Kết Quả Code Review (Claude Code — Reviewer)

- **Kết luận**: `CHANGES_REQUESTED` — 1 🔴 Blocker: quota bị vượt qua hoàn toàn khi gửi request song song (đã tái hiện). Phần còn lại của task chất lượng tốt.
- **Ngày review**: 2026-10-29 — **Lần review**: 1

### Xác minh độc lập
```bash
dotnet build repos/linguabridge/LinguaBridge.sln --no-incremental   # 0 Warning(s), 0 Error(s)
dotnet test  repos/linguabridge/LinguaBridge.sln --no-build          # Unit 469/469, Integration 118/118 PASS (Docker 29.9.2)
dotnet build AppInHouse.sln                                          # 0 Warning(s), 0 Error(s)
grep secrets (sk-..., "ApiKey": "<giá trị>") trong src/ tests/       # không có

# Probe trong scratchpad (ngoài repo, dùng lại ContentApiFixture + Postgres thật):
#   FakeLlm trả lời sau 1s; 1 học viên Free (hạn mức 10/ngày) gửi 30 tin nhắn song song vào 1 session
#   → 200=30  429=0  ledgerRows=30  modelCalls=30
```
- **Phạm vi file**: đúng whitelist. `LearningEndpoints.RequireLearner` → `internal` và `ContentApiFixture` thay `ILlmProvider`: trong glob được phép, có lý do → chấp nhận.

### Điểm tốt
- **Không để lộ bí mật**: `appsettings.json` để trống key; lỗi provider → 503 với message chung, chi tiết chỉ nằm trong log (body cắt còn 300 ký tự).
- **Giảm thiểu prompt injection đúng cách**: lời học viên chỉ nằm ở turn `user`, không bao giờ nối vào system prompt (có test); tin nhắn giới hạn 1000 ký tự.
- **Model lỗi thì học viên không mất lượt**: message, reply và ledger được lưu trong một `SaveChanges`, chỉ khi thành công.
- **Ownership**: session luôn được tra theo `(id, userId)`; người khác nhận 404 (có mutation test).
- Parser có fallback khi model trả text thường hoặc JSON hỏng; ước lượng token khi provider không trả `usage`, để call đó không thành miễn phí.
- **Cách làm "test có răng"** (mutation test 4 chỗ) tiếp tục được duy trì.

### Findings
| # | Mức độ | File:dòng | Vấn đề | Đề xuất |
|---|---|---|---|---|
| 1 | 🔴 Blocker | `Commands/SendMessageCommand.cs:51-73`, `Quota/AiQuotaPolicy.cs:37-47` | **Quota không chặn được request song song.** `EnsureAllowedAsync` đọc ledger, rồi gọi LLM (nhiều giây), rồi mới ghi ledger. Mọi request đến trong khoảng đó đều thấy cùng số đã dùng và đều qua. Đã tái hiện: 30 request song song của một user Free → **30×200, 30 lần gọi model, 30 dòng ledger** (hạn mức 10). Rủi ro 5.c.3 ghi "vượt 1–2 lượt" là đánh giá thấp: chỉ cần `Promise.all` là vượt không giới hạn, và mỗi request vượt là **tiền thật** trả cho OpenAI. Tiêu chí "lần thứ 11 bị chặn" chỉ đúng khi gửi tuần tự. | **Giữ chỗ trước khi gọi model**, kiểm tra và giữ chỗ trong một bước nguyên tử: (a) `pg_advisory_xact_lock(<hash userId>)` bao quanh "đếm + INSERT ledger tạm (token ước lượng)", rồi gọi LLM ngoài transaction; thành công → cập nhật token/cost thật; lỗi → xóa dòng tạm. Hoặc (b) một bảng đếm theo `(user_id, day)` với `UPDATE … SET used = used + 1 WHERE used < limit RETURNING`. Không cần giữ transaction trong lúc gọi LLM. Thêm integration test song song (như probe trên) khẳng định số 200 ≤ hạn mức. |
| 2 | 🟡 Major | `Commands/SendMessageCommand.cs:83` | `SaveChangesAsync(cancellationToken)` chạy **sau** khi đã trả tiền cho model. Client ngắt kết nối đúng lúc đó → save bị hủy → call đã tính tiền nhưng **không có dòng ledger, không trừ quota**. Một client cố ý có thể đốt chi phí mà không bao giờ hết lượt. | Sau khi model trả lời, lưu bằng `CancellationToken.None` (đã trả tiền thì phải ghi sổ). Nếu sửa #1 theo hướng giữ chỗ trước thì lỗ hổng này cũng tự đóng — kiểm tra lại khi sửa. |
| 3 | 🔵 Minor | `Llm/FallbackLlmProvider.cs:13-20` | Khi OpenAI timeout, fallback gọi tiếp Azure với cùng timeout → học viên có thể chờ tới ~60s. Token OpenAI đã tiêu khi trả về JSON hỏng (rồi chuyển sang Azure) cũng không được ghi sổ. | Ngân sách thời gian chung cho cả hai provider, hoặc timeout ngắn hơn cho primary. Ghi nhận chi phí của primary nếu có `usage`. Có thể để backlog. |

### Cần Gemini quyết định
1. **Đơn vị quota** (5.c.1): Free = 10 **request**/ngày, VIP = **token**/ngày. Spec §3.3 và §3.4 mâu thuẫn nhau; cách Coder chọn hợp lý nhưng cần chốt chính thức (hiển thị cho người dùng cũng khác nhau).
2. **"Ngày" = ngày UTC** — lần thứ ba xuất hiện (TASK-014 cũng vậy). Khuyến nghị một quyết định chung về múi giờ học viên cho cả SRS lẫn quota.
3. **Vị trí `ILlmProvider`**: đang khai báo trong module AiTutor, nên `Infrastructure.External` giờ tham chiếu `Modules.AiTutor`. Ổn cho V1; nếu module khác cần LLM (chấm phát âm, sinh nội dung) thì nên chuyển abstraction sang `LinguaBridge.Application`.
4. **Mở rộng `LlmRequest`/`LlmResponse`** (5.c.4: `History`, `ModelProvider`): Reviewer đề nghị chấp nhận — thiếu `History` thì tutor không nhớ hội thoại.
5. **Smoke test với key OpenAI thật** trước khi bật production (5.c.13): wire format mới chỉ được kiểm bằng HTTP stub. Kết hợp với smoke test token STS thật còn treo từ TASK-012.
6. **Entitlement VIP mà `PlanId` null** đang bị coi là Free (inner join). Cần TASK-016 bảo đảm VIP luôn có plan, hoặc chốt quota mặc định cho VIP.

---

### Review lần 2 (2026-10-29)

- **Kết luận**: `REVIEW_PASSED` — Blocker #1 và Major #2 đã được sửa và xác minh. Minor #3 chuyển backlog theo đề xuất của Coder (chờ Gemini). Còn 1 🔵 Minor mới, không chặn.

#### Xác minh độc lập
```bash
dotnet build repos/linguabridge/LinguaBridge.sln --no-incremental   # 0 Warning(s), 0 Error(s)
dotnet test  repos/linguabridge/LinguaBridge.sln --no-build          # Unit 473/473, Integration 121/121 PASS
dotnet build AppInHouse.sln                                          # 0 Warning(s), 0 Error(s)

# Chạy lại đúng probe của lần 1 (scratchpad, model giả trả lời sau 1s, user Free hạn mức 10, 30 request song song):
#   Lần 1: 200=30 429=0  ledgerRows=30 modelCalls=30
#   Lần 2: 200=10 429=20 ledgerRows=10 modelCalls=10   ✅
```

#### Đánh giá các bản sửa
| # lần 1 | Trạng thái | Nhận xét |
|---|---|---|
| 1 🔴 Quota song song | ✅ Đã sửa | `TryReserveAsync`: `pg_advisory_xact_lock` theo user → đếm → INSERT dòng giữ chỗ → commit, trong một transaction ngắn. Dưới Read Committed, câu đếm chạy **sau** khi có lock nên luôn thấy dòng giữ chỗ của transaction trước. Lock **không** giữ trong lúc gọi model; user khác không chặn nhau. Free chính xác tuyệt đối; VIP có thể lệch tối đa phần completion của các request đang bay (đã ghi rõ ở 5.d). Có integration test song song cho cả Free, VIP và provider lỗi, kèm mutation test (bỏ lock → fail). |
| 2 🟡 Ngắt kết nối | ✅ Đã sửa | Lưu cuối và bước trả lại lượt đều dùng `CancellationToken.None`. Hủy **trong** lúc gọi model thì trả lại lượt (`catch` bắt cả `OperationCanceledException`); hủy **sau** khi model trả lời thì vẫn ghi sổ. Có unit test cho cả hai thời điểm. |
| 3 🔵 Fallback timeout | ⏸ Backlog | Coder nêu lý do: provider không trả `usage` khi lỗi nên không có số thật để ghi. Chờ Gemini quyết. |

Cách xử lý dòng giữ chỗ khi tiến trình chết giữa chừng (giữ ước lượng tới hết ngày, nghiêng về an toàn chi phí) là hợp lý.

#### Finding mới
| # | Mức độ | File:dòng | Vấn đề | Đề xuất |
|---|---|---|---|---|
| 4 | 🔵 Minor | `AiTutor/AiTutorRepository.cs` (`TryReserveAsync`) | Khóa advisory dùng `hashtextextended(userId)` trần, chung không gian khóa của cả database. Sau này nếu module khác cũng khóa theo userId (ví dụ Billing), hai tính năng sẽ chặn nhau ngoài ý muốn. | Thêm tiền tố namespace vào khóa, ví dụ `hashtextextended('ai-quota:' \|\| userId, 0)`. Có thể sửa khi chạm lại file. |

#### Cần Gemini quyết định
Giữ nguyên danh sách ở lần 1 (đơn vị quota, múi giờ, vị trí `ILlmProvider`, smoke test key thật, VIP không có `PlanId`), cộng thêm Minor #3.


---

## 7. Quyết Định Kiến Trúc & Nghiệm Thu Cuối Cùng (Gemini Tech Lead)

- **Kết luận**: **`APPROVED`**
- **Đánh giá chuyên môn**:
  - Quá trình phát hiện và xử lý Blocker/Major ở Vòng 1 cực kỳ chất lượng: Cơ chế giữ chỗ trước (Pre-reservation) kết hợp `pg_advisory_xact_lock` theo User ID giải quyết triệt để vấn đề gian lận song song mà không giữ lock trong khi gọi model LLM.
  - Việc chuyển `CancellationToken.None` khi lưu Ledger và hoàn trả lượt sau khi LLM phản hồi giúp hệ thống không bị rò rỉ chi phí API khi client hủy kết nối giữa chừng.
  - Bộ test đạt quy mô ấn tượng (473 unit + 121 integration tests), mô phỏng tải song song 30 requests và kiểm chứng mutation test đáng tin cậy.

### Quyết định chính thức của Tech Lead:

1. **Về Đơn vị Quota**:
   - **Phê duyệt chuẩn hóa**: 
     - **Free Tier**: 10 **lượt tương tác (requests)** / ngày (`AiTutor:FreeDailyRequests`).
     - **VIP Tier**: Tổng **token** / ngày theo cấu hình của gói subscription (`ai_daily_token_quota`).
     - Việc tách bạch này rất tự nhiên và thuận lợi cho trải nghiệm người dùng (người dùng miễn phí dễ nắm bắt số lượt, người dùng trả phí có hạn mức token dồi dào).
2. **Về Mở rộng `LlmRequest` / `LlmResponse`**:
   - **Chấp thuận hoàn toàn**: Việc bổ sung `History` là bắt buộc để duy trì ngữ cảnh trò chuyện của trợ lý gia sư, và `ModelProvider` là thông tin cần thiết cho việc kiểm toán sổ cái `ai_usage_ledger`.
3. **Về Múi giờ reset hạn mức (UTC vs Local)**:
   - Giữ nguyên UTC cho MVP. Trong task User Profile / Preferences, sẽ chuyển sang reset theo `00:00` giờ địa phương của học viên.
4. **Về Advisory Lock Namespace**:
   - Ghi nhận khuyến nghị của Reviewer: Sẽ bổ sung namespace prefix cho advisory lock (ví dụ `hashtextextended('ai-quota:' || userId, 0)`) trong đợt refactor sau để bảo đảm cô lập hoàn toàn giữa các module.
5. **Về Timeout Fallback Azure**:
   - Chuyển vào Backlog tối ưu hóa: Sẽ thiết lập ngân sách thời gian tổng thể (Overall Request Budget, ví dụ tối đa 35s cho toàn bộ pipeline LLM) thay vì chạy timeout độc lập của từng provider nối tiếp nhau.
