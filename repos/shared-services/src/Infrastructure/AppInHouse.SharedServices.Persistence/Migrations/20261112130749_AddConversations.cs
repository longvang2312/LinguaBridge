using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AppInHouse.SharedServices.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddConversations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "conversations",
                schema: "shared",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_code = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    type = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    direct_key = table.Column<string>(type: "character varying(201)", maxLength: 201, nullable: true),
                    created_by = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_message_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_conversations", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "chat_messages",
                schema: "shared",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    conversation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    sender_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    sender_name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    message_type = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    content = table.Column<string>(type: "text", nullable: false),
                    attachment_file_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_chat_messages", x => x.id);
                    table.ForeignKey(
                        name: "fk_chat_messages_conversations_conversation_id",
                        column: x => x.conversation_id,
                        principalSchema: "shared",
                        principalTable: "conversations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "conversation_members",
                schema: "shared",
                columns: table => new
                {
                    conversation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    role = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    joined_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_read_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    is_muted = table.Column<bool>(type: "boolean", nullable: false),
                    display_name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    email = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    chat_id = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_conversation_members", x => new { x.conversation_id, x.user_id });
                    table.ForeignKey(
                        name: "fk_conversation_members_conversations_conversation_id",
                        column: x => x.conversation_id,
                        principalSchema: "shared",
                        principalTable: "conversations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_chat_messages_conversation_id_created_at",
                schema: "shared",
                table: "chat_messages",
                columns: new[] { "conversation_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_chat_messages_created_at",
                schema: "shared",
                table: "chat_messages",
                column: "created_at");

            migrationBuilder.CreateIndex(
                name: "ix_conversation_members_user_id",
                schema: "shared",
                table: "conversation_members",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "ix_conversations_project_code_direct_key",
                schema: "shared",
                table: "conversations",
                columns: new[] { "project_code", "direct_key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_conversations_project_code_last_message_at",
                schema: "shared",
                table: "conversations",
                columns: new[] { "project_code", "last_message_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "chat_messages",
                schema: "shared");

            migrationBuilder.DropTable(
                name: "conversation_members",
                schema: "shared");

            migrationBuilder.DropTable(
                name: "conversations",
                schema: "shared");
        }
    }
}
