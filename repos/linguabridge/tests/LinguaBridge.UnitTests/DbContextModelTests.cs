using LinguaBridge.Domain.Enums;
using LinguaBridge.Infrastructure.Persistence;
using LinguaBridge.Modules.AiTutor.Entities;
using LinguaBridge.Modules.Billing.Entities;
using LinguaBridge.Modules.Content.Entities;
using LinguaBridge.Modules.Learning.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;

namespace LinguaBridge.UnitTests;

/// <summary>
/// Verifies the EF Core model against the data architecture (design doc, section 3) without a database:
/// building the Npgsql model and generating its DDL never opens a connection.
/// </summary>
public class DbContextModelTests
{
    private static readonly IModel Model = BuildModel();

    public static TheoryData<Type, string, string> EntityTables => new()
    {
        { typeof(Course), "content", "courses" },
        { typeof(CourseTranslation), "content", "course_translations" },
        { typeof(Lesson), "content", "lessons" },
        { typeof(LessonTranslation), "content", "lesson_translations" },
        { typeof(Question), "content", "questions" },
        { typeof(QuestionTranslation), "content", "question_translations" },
        { typeof(QuestionOption), "content", "question_options" },
        { typeof(UserCourseProgress), "learning", "user_course_progress" },
        { typeof(ExerciseAttempt), "learning", "exercise_attempts" },
        { typeof(QuestionResponse), "learning", "question_responses" },
        { typeof(UserVocabularyProgress), "srs", "user_vocabulary_progress" },
        { typeof(ReviewLog), "srs", "review_logs" },
        { typeof(ChatSession), "ai", "chat_sessions" },
        { typeof(ChatMessage), "ai", "chat_messages" },
        { typeof(AiUsageLedger), "ai", "ai_usage_ledger" },
        { typeof(SubscriptionPlan), "billing", "subscription_plans" },
        { typeof(UserEntitlement), "billing", "user_entitlements" },
        { typeof(WebhookInbox), "billing", "webhook_inbox" },
    };

    public static TheoryData<Type, string[]> PrimaryKeys => new()
    {
        { typeof(CourseTranslation), ["CourseId", "BaseLanguage"] },
        { typeof(LessonTranslation), ["LessonId", "BaseLanguage"] },
        { typeof(QuestionTranslation), ["QuestionId", "BaseLanguage"] },
        { typeof(UserCourseProgress), ["UserId", "CourseId"] },
        { typeof(UserEntitlement), ["UserId"] },
        { typeof(SubscriptionPlan), ["Id"] },
        { typeof(Course), ["Id"] },
        { typeof(WebhookInbox), ["Id"] },
    };

    public static TheoryData<Type, Type, string, DeleteBehavior> ForeignKeys => new()
    {
        { typeof(CourseTranslation), typeof(Course), "CourseId", DeleteBehavior.Cascade },
        { typeof(Lesson), typeof(Course), "CourseId", DeleteBehavior.Cascade },
        { typeof(LessonTranslation), typeof(Lesson), "LessonId", DeleteBehavior.Cascade },
        { typeof(Question), typeof(Lesson), "LessonId", DeleteBehavior.Cascade },
        { typeof(QuestionTranslation), typeof(Question), "QuestionId", DeleteBehavior.Cascade },
        { typeof(QuestionOption), typeof(Question), "QuestionId", DeleteBehavior.Cascade },
        { typeof(QuestionResponse), typeof(ExerciseAttempt), "AttemptId", DeleteBehavior.Cascade },
        { typeof(ReviewLog), typeof(UserVocabularyProgress), "UserVocabularyId", DeleteBehavior.Cascade },
        { typeof(ChatMessage), typeof(ChatSession), "SessionId", DeleteBehavior.Cascade },
        { typeof(UserEntitlement), typeof(SubscriptionPlan), "PlanId", DeleteBehavior.NoAction },
    };

    [Fact]
    public void Model_contains_exactly_the_eighteen_designed_entities()
    {
        var expected = EntityTables.Select(row => (Type)row[0]).OrderBy(t => t.Name).ToArray();
        var actual = Model.GetEntityTypes().Select(t => t.ClrType).OrderBy(t => t.Name).ToArray();

        Assert.Equal(18, expected.Length);
        Assert.Equal(expected, actual);
    }

    [Theory]
    [MemberData(nameof(EntityTables))]
    public void Entity_is_mapped_to_its_module_schema_and_table(Type clrType, string schema, string table)
    {
        var entityType = Entity(clrType);

        Assert.Equal(schema, entityType.GetSchema());
        Assert.Equal(table, entityType.GetTableName());
    }

