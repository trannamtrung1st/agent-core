using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgentCore.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AccountScopedEmail : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Purpose",
                table: "Credentials",
                type: "TEXT",
                nullable: false,
                defaultValue: "General");

            migrationBuilder.CreateTable(
                name: "EmailAccountProfiles",
                columns: table => new
                {
                    CredentialId = table.Column<string>(type: "TEXT", nullable: false),
                    ProfileJson = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EmailAccountProfiles", x => x.CredentialId);
                    table.ForeignKey(
                        name: "FK_EmailAccountProfiles_Credentials_CredentialId",
                        column: x => x.CredentialId,
                        principalTable: "Credentials",
                        principalColumn: "CredentialId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "EmailAccountSelections",
                columns: table => new
                {
                    RequestId = table.Column<string>(type: "TEXT", nullable: false),
                    SelectionJson = table.Column<string>(type: "TEXT", nullable: false),
                    Status = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EmailAccountSelections", x => x.RequestId);
                });

            migrationBuilder.CreateTable(
                name: "EmailDrafts",
                columns: table => new
                {
                    DraftId = table.Column<string>(type: "TEXT", nullable: false),
                    AgentInstanceId = table.Column<string>(type: "TEXT", nullable: false),
                    SessionId = table.Column<string>(type: "TEXT", nullable: false),
                    DraftJson = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAtUtc = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EmailDrafts", x => x.DraftId);
                });

            migrationBuilder.CreateTable(
                name: "EmailSendOperations",
                columns: table => new
                {
                    DraftId = table.Column<string>(type: "TEXT", nullable: false),
                    AccountId = table.Column<string>(type: "TEXT", nullable: false),
                    DraftRevision = table.Column<long>(type: "INTEGER", nullable: false),
                    ApprovalId = table.Column<string>(type: "TEXT", nullable: false),
                    ActionHash = table.Column<string>(type: "TEXT", nullable: false),
                    ResultJson = table.Column<string>(type: "TEXT", nullable: true),
                    ClaimedAtUtc = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EmailSendOperations", x => x.DraftId);
                    table.ForeignKey(
                        name: "FK_EmailSendOperations_EmailDrafts_DraftId",
                        column: x => x.DraftId,
                        principalTable: "EmailDrafts",
                        principalColumn: "DraftId",
                        onDelete: ReferentialAction.Restrict);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "EmailAccountProfiles");

            migrationBuilder.DropTable(
                name: "EmailAccountSelections");

            migrationBuilder.DropTable(
                name: "EmailSendOperations");

            migrationBuilder.DropTable(
                name: "EmailDrafts");

            migrationBuilder.DropColumn(
                name: "Purpose",
                table: "Credentials");
        }
    }
}
