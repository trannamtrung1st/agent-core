using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgentCore.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class P7DefinitionResources : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AgentDefinitionDraftResources",
                columns: table => new
                {
                    ResourceId = table.Column<string>(type: "TEXT", maxLength: 36, nullable: false),
                    DraftId = table.Column<string>(type: "TEXT", maxLength: 36, nullable: false),
                    LogicalPath = table.Column<string>(type: "TEXT", maxLength: 240, nullable: false),
                    Kind = table.Column<int>(type: "INTEGER", nullable: false),
                    MediaType = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    ContentSha256 = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    ByteLength = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAtUtc = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AgentDefinitionDraftResources", x => x.ResourceId);
                });

            migrationBuilder.CreateTable(
                name: "AgentDefinitionPublicationResources",
                columns: table => new
                {
                    DefinitionId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    Version = table.Column<int>(type: "INTEGER", nullable: false),
                    ResourceId = table.Column<string>(type: "TEXT", maxLength: 36, nullable: false),
                    LogicalPath = table.Column<string>(type: "TEXT", maxLength: 240, nullable: false),
                    Kind = table.Column<int>(type: "INTEGER", nullable: false),
                    MediaType = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    ContentSha256 = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    ByteLength = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AgentDefinitionPublicationResources", x => new { x.DefinitionId, x.Version, x.ResourceId });
                });

            migrationBuilder.CreateIndex(
                name: "IX_AgentDefinitionDraftResources_DraftId",
                table: "AgentDefinitionDraftResources",
                column: "DraftId");

            migrationBuilder.CreateIndex(
                name: "IX_AgentDefinitionDraftResources_DraftId_LogicalPath",
                table: "AgentDefinitionDraftResources",
                columns: new[] { "DraftId", "LogicalPath" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AgentDefinitionPublicationResources_DefinitionId_Version_LogicalPath",
                table: "AgentDefinitionPublicationResources",
                columns: new[] { "DefinitionId", "Version", "LogicalPath" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AgentDefinitionDraftResources");

            migrationBuilder.DropTable(
                name: "AgentDefinitionPublicationResources");
        }
    }
}