    [Theory]
    [MemberData(nameof(PrimaryKeys))]
    public void Entity_has_the_designed_primary_key(Type clrType, string[] keyProperties)
    {
        var key = Entity(clrType).FindPrimaryKey();

        Assert.NotNull(key);
        Assert.Equal(keyProperties, key.Properties.Select(p => p.Name));
    }

    [Fact]
    public void Course_code_is_unique()
    {
        Assert.True(Index(typeof(Course), "Code").IsUnique);
    }

    [Fact]
    public void Vocabulary_card_is_unique_per_user_and_item()
    {
        var index = Index(typeof(UserVocabularyProgress), "UserId", "VocabularyId");

        Assert.True(index.IsUnique);
        Assert.Equal("uq_user_vocab", index.GetDatabaseName());
    }

    [Fact]
    public void Webhook_is_unique_per_provider_and_event_for_idempotency()
    {
        var index = Index(typeof(WebhookInbox), "Provider", "EventId");

        Assert.True(index.IsUnique);
        Assert.Equal("uq_provider_event", index.GetDatabaseName());
    }

    [Theory]
    [InlineData(typeof(UserVocabularyProgress), "idx_srs_user_due", new[] { "UserId", "NextReviewDate" })]
    [InlineData(typeof(AiUsageLedger), "idx_ai_usage_user_daily", new[] { "UserId", "CreatedAt" })]
    public void Hot_query_indexes_exist(Type clrType, string name, string[] properties)
    {
        var index = Index(clrType, properties);

        Assert.False(index.IsUnique);
        Assert.Equal(name, index.GetDatabaseName());
    }

    [Theory]
    [MemberData(nameof(ForeignKeys))]
    public void Foreign_key_points_to_parent_with_designed_delete_rule(Type dependent, Type principal, string property, DeleteBehavior onDelete)
    {
        var foreignKey = Entity(dependent).GetForeignKeys().Single(fk => fk.Properties.Select(p => p.Name).SequenceEqual([property]));

        Assert.Equal(principal, foreignKey.PrincipalEntityType.ClrType);
        Assert.Equal(onDelete, foreignKey.DeleteBehavior);
    }

    [Fact]
    public void Only_the_designed_foreign_keys_exist()
    {
        Assert.Equal(ForeignKeys.Count(), Model.GetEntityTypes().SelectMany(t => t.GetForeignKeys()).Count());
    }

    [Fact]
    public void No_foreign_key_crosses_a_module_schema()
    {
        var crossSchema = Model.GetEntityTypes()
            .SelectMany(t => t.GetForeignKeys())
            .Where(fk => fk.DeclaringEntityType.GetSchema() != fk.PrincipalEntityType.GetSchema())
            .Select(fk => $"{fk.DeclaringEntityType.ClrType.Name} -> {fk.PrincipalEntityType.ClrType.Name}");

        Assert.Empty(crossSchema);
    }

    [Theory]
    [InlineData(typeof(WebhookInbox), "Payload", "jsonb")]
    [InlineData(typeof(AiUsageLedger), "EstimatedCostUsd", "numeric(10,6)")]
    [InlineData(typeof(SubscriptionPlan), "PriceVnd", "numeric(18,0)")]
    [InlineData(typeof(UserVocabularyProgress), "NextReviewDate", "date")]
    [InlineData(typeof(UserVocabularyProgress), "EaseFactor", "double precision")]
    [InlineData(typeof(Course), "CreatedAt", "timestamp with time zone")]
    [InlineData(typeof(Course), "Code", "character varying(50)")]
    [InlineData(typeof(Course), "TargetLanguage", "character varying(10)")]
    [InlineData(typeof(Course), "Status", "character varying(20)")]
    [InlineData(typeof(ReviewLog), "Rating", "integer")]
    [InlineData(typeof(Question), "TargetText", "text")]
    public void Column_has_the_designed_store_type(Type clrType, string property, string storeType)
    {
        Assert.Equal(storeType, Entity(clrType).FindProperty(property)!.GetColumnType());
    }

    [Fact]
    public void Language_columns_store_codes_not_enum_names()
    {
        var course = Entity(typeof(Course));
        var translation = Entity(typeof(CourseTranslation));

        Assert.Equal("zh", course.FindProperty("TargetLanguage")!.GetValueConverter()!.ConvertToProvider(TargetLanguage.Chinese));
        Assert.Equal("HSK3", course.FindProperty("Level")!.GetValueConverter()!.ConvertToProvider(LanguageLevel.Hsk3));
        Assert.Equal("B2", course.FindProperty("Level")!.GetValueConverter()!.ConvertToProvider(LanguageLevel.B2));
        Assert.Equal("vi", translation.FindProperty("BaseLanguage")!.GetValueConverter()!.ConvertToProvider(BaseLanguage.Vietnamese));
        Assert.Equal(LanguageLevel.Hsk3, course.FindProperty("Level")!.GetValueConverter()!.ConvertFromProvider("HSK3"));
    }

