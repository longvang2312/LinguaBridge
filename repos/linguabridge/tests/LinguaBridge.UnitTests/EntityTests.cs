using LinguaBridge.Domain.Enums;
using LinguaBridge.Modules.AiTutor.Entities;
using LinguaBridge.Modules.Billing.Entities;
using LinguaBridge.Modules.Content.Entities;
using LinguaBridge.Modules.Learning.Entities;

namespace LinguaBridge.UnitTests;

public class EntityTests
{
    [Fact]
    public void New_course_starts_as_draft_version_one()
    {
        var course = new Course("ZH_HSK_1", TargetLanguage.Chinese, LanguageLevel.Hsk1);

        Assert.Equal(ContentStatus.Draft, course.Status);
        Assert.Equal(1, course.Version);
        Assert.Equal(course.CreatedAt, course.UpdatedAt);
    }

    [Fact]
    public void Course_rejects_a_level_from_the_other_language()
    {
        Assert.Throws<ArgumentException>(() => new Course("EN_HSK_1", TargetLanguage.English, LanguageLevel.Hsk1));
    }

    [Fact]
    public void Course_rejects_a_code_longer_than_the_column()
    {
        Assert.Throws<ArgumentException>(() => new Course(new string('X', Course.CodeMaxLength + 1), TargetLanguage.English, LanguageLevel.A1));
    }

    [Fact]
    public void Setting_a_translation_twice_replaces_it()
    {
        var course = new Course("EN_CEFR_A1", TargetLanguage.English, LanguageLevel.A1);

        course.SetTranslation(BaseLanguage.Vietnamese, "Tiếng Anh A1");
        course.SetTranslation(BaseLanguage.Vietnamese, "Tiếng Anh cơ bản A1", "Mô tả");
        course.SetTranslation(BaseLanguage.English, "English A1");

        Assert.Equal(2, course.Translations.Count);
        var vietnamese = course.Translations.Single(t => t.BaseLanguage == BaseLanguage.Vietnamese);
        Assert.Equal("Tiếng Anh cơ bản A1", vietnamese.Title);
        Assert.Equal(course.Id, vietnamese.CourseId);
    }

    [Fact]
    public void Question_options_are_ordered_as_added()
    {
        var question = new Question(Guid.NewGuid(), 0, QuestionType.MultipleChoice, "你好", "nǐ hǎo");

        question.AddOption("Hello", isCorrect: true);
        question.AddOption("Goodbye", isCorrect: false);

        Assert.Equal([0, 1], question.Options.Select(o => o.OrderIndex));
        Assert.All(question.Options, o => Assert.Equal(question.Id, o.QuestionId));
    }

    [Fact]
    public void Attempt_completion_validates_score_and_locks_the_attempt()
    {
        var attempt = new ExerciseAttempt(Guid.NewGuid(), Guid.NewGuid());
        attempt.AddResponse(Guid.NewGuid(), isCorrect: true, timeSpentMs: 1200);

        Assert.Throws<ArgumentOutOfRangeException>(() => attempt.Complete(101, 10));

        attempt.Complete(80, 16);

        Assert.True(attempt.IsCompleted);
        Assert.Equal(80, attempt.ScorePercentage);
        Assert.Throws<InvalidOperationException>(() => attempt.AddResponse(Guid.NewGuid(), false, 500));
    }

    [Fact]
    public void Course_progress_completes_when_all_lessons_are_done()
    {
        var progress = new UserCourseProgress(Guid.NewGuid(), Guid.NewGuid(), totalLessonsCount: 2);

        progress.RecordLessonCompleted();
        Assert.False(progress.IsCompleted);

        progress.RecordLessonCompleted();
        progress.RecordLessonCompleted();
        Assert.True(progress.IsCompleted);
        Assert.Equal(2, progress.CompletedLessonsCount);
    }

    [Fact]
    public void New_vocabulary_card_uses_sm2_defaults()
    {
        var card = new UserVocabularyProgress(Guid.NewGuid(), Guid.NewGuid(), TargetLanguage.Chinese, new DateOnly(2026, 10, 28));

        Assert.Equal(2.5, card.EaseFactor);
        Assert.Equal(0, card.Repetitions);
        Assert.Equal(SrsState.Learning, card.State);
    }

    [Fact]
    public void Review_log_rejects_ratings_outside_one_to_four()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ReviewLog(Guid.NewGuid(), (ReviewRating)5, 2.5, 2.6, 1, 6));
    }

    [Fact]
    public void Chat_session_counts_messages_and_refuses_them_once_ended()
    {
        var session = new ChatSession(Guid.NewGuid(), TargetLanguage.English, "JobInterview", "Mock interview");

        var message = session.AddMessage(SenderType.User, "Hello, I'm here for the interview.");
        session.End();

        Assert.Equal(session.Id, message.SessionId);
        Assert.Equal(1, session.TotalMessagesCount);
        Assert.Throws<InvalidOperationException>(() => session.AddMessage(SenderType.Assistant, "Welcome!"));
    }

    [Fact]
    public void Usage_ledger_totals_tokens()
    {
        var entry = new AiUsageLedger(Guid.NewGuid(), "Roleplay", "OpenAI_GPT4o_Mini", 120, 80, 0.000123m);

        Assert.Equal(200, entry.TotalTokens);
    }

    [Fact]
    public void Renewing_before_expiry_extends_from_the_current_expiry()
    {
        var plan = new SubscriptionPlan("vip_monthly", "VIP Monthly", 99000m, 30, 50_000);
        var entitlement = new UserEntitlement(Guid.NewGuid());

        entitlement.ActivatePlan(plan);
        var firstExpiry = entitlement.ExpiresAt!.Value;
        entitlement.ActivatePlan(plan);

        Assert.Equal(EntitlementTier.Vip, entitlement.Tier);
        Assert.Equal("vip_monthly", entitlement.PlanId);
        Assert.Equal(firstExpiry.AddDays(30), entitlement.ExpiresAt);
    }

    [Fact]
    public void Plan_price_in_vnd_has_no_fraction()
    {
        Assert.Throws<ArgumentException>(() => new SubscriptionPlan("vip_monthly", "VIP Monthly", 99000.5m, 30, 50_000));
    }

    [Fact]
    public void Webhook_moves_from_pending_to_processed_once()
    {
        var webhook = new WebhookInbox("VNPay", "TXN-001", """{"amount":99000}""");
        Assert.Equal(WebhookStatus.Pending, webhook.Status);

        webhook.MarkFailed("Gateway timeout");
        Assert.Equal(WebhookStatus.Failed, webhook.Status);

        webhook.MarkProcessed();
        Assert.Equal(WebhookStatus.Processed, webhook.Status);
        Assert.Null(webhook.ErrorMessage);
        Assert.Throws<InvalidOperationException>(() => webhook.MarkProcessed());
    }
}
