using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgentCore.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AgentInstanceWorkspace : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AgentWorkspaceItems",
                columns: table => new
                {
                    ItemId = table.Column<string>(type: "TEXT", maxLength: 36, nullable: false),
                    AgentInstanceId = table.Column<string>(type: "TEXT", maxLength: 36, nullable: false),
                    PathKey = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                    Revision = table.Column<long>(type: "INTEGER", nullable: false),
                    ByteSize = table.Column<long>(type: "INTEGER", nullable: false),
                    BlobKey = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    MetadataJson = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AgentWorkspaceItems", x => x.ItemId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AgentWorkspaceItems_AgentInstanceId_PathKey",
                table: "AgentWorkspaceItems",
                columns: new[] { "AgentInstanceId", "PathKey" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AgentWorkspaceItems");
        }
    }
}
