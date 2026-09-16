using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgentCore.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Attachments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Attachments",
                columns: table => new
                {
                    AttachmentId = table.Column<string>(type: "TEXT", maxLength: 36, nullable: false),
                    SessionId = table.Column<string>(type: "TEXT", maxLength: 36, nullable: false),
                    BlobKey = table.Column<string>(type: "TEXT", maxLength: 80, nullable: false),
                    DisplayName = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    ContentType = table.Column<string>(type: "TEXT", nullable: false),
                    ByteSize = table.Column<long>(type: "INTEGER", nullable: false),
                    Sha256Hex = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    State = table.Column<string>(type: "TEXT", nullable: false),
                    Readable = table.Column<bool>(type: "INTEGER", nullable: false),
                    EntryId = table.Column<string>(type: "TEXT", nullable: true),
                    StageForNextTurn = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAtUtc = table.Column<long>(type: "INTEGER", nullable: false),
                    ExpiresAtUtc = table.Column<long>(type: "INTEGER", nullable: true),
                    BoundAtUtc = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Attachments", x => x.AttachmentId);
                });

            migrationBuilder.CreateTable(
                name: "MessageAttachments",
                columns: table => new
                {
                    EntryId = table.Column<string>(type: "TEXT", nullable: false),
                    AttachmentId = table.Column<string>(type: "TEXT", nullable: false),
                    SessionId = table.Column<string>(type: "TEXT", nullable: false),
                    Ordinal = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MessageAttachments", x => new { x.EntryId, x.AttachmentId });
                });

            migrationBuilder.CreateIndex(
                name: "IX_Attachments_SessionId",
                table: "Attachments",
                column: "SessionId");

            migrationBuilder.CreateIndex(
                name: "IX_Attachments_State_ExpiresAtUtc",
                table: "Attachments",
                columns: new[] { "State", "ExpiresAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_MessageAttachments_SessionId",
                table: "MessageAttachments",
                column: "SessionId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "MessageAttachments");
            migrationBuilder.DropTable(name: "Attachments");
        }
    }
}
