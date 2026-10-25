using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace AgentCore.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class TenantDynamicModels : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Refuse conversion of installed execution pins before changing any persistent schema/data.
            migrationBuilder.Sql("""
                CREATE TEMP TABLE DynamicModelLegacyGuard(Rows INTEGER CONSTRAINT LegacyModelDataResetRequired CHECK(Rows=0));
                INSERT INTO DynamicModelLegacyGuard SELECT
                    (SELECT count(*) FROM SessionSnapshots WHERE ModelCatalogKey IS NOT NULL OR SummaryModelCatalogKey IS NOT NULL)
                    +(SELECT count(*) FROM ConversationEntries WHERE ModelCatalogKey IS NOT NULL)
                    +(SELECT count(*) FROM AgentRuns)
                    +(SELECT count(*) FROM TriggerOccurrences WHERE ModelCatalogKey IS NOT NULL)
                    +(SELECT count(*) FROM AgentInstances WHERE UnattendedModelCatalogKey IS NOT NULL)
                    +(SELECT count(*) FROM Automations WHERE ModelOverrideCatalogKey IS NOT NULL)
                    +(SELECT count(*) FROM (
                        SELECT DefinitionJson AS Document FROM Sessions
                        UNION ALL SELECT CandidateJson FROM AgentDefinitionDrafts
                        UNION ALL SELECT PayloadJson FROM AgentDefinitionPublications
                        UNION ALL SELECT SettingsOverridesJson FROM AgentInstances WHERE SettingsOverridesJson IS NOT NULL
                        UNION ALL SELECT PayloadJson FROM TenantApprovalRequests
                        UNION ALL SELECT PayloadJson FROM CoreEvents
                        UNION ALL SELECT PayloadJson FROM CoreEventBuckets
                    ) AS LegacyDocuments WHERE NOT json_valid(Document) OR EXISTS (
                        SELECT 1 FROM json_tree(CASE WHEN json_valid(Document) THEN Document ELSE '{}' END)
                        WHERE lower(key) = 'languagemodel'
                            OR (lower(key) IN ('catalogkey','provideralias','providerlanguagemodel','modelcatalogkey','unattendedmodelcatalogkey','modeloverridecatalogkey') AND type <> 'null')
                    ));
                DROP TABLE DynamicModelLegacyGuard;
                """);
            migrationBuilder.AddColumn<string>(name: "ModelBindingJson", table: "SessionSnapshots", type: "TEXT", nullable: true);
            migrationBuilder.AddColumn<string>(name: "SummaryModelBindingJson", table: "SessionSnapshots", type: "TEXT", nullable: true);
            migrationBuilder.AddColumn<string>(name: "ModelBindingJson", table: "ConversationEntries", type: "TEXT", nullable: true);
            migrationBuilder.AddColumn<string>(name: "ModelBindingJson", table: "TriggerOccurrences", type: "TEXT", nullable: true);
            migrationBuilder.CreateTable(
                name: "ModelPresets",
                columns: table => new
                {
                    PresetId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Kind = table.Column<int>(type: "INTEGER", nullable: false),
                    ExternalModelId = table.Column<string>(type: "TEXT", nullable: false),
                    Label = table.Column<string>(type: "TEXT", nullable: false),
                    Transport = table.Column<int>(type: "INTEGER", nullable: false),
                    CapabilitiesJson = table.Column<string>(type: "TEXT", nullable: false),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    Archived = table.Column<bool>(type: "INTEGER", nullable: false),
                    Revision = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ModelPresets", x => x.PresetId);
                    table.UniqueConstraint("AK_ModelPresets_PresetId_Kind_ExternalModelId", x => new { x.PresetId, x.Kind, x.ExternalModelId });
                    table.CheckConstraint("CK_ModelPreset_Values", "Revision>=1 AND Kind BETWEEN 0 AND 4 AND Transport BETWEEN 0 AND 3 AND length(ExternalModelId) BETWEEN 1 AND 255 AND length(Label) BETWEEN 1 AND 120 AND json_valid(CapabilitiesJson)");
                });

            migrationBuilder.CreateTable(
                name: "ProviderConnections",
                columns: table => new
                {
                    ConnectionId = table.Column<Guid>(type: "TEXT", nullable: false),
                    TenantId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Kind = table.Column<int>(type: "INTEGER", nullable: false),
                    Label = table.Column<string>(type: "TEXT", nullable: false),
                    Endpoint = table.Column<string>(type: "TEXT", nullable: false),
                    CredentialId = table.Column<string>(type: "TEXT", nullable: true),
                    NoCredential = table.Column<bool>(type: "INTEGER", nullable: false),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    Revision = table.Column<long>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    LastTestAt = table.Column<long>(type: "INTEGER", nullable: true),
                    LastTestResult = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProviderConnections", x => x.ConnectionId);
                    table.UniqueConstraint("AK_ProviderConnections_TenantId_ConnectionId_Kind", x => new { x.TenantId, x.ConnectionId, x.Kind });
                    table.CheckConstraint("CK_ProviderConnection_Values", "Revision>=1 AND Kind BETWEEN 0 AND 4 AND length(Label) BETWEEN 1 AND 120 AND length(Endpoint) BETWEEN 1 AND 2048 AND ((NoCredential=1 AND CredentialId IS NULL AND Kind=4) OR (NoCredential=0 AND CredentialId IS NOT NULL))");
                    table.ForeignKey(
                        name: "FK_ProviderConnections_Credentials_CredentialId",
                        column: x => x.CredentialId,
                        principalTable: "Credentials",
                        principalColumn: "CredentialId",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ProviderConnections_TenantCredentials_TenantId_CredentialId",
                        columns: x => new { x.TenantId, x.CredentialId },
                        principalTable: "TenantCredentials",
                        principalColumns: new[] { "TenantId", "CredentialId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ProviderConnections_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "TenantId",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ModelOfferings",
                columns: table => new
                {
                    OfferingId = table.Column<Guid>(type: "TEXT", nullable: false),
                    TenantId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ConnectionId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Kind = table.Column<int>(type: "INTEGER", nullable: false),
                    PresetId = table.Column<Guid>(type: "TEXT", nullable: true),
                    ExternalModelId = table.Column<string>(type: "TEXT", nullable: false),
                    Label = table.Column<string>(type: "TEXT", nullable: false),
                    Transport = table.Column<int>(type: "INTEGER", nullable: false),
                    CapabilitiesJson = table.Column<string>(type: "TEXT", nullable: false),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    Retired = table.Column<bool>(type: "INTEGER", nullable: false),
                    Revision = table.Column<long>(type: "INTEGER", nullable: false),
                    Observation = table.Column<int>(type: "INTEGER", nullable: false),
                    LastSeenAt = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ModelOfferings", x => x.OfferingId);
                    table.UniqueConstraint("AK_ModelOfferings_TenantId_OfferingId", x => new { x.TenantId, x.OfferingId });
                    table.CheckConstraint("CK_ModelOffering_Values", "Revision>=1 AND Kind BETWEEN 0 AND 4 AND Transport BETWEEN 0 AND 3 AND Observation BETWEEN 0 AND 2 AND NOT(Retired=1 AND Enabled=1) AND length(ExternalModelId) BETWEEN 1 AND 255 AND length(Label) BETWEEN 1 AND 120 AND json_valid(CapabilitiesJson)");
                    table.ForeignKey(
                        name: "FK_ModelOfferings_ModelPresets_PresetId_Kind_ExternalModelId",
                        columns: x => new { x.PresetId, x.Kind, x.ExternalModelId },
                        principalTable: "ModelPresets",
                        principalColumns: new[] { "PresetId", "Kind", "ExternalModelId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ModelOfferings_ProviderConnections_TenantId_ConnectionId_Kind",
                        columns: x => new { x.TenantId, x.ConnectionId, x.Kind },
                        principalTable: "ProviderConnections",
                        principalColumns: new[] { "TenantId", "ConnectionId", "Kind" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "TenantModelSettings",
                columns: table => new
                {
                    TenantId = table.Column<Guid>(type: "TEXT", nullable: false),
                    DefaultOfferingId = table.Column<Guid>(type: "TEXT", nullable: true),
                    DefaultReasoningEffort = table.Column<string>(type: "TEXT", nullable: true),
                    Revision = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TenantModelSettings", x => x.TenantId);
                    table.CheckConstraint("CK_TenantModelSettings_Values", "Revision>=1 AND (DefaultOfferingId IS NOT NULL OR DefaultReasoningEffort IS NULL)");
                    table.ForeignKey(
                        name: "FK_TenantModelSettings_ModelOfferings_TenantId_DefaultOfferingId",
                        columns: x => new { x.TenantId, x.DefaultOfferingId },
                        principalTable: "ModelOfferings",
                        principalColumns: new[] { "TenantId", "OfferingId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TenantModelSettings_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "TenantId",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.InsertData(
                table: "ModelPresets",
                columns: new[] { "PresetId", "Archived", "CapabilitiesJson", "Enabled", "ExternalModelId", "Kind", "Label", "Revision", "Transport" },
                values: new object[,]
                {
                    { new Guid("dca10000-0000-4000-8000-000000000001"), false, "{\"tools\":true,\"vision\":true,\"hierarchy\":1,\"semanticMode\":0,\"structuredOutput\":true,\"forcedToolChoice\":true,\"reasoningEfforts\":[\"low\",\"medium\",\"high\"],\"defaultReasoningEffort\":\"medium\",\"source\":2,\"verifiedAt\":null}", true, "gpt-6-luna", 0, "GPT-6 Luna", 1L, 0 },
                    { new Guid("dca10000-0000-4000-8000-000000000002"), false, "{\"tools\":true,\"vision\":true,\"hierarchy\":1,\"semanticMode\":0,\"structuredOutput\":true,\"forcedToolChoice\":true,\"reasoningEfforts\":[\"low\",\"medium\",\"high\"],\"defaultReasoningEffort\":\"medium\",\"source\":2,\"verifiedAt\":null}", true, "gpt-6.1-sol", 0, "GPT-6.1 Sol", 1L, 0 },
                    { new Guid("dca10000-0000-4000-8000-000000000003"), false, "{\"tools\":true,\"vision\":true,\"hierarchy\":2,\"semanticMode\":0,\"structuredOutput\":true,\"forcedToolChoice\":true,\"reasoningEfforts\":[\"low\",\"medium\",\"high\",\"xhigh\",\"max\"],\"defaultReasoningEffort\":\"medium\",\"source\":2,\"verifiedAt\":null}", true, "claude-haiku-5-5", 1, "Claude Haiku 5.5", 1L, 1 },
                    { new Guid("dca10000-0000-4000-8000-000000000004"), false, "{\"tools\":true,\"vision\":true,\"hierarchy\":2,\"semanticMode\":0,\"structuredOutput\":true,\"forcedToolChoice\":false,\"reasoningEfforts\":[\"low\",\"medium\",\"high\",\"xhigh\",\"max\"],\"defaultReasoningEffort\":\"high\",\"source\":2,\"verifiedAt\":null}", true, "claude-sonnet-5-5", 1, "Claude Sonnet 5.5", 1L, 1 },
                    { new Guid("dca10000-0000-4000-8000-000000000005"), false, "{\"tools\":true,\"vision\":true,\"hierarchy\":2,\"semanticMode\":0,\"structuredOutput\":true,\"forcedToolChoice\":false,\"reasoningEfforts\":[\"minimal\",\"low\",\"medium\",\"high\"],\"defaultReasoningEffort\":\"minimal\",\"source\":2,\"verifiedAt\":null}", true, "gemini-3.5-flash-lite", 2, "Gemini 3.5 Flash Lite", 1L, 2 },
                    { new Guid("dca10000-0000-4000-8000-000000000006"), false, "{\"tools\":true,\"vision\":true,\"hierarchy\":2,\"semanticMode\":0,\"structuredOutput\":true,\"forcedToolChoice\":false,\"reasoningEfforts\":[\"low\",\"medium\",\"high\"],\"defaultReasoningEffort\":\"medium\",\"source\":2,\"verifiedAt\":null}", true, "gemini-3.8-flash", 2, "Gemini 3.8 Flash", 1L, 2 },
                    { new Guid("dca10000-0000-4000-8000-000000000007"), false, "{\"tools\":true,\"vision\":false,\"hierarchy\":1,\"semanticMode\":0,\"structuredOutput\":true,\"forcedToolChoice\":true,\"reasoningEfforts\":[\"low\",\"medium\",\"high\"],\"defaultReasoningEffort\":\"medium\",\"source\":2,\"verifiedAt\":null}", true, "deepseek/deepseek-v4.1-flash", 3, "DeepSeek V4.1 Flash", 1L, 3 }
                });

            migrationBuilder.CreateIndex(
                name: "IX_ModelOfferings_PresetId_Kind_ExternalModelId",
                table: "ModelOfferings",
                columns: new[] { "PresetId", "Kind", "ExternalModelId" });

            migrationBuilder.CreateIndex(
                name: "IX_ModelOfferings_TenantId_ConnectionId_ExternalModelId",
                table: "ModelOfferings",
                columns: new[] { "TenantId", "ConnectionId", "ExternalModelId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ModelOfferings_TenantId_ConnectionId_Kind",
                table: "ModelOfferings",
                columns: new[] { "TenantId", "ConnectionId", "Kind" });

            migrationBuilder.CreateIndex(
                name: "IX_ModelPresets_Kind_ExternalModelId",
                table: "ModelPresets",
                columns: new[] { "Kind", "ExternalModelId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ProviderConnections_CredentialId",
                table: "ProviderConnections",
                column: "CredentialId");

            migrationBuilder.CreateIndex(
                name: "IX_ProviderConnections_TenantId_CredentialId",
                table: "ProviderConnections",
                columns: new[] { "TenantId", "CredentialId" });

            migrationBuilder.CreateIndex(
                name: "IX_TenantModelSettings_TenantId_DefaultOfferingId",
                table: "TenantModelSettings",
                columns: new[] { "TenantId", "DefaultOfferingId" });

            migrationBuilder.Sql("""
                CREATE TRIGGER TR_ProviderConnections_Credential_INSERT BEFORE INSERT ON ProviderConnections WHEN NEW.CredentialId IS NOT NULL
                BEGIN
                    SELECT RAISE(ABORT,'model connection requires an owned inference API credential') WHERE NOT EXISTS
                        (SELECT 1 FROM Credentials c JOIN TenantCredentials t ON t.CredentialId=c.CredentialId
                        WHERE c.CredentialId=NEW.CredentialId AND t.TenantId=NEW.TenantId AND t.DeletedAt IS NULL AND c.Purpose='ModelInference' AND c.Kind='ApiKey');
                END;
                CREATE TRIGGER TR_ProviderConnections_Credential_UPDATE BEFORE UPDATE ON ProviderConnections WHEN NEW.CredentialId IS NOT NULL
                BEGIN
                    SELECT RAISE(ABORT,'model connection requires an owned inference API credential') WHERE NOT EXISTS
                        (SELECT 1 FROM Credentials c JOIN TenantCredentials t ON t.CredentialId=c.CredentialId
                        WHERE c.CredentialId=NEW.CredentialId AND t.TenantId=NEW.TenantId AND t.DeletedAt IS NULL AND c.Purpose='ModelInference' AND c.Kind='ApiKey');
                END;
                CREATE TRIGGER TR_ProviderConnections_Identity_UPDATE BEFORE UPDATE ON ProviderConnections
                WHEN NEW.ConnectionId<>OLD.ConnectionId OR NEW.TenantId<>OLD.TenantId OR NEW.Kind<>OLD.Kind OR NEW.CreatedAt<>OLD.CreatedAt
                BEGIN SELECT RAISE(ABORT,'model connection identity is immutable'); END;
                CREATE TRIGGER TR_ModelOfferings_Identity_UPDATE BEFORE UPDATE ON ModelOfferings
                WHEN NEW.OfferingId<>OLD.OfferingId OR NEW.TenantId<>OLD.TenantId OR NEW.ConnectionId<>OLD.ConnectionId OR NEW.Kind<>OLD.Kind
                    OR NEW.ExternalModelId<>OLD.ExternalModelId OR NEW.PresetId IS NOT OLD.PresetId OR OLD.Retired=1 AND NEW.Retired<>1
                BEGIN SELECT RAISE(ABORT,'model offering identity and retirement are immutable'); END;
                CREATE TRIGGER TR_ProviderConnections_Retain_DELETE BEFORE DELETE ON ProviderConnections
                BEGIN SELECT RAISE(ABORT,'retire model connections without deleting historical identity'); END;
                CREATE TRIGGER TR_ModelOfferings_Retain_DELETE BEFORE DELETE ON ModelOfferings
                BEGIN SELECT RAISE(ABORT,'retire model offerings without deleting historical identity'); END;
                CREATE TRIGGER TR_Credentials_ModelInferencePurpose_UPDATE BEFORE UPDATE ON Credentials
                WHEN EXISTS(SELECT 1 FROM ProviderConnections p WHERE p.CredentialId=OLD.CredentialId)
                    AND (NEW.CredentialId<>OLD.CredentialId OR NEW.Purpose<>'ModelInference' OR NEW.Kind<>'ApiKey')
                BEGIN SELECT RAISE(ABORT,'referenced model credential purpose and identity are immutable'); END;
                UPDATE Tenants SET Revision=Revision+1 WHERE TenantId IN (SELECT TenantId FROM AccessRoles WHERE IsBuiltin=1 AND Key='owner');
                INSERT OR IGNORE INTO AccessRolePermissions(RoleId,PermissionKey)
                    SELECT r.RoleId,p.value FROM AccessRoles r JOIN json_each('["models.read","models.manage","model-connections.read","model-connections.manage","model-connections.test"]') p WHERE r.IsBuiltin=1 AND r.Key='owner' AND r.HumanOnly=1 AND r.TenantId IS NOT NULL;
                INSERT OR IGNORE INTO AccessRolePermissions(RoleId,PermissionKey)
                    SELECT r.RoleId,'models.read' FROM AccessRoles r WHERE r.IsBuiltin=1 AND r.Key IN ('reader','contributor') AND r.TenantId IS NOT NULL;
                INSERT OR IGNORE INTO AccessRolePermissions(RoleId,PermissionKey)
                    SELECT r.RoleId,p.value FROM AccessRoles r JOIN json_each('["system.model-presets.read","system.model-presets.manage"]') p WHERE r.IsBuiltin=1 AND r.Key='system-operator' AND r.HumanOnly=1 AND r.TenantId IS NULL;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                CREATE TEMP TABLE ModelManagementDowngradeGuard(Rows INTEGER CONSTRAINT RetainedModelDataRequiresExplicitReset CHECK(Rows=0));
                INSERT INTO ModelManagementDowngradeGuard SELECT (SELECT count(*) FROM ProviderConnections)+(SELECT count(*) FROM ModelOfferings)+(SELECT count(*) FROM TenantModelSettings)
                    +(SELECT count(*) FROM ModelPresets WHERE Revision<>1 OR lower(PresetId) NOT IN ('dca10000-0000-4000-8000-000000000001','dca10000-0000-4000-8000-000000000002','dca10000-0000-4000-8000-000000000003','dca10000-0000-4000-8000-000000000004','dca10000-0000-4000-8000-000000000005','dca10000-0000-4000-8000-000000000006','dca10000-0000-4000-8000-000000000007'))
                    +(SELECT count(*) FROM SessionSnapshots WHERE ModelBindingJson IS NOT NULL OR SummaryModelBindingJson IS NOT NULL)
                    +(SELECT count(*) FROM ConversationEntries WHERE ModelBindingJson IS NOT NULL)
                    +(SELECT count(*) FROM TriggerOccurrences WHERE ModelBindingJson IS NOT NULL)
                    +(SELECT count(*) FROM AgentRuns)
                    +(SELECT count(*) FROM AccessRolePermissions p JOIN AccessRoles r ON r.RoleId=p.RoleId WHERE r.IsBuiltin=0 AND (p.PermissionKey LIKE 'models.%' OR p.PermissionKey LIKE 'model-connections.%' OR p.PermissionKey LIKE 'system.model-presets.%'));
                DROP TABLE ModelManagementDowngradeGuard;
                DROP TRIGGER TR_ProviderConnections_Credential_INSERT;
                DROP TRIGGER TR_ProviderConnections_Credential_UPDATE;
                DROP TRIGGER TR_ProviderConnections_Identity_UPDATE;
                DROP TRIGGER TR_ModelOfferings_Identity_UPDATE;
                DROP TRIGGER TR_ProviderConnections_Retain_DELETE;
                DROP TRIGGER TR_ModelOfferings_Retain_DELETE;
                DROP TRIGGER TR_Credentials_ModelInferencePurpose_UPDATE;
                UPDATE Tenants SET Revision=Revision+1 WHERE TenantId IN (SELECT r.TenantId FROM AccessRoles r JOIN AccessRolePermissions p ON p.RoleId=r.RoleId WHERE r.IsBuiltin=1 AND (p.PermissionKey LIKE 'models.%' OR p.PermissionKey LIKE 'model-connections.%'));
                DELETE FROM AccessRolePermissions WHERE (PermissionKey LIKE 'models.%' OR PermissionKey LIKE 'model-connections.%' OR PermissionKey LIKE 'system.model-presets.%') AND RoleId IN (SELECT RoleId FROM AccessRoles WHERE IsBuiltin=1);
                """);
            migrationBuilder.Sql("""
                ALTER TABLE SessionSnapshots DROP COLUMN ModelBindingJson;
                ALTER TABLE SessionSnapshots DROP COLUMN SummaryModelBindingJson;
                ALTER TABLE ConversationEntries DROP COLUMN ModelBindingJson;
                ALTER TABLE TriggerOccurrences DROP COLUMN ModelBindingJson;
                """);
            migrationBuilder.DropTable(
                name: "TenantModelSettings");

            migrationBuilder.DropTable(
                name: "ModelOfferings");

            migrationBuilder.DropTable(
                name: "ModelPresets");

            migrationBuilder.DropTable(
                name: "ProviderConnections");
        }
    }
}
