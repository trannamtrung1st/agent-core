using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgentCore.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class MemoryScope : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_StructuredMemories_SessionId_Kind_SubjectKey",
                table: "StructuredMemories");

            migrationBuilder.AddColumn<string>(
                name: "OriginMemoryId",
                table: "StructuredMemories",
                type: "TEXT",
                maxLength: 36,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "OriginSessionId",
                table: "StructuredMemories",
                type: "TEXT",
                maxLength: 36,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "OwnerInstanceId",
                table: "StructuredMemories",
                type: "TEXT",
                maxLength: 36,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "OwnerProfileId",
                table: "StructuredMemories",
                type: "TEXT",
                maxLength: 36,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Scope",
                table: "StructuredMemories",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateIndex(
                name: "IX_StructuredMemories_OwnerInstanceId_OwnerProfileId_Kind_SubjectKey",
                table: "StructuredMemories",
                columns: new[] { "OwnerInstanceId", "OwnerProfileId", "Kind", "SubjectKey" },
                unique: true,
                filter: "Status = 0 AND Scope = 1");

            migrationBuilder.CreateIndex(
                name: "IX_StructuredMemories_SessionId_Kind_SubjectKey",
                table: "StructuredMemories",
                columns: new[] { "SessionId", "Kind", "SubjectKey" },
                unique: true,
                filter: "Status = 0 AND Scope = 0");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_StructuredMemories_OwnerInstanceId_OwnerProfileId_Kind_SubjectKey",
                table: "StructuredMemories");

            migrationBuilder.DropIndex(
                name: "IX_StructuredMemories_SessionId_Kind_SubjectKey",
                table: "StructuredMemories");

            migrationBuilder.DropColumn(
                name: "OriginMemoryId",
                table: "StructuredMemories");

            migrationBuilder.DropColumn(
                name: "OriginSessionId",
                table: "StructuredMemories");

            migrationBuilder.DropColumn(
                name: "OwnerInstanceId",
                table: "StructuredMemories");

            migrationBuilder.DropColumn(
                name: "OwnerProfileId",
                table: "StructuredMemories");

            migrationBuilder.DropColumn(
                name: "Scope",
                table: "StructuredMemories");

            migrationBuilder.CreateIndex(
                name: "IX_StructuredMemories_SessionId_Kind_SubjectKey",
                table: "StructuredMemories",
                columns: new[] { "SessionId", "Kind", "SubjectKey" },
                unique: true,
                filter: "Status = 0");
        }
    }
}
