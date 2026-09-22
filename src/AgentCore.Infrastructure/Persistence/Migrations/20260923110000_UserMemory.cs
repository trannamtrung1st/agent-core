using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgentCore.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class UserMemory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_StructuredMemories_UserOwner_Kind_SubjectKey",
                table: "StructuredMemories",
                columns: new[] { "OwnerProfileId", "Kind", "SubjectKey" },
                unique: true,
                filter: "Status = 0 AND Scope = 2");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_StructuredMemories_UserOwner_Kind_SubjectKey",
                table: "StructuredMemories");
        }
    }
}
