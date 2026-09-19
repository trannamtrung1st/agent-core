using AgentCore.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgentCore.Infrastructure.Persistence.Migrations
{
    [DbContext(typeof(AgentCoreDbContext))]
    [Migration("20260919080000_SessionModelSelection")]
    public partial class SessionModelSelection : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ModelCatalogKey",
                table: "SessionSnapshots",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ModelProviderAlias",
                table: "SessionSnapshots",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ModelId",
                table: "SessionSnapshots",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ModelSelectionSource",
                table: "SessionSnapshots",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ModelReasoningEffort",
                table: "SessionSnapshots",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ModelCatalogKey",
                table: "ConversationEntries",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ModelProviderAlias",
                table: "ConversationEntries",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ModelId",
                table: "ConversationEntries",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ModelReasoningEffort",
                table: "ConversationEntries",
                type: "TEXT",
                nullable: true);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(name: "ModelCatalogKey", table: "SessionSnapshots");
            migrationBuilder.DropColumn(name: "ModelProviderAlias", table: "SessionSnapshots");
            migrationBuilder.DropColumn(name: "ModelId", table: "SessionSnapshots");
            migrationBuilder.DropColumn(name: "ModelSelectionSource", table: "SessionSnapshots");
            migrationBuilder.DropColumn(name: "ModelReasoningEffort", table: "SessionSnapshots");
            migrationBuilder.DropColumn(name: "ModelCatalogKey", table: "ConversationEntries");
            migrationBuilder.DropColumn(name: "ModelProviderAlias", table: "ConversationEntries");
            migrationBuilder.DropColumn(name: "ModelId", table: "ConversationEntries");
            migrationBuilder.DropColumn(name: "ModelReasoningEffort", table: "ConversationEntries");
        }
    }
}
