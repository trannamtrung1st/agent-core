using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgentCore.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class GmailOAuthAccounts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "GoogleSubject",
                table: "EmailAccountProfiles",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "SecretRevision",
                table: "Credentials",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.CreateIndex(
                name: "IX_EmailAccountProfiles_GoogleSubject",
                table: "EmailAccountProfiles",
                column: "GoogleSubject",
                unique: true);
            migrationBuilder.Sql("""
                UPDATE Credentials SET Kind = 'Token', ProtectedPayload = '', Revision = Revision + 1, SecretRevision = 1
                WHERE Purpose = 'EmailAccount' AND CredentialId IN
                    (SELECT CredentialId FROM EmailAccountProfiles WHERE json_extract(ProfileJson, '$.Preset') IN (0, 'Gmail'));
                UPDATE EmailAccountProfiles SET ProfileJson = json_set(ProfileJson,
                    '$.AuthMethod', 1, '$.ConnectionStatus', 0, '$.AuthEpoch', 1,
                    '$.ImapUsername', '', '$.SmtpUsername', '', '$.ImapEndpoint', NULL, '$.SmtpEndpoint', NULL)
                WHERE json_extract(ProfileJson, '$.Preset') IN (0, 'Gmail');
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_EmailAccountProfiles_GoogleSubject",
                table: "EmailAccountProfiles");

            migrationBuilder.DropColumn(
                name: "GoogleSubject",
                table: "EmailAccountProfiles");

            migrationBuilder.DropColumn(
                name: "SecretRevision",
                table: "Credentials");
        }
    }
}
