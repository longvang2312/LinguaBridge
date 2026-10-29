using LinguaBridge.Modules.AiTutor;
using LinguaBridge.Modules.AiTutor.Abstractions;
using LinguaBridge.Modules.AiTutor.Quota;
using Microsoft.Extensions.Configuration;

namespace LinguaBridge.UnitTests.AiTutor;

public class AiQuotaTests
{
    [Fact]
    public async Task A_free_learner_gets_ten_requests_and_the_eleventh_is_blocked()
    {
        var h = new AiHarness();
        var session = await h.NewSessionAsync();

        for (var i = 1; i <= 10; i++)
        {
            var result = await h.SayAsync(session, "Same question every time");
            Assert.True(result.IsSuccess, $"request {i}: {result.Error?.Message}");
        }

        var blocked = await Assert.ThrowsAsync<AiQuotaExceededException>(() => h.SayAsync(session, "Same question every time"));

        Assert.Equal("AI quota exhausted. Upgrade to VIP or wait until tomorrow.", blocked.Message);
        Assert.Equal(10, h.Llm.Requests.Count);
        Assert.Equal(10, h.Repo.Ledger.Count);
        Assert.Equal(20, h.Repo.Messages.Count);
    }

    [Fact]
    public async Task Exactly_at_the_limit_is_allowed_and_one_more_is_not()
    {
        var h = new AiHarness(new AiTutorOptions { FreeDailyRequests = 3 });
        var session = await h.NewSessionAsync();

        for (var i = 0; i < 3; i++)
            Assert.True((await h.SayAsync(session, "hi")).IsSuccess);

        await Assert.ThrowsAsync<AiQuotaExceededException>(() => h.SayAsync(session, "hi"));
    }

    [Fact]
    public async Task The_allowance_returns_at_the_next_utc_midnight()
    {
        var h = new AiHarness(new AiTutorOptions { FreeDailyRequests = 1 });
        var session = await h.NewSessionAsync();
        h.Clock.UtcNow = new DateTimeOffset(2026, 10, 29, 23, 59, 0, TimeSpan.Zero);

        Assert.True((await h.SayAsync(session, "hi")).IsSuccess);
        var blocked = await Assert.ThrowsAsync<AiQuotaExceededException>(() => h.SayAsync(session, "hi"));
        Assert.Equal(new DateTimeOffset(2026, 10, 30, 0, 0, 0, TimeSpan.Zero), blocked.ResetsAt);

        h.Clock.UtcNow = new DateTimeOffset(2026, 10, 30, 0, 0, 1, TimeSpan.Zero);
        Assert.True((await h.SayAsync(session, "hi")).IsSuccess);
    }

    [Fact]
    public async Task One_learners_usage_does_not_count_against_another()
    {
        var h = new AiHarness(new AiTutorOptions { FreeDailyRequests = 1 });
        var other = Guid.NewGuid();
        var mine = await h.NewSessionAsync();
        var theirs = await h.NewSessionAsync(user: other);

        Assert.True((await h.SayAsync(mine, "hi")).IsSuccess);

        Assert.True((await h.SayAsync(theirs, "hi", other)).IsSuccess);
        await Assert.ThrowsAsync<AiQuotaExceededException>(() => h.SayAsync(mine, "hi"));
    }

    [Fact]
    public async Task A_free_learner_is_limited_by_requests_not_tokens()
    {
        var h = new AiHarness(new AiTutorOptions { FreeDailyRequests = 5 });
        var session = await h.NewSessionAsync();
        h.Llm.Respond = _ => new LlmResponse(ScriptedLlm.JsonReply, 50_000, 50_000, 1m);

        for (var i = 0; i < 5; i++)
            Assert.True((await h.SayAsync(session, "hi")).IsSuccess);
    }

