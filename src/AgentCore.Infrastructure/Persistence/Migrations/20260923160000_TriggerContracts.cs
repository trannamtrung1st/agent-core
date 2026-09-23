using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgentCore.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class TriggerContracts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "TriggerOccurrences",
                columns: table => new
                {
                    OccurrenceId = table.Column<string>(type: "TEXT", maxLength: 36, nullable: false),
                    DedupeKey = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    RegistrationId = table.Column<string>(type: "TEXT", maxLength: 36, nullable: true),
                    AgentInstanceId = table.Column<string>(type: "TEXT", maxLength: 36, nullable: false),
                    ProfileId = table.Column<string>(type: "TEXT", maxLength: 36, nullable: false),
                    SourceKind = table.Column<int>(type: "INTEGER", nullable: false),
                    ScheduledAtUtc = table.Column<long>(type: "INTEGER", nullable: true),
                    ObservedAtUtc = table.Column<long>(type: "INTEGER", nullable: false),
                    AdmittedAtUtc = table.Column<long>(type: "INTEGER", nullable: false),
                    EvidenceJson = table.Column<string>(type: "TEXT", maxLength: 4096, nullable: false),
                    SourceEventId = table.Column<string>(type: "TEXT", maxLength: 36, nullable: true),
                    ScheduleRevision = table.Column<long>(type: "INTEGER", nullable: true),
                    Disposition = table.Column<int>(type: "INTEGER", nullable: false),
                    DispositionReason = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    RoutingRevision = table.Column<long>(type: "INTEGER", nullable: false),
                    RoutingUpdatedAtUtc = table.Column<long>(type: "INTEGER", nullable: true),
                    ClaimId = table.Column<string>(type: "TEXT", maxLength: 36, nullable: true),
                    ClaimLeaseExpiresAtUtc = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TriggerOccurrences", x => x.OccurrenceId);
                });

            migrationBuilder.CreateTable(
                name: "TriggerRegistrations",
                columns: table => new
                {
                    RegistrationId = table.Column<string>(type: "TEXT", maxLength: 36, nullable: false),
                    AgentInstanceId = table.Column<string>(type: "TEXT", maxLength: 36, nullable: false),
                    ProfileId = table.Column<string>(type: "TEXT", maxLength: 36, nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    Intent = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    ScheduleKind = table.Column<int>(type: "INTEGER", nullable: false),
                    ScheduleJson = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: false),
                    NextOccurrenceAtUtc = table.Column<long>(type: "INTEGER", nullable: true),
                    ExpiresAtUtc = table.Column<long>(type: "INTEGER", nullable: true),
                    OccurrenceCount = table.Column<int>(type: "INTEGER", nullable: false),
                    Revision = table.Column<long>(type: "INTEGER", nullable: false),
                    ScheduleRevision = table.Column<long>(type: "INTEGER", nullable: false),
                    AuthorizationOrigin = table.Column<int>(type: "INTEGER", nullable: false),
                    SourceSessionId = table.Column<string>(type: "TEXT", maxLength: 36, nullable: true),
                    SourceEventId = table.Column<string>(type: "TEXT", maxLength: 36, nullable: true),
                    CreatedAtUtc = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAtUtc = table.Column<long>(type: "INTEGER", nullable: false),
                    SuspensionReason = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TriggerRegistrations", x => x.RegistrationId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_TriggerOccurrences_AgentInstanceId_ProfileId_Disposition",
                table: "TriggerOccurrences",
                columns: new[] { "AgentInstanceId", "ProfileId", "Disposition" });

            migrationBuilder.CreateIndex(
                name: "IX_TriggerOccurrences_DedupeKey",
                table: "TriggerOccurrences",
                column: "DedupeKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TriggerOccurrences_Disposition_ClaimLeaseExpiresAtUtc",
                table: "TriggerOccurrences",
                columns: new[] { "Disposition", "ClaimLeaseExpiresAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_TriggerRegistrations_AgentInstanceId_ProfileId_Status",
                table: "TriggerRegistrations",
                columns: new[] { "AgentInstanceId", "ProfileId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_TriggerRegistrations_Status_NextOccurrenceAtUtc_RegistrationId",
                table: "TriggerRegistrations",
                columns: new[] { "Status", "NextOccurrenceAtUtc", "RegistrationId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TriggerOccurrences");

            migrationBuilder.DropTable(
                name: "TriggerRegistrations");
        }
    }
}
