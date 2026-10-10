using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgentCore.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ScopedInstanceConfiguration : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "SettingsOverridesJson",
                table: "AgentInstances",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "EnabledOverride",
                table: "AgentDefinitionSkillStates",
                type: "INTEGER",
                nullable: true);

            // Revision 1 is untouched initialization; preserve modified or ambiguous historical records.
            migrationBuilder.Sql("UPDATE AgentDefinitionSkillStates SET EnabledOverride = CASE WHEN Revision = 1 THEN NULL ELSE Enabled END;");
            migrationBuilder.DropColumn(name: "Enabled", table: "AgentDefinitionSkillStates");

            migrationBuilder.CreateTable(
                name: "AgentDefinitionResourceStates",
                columns: table => new
                {
                    InstanceId = table.Column<string>(type: "TEXT", maxLength: 36, nullable: false),
                    ResourceId = table.Column<string>(type: "TEXT", maxLength: 36, nullable: false),
                    EnabledOverride = table.Column<bool>(type: "INTEGER", nullable: true),
                    Revision = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAtUtc = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AgentDefinitionResourceStates", x => new { x.InstanceId, x.ResourceId });
                    table.ForeignKey(
                        name: "FK_AgentDefinitionResourceStates_AgentInstances_InstanceId",
                        column: x => x.InstanceId,
                        principalTable: "AgentInstances",
                        principalColumn: "InstanceId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AgentInstanceResources",
                columns: table => new
                {
                    InstanceId = table.Column<string>(type: "TEXT", maxLength: 36, nullable: false),
                    ResourceId = table.Column<string>(type: "TEXT", maxLength: 36, nullable: false),
                    LogicalPath = table.Column<string>(type: "TEXT", maxLength: 240, nullable: false),
                    Revision = table.Column<long>(type: "INTEGER", nullable: false),
                    PayloadJson = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AgentInstanceResources", x => new { x.InstanceId, x.ResourceId });
                    table.ForeignKey(
                        name: "FK_AgentInstanceResources_AgentInstances_InstanceId",
                        column: x => x.InstanceId,
                        principalTable: "AgentInstances",
                        principalColumn: "InstanceId",
                        onDelete: ReferentialAction.Cascade);
                });

            // Preserve exact historical nonterminal execution; creation pins were its original authority.
            // Never resolve mutable Instance settings during this upgrade.
            migrationBuilder.Sql("""
                UPDATE AgentRuns SET PayloadJson = json_set(PayloadJson, '$.admission.configuration', json_object(
                    'definition', json((SELECT DefinitionJson FROM Sessions WHERE SessionId = AgentRuns.SessionId)),
                    'instanceRevision', 0,
                    'personaRevision', coalesce((SELECT PinnedPersonaRevision FROM Sessions WHERE SessionId = AgentRuns.SessionId), 0),
                    'resources', json(coalesce((SELECT json_group_array(json_object(
                        'key', 'definition:' || ResourceId, 'logicalPath', LogicalPath, 'kind', Kind,
                        'mediaType', MediaType, 'contentSha256', ContentSha256, 'byteLength', ByteLength,
                        'virtualPath', '/agent/resources/' || LogicalPath))
                        FROM AgentDefinitionPublicationResources
                        WHERE DefinitionId = json_extract(AgentRuns.PayloadJson, '$.admission.definitionId')
                            AND Version = json_extract(AgentRuns.PayloadJson, '$.admission.definitionVersion')), '[]'))))
                WHERE Status IN (0, 1, 2, 3, 7) AND json_extract(PayloadJson, '$.admission.configuration') IS NULL
                    AND EXISTS (SELECT 1 FROM Sessions WHERE SessionId = AgentRuns.SessionId
                        AND json_extract(DefinitionJson, '$.id') = json_extract(AgentRuns.PayloadJson, '$.admission.definitionId')
                        AND json_extract(DefinitionJson, '$.version') = json_extract(AgentRuns.PayloadJson, '$.admission.definitionVersion'));
                """);
            // Exact reconstruction is impossible for a mismatched/missing historical Session pin. Fail closed,
            // retaining its checkpoint, effect evidence and budget/cleanup pin for owner inspection.
            migrationBuilder.Sql("""
                UPDATE AgentRuns SET Status = 5, Revision = Revision + 1,
                    NextRetryAtUtc = NULL, LeaseExpiresAtUtc = NULL, ApprovalExpiresAtUtc = NULL,
                    PayloadJson = json_set(PayloadJson, '$.claim', NULL, '$.approval', NULL, '$.wait', NULL,
                        '$.cancellationRequested', json('false'), '$.cancellationRequestedAtUtc', NULL, '$.result', NULL,
                        '$.failure', json_object('code', 'configuration-migration-unavailable',
                            'summary', 'Historical execution configuration could not be reconstructed; owner review is required.',
                            'failedAtUtc', strftime('%Y-%m-%dT%H:%M:%fZ', UpdatedAtUtc / 1000.0, 'unixepoch')))
                WHERE Status IN (0, 1, 2, 3, 7) AND json_extract(PayloadJson, '$.admission.configuration') IS NULL;
                """);

            migrationBuilder.CreateIndex(
                name: "IX_AgentInstanceResources_InstanceId_LogicalPath",
                table: "AgentInstanceResources",
                columns: new[] { "InstanceId", "LogicalPath" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            throw new System.NotSupportedException("Scoped configuration contains owned data and immutable Run evidence. Restore a pre-upgrade backup instead of a destructive downgrade.");
        }
    }
}
