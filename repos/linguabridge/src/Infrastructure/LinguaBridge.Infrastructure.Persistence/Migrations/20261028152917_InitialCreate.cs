using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LinguaBridge.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "ai");

            migrationBuilder.EnsureSchema(
                name: "content");

            migrationBuilder.EnsureSchema(
                name: "learning");

            migrationBuilder.EnsureSchema(
                name: "srs");

            migrationBuilder.EnsureSchema(
                name: "billing");

            migrationBuilder.CreateTable(
                name: "ai_usage_ledger",
                schema: "ai",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    task_type = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    model_provider = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    prompt_tokens = table.Column<int>(type: "integer", nullable: false),
                    completion_tokens = table.Column<int>(type: "integer", nullable: false),
                    total_tokens = table.Column<int>(type: "integer", nullable: false),
                    estimated_cost_usd = table.Column<decimal>(type: "numeric(10,6)", precision: 10, scale: 6, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_ai_usage_ledger", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "chat_sessions",
                schema: "ai",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    target_language = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    scenario_code = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    title = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ended_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    total_messages_count = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_chat_sessions", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "courses",
                schema: "content",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    code = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    target_language = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    level = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false),
                    thumbnail_url = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_courses", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "exercise_attempts",
                schema: "learning",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    lesson_id = table.Column<Guid>(type: "uuid", nullable: false),
                    score_percentage = table.Column<int>(type: "integer", nullable: false),
                    xp_earned = table.Column<int>(type: "integer", nullable: false),
                    duration_seconds = table.Column<int>(type: "integer", nullable: false),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_exercise_attempts", x => x.id);
                    table.CheckConstraint("ck_exercise_attempts_score_percentage", "score_percentage BETWEEN 0 AND 100");
                });

            migrationBuilder.CreateTable(
                name: "subscription_plans",
                schema: "billing",
                columns: table => new
                {
                    id = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    price_vnd = table.Column<decimal>(type: "numeric(18,0)", precision: 18, scale: 0, nullable: false),
                    duration_days = table.Column<int>(type: "integer", nullable: false),
                    ai_daily_token_quota = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_subscription_plans", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "user_course_progress",
                schema: "learning",
                columns: table => new
                {
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    course_id = table.Column<Guid>(type: "uuid", nullable: false),
                    completed_lessons_count = table.Column<int>(type: "integer", nullable: false),
                    total_lessons_count = table.Column<int>(type: "integer", nullable: false),
                    is_completed = table.Column<bool>(type: "boolean", nullable: false),
                    last_accessed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_user_course_progress", x => new { x.user_id, x.course_id });
                });

            migrationBuilder.CreateTable(
                name: "user_vocabulary_progress",
                schema: "srs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    vocabulary_id = table.Column<Guid>(type: "uuid", nullable: false),
                    target_language = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    repetitions = table.Column<int>(type: "integer", nullable: false),
                    ease_factor = table.Column<double>(type: "double precision", nullable: false),
                    interval_days = table.Column<int>(type: "integer", nullable: false),
                    next_review_date = table.Column<DateOnly>(type: "date", nullable: false),
                    last_review_date = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    state = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_user_vocabulary_progress", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "webhook_inbox",
                schema: "billing",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    provider = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    event_id = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    payload = table.Column<string>(type: "jsonb", nullable: false),
                    processed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    error_message = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_webhook_inbox", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "chat_messages",
                schema: "ai",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    session_id = table.Column<Guid>(type: "uuid", nullable: false),
                    sender_type = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    content = table.Column<string>(type: "text", nullable: false),
                    pinyin_or_ipa = table.Column<string>(type: "text", nullable: true),
                    translation = table.Column<string>(type: "text", nullable: true),
                    grammar_correction = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_chat_messages", x => x.id);
                    table.ForeignKey(
                        name: "fk_chat_messages_chat_sessions_session_id",
                        column: x => x.session_id,
                        principalSchema: "ai",
                        principalTable: "chat_sessions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "course_translations",
                schema: "content",
                columns: table => new
                {
                    course_id = table.Column<Guid>(type: "uuid", nullable: false),
                    base_language = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    title = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    description = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_course_translations", x => new { x.course_id, x.base_language });
                    table.ForeignKey(
                        name: "fk_course_translations_courses_course_id",
                        column: x => x.course_id,
                        principalSchema: "content",
                        principalTable: "courses",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "lessons",
                schema: "content",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    course_id = table.Column<Guid>(type: "uuid", nullable: false),
                    order_index = table.Column<int>(type: "integer", nullable: false),
                    lesson_type = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    xp_reward = table.Column<int>(type: "integer", nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_lessons", x => x.id);
                    table.ForeignKey(
                        name: "fk_lessons_courses_course_id",
                        column: x => x.course_id,
                        principalSchema: "content",
                        principalTable: "courses",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "question_responses",
                schema: "learning",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    attempt_id = table.Column<Guid>(type: "uuid", nullable: false),
                    question_id = table.Column<Guid>(type: "uuid", nullable: false),
                    selected_option_id = table.Column<Guid>(type: "uuid", nullable: true),
                    user_input_text = table.Column<string>(type: "text", nullable: true),
                    is_correct = table.Column<bool>(type: "boolean", nullable: false),
                    time_spent_ms = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_question_responses", x => x.id);
                    table.ForeignKey(
                        name: "fk_question_responses_exercise_attempts_attempt_id",
                        column: x => x.attempt_id,
                        principalSchema: "learning",
                        principalTable: "exercise_attempts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "user_entitlements",
                schema: "billing",
                columns: table => new
                {
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tier = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    plan_id = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_user_entitlements", x => x.user_id);
                    table.ForeignKey(
                        name: "fk_user_entitlements_subscription_plans_plan_id",
                        column: x => x.plan_id,
                        principalSchema: "billing",
                        principalTable: "subscription_plans",
                        principalColumn: "id");
                });

            migrationBuilder.CreateTable(
                name: "review_logs",
                schema: "srs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_vocabulary_id = table.Column<Guid>(type: "uuid", nullable: false),
                    rating = table.Column<int>(type: "integer", nullable: false),
                    old_ease_factor = table.Column<double>(type: "double precision", nullable: false),
                    new_ease_factor = table.Column<double>(type: "double precision", nullable: false),
                    old_interval_days = table.Column<int>(type: "integer", nullable: false),
                    new_interval_days = table.Column<int>(type: "integer", nullable: false),
                    reviewed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_review_logs", x => x.id);
                    table.ForeignKey(
                        name: "fk_review_logs_user_vocabulary_progress_user_vocabulary_id",
                        column: x => x.user_vocabulary_id,
                        principalSchema: "srs",
                        principalTable: "user_vocabulary_progress",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "lesson_translations",
                schema: "content",
                columns: table => new
                {
                    lesson_id = table.Column<Guid>(type: "uuid", nullable: false),
                    base_language = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    title = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    guide_text = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_lesson_translations", x => new { x.lesson_id, x.base_language });
                    table.ForeignKey(
                        name: "fk_lesson_translations_lessons_lesson_id",
                        column: x => x.lesson_id,
                        principalSchema: "content",
                        principalTable: "lessons",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "questions",
                schema: "content",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    lesson_id = table.Column<Guid>(type: "uuid", nullable: false),
                    order_index = table.Column<int>(type: "integer", nullable: false),
                    question_type = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    audio_prompt_url = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    target_text = table.Column<string>(type: "text", nullable: false),
                    pinyin_or_ipa = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    grammar_notes = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_questions", x => x.id);
                    table.ForeignKey(
                        name: "fk_questions_lessons_lesson_id",
                        column: x => x.lesson_id,
                        principalSchema: "content",
                        principalTable: "lessons",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "question_options",
                schema: "content",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    question_id = table.Column<Guid>(type: "uuid", nullable: false),
                    option_text = table.Column<string>(type: "text", nullable: false),
                    pinyin_or_ipa = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    is_correct = table.Column<bool>(type: "boolean", nullable: false),
                    order_index = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_question_options", x => x.id);
                    table.ForeignKey(
                        name: "fk_question_options_questions_question_id",
                        column: x => x.question_id,
                        principalSchema: "content",
                        principalTable: "questions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "question_translations",
                schema: "content",
                columns: table => new
                {
                    question_id = table.Column<Guid>(type: "uuid", nullable: false),
                    base_language = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    prompt_translation = table.Column<string>(type: "text", nullable: false),
                    explanation = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_question_translations", x => new { x.question_id, x.base_language });
                    table.ForeignKey(
                        name: "fk_question_translations_questions_question_id",
                        column: x => x.question_id,
                        principalSchema: "content",
                        principalTable: "questions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "idx_ai_usage_user_daily",
                schema: "ai",
                table: "ai_usage_ledger",
                columns: new[] { "user_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_chat_messages_session_id",
                schema: "ai",
                table: "chat_messages",
                column: "session_id");

            migrationBuilder.CreateIndex(
                name: "ix_courses_code",
                schema: "content",
                table: "courses",
                column: "code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_lessons_course_id",
                schema: "content",
                table: "lessons",
                column: "course_id");

            migrationBuilder.CreateIndex(
                name: "ix_question_options_question_id",
                schema: "content",
                table: "question_options",
                column: "question_id");

            migrationBuilder.CreateIndex(
                name: "ix_question_responses_attempt_id",
                schema: "learning",
                table: "question_responses",
                column: "attempt_id");

            migrationBuilder.CreateIndex(
                name: "ix_questions_lesson_id",
                schema: "content",
                table: "questions",
                column: "lesson_id");

            migrationBuilder.CreateIndex(
                name: "ix_review_logs_user_vocabulary_id",
                schema: "srs",
                table: "review_logs",
                column: "user_vocabulary_id");

            migrationBuilder.CreateIndex(
                name: "ix_user_entitlements_plan_id",
                schema: "billing",
                table: "user_entitlements",
                column: "plan_id");

            migrationBuilder.CreateIndex(
                name: "idx_srs_user_due",
                schema: "srs",
                table: "user_vocabulary_progress",
                columns: new[] { "user_id", "next_review_date" });

            migrationBuilder.CreateIndex(
                name: "uq_user_vocab",
                schema: "srs",
                table: "user_vocabulary_progress",
                columns: new[] { "user_id", "vocabulary_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "uq_provider_event",
                schema: "billing",
                table: "webhook_inbox",
                columns: new[] { "provider", "event_id" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ai_usage_ledger",
                schema: "ai");

            migrationBuilder.DropTable(
                name: "chat_messages",
                schema: "ai");

            migrationBuilder.DropTable(
                name: "course_translations",
                schema: "content");

            migrationBuilder.DropTable(
                name: "lesson_translations",
                schema: "content");

            migrationBuilder.DropTable(
                name: "question_options",
                schema: "content");

            migrationBuilder.DropTable(
                name: "question_responses",
                schema: "learning");

            migrationBuilder.DropTable(
                name: "question_translations",
                schema: "content");

            migrationBuilder.DropTable(
                name: "review_logs",
                schema: "srs");

            migrationBuilder.DropTable(
                name: "user_course_progress",
                schema: "learning");

            migrationBuilder.DropTable(
                name: "user_entitlements",
                schema: "billing");

            migrationBuilder.DropTable(
                name: "webhook_inbox",
                schema: "billing");

            migrationBuilder.DropTable(
                name: "chat_sessions",
                schema: "ai");

            migrationBuilder.DropTable(
                name: "exercise_attempts",
                schema: "learning");

            migrationBuilder.DropTable(
                name: "questions",
                schema: "content");

            migrationBuilder.DropTable(
                name: "user_vocabulary_progress",
                schema: "srs");

            migrationBuilder.DropTable(
                name: "subscription_plans",
                schema: "billing");

            migrationBuilder.DropTable(
                name: "lessons",
                schema: "content");

            migrationBuilder.DropTable(
                name: "courses",
                schema: "content");
        }
    }
}
