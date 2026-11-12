using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AppInHouse.SharedServices.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddPlatformServices : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "background_jobs",
                schema: "shared",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    queue = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    type = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    payload_json = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    attempts = table.Column<int>(type: "integer", nullable: false),
                    max_attempts = table.Column<int>(type: "integer", nullable: false),
                    next_attempt_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_error = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_background_jobs", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "inbound_webhook_logs",
                schema: "shared",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    source = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    signature_valid = table.Column<bool>(type: "boolean", nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    payload_size = table.Column<int>(type: "integer", nullable: false),
                    payload = table.Column<string>(type: "text", nullable: true),
                    error = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    processed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_inbound_webhook_logs", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "notification_logs",
                schema: "shared",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_code = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    template_code = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    channels = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    recipient = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    latency_ms = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_notification_logs", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "notification_templates",
                schema: "shared",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    code = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    subject_template = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    body_template = table.Column<string>(type: "text", nullable: false),
                    default_channel = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    priority = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_notification_templates", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "project_tenants",
                schema: "shared",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_code = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    description = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    environment = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    daily_email_quota = table.Column<int>(type: "integer", nullable: true),
                    daily_chat_quota = table.Column<int>(type: "integer", nullable: true),
                    storage_quota_bytes = table.Column<long>(type: "bigint", nullable: true),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_project_tenants", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "webhook_subscriptions",
                schema: "shared",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_code = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    target_url = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    protected_secret = table.Column<string>(type: "text", nullable: false),
                    secret_hint = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    subscribed_events = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_delivery_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_status = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    success_count = table.Column<int>(type: "integer", nullable: false),
                    failure_count = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_webhook_subscriptions", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_background_jobs_created_at",
                schema: "shared",
                table: "background_jobs",
                column: "created_at");

            migrationBuilder.CreateIndex(
                name: "ix_background_jobs_queue_status",
                schema: "shared",
                table: "background_jobs",
                columns: new[] { "queue", "status" });

            migrationBuilder.CreateIndex(
                name: "ix_inbound_webhook_logs_source_created_at",
                schema: "shared",
                table: "inbound_webhook_logs",
                columns: new[] { "source", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_notification_logs_created_at",
                schema: "shared",
                table: "notification_logs",
                column: "created_at");

            migrationBuilder.CreateIndex(
                name: "ix_notification_logs_project_code_created_at",
                schema: "shared",
                table: "notification_logs",
                columns: new[] { "project_code", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_notification_templates_code",
                schema: "shared",
                table: "notification_templates",
                column: "code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_project_tenants_project_code",
                schema: "shared",
                table: "project_tenants",
                column: "project_code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_webhook_subscriptions_project_code",
                schema: "shared",
                table: "webhook_subscriptions",
                column: "project_code");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "background_jobs",
                schema: "shared");

            migrationBuilder.DropTable(
                name: "inbound_webhook_logs",
                schema: "shared");

            migrationBuilder.DropTable(
                name: "notification_logs",
                schema: "shared");

            migrationBuilder.DropTable(
                name: "notification_templates",
                schema: "shared");

            migrationBuilder.DropTable(
                name: "project_tenants",
                schema: "shared");

            migrationBuilder.DropTable(
                name: "webhook_subscriptions",
                schema: "shared");
        }
    }
}
