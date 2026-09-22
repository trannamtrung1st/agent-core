using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgentCore.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class StructuredSessionMemory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "StructuredMemories",
                columns: table => new
                {
                    MemoryId = table.Column<string>(type: "TEXT", maxLength: 36, nullable: false),
                    SessionId = table.Column<string>(type: "TEXT", maxLength: 36, nullable: false),
                    Kind = table.Column<int>(type: "INTEGER", nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    Subject = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    Content = table.Column<string>(type: "TEXT", nullable: false),
                    SubjectKey = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    Source = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    SourceEntryIdsJson = table.Column<string>(type: "TEXT", nullable: false),
                    SupersedesMemoryId = table.Column<string>(type: "TEXT", maxLength: 36, nullable: true),
                    ProvenanceRecordedAtUtc = table.Column<long>(type: "INTEGER", nullable: false),
                    CreatedAtUtc = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAtUtc = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StructuredMemories", x => x.MemoryId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_StructuredMemories_SessionId_Kind_SubjectKey",
                table: "StructuredMemories",
                columns: new[] { "SessionId", "Kind", "SubjectKey" },
                unique: true,
                filter: "Status = 0");

            migrationBuilder.CreateIndex(
                name: "IX_StructuredMemories_SessionId_Status",
                table: "StructuredMemories",
                columns: new[] { "SessionId", "Status" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "StructuredMemories");
        }
    }
}
