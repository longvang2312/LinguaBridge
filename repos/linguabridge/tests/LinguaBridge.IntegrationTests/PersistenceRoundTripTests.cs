using LinguaBridge.Domain.Enums;
using LinguaBridge.Infrastructure.Persistence;
using LinguaBridge.Modules.AiTutor.Entities;
using LinguaBridge.Modules.Billing.Entities;
using LinguaBridge.Modules.Content.Entities;
using LinguaBridge.Modules.Learning.Entities;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace LinguaBridge.IntegrationTests;

/// <summary>
/// Saves and reloads entities through the real <see cref="LinguaBridgeDbContext"/> model on in-memory SQLite.
/// This proves EF can materialise the encapsulated entities (private constructors and setters, backing-field
/// collections) and that keys, cascades and unique constraints behave. SQLite ignores schemas and column types,
/// so PostgreSQL-specific mapping is covered by <c>DbContextModelTests</c> instead.
/// </summary>
public sealed class PersistenceRoundTripTests : IDisposable
{
    private readonly SqliteConnection _connection = new("DataSource=:memory:");

    public PersistenceRoundTripTests()
    {
        _connection.Open();
        using var context = CreateContext();
        context.Database.EnsureCreated();
    }

    public void Dispose() => _connection.Dispose();

    [Fact]
    public async Task Content_aggregates_round_trip_with_their_children()
    {
        var course = new Course("ZH_HSK_1", TargetLanguage.Chinese, LanguageLevel.Hsk1);
        course.SetTranslation(BaseLanguage.Vietnamese, "Tiếng Trung HSK 1", "Nhập môn");
        var lesson = new Lesson(course.Id, 0, LessonType.Pinyin);
        lesson.SetTranslation(BaseLanguage.Vietnamese, "Thanh điệu");
        var question = new Question(lesson.Id, 0, QuestionType.MultipleChoice, "你好", "nǐ hǎo");
        question.SetTranslation(BaseLanguage.Vietnamese, "Xin chào");
        question.AddOption("Hello", isCorrect: true);
        question.AddOption("Thanks", isCorrect: false);

        await using (var context = CreateContext())
        {
            context.AddRange(course, lesson, question);
            await context.SaveChangesAsync();
        }

        await using (var context = CreateContext())
        {
            var loadedCourse = await context.Courses.Include(c => c.Translations).SingleAsync();
            Assert.Equal(TargetLanguage.Chinese, loadedCourse.TargetLanguage);
            Assert.Equal(LanguageLevel.Hsk1, loadedCourse.Level);
            Assert.Equal(ContentStatus.Draft, loadedCourse.Status);
            Assert.Equal("Tiếng Trung HSK 1", Assert.Single(loadedCourse.Translations).Title);

            var loadedQuestion = await context.Questions.Include(q => q.Options).Include(q => q.Translations).SingleAsync();
            Assert.Equal(lesson.Id, loadedQuestion.LessonId);
            Assert.Equal(["Hello", "Thanks"], loadedQuestion.Options.OrderBy(o => o.OrderIndex).Select(o => o.OptionText));
            Assert.Single(loadedQuestion.Translations);
        }
    }

    [Fact]
    public async Task Children_added_to_a_loaded_aggregate_are_inserted()
    {
        var course = new Course("EN_CEFR_A1", TargetLanguage.English, LanguageLevel.A1);
        var attempt = new ExerciseAttempt(Guid.NewGuid(), Guid.NewGuid());
        await using (var context = CreateContext())
        {
            context.AddRange(course, attempt);
            await context.SaveChangesAsync();
        }

        // Client-generated keys on new children must be treated as inserts, not updates of missing rows.
        await using (var context = CreateContext())
        {
            var loadedCourse = await context.Courses.Include(c => c.Translations).SingleAsync();
            loadedCourse.SetTranslation(BaseLanguage.English, "English A1");
            var loadedAttempt = await context.ExerciseAttempts.Include(a => a.Responses).SingleAsync();
            loadedAttempt.AddResponse(Guid.NewGuid(), isCorrect: true, timeSpentMs: 900);
            loadedAttempt.Complete(100, 20);
            await context.SaveChangesAsync();
        }

        await using (var context = CreateContext())
        {
            Assert.Equal(1, await context.CourseTranslations.CountAsync());
            var loadedAttempt = await context.ExerciseAttempts.Include(a => a.Responses).SingleAsync();
            Assert.Single(loadedAttempt.Responses);
            Assert.True(loadedAttempt.IsCompleted);
        }
    }

