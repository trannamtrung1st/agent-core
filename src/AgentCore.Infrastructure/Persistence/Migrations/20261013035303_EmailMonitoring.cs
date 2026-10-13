using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgentCore.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class EmailMonitoring : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "EmailWatchEvents",
                columns: table => new
                {
                    EventId = table.Column<string>(type: "TEXT", nullable: false),
                    AuthorityJson = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EmailWatchEvents", x => x.EventId);
                    table.ForeignKey(
                        name: "FK_EmailWatchEvents_CoreEvents_EventId",
                        column: x => x.EventId,
                        principalTable: "CoreEvents",
                        principalColumn: "EventId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "EmailWatches",
                columns: table => new
                {
                    AccountId = table.Column<string>(type: "TEXT", nullable: false),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    NextCheckAtUtc = table.Column<long>(type: "INTEGER", nullable: false),
                    RuntimeRevision = table.Column<long>(type: "INTEGER", nullable: false),
                    StateJson = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EmailWatches", x => x.AccountId);
                    table.ForeignKey(
                        name: "FK_EmailWatches_Credentials_AccountId",
                        column: x => x.AccountId,
                        principalTable: "Credentials",
                        principalColumn: "CredentialId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_EmailWatches_Enabled_NextCheckAtUtc",
                table: "EmailWatches",
                columns: new[] { "Enabled", "NextCheckAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "EmailWatchEvents");

            migrationBuilder.DropTable(
                name: "EmailWatches");
        }
    }
}
