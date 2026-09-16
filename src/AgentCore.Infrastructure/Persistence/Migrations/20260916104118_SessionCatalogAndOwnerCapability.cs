using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgentCore.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class SessionCatalogAndOwnerCapability : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "ArchivedAtUtc",
                table: "Sessions",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "DurablyDeletedAtUtc",
                table: "Sessions",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "RuntimeEpoch",
                table: "Sessions",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<string>(
                name: "Title",
                table: "Sessions",
                type: "TEXT",
                maxLength: 200,
                nullable: false,
                defaultValue: "New chat");

            migrationBuilder.AddColumn<bool>(
                name: "WorkspaceOwned",
                table: "Sessions",
                type: "INTEGER",
                nullable: false,
                defaultValue: true);

            migrationBuilder.CreateTable(
                name: "OwnerCapabilities",
                columns: table => new
                {
                    TokenHash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    CreatedAtUtc = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OwnerCapabilities", x => x.TokenHash);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Sessions_DurablyDeletedAtUtc_ArchivedAtUtc_UpdatedAtUtc_SessionId",
                table: "Sessions",
                columns: new[] { "DurablyDeletedAtUtc", "ArchivedAtUtc", "UpdatedAtUtc", "SessionId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "OwnerCapabilities");

            migrationBuilder.DropIndex(
                name: "IX_Sessions_DurablyDeletedAtUtc_ArchivedAtUtc_UpdatedAtUtc_SessionId",
                table: "Sessions");

            migrationBuilder.DropColumn(
                name: "ArchivedAtUtc",
                table: "Sessions");

            migrationBuilder.DropColumn(
                name: "DurablyDeletedAtUtc",
                table: "Sessions");

            migrationBuilder.DropColumn(
                name: "RuntimeEpoch",
                table: "Sessions");

            migrationBuilder.DropColumn(
                name: "Title",
                table: "Sessions");

            migrationBuilder.DropColumn(
                name: "WorkspaceOwned",
                table: "Sessions");
        }
    }
}