    [Fact]
    public async Task A_paid_learner_is_limited_by_the_plans_token_quota()
    {
        var h = new AiHarness();
        h.Entitlements.GrantPaid(h.User, 2000);
        var session = await h.NewSessionAsync();
        h.Repo.Spend(h.User, 1700);

        var spent = await h.SayAsync(session, "hi");
        Assert.True(spent.IsSuccess);
        Assert.Equal(150, spent.Value.TokensUsed);

        // 1850 of 2000 used, and the prompt alone is estimated above the 150 left: it no longer fits.
        await Assert.ThrowsAsync<AiQuotaExceededException>(() => h.SayAsync(session, "hi"));
    }

    [Fact]
    public async Task A_paid_learner_can_make_far_more_than_ten_requests()
    {
        var h = new AiHarness();
        h.Entitlements.GrantPaid(h.User, 1_000_000);
        var session = await h.NewSessionAsync();

        for (var i = 0; i < 25; i++)
            Assert.True((await h.SayAsync(session, "hi")).IsSuccess);
    }

    [Fact]
    public async Task A_request_whose_estimated_size_does_not_fit_is_refused_before_the_model_is_called()
    {
        var h = new AiHarness();
        h.Entitlements.GrantPaid(h.User, 600);
        var session = await h.NewSessionAsync();
        h.Repo.Spend(h.User, 590);

        await Assert.ThrowsAsync<AiQuotaExceededException>(() => h.SayAsync(session, "hi"));

        Assert.Empty(h.Llm.Requests);
        Assert.Empty(h.Repo.Messages);
    }

    [Fact]
    public async Task Status_for_a_free_learner_counts_requests()
    {
        var h = new AiHarness();
        var session = await h.NewSessionAsync();
        for (var i = 0; i < 4; i++)
            await h.SayAsync(session, "hi");

        var status = await h.Quota.GetStatusAsync(h.User, default);

        Assert.Equal(AiQuotaPolicy.FreePlan, status.Plan);
        Assert.Equal(10, status.DailyRequestLimit);
        Assert.Equal(4, status.RequestsToday);
        Assert.Equal(6, status.RequestsRemaining);
        Assert.Equal(600, status.TokensToday);
        Assert.Null(status.DailyTokenLimit);
        Assert.Null(status.TokensRemaining);
        Assert.Equal(new DateTimeOffset(2026, 10, 30, 0, 0, 0, TimeSpan.Zero), status.ResetsAt);
    }

    [Fact]
    public async Task Status_for_a_paid_learner_counts_tokens_and_never_goes_negative()
    {
        var h = new AiHarness();
        h.Entitlements.GrantPaid(h.User, 1000);
        h.Repo.Spend(h.User, 1500);

        var status = await h.Quota.GetStatusAsync(h.User, default);

        Assert.Equal(AiQuotaPolicy.PaidPlan, status.Plan);
        Assert.Equal(1000, status.DailyTokenLimit);
        Assert.Equal(1500, status.TokensToday);
        Assert.Equal(0, status.TokensRemaining);
        Assert.Null(status.DailyRequestLimit);
        Assert.Null(status.RequestsRemaining);
    }

    [Theory]
    [InlineData("", 0)]
    [InlineData("abcd", 1)]
    [InlineData("abcde", 2)]
    public void Token_estimate_is_about_one_per_four_characters_rounded_up(string text, int expected)
    {
        Assert.Equal(expected, AiQuotaPolicy.EstimateTokens(text));
    }

    [Fact]
    public void Options_are_read_from_configuration_and_bad_values_are_refused()
    {
        var good = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["AiTutor:FreeDailyRequests"] = "3", ["AiTutor:ModelId"] = " gpt-x " })
            .Build();
        var options = AiTutorOptions.From(good);
        Assert.Equal(3, options.FreeDailyRequests);
        Assert.Equal("gpt-x", options.ModelId);
        Assert.Equal(10, options.HistoryMessages);

        var bad = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["AiTutor:FreeDailyRequests"] = "-1" })
            .Build();
        Assert.Throws<InvalidOperationException>(() => AiTutorOptions.From(bad));
    }
}
