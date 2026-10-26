using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable
namespace AgentCore.Infrastructure.Persistence.Migrations;

public partial class DecisionIntelligencePreferences : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // Refusal runs before schema mutation. Immutable publications, Run configuration and
        // prepared approvals cannot be reserialized under a new preference contract silently.
        migrationBuilder.Sql("""
            CREATE TEMP TABLE DecisionPreferenceGuard (Rows INTEGER CONSTRAINT DecisionPreferenceRequiresBackedUpFreshState CHECK(Rows=0));
            INSERT INTO DecisionPreferenceGuard SELECT count(*) FROM AgentDefinitionPublications
              WHERE json_type(PayloadJson,'$.modelDefaults')='object' AND json_extract(PayloadJson,'$.modelDefaults.mode') IS NULL;
            INSERT INTO DecisionPreferenceGuard SELECT count(*) FROM AgentDefinitionDrafts
              WHERE json_type(CandidateJson,'$.modelDefaults')='object' AND json_extract(CandidateJson,'$.modelDefaults.mode') IS NULL;
            INSERT INTO DecisionPreferenceGuard SELECT count(*) FROM AgentRuns
              WHERE json_type(PayloadJson,'$.admission.configuration.definition.modelDefaults')='object'
                AND json_extract(PayloadJson,'$.admission.configuration.definition.modelDefaults.mode') IS NULL;
            INSERT INTO DecisionPreferenceGuard SELECT count(*) FROM Sessions
              WHERE json_type(DefinitionJson,'$.modelDefaults')='object' AND json_extract(DefinitionJson,'$.modelDefaults.mode') IS NULL;
            INSERT INTO DecisionPreferenceGuard SELECT count(*) FROM TenantApprovalRequests
              WHERE json_type(PayloadJson,'$.Operation.AutomationMutation')='object'
                OR json_type(PayloadJson,'$.Operation.ManagementMutation')='object'
                OR json_type(PayloadJson,'$.Operation.AutomationAdmission')='object';
            INSERT INTO DecisionPreferenceGuard SELECT count(*) FROM AgentInstances
              WHERE UnattendedOfferingId IS NULL AND UnattendedReasoningEffort IS NOT NULL;
            INSERT INTO DecisionPreferenceGuard SELECT count(*) FROM Automations
              WHERE ModelOverrideOfferingId IS NULL AND ModelOverrideReasoningEffort IS NOT NULL;
            DROP TABLE DecisionPreferenceGuard;
            """);
        migrationBuilder.Sql("PRAGMA foreign_keys=OFF;", suppressTransaction: true);
        migrationBuilder.Sql(FrozenMigrationMetadata.Read("ac6d98ef7ad68f459fff90ec8350c71d537cfff42abdd03f7cf9932268dae03d"));
        migrationBuilder.AddColumn<string>("ModelDecisionJson", "TriggerOccurrences", type: "TEXT", nullable: true);
        migrationBuilder.AddColumn<string>("RoutingJson", "TenantModelSettings", type: "TEXT", nullable: true);
        migrationBuilder.AddColumn<string>("ModelDecisionJson", "SessionSnapshots", type: "TEXT", nullable: true);
        migrationBuilder.AddColumn<string>("ModelPreferenceJson", "SessionSnapshots", type: "TEXT", nullable: true);
        migrationBuilder.AddColumn<string>("ModelPreferenceJson", "Automations", type: "TEXT", nullable: true);
        migrationBuilder.AddColumn<string>("UnattendedModelJson", "AgentInstances", type: "TEXT", nullable: true);
        migrationBuilder.Sql("""
            UPDATE AgentInstances SET UnattendedModelJson = json_object('mode','fixed','offeringId',UnattendedOfferingId,
                'reasoning',json_object('mode',CASE WHEN UnattendedReasoningEffort IS NULL THEN 'modelDefault' ELSE 'fixed' END,'effort',UnattendedReasoningEffort))
                WHERE UnattendedOfferingId IS NOT NULL;
            UPDATE Automations SET ModelPreferenceJson = json_object('Mode','fixed','OfferingId',ModelOverrideOfferingId,
                'Reasoning',json_object('Mode',CASE WHEN ModelOverrideReasoningEffort IS NULL THEN 'modelDefault' ELSE 'fixed' END,'Effort',ModelOverrideReasoningEffort))
                WHERE ModelOverrideOfferingId IS NOT NULL;
            UPDATE SessionSnapshots SET ModelPreferenceJson = json_object('Mode',CASE WHEN ModelSelectionSource IN ('User','Host') THEN 'fixed' ELSE 'inherit' END,
                'OfferingId',CASE WHEN ModelSelectionSource IN ('User','Host') THEN ModelOfferingId ELSE NULL END,
                'Reasoning',json_object('Mode',CASE WHEN ModelHasExplicitReasoningEffort=1 THEN 'fixed' ELSE 'modelDefault' END,
                    'Effort',CASE WHEN ModelHasExplicitReasoningEffort=1 THEN ModelReasoningEffort ELSE NULL END));
            UPDATE AgentInstances SET SettingsOverridesJson = json_remove(json_set(SettingsOverridesJson,'$.modelPreference',json_object('value',json_object(
                'mode', CASE WHEN json_extract(SettingsOverridesJson,'$.modelOfferingId.value') IS NULL THEN 'inherit' ELSE 'fixed' END,
                'offeringId',json_extract(SettingsOverridesJson,'$.modelOfferingId.value'),
                'reasoning',json_object('mode',CASE WHEN json_extract(SettingsOverridesJson,'$.reasoningEffort.value') IS NULL THEN 'modelDefault' ELSE 'fixed' END,
                    'effort',json_extract(SettingsOverridesJson,'$.reasoningEffort.value'))))), '$.modelOfferingId','$.reasoningEffort')
                WHERE json_type(SettingsOverridesJson,'$.modelOfferingId')='object' OR json_type(SettingsOverridesJson,'$.reasoningEffort')='object';
            -- Native DROP COLUMN retains unrelated triggers, indexes, foreign keys and evidence.
            ALTER TABLE Automations DROP COLUMN ModelOverrideOfferingId;
            ALTER TABLE Automations DROP COLUMN ModelOverrideReasoningEffort;
            ALTER TABLE AgentInstances DROP COLUMN UnattendedOfferingId;
            ALTER TABLE AgentInstances DROP COLUMN UnattendedReasoningEffort;
            ALTER TABLE SessionSnapshots DROP COLUMN ModelHasExplicitReasoningEffort;
            CREATE TEMP TABLE DecisionPreferenceForeignKeys (Rows INTEGER CHECK(Rows=0));
            INSERT INTO DecisionPreferenceForeignKeys SELECT count(*) FROM pragma_foreign_key_check;
            DROP TABLE DecisionPreferenceForeignKeys;
            """);
        migrationBuilder.Sql("PRAGMA foreign_keys=ON;", suppressTransaction: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.Sql("CREATE TEMP TABLE DecisionPreferenceRollback(Rows INTEGER CONSTRAINT DecisionPreferenceRestoreMatchedBackupRequired CHECK(Rows=0)); INSERT INTO DecisionPreferenceRollback VALUES (1);");
}
