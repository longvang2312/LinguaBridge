using LinguaBridge.Domain.Common;
using LinguaBridge.Modules.AiTutor.Entities;
using LinguaBridge.Modules.Billing.Entities;
using LinguaBridge.Modules.Content.Entities;
using LinguaBridge.Modules.Learning.Entities;
using Microsoft.EntityFrameworkCore;

namespace LinguaBridge.Infrastructure.Persistence;

/// <summary>
/// Single context over one PostgreSQL database. Module boundaries are kept by schema (see <see cref="Schemas"/>):
/// foreign keys only ever point within a schema, and cross-module references are plain ids.
/// All database identifiers are snake_case (<c>content.courses.target_language</c>), so SQL needs no quoting.
/// </summary>
public class LinguaBridgeDbContext(DbContextOptions<LinguaBridgeDbContext> options) : DbContext(options)
{
    // content
    public DbSet<Course> Courses => Set<Course>();
    public DbSet<CourseTranslation> CourseTranslations => Set<CourseTranslation>();
    public DbSet<Lesson> Lessons => Set<Lesson>();
    public DbSet<LessonTranslation> LessonTranslations => Set<LessonTranslation>();
    public DbSet<Question> Questions => Set<Question>();
    public DbSet<QuestionTranslation> QuestionTranslations => Set<QuestionTranslation>();
    public DbSet<QuestionOption> QuestionOptions => Set<QuestionOption>();

    // learning
    public DbSet<UserCourseProgress> UserCourseProgress => Set<UserCourseProgress>();
    public DbSet<ExerciseAttempt> ExerciseAttempts => Set<ExerciseAttempt>();
    public DbSet<QuestionResponse> QuestionResponses => Set<QuestionResponse>();

    // srs
    public DbSet<UserVocabularyProgress> UserVocabularyProgress => Set<UserVocabularyProgress>();
    public DbSet<ReviewLog> ReviewLogs => Set<ReviewLog>();

    // ai
    public DbSet<ChatSession> ChatSessions => Set<ChatSession>();
    public DbSet<ChatMessage> ChatMessages => Set<ChatMessage>();
    public DbSet<AiUsageLedger> AiUsageLedger => Set<AiUsageLedger>();

    // billing
    public DbSet<SubscriptionPlan> SubscriptionPlans => Set<SubscriptionPlan>();
    public DbSet<UserEntitlement> UserEntitlements => Set<UserEntitlement>();
    public DbSet<WebhookInbox> WebhookInbox => Set<WebhookInbox>();

    /// <summary>
    /// snake_case is part of the model, not of the caller's options: every way of creating this context
    /// (DI, design-time factory, tests) must produce the same model the migrations were generated from.
    /// </summary>
    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        optionsBuilder.UseSnakeCaseNamingConvention();
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(LinguaBridgeDbContext).Assembly);

        // Domain events are dispatched in memory, never stored.
        foreach (var entityType in modelBuilder.Model.GetEntityTypes().Where(t => typeof(AggregateRoot).IsAssignableFrom(t.ClrType)).ToList())
            modelBuilder.Entity(entityType.ClrType).Ignore(nameof(AggregateRoot.DomainEvents));
    }
}