    [Fact]
    public async Task Deleting_a_course_cascades_through_lessons_questions_and_options()
    {
        var course = new Course("EN_CEFR_A2", TargetLanguage.English, LanguageLevel.A2);
        course.SetTranslation(BaseLanguage.Vietnamese, "Tiếng Anh A2");
        var lesson = new Lesson(course.Id, 0, LessonType.Vocabulary);
        var question = new Question(lesson.Id, 0, QuestionType.Translate, "Good morning");
        question.AddOption("Chào buổi sáng", isCorrect: true);

        await using (var context = CreateContext())
        {
            context.AddRange(course, lesson, question);
            await context.SaveChangesAsync();
        }

        await using (var context = CreateContext())
        {
            await context.Courses.Where(c => c.Id == course.Id).ExecuteDeleteAsync();
        }

        await using (var context = CreateContext())
        {
            Assert.Equal(0, await context.CourseTranslations.CountAsync());
            Assert.Equal(0, await context.Lessons.CountAsync());
            Assert.Equal(0, await context.Questions.CountAsync());
            Assert.Equal(0, await context.QuestionOptions.CountAsync());
        }
    }

    [Fact]
    public async Task Redelivered_webhook_is_rejected_by_the_unique_constraint()
    {
        await using (var context = CreateContext())
        {
            context.WebhookInbox.Add(new WebhookInbox("Stripe", "evt_123", """{"id":"evt_123"}"""));
            await context.SaveChangesAsync();
        }

        await using (var context = CreateContext())
        {
            context.WebhookInbox.Add(new WebhookInbox("Stripe", "evt_123", """{"id":"evt_123"}"""));
            await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
        }
    }

    [Fact]
    public async Task A_plan_in_use_cannot_be_deleted()
    {
        var plan = new SubscriptionPlan("vip_yearly", "VIP Yearly", 990000m, 365, 50_000);
        var entitlement = new UserEntitlement(Guid.NewGuid());
        entitlement.ActivatePlan(plan);

        await using (var context = CreateContext())
        {
            context.AddRange(plan, entitlement);
            await context.SaveChangesAsync();
        }

        await using (var context = CreateContext())
        {
            await Assert.ThrowsAnyAsync<Exception>(() => context.SubscriptionPlans.Where(p => p.Id == plan.Id).ExecuteDeleteAsync());
        }
    }

    [Fact]
    public async Task Progress_srs_and_ai_records_round_trip()
    {
        var progress = new UserCourseProgress(Guid.NewGuid(), Guid.NewGuid(), totalLessonsCount: 10);
        progress.RecordLessonCompleted();
        var card = new UserVocabularyProgress(Guid.NewGuid(), Guid.NewGuid(), TargetLanguage.Chinese, new DateOnly(2026, 10, 29));
        var review = new ReviewLog(card.Id, ReviewRating.Good, 2.5, 2.5, 0, 1);
        var session = new ChatSession(Guid.NewGuid(), TargetLanguage.English, "BusinessEmail", "Follow-up email");
        var message = session.AddMessage(SenderType.Assistant, "Let's draft it.", translation: "Hãy soạn thảo nhé.");
        var usage = new AiUsageLedger(session.UserId, "Roleplay", "OpenAI_GPT4o_Mini", 100, 50, 0.000075m);

        await using (var context = CreateContext())
        {
            context.AddRange(progress, card, review, session, message, usage);
            await context.SaveChangesAsync();
        }

        await using (var context = CreateContext())
        {
            var loadedProgress = await context.UserCourseProgress.SingleAsync();
            Assert.Equal((progress.UserId, progress.CourseId), (loadedProgress.UserId, loadedProgress.CourseId));
            Assert.Equal(1, loadedProgress.CompletedLessonsCount);

            var loadedCard = await context.UserVocabularyProgress.SingleAsync();
            Assert.Equal(new DateOnly(2026, 10, 29), loadedCard.NextReviewDate);
            Assert.Equal(SrsState.Learning, loadedCard.State);
            Assert.Equal(ReviewRating.Good, (await context.ReviewLogs.SingleAsync()).Rating);
            Assert.Equal(1, (await context.ChatSessions.SingleAsync()).TotalMessagesCount);
            Assert.Equal(SenderType.Assistant, (await context.ChatMessages.SingleAsync()).SenderType);
            Assert.Equal(0.000075m, (await context.AiUsageLedger.SingleAsync()).EstimatedCostUsd);
        }
    }

    private LinguaBridgeDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<LinguaBridgeDbContext>().UseSqlite(_connection).Options);
}
