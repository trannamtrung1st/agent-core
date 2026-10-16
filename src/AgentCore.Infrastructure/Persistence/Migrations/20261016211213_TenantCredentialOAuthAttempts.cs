using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgentCore.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class TenantCredentialOAuthAttempts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_EmailAccountProfiles_GoogleSubject",
                table: "EmailAccountProfiles");

            migrationBuilder.DropIndex(
                name: "IX_DriveAccountProfiles_GoogleSubject",
                table: "DriveAccountProfiles");

            migrationBuilder.AddColumn<Guid>(
                name: "TenantId",
                table: "EmailAccountProfiles",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "TenantId",
                table: "DriveAccountProfiles",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddUniqueConstraint(
                name: "AK_TenantUsers_TenantId_PrincipalId_AccountId",
                table: "TenantUsers",
                columns: new[] { "TenantId", "PrincipalId", "AccountId" });

            migrationBuilder.AddUniqueConstraint(
                name: "AK_AuthenticationSessions_SessionId_AccountId",
                table: "AuthenticationSessions",
                columns: new[] { "SessionId", "AccountId" });

            migrationBuilder.CreateTable(
                name: "CredentialOAuthAttempts",
                columns: table => new
                {
                    AttemptId = table.Column<Guid>(type: "TEXT", nullable: false),
                    TenantId = table.Column<Guid>(type: "TEXT", nullable: false),
                    CredentialId = table.Column<string>(type: "TEXT", nullable: false),
                    AccountId = table.Column<Guid>(type: "TEXT", nullable: false),
                    AuthenticationSessionId = table.Column<Guid>(type: "TEXT", nullable: false),
                    PrincipalId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Purpose = table.Column<string>(type: "TEXT", nullable: false),
                    CredentialRevision = table.Column<long>(type: "INTEGER", nullable: false),
                    AuthEpoch = table.Column<long>(type: "INTEGER", nullable: false),
                    StateHash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    CorrelationHash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    ProtectedProof = table.Column<string>(type: "TEXT", maxLength: 65536, nullable: false),
                    ProviderJson = table.Column<string>(type: "TEXT", maxLength: 8192, nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    ExpiresAt = table.Column<long>(type: "INTEGER", nullable: false),
                    Revision = table.Column<long>(type: "INTEGER", nullable: false),
                    Outcome = table.Column<int>(type: "INTEGER", nullable: false),
                    ClaimedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    CompletedAt = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CredentialOAuthAttempts", x => x.AttemptId);
                    table.ForeignKey(
                        name: "FK_CredentialOAuthAttempts_AuthenticationSessions_AuthenticationSessionId_AccountId",
                        columns: x => new { x.AuthenticationSessionId, x.AccountId },
                        principalTable: "AuthenticationSessions",
                        principalColumns: new[] { "SessionId", "AccountId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CredentialOAuthAttempts_TenantCredentials_TenantId_CredentialId",
                        columns: x => new { x.TenantId, x.CredentialId },
                        principalTable: "TenantCredentials",
                        principalColumns: new[] { "TenantId", "CredentialId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CredentialOAuthAttempts_TenantUsers_TenantId_PrincipalId_AccountId",
                        columns: x => new { x.TenantId, x.PrincipalId, x.AccountId },
                        principalTable: "TenantUsers",
                        principalColumns: new[] { "TenantId", "PrincipalId", "AccountId" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_EmailAccountProfiles_GoogleSubject",
                table: "EmailAccountProfiles",
                column: "GoogleSubject",
                unique: true,
                filter: "TenantId IS NULL AND GoogleSubject IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_EmailAccountProfiles_TenantId_CredentialId",
                table: "EmailAccountProfiles",
                columns: new[] { "TenantId", "CredentialId" });

            migrationBuilder.CreateIndex(
                name: "IX_EmailAccountProfiles_TenantId_GoogleSubject",
                table: "EmailAccountProfiles",
                columns: new[] { "TenantId", "GoogleSubject" },
                unique: true,
                filter: "GoogleSubject IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_DriveAccountProfiles_GoogleSubject",
                table: "DriveAccountProfiles",
                column: "GoogleSubject",
                unique: true,
                filter: "TenantId IS NULL AND GoogleSubject IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_DriveAccountProfiles_TenantId_CredentialId",
                table: "DriveAccountProfiles",
                columns: new[] { "TenantId", "CredentialId" });

            migrationBuilder.CreateIndex(
                name: "IX_DriveAccountProfiles_TenantId_GoogleSubject",
                table: "DriveAccountProfiles",
                columns: new[] { "TenantId", "GoogleSubject" },
                unique: true,
                filter: "GoogleSubject IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_CredentialOAuthAttempts_AuthenticationSessionId_AccountId",
                table: "CredentialOAuthAttempts",
                columns: new[] { "AuthenticationSessionId", "AccountId" });

            migrationBuilder.CreateIndex(
                name: "IX_CredentialOAuthAttempts_ExpiresAt_Outcome",
                table: "CredentialOAuthAttempts",
                columns: new[] { "ExpiresAt", "Outcome" });

            migrationBuilder.CreateIndex(
                name: "IX_CredentialOAuthAttempts_TenantId_CredentialId",
                table: "CredentialOAuthAttempts",
                columns: new[] { "TenantId", "CredentialId" },
                unique: true,
                filter: "Outcome IN (0, 1)");

            migrationBuilder.CreateIndex(
                name: "IX_CredentialOAuthAttempts_TenantId_PrincipalId_AccountId",
                table: "CredentialOAuthAttempts",
                columns: new[] { "TenantId", "PrincipalId", "AccountId" });

            migrationBuilder.AddForeignKey(
                name: "FK_DriveAccountProfiles_TenantCredentials_TenantId_CredentialId",
                table: "DriveAccountProfiles",
                columns: new[] { "TenantId", "CredentialId" },
                principalTable: "TenantCredentials",
                principalColumns: new[] { "TenantId", "CredentialId" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_EmailAccountProfiles_TenantCredentials_TenantId_CredentialId",
                table: "EmailAccountProfiles",
                columns: new[] { "TenantId", "CredentialId" },
                principalTable: "TenantCredentials",
                principalColumns: new[] { "TenantId", "CredentialId" },
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_DriveAccountProfiles_TenantCredentials_TenantId_CredentialId",
                table: "DriveAccountProfiles");

            migrationBuilder.DropForeignKey(
                name: "FK_EmailAccountProfiles_TenantCredentials_TenantId_CredentialId",
                table: "EmailAccountProfiles");

            migrationBuilder.DropTable(
                name: "CredentialOAuthAttempts");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_TenantUsers_TenantId_PrincipalId_AccountId",
                table: "TenantUsers");

            migrationBuilder.DropIndex(
                name: "IX_EmailAccountProfiles_GoogleSubject",
                table: "EmailAccountProfiles");

            migrationBuilder.DropIndex(
                name: "IX_EmailAccountProfiles_TenantId_CredentialId",
                table: "EmailAccountProfiles");

            migrationBuilder.DropIndex(
                name: "IX_EmailAccountProfiles_TenantId_GoogleSubject",
                table: "EmailAccountProfiles");

            migrationBuilder.DropIndex(
                name: "IX_DriveAccountProfiles_GoogleSubject",
                table: "DriveAccountProfiles");

            migrationBuilder.DropIndex(
                name: "IX_DriveAccountProfiles_TenantId_CredentialId",
                table: "DriveAccountProfiles");

            migrationBuilder.DropIndex(
                name: "IX_DriveAccountProfiles_TenantId_GoogleSubject",
                table: "DriveAccountProfiles");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_AuthenticationSessions_SessionId_AccountId",
                table: "AuthenticationSessions");

            migrationBuilder.DropColumn(
                name: "TenantId",
                table: "EmailAccountProfiles");

            migrationBuilder.DropColumn(
                name: "TenantId",
                table: "DriveAccountProfiles");

            migrationBuilder.CreateIndex(
                name: "IX_EmailAccountProfiles_GoogleSubject",
                table: "EmailAccountProfiles",
                column: "GoogleSubject",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DriveAccountProfiles_GoogleSubject",
                table: "DriveAccountProfiles",
                column: "GoogleSubject",
                unique: true);
        }
    }
}
