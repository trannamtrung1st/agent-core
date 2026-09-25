using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgentCore.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class P7DefinitionLifecycle : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AgentDefinitionDrafts",
                columns: table => new
                {
                    DraftId = table.Column<string>(type: "TEXT", maxLength: 36, nullable: false),
                    DefinitionId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    Revision = table.Column<long>(type: "INTEGER", nullable: false),
                    CandidateJson = table.Column<string>(type: "TEXT", nullable: false),
                    SourceKind = table.Column<int>(type: "INTEGER", nullable: false),
                    SourceVersion = table.Column<int>(type: "INTEGER", nullable: true),
                    CreatedAtUtc = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAtUtc = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AgentDefinitionDrafts", x => x.DraftId);
                });

            migrationBuilder.CreateTable(
                name: "AgentDefinitionPublications",
                columns: table => new
                {
                    DefinitionId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    Version = table.Column<int>(type: "INTEGER", nullable: false),
                    PayloadJson = table.Column<string>(type: "TEXT", nullable: false),
                    SourceDraftRevision = table.Column<long>(type: "INTEGER", nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    MetadataRevision = table.Column<long>(type: "INTEGER", nullable: false),
                    PublishedAtUtc = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AgentDefinitionPublications", x => new { x.DefinitionId, x.Version });
                });

            migrationBuilder.CreateIndex(
                name: "IX_AgentDefinitionDrafts_DefinitionId",
                table: "AgentDefinitionDrafts",
                column: "DefinitionId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AgentDefinitionDrafts");

            migrationBuilder.DropTable(
                name: "AgentDefinitionPublications");
        }
    }
}