    [Theory]
    [InlineData("TargetLanguage", "fr")]
    [InlineData("Level", "HSK9")]
    [InlineData("Level", "3")]
    public void Unknown_language_codes_fail_instead_of_reading_as_a_default(string property, string storedValue)
    {
        var converter = Entity(typeof(Course)).FindProperty(property)!.GetValueConverter()!;

        Assert.Throws<InvalidOperationException>(() => converter.ConvertFromProvider(storedValue));
    }

    [Fact]
    public void Domain_events_are_not_persisted()
    {
        Assert.Null(Entity(typeof(Course)).FindProperty("DomainEvents"));
        Assert.Null(Entity(typeof(Course)).FindNavigation("DomainEvents"));
    }

    [Fact]
    public void Postgres_ddl_can_be_generated_for_every_schema()
    {
        using var context = CreateContext();

        var script = context.Database.GenerateCreateScript();

        foreach (var schema in new[] { Schemas.Content, Schemas.Learning, Schemas.Srs, Schemas.Ai, Schemas.Billing })
            Assert.Contains($"CREATE SCHEMA {schema};", script);
        Assert.Contains("CREATE TABLE content.courses (", script);
        Assert.Contains("CREATE UNIQUE INDEX uq_provider_event ON billing.webhook_inbox (provider, event_id);", script);
        Assert.Contains("CREATE INDEX idx_srs_user_due ON srs.user_vocabulary_progress (user_id, next_review_date);", script);
        Assert.Contains("payload jsonb NOT NULL", script);
    }

    [Fact]
    public void Ddl_contains_no_identifier_that_needs_quoting()
    {
        using var context = CreateContext();

        var script = context.Database.GenerateCreateScript();

        // Npgsql quotes only identifiers that are not plain lower-case, so any quote means a name escaped the convention.
        Assert.DoesNotContain("\"", script);
    }

    [Theory]
    [InlineData(typeof(Course), "TargetLanguage", "target_language")]
    [InlineData(typeof(Course), "CreatedAt", "created_at")]
    [InlineData(typeof(Question), "PinyinOrIpa", "pinyin_or_ipa")]
    [InlineData(typeof(ExerciseAttempt), "ScorePercentage", "score_percentage")]
    [InlineData(typeof(UserVocabularyProgress), "EaseFactor", "ease_factor")]
    [InlineData(typeof(UserVocabularyProgress), "NextReviewDate", "next_review_date")]
    [InlineData(typeof(AiUsageLedger), "EstimatedCostUsd", "estimated_cost_usd")]
    [InlineData(typeof(UserEntitlement), "UserId", "user_id")]
    public void Columns_are_snake_case(Type clrType, string property, string column)
    {
        var entityType = Entity(clrType);
        var table = StoreObjectIdentifier.Table(entityType.GetTableName()!, entityType.GetSchema());

        Assert.Equal(column, entityType.FindProperty(property)!.GetColumnName(table));
    }

    [Fact]
    public void Generated_constraint_names_are_snake_case()
    {
        Assert.Equal("pk_courses", Entity(typeof(Course)).FindPrimaryKey()!.GetName());
        Assert.Equal("ix_courses_code", Index(typeof(Course), "Code").GetDatabaseName());
        Assert.Equal(
            "fk_lessons_courses_course_id",
            Entity(typeof(Lesson)).GetForeignKeys().Single().GetConstraintName());
    }

    [Fact]
    public void Model_has_no_changes_missing_from_the_migrations()
    {
        using var context = CreateContext();

        Assert.False(context.Database.HasPendingModelChanges(), "Run 'dotnet ef migrations add' - the model has changed since the last migration.");
    }

    [Fact]
    public void Initial_migration_is_present()
    {
        using var context = CreateContext();

        Assert.Contains(context.Database.GetMigrations(), m => m.EndsWith("_InitialCreate", StringComparison.Ordinal));
    }

    private static IEntityType Entity(Type clrType) =>
        Model.FindEntityType(clrType) ?? throw new InvalidOperationException($"{clrType.Name} is not in the model.");

    private static IIndex Index(Type clrType, params string[] properties) =>
        Entity(clrType).GetIndexes().Single(i => i.Properties.Select(p => p.Name).SequenceEqual(properties));

    private static LinguaBridgeDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<LinguaBridgeDbContext>().UseNpgsql("Host=localhost;Database=model_only").Options);

    private static IModel BuildModel()
    {
        using var context = CreateContext();
        return context.GetService<IDesignTimeModel>().Model;
    }
}
