using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgentCore.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class TenantSandboxVerificationOwnership : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "AttemptId",
                table: "SandboxProfileVerifications",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "TenantId",
                table: "SandboxProfileVerifications",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "TenantSandboxVerificationAttempts",
                columns: table => new
                {
                    AttemptId = table.Column<Guid>(type: "TEXT", nullable: false),
                    TenantId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ProfileId = table.Column<string>(type: "TEXT", nullable: false),
                    ProfileRevision = table.Column<long>(type: "INTEGER", nullable: false),
                    Revision = table.Column<long>(type: "INTEGER", nullable: false),
                    SecurityRevision = table.Column<long>(type: "INTEGER", nullable: false),
                    Active = table.Column<bool>(type: "INTEGER", nullable: false),
                    LeaseExpiresAt = table.Column<long>(type: "INTEGER", nullable: false),
                    CleanupClaimExpiresAt = table.Column<long>(type: "INTEGER", nullable: true),
                    StartedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    Json = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TenantSandboxVerificationAttempts", x => x.AttemptId);
                    table.UniqueConstraint("AK_TenantSandboxVerificationAttempts_TenantId_ProfileId_ProfileRevision_AttemptId", x => new { x.TenantId, x.ProfileId, x.ProfileRevision, x.AttemptId });
                    table.ForeignKey(
                        name: "FK_TenantSandboxVerificationAttempts_SandboxProfiles_ProfileId_ProfileRevision",
                        columns: x => new { x.ProfileId, x.ProfileRevision },
                        principalTable: "SandboxProfiles",
                        principalColumns: new[] { "Id", "Revision" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TenantSandboxVerificationAttempts_TenantSandboxProfiles_TenantId_ProfileId",
                        columns: x => new { x.TenantId, x.ProfileId },
                        principalTable: "TenantSandboxProfiles",
                        principalColumns: new[] { "TenantId", "ProfileId" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SandboxProfileVerifications_TenantId_ProfileId_ProfileRevision_AttemptId",
                table: "SandboxProfileVerifications",
                columns: new[] { "TenantId", "ProfileId", "ProfileRevision", "AttemptId" });

            migrationBuilder.AddCheckConstraint(
                name: "CK_SandboxVerification_TenantJson",
                table: "SandboxProfileVerifications",
                sql: "(TenantId IS NULL AND AttemptId IS NULL AND json_extract(Json, '$.tenantId') IS NULL) OR (TenantId IS NOT NULL AND AttemptId IS NOT NULL AND json_type(Json, '$.tenantId') IS 'text' AND lower(json_extract(Json, '$.tenantId')) = lower(TenantId))");

            migrationBuilder.CreateIndex(
                name: "IX_TenantSandboxVerificationAttempts_Active_LeaseExpiresAt",
                table: "TenantSandboxVerificationAttempts",
                columns: new[] { "Active", "LeaseExpiresAt" });

            migrationBuilder.CreateIndex(
                name: "IX_TenantSandboxVerificationAttempts_ProfileId_ProfileRevision",
                table: "TenantSandboxVerificationAttempts",
                columns: new[] { "ProfileId", "ProfileRevision" });

            migrationBuilder.CreateIndex(
                name: "IX_TenantSandboxVerificationAttempts_TenantId_ProfileId",
                table: "TenantSandboxVerificationAttempts",
                columns: new[] { "TenantId", "ProfileId" },
                unique: true,
                filter: "Active = 1");

            migrationBuilder.CreateIndex(
                name: "IX_TenantSandboxVerificationAttempts_TenantId_ProfileId_SecurityRevision",
                table: "TenantSandboxVerificationAttempts",
                columns: new[] { "TenantId", "ProfileId", "SecurityRevision" });

            migrationBuilder.AddForeignKey(
                name: "FK_SandboxProfileVerifications_TenantSandboxVerificationAttempts_TenantId_ProfileId_ProfileRevision_AttemptId",
                table: "SandboxProfileVerifications",
                columns: new[] { "TenantId", "ProfileId", "ProfileRevision", "AttemptId" },
                principalTable: "TenantSandboxVerificationAttempts",
                principalColumns: new[] { "TenantId", "ProfileId", "ProfileRevision", "AttemptId" },
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_SandboxProfileVerifications_TenantSandboxVerificationAttempts_TenantId_ProfileId_ProfileRevision_AttemptId",
                table: "SandboxProfileVerifications");

            migrationBuilder.DropTable(
                name: "TenantSandboxVerificationAttempts");

            migrationBuilder.DropIndex(
                name: "IX_SandboxProfileVerifications_TenantId_ProfileId_ProfileRevision_AttemptId",
                table: "SandboxProfileVerifications");

            migrationBuilder.DropCheckConstraint(
                name: "CK_SandboxVerification_TenantJson",
                table: "SandboxProfileVerifications");

            migrationBuilder.DropColumn(
                name: "AttemptId",
                table: "SandboxProfileVerifications");

            migrationBuilder.DropColumn(
                name: "TenantId",
                table: "SandboxProfileVerifications");
        }
    }
}
