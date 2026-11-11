using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AppInHouse.SharedServices.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "shared");

            migrationBuilder.CreateTable(
                name: "api_keys",
                schema: "shared",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_code = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    key_prefix = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    hashed_key = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    scopes = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    is_revoked = table.Column<bool>(type: "boolean", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_used_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_api_keys", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "audit_logs",
                schema: "shared",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    actor = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    action = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    resource_type = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    resource_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    details_json = table.Column<string>(type: "jsonb", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_audit_logs", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "channel_configurations",
                schema: "shared",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_code = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    channel_type = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    provider_type = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    settings_json = table.Column<string>(type: "jsonb", nullable: false),
                    encrypted_secret = table.Column<string>(type: "text", nullable: true),
                    last_tested_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_test_status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    activated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_channel_configurations", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "delivery_logs",
                schema: "shared",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_code = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    channel_type = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    recipient = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    provider_response = table.Column<string>(type: "text", nullable: true),
                    latency_ms = table.Column<int>(type: "integer", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_delivery_logs", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "stored_files",
                schema: "shared",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_code = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    storage_profile_id = table.Column<Guid>(type: "uuid", nullable: false),
                    storage_provider = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    object_key = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    original_file_name = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    mime_type = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    file_size_bytes = table.Column<long>(type: "bigint", nullable: false),
                    file_hash_sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    is_public = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_stored_files", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "system_logs",
                schema: "shared",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    level = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    category = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    message = table.Column<string>(type: "text", nullable: false),
                    exception = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_system_logs", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "media_logs",
                schema: "shared",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_code = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    file_id = table.Column<Guid>(type: "uuid", nullable: true),
                    action = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    error_message = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_media_logs", x => x.id);
                    table.ForeignKey(
                        name: "fk_media_logs_stored_files_file_id",
                        column: x => x.file_id,
                        principalSchema: "shared",
                        principalTable: "stored_files",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "ix_api_keys_key_prefix",
                schema: "shared",
                table: "api_keys",
                column: "key_prefix");

            migrationBuilder.CreateIndex(
                name: "ix_audit_logs_created_at",
                schema: "shared",
                table: "audit_logs",
                column: "created_at");

            migrationBuilder.CreateIndex(
                name: "uq_channel_version",
                schema: "shared",
                table: "channel_configurations",
                columns: new[] { "project_code", "channel_type", "name", "version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_delivery_logs_project_code_created_at",
                schema: "shared",
                table: "delivery_logs",
                columns: new[] { "project_code", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_media_logs_file_id",
                schema: "shared",
                table: "media_logs",
                column: "file_id");

            migrationBuilder.CreateIndex(
                name: "ix_media_logs_project_code_created_at",
                schema: "shared",
                table: "media_logs",
                columns: new[] { "project_code", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_stored_files_project_code_object_key",
                schema: "shared",
                table: "stored_files",
                columns: new[] { "project_code", "object_key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_system_logs_created_at",
                schema: "shared",
                table: "system_logs",
                column: "created_at");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "api_keys",
                schema: "shared");

            migrationBuilder.DropTable(
                name: "audit_logs",
                schema: "shared");

            migrationBuilder.DropTable(
                name: "channel_configurations",
                schema: "shared");

            migrationBuilder.DropTable(
                name: "delivery_logs",
                schema: "shared");

            migrationBuilder.DropTable(
                name: "media_logs",
                schema: "shared");

            migrationBuilder.DropTable(
                name: "system_logs",
                schema: "shared");

            migrationBuilder.DropTable(
                name: "stored_files",
                schema: "shared");
        }
    }
}
