using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgentCore.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class DefinitionResourceIdentityScope : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropPrimaryKey(
                name: "PK_AgentDefinitionDraftResources",
                table: "AgentDefinitionDraftResources");

            migrationBuilder.AddPrimaryKey(
                name: "PK_AgentDefinitionDraftResources",
                table: "AgentDefinitionDraftResources",
                columns: new[] { "DraftId", "ResourceId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            throw new NotSupportedException("Resource identity rollback requires restoring a pre-cutover backup; resource IDs can be retained across drafts.");
        }
    }
}
