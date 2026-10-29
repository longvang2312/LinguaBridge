using LinguaBridge.Domain.Enums;
using LinguaBridge.Infrastructure.Persistence;
using LinguaBridge.Modules.Billing.Entities;
using LinguaBridge.Modules.Content.Entities;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Testcontainers.PostgreSql;

namespace LinguaBridge.IntegrationTests;

/// <summary>Starts one disposable PostgreSQL container and applies the real migrations to it once for all tests.</summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:17-alpine").Build();

    public string ConnectionString => _container.GetConnectionString();

    public async Task InitializeAsync()
    {
        await _container.StartAsync();

        await using var context = CreateContext();
        await context.Database.MigrateAsync();
    }

    public Task DisposeAsync() => _container.DisposeAsync().AsTask();

    public LinguaBridgeDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<LinguaBridgeDbContext>().UseNpgsql(ConnectionString).Options);
}

/// <summary>
/// Verifies the <c>InitialCreate</c> migration against a real PostgreSQL server (needs Docker).
/// The tests share one database, so each one uses its own unique keys.
/// </summary>
[Trait("Category", "Integration")]
public class PostgresMigrationTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    [Fact]
    public async Task Migration_is_applied_and_nothing_is_pending()
    {
        await using var context = postgres.CreateContext();

        Assert.Contains(await context.Database.GetAppliedMigrationsAsync(), m => m.EndsWith("_InitialCreate", StringComparison.Ordinal));
        Assert.Empty(await context.Database.GetPendingMigrationsAsync());
    }

    [Fact]
    public async Task All_module_schemas_exist()
    {
        var schemas = await QueryStringsAsync("SELECT schema_name FROM information_schema.schemata");

        Assert.Superset(new HashSet<string> { "content", "learning", "srs", "ai", "billing" }, schemas.ToHashSet());
    }

    [Fact]
    public async Task All_eighteen_tables_exist_in_their_schemas()
    {
        var tables = await QueryStringsAsync(
            "SELECT table_schema || '.' || table_name FROM information_schema.tables " +
            "WHERE table_schema IN ('content', 'learning', 'srs', 'ai', 'billing')");

        string[] expected =
        [
            "content.courses", "content.course_translations", "content.lessons", "content.lesson_translations",
            "content.questions", "content.question_translations", "content.question_options",
            "learning.user_course_progress", "learning.exercise_attempts", "learning.question_responses",
            "srs.user_vocabulary_progress", "srs.review_logs",
            "ai.chat_sessions", "ai.chat_messages", "ai.ai_usage_ledger",
            "billing.subscription_plans", "billing.user_entitlements", "billing.webhook_inbox",
        ];
        Assert.Equal(expected.Order(), tables.Order());
    }

    [Fact]
    public async Task Columns_are_snake_case_with_designed_types()
    {
        var columns = await QueryStringsAsync(
            "SELECT table_schema || '.' || table_name || '.' || column_name || ':' || data_type FROM information_schema.columns " +
            "WHERE table_schema IN ('content', 'learning', 'srs', 'ai', 'billing')");

        Assert.Contains("content.courses.target_language:character varying", columns);
        Assert.Contains("content.questions.pinyin_or_ipa:character varying", columns);
        Assert.Contains("srs.user_vocabulary_progress.next_review_date:date", columns);
        Assert.Contains("srs.user_vocabulary_progress.ease_factor:double precision", columns);
        Assert.Contains("ai.ai_usage_ledger.estimated_cost_usd:numeric", columns);
        Assert.Contains("billing.webhook_inbox.payload:jsonb", columns);
        Assert.Contains("learning.exercise_attempts.started_at:timestamp with time zone", columns);
        Assert.DoesNotContain(columns, c => c.Split(':')[0].Any(char.IsUpper));
    }

    [Theory]
    [InlineData("uq_provider_event", "billing", "webhook_inbox", true)]
    [InlineData("uq_user_vocab", "srs", "user_vocabulary_progress", true)]
    [InlineData("idx_srs_user_due", "srs", "user_vocabulary_progress", false)]
    [InlineData("idx_ai_usage_user_daily", "ai", "ai_usage_ledger", false)]
    [InlineData("ix_courses_code", "content", "courses", true)]
    public async Task Named_index_exists_on_its_table(string index, string schema, string table, bool unique)
    {
        var definitions = await QueryStringsAsync(
            $"SELECT schemaname || '.' || tablename || '|' || indexdef FROM pg_indexes WHERE indexname = '{index}'");

        var definition = Assert.Single(definitions);
        Assert.StartsWith($"{schema}.{table}|", definition);
        Assert.Equal(unique, definition.Contains("CREATE UNIQUE INDEX", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Deleting_a_course_cascades_to_its_lessons()
    {
        var course = new Course(UniqueCode("EN"), TargetLanguage.English, LanguageLevel.B1);
        course.SetTranslation(BaseLanguage.Vietnamese, "Tiếng Anh B1");
        var lesson = new Lesson(course.Id, 0, LessonType.Grammar);
        await using (var context = postgres.CreateContext())
        {
            context.AddRange(course, lesson);
            await context.SaveChangesAsync();
        }

        await using (var context = postgres.CreateContext())
        {
            Assert.Equal(1, await context.Courses.Where(c => c.Id == course.Id).ExecuteDeleteAsync());
        }

        await using (var context = postgres.CreateContext())
        {
            Assert.False(await context.Lessons.AnyAsync(l => l.Id == lesson.Id));
            Assert.False(await context.CourseTranslations.AnyAsync(t => t.CourseId == course.Id));
        }
    }

    [Fact]
    public async Task Duplicate_webhook_event_violates_uq_provider_event()
    {
        var eventId = $"evt_{Guid.NewGuid():N}";
        await using (var context = postgres.CreateContext())
        {
            context.WebhookInbox.Add(new WebhookInbox("VNPay", eventId, """{"vnp_TxnRef":"1"}"""));
            await context.SaveChangesAsync();
        }

        await using (var context = postgres.CreateContext())
        {
            context.WebhookInbox.Add(new WebhookInbox("VNPay", eventId, """{"vnp_TxnRef":"1"}"""));
            var ex = await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());

            var postgresError = Assert.IsType<PostgresException>(ex.InnerException);
            Assert.Equal(PostgresErrorCodes.UniqueViolation, postgresError.SqlState);
            Assert.Equal("uq_provider_event", postgresError.ConstraintName);
        }
    }

    [Fact]
    public async Task Score_outside_zero_to_hundred_is_rejected_by_the_database()
    {
        // Bypasses the entity (whose Guard would reject it first) to prove the check constraint itself.
        await using var context = postgres.CreateContext();

        var ex = await Assert.ThrowsAsync<PostgresException>(() => context.Database.ExecuteSqlAsync(
            $"INSERT INTO learning.exercise_attempts (id, user_id, lesson_id, score_percentage, xp_earned, duration_seconds, started_at) VALUES ({Guid.NewGuid()}, {Guid.NewGuid()}, {Guid.NewGuid()}, 150, 0, 0, now())"));

        Assert.Equal(PostgresErrorCodes.CheckViolation, ex.SqlState);
        Assert.Equal("ck_exercise_attempts_score_percentage", ex.ConstraintName);
    }

    [Fact]
    public async Task Values_are_stored_as_designed_and_read_back()
    {
        var course = new Course(UniqueCode("ZH"), TargetLanguage.Chinese, LanguageLevel.Hsk2);
        var webhook = new WebhookInbox("Stripe", $"evt_{Guid.NewGuid():N}", """{"amount": 99000, "currency": "vnd"}""");
        await using (var context = postgres.CreateContext())
        {
            context.AddRange(course, webhook);
            await context.SaveChangesAsync();
        }

        var stored = await QueryStringsAsync(
            $"SELECT target_language || '|' || level || '|' || status FROM content.courses WHERE id = '{course.Id}'");
        Assert.Equal("zh|HSK2|Draft", Assert.Single(stored));

        // jsonb normalises the document, so it can be queried by key.
        var amount = await QueryStringsAsync($"SELECT payload->>'amount' FROM billing.webhook_inbox WHERE id = '{webhook.Id}'");
        Assert.Equal("99000", Assert.Single(amount));

        await using (var context = postgres.CreateContext())
        {
            var loaded = await context.Courses.SingleAsync(c => c.Id == course.Id);
            Assert.Equal(LanguageLevel.Hsk2, loaded.Level);
            Assert.Equal(TimeSpan.Zero, loaded.CreatedAt.Offset);
        }
    }

    /// <summary>Course codes are unique and the tests share one database; prefix + 32 hex chars stays within the 50-char column.</summary>
    private static string UniqueCode(string prefix) => $"{prefix}_{Guid.NewGuid():N}";

    private async Task<List<string>> QueryStringsAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(postgres.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync();

        var values = new List<string>();
        while (await reader.ReadAsync())
            values.Add(reader.GetString(0));
        return values;
    }
}
