using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using LinguaBridge.Domain.Enums;
using LinguaBridge.Infrastructure.Persistence;
using LinguaBridge.Modules.AiTutor.Abstractions;
using LinguaBridge.Modules.Billing.Entities;
using LinguaBridge.Modules.Content.Entities;
using LinguaBridge.Modules.Learning.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace LinguaBridge.IntegrationTests;

/// <summary>
/// The AI tutor API end to end on a real PostgreSQL, with a scripted model in place of OpenAI. Each test uses its own
/// learner, so the free daily allowance of one test never leaks into another.
/// </summary>
[Trait("Category", "Integration")]
public sealed class AiTutorApiTests(ContentApiFixture api) : IClassFixture<ContentApiFixture>
{
    private const string Ai = "/api/v1/ai";

    // ---- Access -----------------------------------------------------------------------------------------------

    [Fact]
    public async Task Anonymous_callers_are_rejected()
    {
        using var client = api.Anonymous();
        var id = Guid.NewGuid();

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync($"{Ai}/sessions", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync($"{Ai}/sessions/{id}/messages", new { text = "hi" })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync($"{Ai}/sessions/{id}/messages")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.DeleteAsync($"{Ai}/sessions/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync($"{Ai}/quota")).StatusCode);
    }

    [Fact]
    public async Task A_token_that_belongs_to_a_service_and_not_a_learner_is_refused()
    {
        using var service = api.WithSubject("internal-service");
        var id = Guid.NewGuid();

        foreach (var response in new[]
        {
            await service.PostAsJsonAsync($"{Ai}/sessions", new { targetLanguage = "en", scenarioCode = "FreeTalk" }),
            await service.PostAsJsonAsync($"{Ai}/sessions/{id}/messages", new { text = "hi" }),
            await service.GetAsync($"{Ai}/sessions/{id}/messages"),
            await service.DeleteAsync($"{Ai}/sessions/{id}"),
            await service.GetAsync($"{Ai}/quota"),
        })
        {
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            Assert.Equal("learner.required", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        }
    }

    // ---- Sessions ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Creating_a_session_returns_201_with_a_location_and_the_normalised_scenario()
    {
        using var learner = api.LearnerAs(Guid.NewGuid());

        var response = await learner.PostAsJsonAsync($"{Ai}/sessions", new { targetLanguage = "zh", scenarioCode = "jobinterview" });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var session = await response.Content.ReadFromJsonAsync<JsonElement>();
        var id = session.GetProperty("id").GetGuid();
        Assert.Equal($"{Ai}/sessions/{id}/messages", response.Headers.Location!.OriginalString);
        Assert.Equal("JobInterview", session.GetProperty("scenarioCode").GetString());
        Assert.Equal("zh", session.GetProperty("targetLanguage").GetString());
        Assert.Equal(0, session.GetProperty("totalMessagesCount").GetInt32());
        Assert.Equal(JsonValueKind.Null, session.GetProperty("endedAt").ValueKind);
    }

    [Theory]
    [InlineData("en", "Karaoke", "ScenarioCode")]
    [InlineData("en", null, "ScenarioCode")]
    [InlineData("fr", "FreeTalk", "targetLanguage")]
    [InlineData(null, "FreeTalk", "targetLanguage")]
    public async Task A_bad_session_request_is_a_400_naming_the_field(string? language, string? scenario, string field)
    {
        using var learner = api.LearnerAs(Guid.NewGuid());

        var response = await learner.PostAsJsonAsync($"{Ai}/sessions", new { targetLanguage = language, scenarioCode = scenario });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var errors = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errors");
        Assert.True(errors.TryGetProperty(field, out _), $"expected an error for {field}");
    }

    [Fact]
    public async Task Ending_a_session_is_204_repeatable_and_blocks_new_messages()
    {
        using var learner = api.LearnerAs(Guid.NewGuid());
        var session = await CreateSessionAsync(learner);

        Assert.Equal(HttpStatusCode.NoContent, (await learner.DeleteAsync($"{Ai}/sessions/{session}")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await learner.DeleteAsync($"{Ai}/sessions/{session}")).StatusCode);

        var send = await learner.PostAsJsonAsync($"{Ai}/sessions/{session}/messages", new { text = "still there?" });
        Assert.Equal(HttpStatusCode.Conflict, send.StatusCode);
        Assert.Equal("session.ended", (await send.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());

        // The conversation stays readable.
        Assert.Equal(HttpStatusCode.OK, (await learner.GetAsync($"{Ai}/sessions/{session}/messages")).StatusCode);
    }

    [Fact]
    public async Task Another_learners_session_is_a_404_for_every_operation()
    {
        using var owner = api.LearnerAs(Guid.NewGuid());
        using var stranger = api.LearnerAs(Guid.NewGuid());
        var session = await CreateSessionAsync(owner);
        await owner.PostAsJsonAsync($"{Ai}/sessions/{session}/messages", new { text = "private" });

        Assert.Equal(HttpStatusCode.NotFound, (await stranger.PostAsJsonAsync($"{Ai}/sessions/{session}/messages", new { text = "hi" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.GetAsync($"{Ai}/sessions/{session}/messages")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.DeleteAsync($"{Ai}/sessions/{session}")).StatusCode);

        // The owner's session is untouched by the stranger's attempt to end it.
        Assert.Equal(HttpStatusCode.OK, (await owner.PostAsJsonAsync($"{Ai}/sessions/{session}/messages", new { text = "still mine" })).StatusCode);
    }

    // ---- A tutoring turn --------------------------------------------------------------------------------------

    [Fact]
    public async Task A_message_returns_the_structured_reply_and_the_history_lists_both_sides_in_order()
    {
        var userId = Guid.NewGuid();
        using var learner = api.LearnerAs(userId);
        var session = await CreateSessionAsync(learner);

        var response = await learner.PostAsJsonAsync($"{Ai}/sessions/{session}/messages", new { text = "I want this job." });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var reply = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Tell me about yourself.", reply.GetProperty("replyText").GetString());
        Assert.Equal("/tel mi/", reply.GetProperty("pinyinOrIpa").GetString());
        Assert.Equal("Hay ke ve ban than.", reply.GetProperty("translation").GetString());
        Assert.Equal("Good start.", reply.GetProperty("grammarTips").GetString());
        Assert.True(reply.GetProperty("isStructured").GetBoolean());
        Assert.Equal(150, reply.GetProperty("tokensUsed").GetInt32());

        var history = await (await learner.GetAsync($"{Ai}/sessions/{session}/messages")).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(2, history.GetProperty("total").GetInt32());
        var items = history.GetProperty("items").EnumerateArray().ToArray();
        Assert.Equal(["User", "Assistant"], items.Select(i => i.GetProperty("sender").GetString()));
        Assert.Equal("I want this job.", items[0].GetProperty("content").GetString());
        Assert.Equal("Tell me about yourself.", items[1].GetProperty("content").GetString());
        Assert.Equal("Good start.", items[1].GetProperty("grammarTips").GetString());
    }

    [Fact]
    public async Task Each_successful_turn_writes_one_ledger_row_with_tokens_cost_and_model()
    {
        var userId = Guid.NewGuid();
        using var learner = api.LearnerAs(userId);
        var session = await CreateSessionAsync(learner);

        await learner.PostAsJsonAsync($"{Ai}/sessions/{session}/messages", new { text = "one" });
        await learner.PostAsJsonAsync($"{Ai}/sessions/{session}/messages", new { text = "two" });

        await using var scope = Scope(out var db);
        var rows = await db.AiUsageLedger.Where(l => l.UserId == userId).ToListAsync();
        Assert.Equal(2, rows.Count);
        Assert.All(rows, row =>
        {
            Assert.Equal(120, row.PromptTokens);
            Assert.Equal(30, row.CompletionTokens);
            Assert.Equal(150, row.TotalTokens);
            Assert.Equal(0.00004m, row.EstimatedCostUsd);
            Assert.Equal("OpenAI_gpt-4o-mini", row.ModelProvider);
            Assert.Equal("TextTutor", row.TaskType);
        });

        var stored = await db.ChatSessions.SingleAsync(s => s.Id == session);
        Assert.Equal(4, stored.TotalMessagesCount);
    }

    [Fact]
    public async Task A_plain_text_model_reply_still_answers_the_learner()
    {
        using var learner = api.LearnerAs(Guid.NewGuid());
        var session = await CreateSessionAsync(learner);

        var reply = await WithModel(_ => new LlmResponse("Sorry, I forgot the format.", 10, 5, 0m), async () =>
            await (await learner.PostAsJsonAsync($"{Ai}/sessions/{session}/messages", new { text = "hi" })).Content.ReadFromJsonAsync<JsonElement>());

        Assert.Equal("Sorry, I forgot the format.", reply.GetProperty("replyText").GetString());
        Assert.False(reply.GetProperty("isStructured").GetBoolean());
        Assert.Equal(JsonValueKind.Null, reply.GetProperty("translation").ValueKind);
        Assert.Equal(JsonValueKind.Null, reply.GetProperty("grammarTips").ValueKind);
    }

    [Fact]
    public async Task The_model_is_told_the_learners_level_language_and_scenario_and_sees_the_earlier_turns()
    {
        var userId = Guid.NewGuid();
        await SeedProgressAsync(userId, TargetLanguage.Chinese, LanguageLevel.Hsk3);
        using var learner = api.LearnerAs(userId);
        var session = await CreateSessionAsync(learner, "zh", "BusinessEmail");

        await learner.PostAsJsonAsync($"{Ai}/sessions/{session}/messages", new { text = "你好" });
        await learner.PostAsJsonAsync($"{Ai}/sessions/{session}/messages?baseLanguage=en", new { text = "谢谢" });

        var requests = api.Llm.Requests.Where(r => r.UserMessage is "你好" or "谢谢").ToArray();
        Assert.Equal(2, requests.Length);
        Assert.Contains("HSK 3", requests[0].SystemPrompt);
        Assert.Contains("business email", requests[0].SystemPrompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Vietnamese", requests[0].SystemPrompt);
        Assert.Contains("translated into English", requests[1].SystemPrompt);
        Assert.Equal(["你好", "Tell me about yourself."], requests[1].History!.Select(t => t.Text));
    }

    [Fact]
    public async Task A_newcomer_starts_at_the_first_level_of_the_language()
    {
        using var learner = api.LearnerAs(Guid.NewGuid());
        var session = await CreateSessionAsync(learner, "en", "FreeTalk");

        await learner.PostAsJsonAsync($"{Ai}/sessions/{session}/messages", new { text = "newcomer-marker" });

        var request = api.Llm.Requests.Single(r => r.UserMessage == "newcomer-marker");
        Assert.Contains("CEFR A1", request.SystemPrompt);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task A_blank_message_is_a_400_and_the_model_is_not_called(string text)
    {
        using var learner = api.LearnerAs(Guid.NewGuid());
        var session = await CreateSessionAsync(learner);
        var before = api.Llm.Requests.Count;

        var response = await learner.PostAsJsonAsync($"{Ai}/sessions/{session}/messages", new { text });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(before, api.Llm.Requests.Count);
    }

    [Fact]
    public async Task A_missing_body_field_and_an_oversized_message_are_400s()
    {
        using var learner = api.LearnerAs(Guid.NewGuid());
        var session = await CreateSessionAsync(learner);

        Assert.Equal(HttpStatusCode.BadRequest, (await learner.PostAsJsonAsync($"{Ai}/sessions/{session}/messages", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await learner.PostAsJsonAsync($"{Ai}/sessions/{session}/messages", new { text = new string('x', 1001) })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await learner.PostAsJsonAsync($"{Ai}/sessions/{session}/messages", new { text = new string('x', 1000) })).StatusCode);
    }

    [Fact]
    public async Task An_unknown_base_language_is_a_400()
    {
        using var learner = api.LearnerAs(Guid.NewGuid());
        var session = await CreateSessionAsync(learner);

        var response = await learner.PostAsJsonAsync($"{Ai}/sessions/{session}/messages?baseLanguage=klingon", new { text = "hi" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ---- Quota ------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_free_learner_can_send_ten_messages_and_the_eleventh_is_a_429()
    {
        var userId = Guid.NewGuid();
        using var learner = api.LearnerAs(userId);
        var session = await CreateSessionAsync(learner);

        for (var i = 1; i <= 10; i++)
        {
            var ok = await learner.PostAsJsonAsync($"{Ai}/sessions/{session}/messages", new { text = "Same prompt every time" });
            Assert.True(ok.StatusCode == HttpStatusCode.OK, $"request {i} returned {(int)ok.StatusCode}");
        }

        var modelCallsBefore = api.Llm.Requests.Count;
        var blocked = await learner.PostAsJsonAsync($"{Ai}/sessions/{session}/messages", new { text = "Same prompt every time" });

        Assert.Equal(HttpStatusCode.TooManyRequests, blocked.StatusCode);
        var problem = await blocked.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("AI quota exhausted. Upgrade to VIP or wait until tomorrow.", problem.GetProperty("detail").GetString());
        Assert.Equal("ai.quota_exhausted", problem.GetProperty("code").GetString());
        Assert.True(problem.TryGetProperty("resetsAt", out _));
        Assert.True(int.Parse(blocked.Headers.GetValues("Retry-After").Single()) is >= 1 and <= 86_400);

        Assert.Equal(modelCallsBefore, api.Llm.Requests.Count);
        await using var scope = Scope(out var db);
        Assert.Equal(10, await db.AiUsageLedger.CountAsync(l => l.UserId == userId));
        Assert.Equal(20, await db.ChatMessages.CountAsync(m => m.SessionId == session));
    }

    [Fact]
    public async Task Thirty_simultaneous_messages_from_a_free_learner_admit_exactly_ten()
    {
        var userId = Guid.NewGuid();
        using var learner = api.LearnerAs(userId);
        var session = await CreateSessionAsync(learner);
        var modelCallsBefore = api.Llm.Requests.Count;

        var statuses = await SendTogetherAsync(learner, session, count: 30);

        Assert.Equal(10, statuses.Count(s => s == HttpStatusCode.OK));
        Assert.Equal(20, statuses.Count(s => s == HttpStatusCode.TooManyRequests));
        Assert.Equal(modelCallsBefore + 10, api.Llm.Requests.Count);

        await using var scope = Scope(out var db);
        Assert.Equal(10, await db.AiUsageLedger.CountAsync(l => l.UserId == userId));
        Assert.Equal(20, await db.ChatMessages.CountAsync(m => m.SessionId == session));
    }

    [Fact]
    public async Task Simultaneous_messages_from_a_vip_learner_cannot_pass_the_plans_token_quota()
    {
        var userId = Guid.NewGuid();
        await SeedVipAsync(userId, dailyTokenQuota: 3000);
        using var learner = api.LearnerAs(userId);
        var session = await CreateSessionAsync(learner);

        var statuses = await SendTogetherAsync(learner, session, count: 30);

        var admitted = statuses.Count(s => s == HttpStatusCode.OK);
        Assert.InRange(admitted, 1, 29);
        Assert.Equal(30 - admitted, statuses.Count(s => s == HttpStatusCode.TooManyRequests));

        await using var scope = Scope(out var db);
        var rows = await db.AiUsageLedger.Where(l => l.UserId == userId).ToListAsync();
        Assert.Equal(admitted, rows.Count);
        Assert.True(rows.Sum(r => r.TotalTokens) <= 3000);
        Assert.All(rows, r => Assert.Equal(150, r.TotalTokens));
    }

    [Fact]
    public async Task Requests_that_fail_while_others_are_in_flight_give_their_allowance_back()
    {
        var userId = Guid.NewGuid();
        using var learner = api.LearnerAs(userId);
        var session = await CreateSessionAsync(learner);

        var statuses = await WithModel(_ => throw new LlmProviderException("down"), () => SendTogetherAsync(learner, session, count: 15));

        Assert.All(statuses.Where(s => s != HttpStatusCode.TooManyRequests), s => Assert.Equal(HttpStatusCode.ServiceUnavailable, s));
        await using var scope = Scope(out var db);
        Assert.Equal(0, await db.AiUsageLedger.CountAsync(l => l.UserId == userId));

        // Nothing was spent, so the whole allowance is still there.
        for (var i = 0; i < 10; i++)
            Assert.Equal(HttpStatusCode.OK, (await learner.PostAsJsonAsync($"{Ai}/sessions/{session}/messages", new { text = "hi" })).StatusCode);
    }

    [Fact]
    public async Task The_quota_endpoint_reports_the_free_allowance_and_what_is_left()
    {
        using var learner = api.LearnerAs(Guid.NewGuid());
        var session = await CreateSessionAsync(learner);
        for (var i = 0; i < 3; i++)
            await learner.PostAsJsonAsync($"{Ai}/sessions/{session}/messages", new { text = "hi" });

        var quota = await (await learner.GetAsync($"{Ai}/quota")).Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal("Free", quota.GetProperty("plan").GetString());
        Assert.Equal(10, quota.GetProperty("dailyRequestLimit").GetInt32());
        Assert.Equal(3, quota.GetProperty("requestsToday").GetInt32());
        Assert.Equal(7, quota.GetProperty("requestsRemaining").GetInt32());
        Assert.Equal(450, quota.GetProperty("tokensToday").GetInt64());
        Assert.Equal(JsonValueKind.Null, quota.GetProperty("dailyTokenLimit").ValueKind);
        Assert.True(quota.GetProperty("resetsAt").GetDateTimeOffset() > DateTimeOffset.UtcNow);
    }

    [Fact]
    public async Task A_vip_learner_is_limited_by_the_plans_token_quota_and_not_by_ten_requests()
    {
        var userId = Guid.NewGuid();
        await SeedVipAsync(userId, dailyTokenQuota: 20_000);
        using var learner = api.LearnerAs(userId);
        var session = await CreateSessionAsync(learner);

        for (var i = 0; i < 12; i++)
            Assert.Equal(HttpStatusCode.OK, (await learner.PostAsJsonAsync($"{Ai}/sessions/{session}/messages", new { text = "hi" })).StatusCode);

        var quota = await (await learner.GetAsync($"{Ai}/quota")).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Paid", quota.GetProperty("plan").GetString());
        Assert.Equal(20_000, quota.GetProperty("dailyTokenLimit").GetInt64());
        Assert.Equal(1800, quota.GetProperty("tokensToday").GetInt64());
        Assert.Equal(18_200, quota.GetProperty("tokensRemaining").GetInt64());
        Assert.Equal(JsonValueKind.Null, quota.GetProperty("dailyRequestLimit").ValueKind);
    }

    [Fact]
    public async Task A_vip_learner_who_used_the_plans_tokens_is_blocked_with_a_429()
    {
        var userId = Guid.NewGuid();
        await SeedVipAsync(userId, dailyTokenQuota: 100);
        using var learner = api.LearnerAs(userId);
        var session = await CreateSessionAsync(learner);

        // The prompt alone is estimated above 100 tokens, so even the first message does not fit this plan.
        var response = await learner.PostAsJsonAsync($"{Ai}/sessions/{session}/messages", new { text = "hi" });

        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
    }

    [Fact]
    public async Task An_expired_vip_plan_falls_back_to_the_free_allowance()
    {
        var userId = Guid.NewGuid();
        await SeedVipAsync(userId, dailyTokenQuota: 1_000_000);
        await using (var scope = Scope(out var db))
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE billing.user_entitlements SET expires_at = {DateTimeOffset.UtcNow.AddDays(-1)} WHERE user_id = {userId}");
        using var learner = api.LearnerAs(userId);

        var quota = await (await learner.GetAsync($"{Ai}/quota")).Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal("Free", quota.GetProperty("plan").GetString());
        Assert.Equal(10, quota.GetProperty("dailyRequestLimit").GetInt32());
    }

    [Fact]
    public async Task One_learners_usage_does_not_use_up_another_learners_allowance()
    {
        using var heavy = api.LearnerAs(Guid.NewGuid());
        using var light = api.LearnerAs(Guid.NewGuid());
        var heavySession = await CreateSessionAsync(heavy);
        var lightSession = await CreateSessionAsync(light);
        for (var i = 0; i < 10; i++)
            await heavy.PostAsJsonAsync($"{Ai}/sessions/{heavySession}/messages", new { text = "hi" });

        Assert.Equal(HttpStatusCode.TooManyRequests, (await heavy.PostAsJsonAsync($"{Ai}/sessions/{heavySession}/messages", new { text = "hi" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await light.PostAsJsonAsync($"{Ai}/sessions/{lightSession}/messages", new { text = "hi" })).StatusCode);
    }

    // ---- When the model is unavailable ------------------------------------------------------------------------

    [Fact]
    public async Task A_model_outage_is_a_503_that_stores_nothing_and_costs_no_allowance()
    {
        var userId = Guid.NewGuid();
        using var learner = api.LearnerAs(userId);
        var session = await CreateSessionAsync(learner);

        var response = await WithModel(_ => throw new LlmProviderException("OpenAI returned HTTP 401 (key sk-secret-key rejected)"), async () =>
            await learner.PostAsJsonAsync($"{Ai}/sessions/{session}/messages", new { text = "hi" }));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var text = await response.Content.ReadAsStringAsync();
        Assert.Equal("ai.provider_unavailable", JsonDocument.Parse(text).RootElement.GetProperty("code").GetString());
        Assert.DoesNotContain("sk-secret-key", text);

        await using var scope = Scope(out var db);
        Assert.Equal(0, await db.ChatMessages.CountAsync(m => m.SessionId == session));
        Assert.Equal(0, await db.AiUsageLedger.CountAsync(l => l.UserId == userId));
        var quota = await (await learner.GetAsync($"{Ai}/quota")).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(0, quota.GetProperty("requestsToday").GetInt32());
    }

    // ---- History paging ---------------------------------------------------------------------------------------

    [Fact]
    public async Task History_supports_offset_and_limit_and_rejects_bad_values()
    {
        using var learner = api.LearnerAs(Guid.NewGuid());
        var session = await CreateSessionAsync(learner);
        for (var i = 0; i < 3; i++)
            await learner.PostAsJsonAsync($"{Ai}/sessions/{session}/messages", new { text = $"m{i}" });

        var page = await (await learner.GetAsync($"{Ai}/sessions/{session}/messages?offset=2&limit=3")).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(6, page.GetProperty("total").GetInt32());
        Assert.Equal(["m1", "Tell me about yourself.", "m2"], page.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("content").GetString()));

        Assert.Equal(HttpStatusCode.BadRequest, (await learner.GetAsync($"{Ai}/sessions/{session}/messages?limit=0")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await learner.GetAsync($"{Ai}/sessions/{session}/messages?limit=201")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await learner.GetAsync($"{Ai}/sessions/{session}/messages?offset=-1")).StatusCode);
    }

    // ---- Helpers ----------------------------------------------------------------------------------------------

    private static async Task<Guid> CreateSessionAsync(HttpClient learner, string language = "en", string scenario = "JobInterview")
    {
        var response = await learner.PostAsJsonAsync($"{Ai}/sessions", new { targetLanguage = language, scenarioCode = scenario });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    /// <summary>Scripts the model for the duration of one call and puts the normal one back, whatever happens.</summary>
    private async Task<T> WithModel<T>(Func<LlmRequest, LlmResponse> respond, Func<Task<T>> act)
    {
        var original = api.Llm.Respond;
        api.Llm.Respond = respond;
        try
        {
            return await act();
        }
        finally
        {
            api.Llm.Respond = original;
        }
    }

    /// <summary>
    /// Fires <paramref name="count"/> messages at once at a model that takes a second to answer, so every request is
    /// in flight together: this is what a script or a double-clicking user does.
    /// </summary>
    private async Task<HttpStatusCode[]> SendTogetherAsync(HttpClient learner, Guid session, int count)
    {
        var original = api.Llm.Latency;
        api.Llm.Latency = TimeSpan.FromSeconds(1);
        try
        {
            var responses = await Task.WhenAll(Enumerable.Range(0, count)
                .Select(_ => learner.PostAsJsonAsync($"{Ai}/sessions/{session}/messages", new { text = "hi" })));
            return responses.Select(r => r.StatusCode).ToArray();
        }
        finally
        {
            api.Llm.Latency = original;
        }
    }

    private AsyncServiceScope Scope(out LinguaBridgeDbContext db)
    {
        var scope = api.Services.CreateAsyncScope();
        db = scope.ServiceProvider.GetRequiredService<LinguaBridgeDbContext>();
        return scope;
    }

    private async Task SeedVipAsync(Guid userId, int dailyTokenQuota)
    {
        await using var scope = Scope(out var db);

        var planId = $"vip_test_{Guid.NewGuid():N}"[..40];
        var plan = new SubscriptionPlan(planId, "VIP test", 99_000, 30, dailyTokenQuota);
        var entitlement = new UserEntitlement(userId);
        entitlement.ActivatePlan(plan);

        db.SubscriptionPlans.Add(plan);
        db.UserEntitlements.Add(entitlement);
        await db.SaveChangesAsync();
    }

    private async Task SeedProgressAsync(Guid userId, TargetLanguage language, LanguageLevel level)
    {
        await using var scope = Scope(out var db);

        var course = new Course($"AI_{Guid.NewGuid():N}"[..30], language, level);
        var progress = new UserCourseProgress(userId, course.Id, totalLessonsCount: 5);
        progress.Update(completedLessonsCount: 1, totalLessonsCount: 5);

        db.Courses.Add(course);
        db.UserCourseProgress.Add(progress);
        await db.SaveChangesAsync();
    }
}
