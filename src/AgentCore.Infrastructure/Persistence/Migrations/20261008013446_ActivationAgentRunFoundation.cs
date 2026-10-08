using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgentCore.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ActivationAgentRunFoundation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "OriginJson",
                table: "Sessions",
                type: "TEXT",
                nullable: false,
                defaultValue: "{\"kind\":0}");

            migrationBuilder.AddColumn<int>(
                name: "Surfaces",
                table: "Sessions",
                type: "INTEGER",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.CreateTable(
                name: "Activations",
                columns: table => new
                {
                    ActivationId = table.Column<string>(type: "TEXT", nullable: false),
                    SessionId = table.Column<string>(type: "TEXT", nullable: false),
                    DedupeKey = table.Column<string>(type: "TEXT", nullable: false),
                    BackgroundSourceKey = table.Column<string>(type: "TEXT", nullable: true),
                    AdmissionHash = table.Column<string>(type: "TEXT", nullable: false),
                    AgentInstanceId = table.Column<string>(type: "TEXT", nullable: false),
                    ProfileId = table.Column<string>(type: "TEXT", nullable: false),
                    PayloadJson = table.Column<string>(type: "TEXT", nullable: false),
                    AdmittedAtUtc = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Activations", x => x.ActivationId);
                    table.ForeignKey(
                        name: "FK_Activations_Sessions_SessionId",
                        column: x => x.SessionId,
                        principalTable: "Sessions",
                        principalColumn: "SessionId",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ActivationSourceEntries",
                columns: table => new
                {
                    SessionId = table.Column<string>(type: "TEXT", nullable: false),
                    EntryId = table.Column<string>(type: "TEXT", nullable: false),
                    ActivationId = table.Column<string>(type: "TEXT", nullable: false),
                    Ordinal = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ActivationSourceEntries", x => new { x.SessionId, x.EntryId });
                    table.ForeignKey(
                        name: "FK_ActivationSourceEntries_Activations_ActivationId",
                        column: x => x.ActivationId,
                        principalTable: "Activations",
                        principalColumn: "ActivationId",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ActivationSourceEntries_ConversationEntries_EntryId",
                        column: x => x.EntryId,
                        principalTable: "ConversationEntries",
                        principalColumn: "EntryId",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "AgentRuns",
                columns: table => new
                {
                    AgentRunId = table.Column<string>(type: "TEXT", nullable: false),
                    ActivationId = table.Column<string>(type: "TEXT", nullable: false),
                    SessionId = table.Column<string>(type: "TEXT", nullable: false),
                    AgentInstanceId = table.Column<string>(type: "TEXT", nullable: false),
                    ProfileId = table.Column<string>(type: "TEXT", nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    Revision = table.Column<long>(type: "INTEGER", nullable: false),
                    NextRetryAtUtc = table.Column<long>(type: "INTEGER", nullable: true),
                    LeaseExpiresAtUtc = table.Column<long>(type: "INTEGER", nullable: true),
                    ApprovalExpiresAtUtc = table.Column<long>(type: "INTEGER", nullable: true),
                    CreatedAtUtc = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAtUtc = table.Column<long>(type: "INTEGER", nullable: false),
                    PayloadJson = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AgentRuns", x => x.AgentRunId);
                    table.ForeignKey(
                        name: "FK_AgentRuns_Activations_ActivationId",
                        column: x => x.ActivationId,
                        principalTable: "Activations",
                        principalColumn: "ActivationId",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_AgentRuns_Sessions_SessionId",
                        column: x => x.SessionId,
                        principalTable: "Sessions",
                        principalColumn: "SessionId",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ActivationSourceEntries_ActivationId_Ordinal",
                table: "ActivationSourceEntries",
                columns: new[] { "ActivationId", "Ordinal" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ActivationSourceEntries_EntryId",
                table: "ActivationSourceEntries",
                column: "EntryId");

            migrationBuilder.CreateIndex(
                name: "IX_Activations_AgentInstanceId_ProfileId_BackgroundSourceKey",
                table: "Activations",
                columns: new[] { "AgentInstanceId", "ProfileId", "BackgroundSourceKey" },
                unique: true,
                filter: "BackgroundSourceKey IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Activations_SessionId_DedupeKey",
                table: "Activations",
                columns: new[] { "SessionId", "DedupeKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AgentRuns_ActivationId",
                table: "AgentRuns",
                column: "ActivationId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AgentRuns_AgentInstanceId_ProfileId_SessionId_CreatedAtUtc",
                table: "AgentRuns",
                columns: new[] { "AgentInstanceId", "ProfileId", "SessionId", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_AgentRuns_SessionId",
                table: "AgentRuns",
                column: "SessionId");

            migrationBuilder.CreateIndex(
                name: "IX_AgentRuns_Status_NextRetryAtUtc_CreatedAtUtc",
                table: "AgentRuns",
                columns: new[] { "Status", "NextRetryAtUtc", "CreatedAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ActivationSourceEntries");

            migrationBuilder.DropTable(
                name: "AgentRuns");

            migrationBuilder.DropTable(
                name: "Activations");

            migrationBuilder.DropColumn(
                name: "OriginJson",
                table: "Sessions");

            migrationBuilder.DropColumn(
                name: "Surfaces",
                table: "Sessions");
        }
    }
}
