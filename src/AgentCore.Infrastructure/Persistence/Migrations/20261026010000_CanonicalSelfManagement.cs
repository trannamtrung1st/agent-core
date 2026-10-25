using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable
namespace AgentCore.Infrastructure.Persistence.Migrations;

/// <summary>One-time live policy normalization; immutable consent and Run payloads are never rewritten.</summary>
public partial class CanonicalSelfManagement : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            CREATE TEMP TABLE SelfManagementLegacyPolicyInvalid (Rows INTEGER CHECK(Rows=0));
            INSERT INTO SelfManagementLegacyPolicyInvalid SELECT count(*) FROM AgentInstances
            WHERE HarnessManagementJson IS NOT NULL AND (
                json_valid(HarnessManagementJson)=0
                OR EXISTS (SELECT 1 FROM json_each(HarnessManagementJson,'$.policy.builtInSkills')
                    WHERE key IN ('configurationAssistance','harnessAuthoring') AND type NOT IN ('true','false','null')));
            DROP TABLE SelfManagementLegacyPolicyInvalid;
            CREATE TEMP TABLE SelfManagementNormalizationRequiresQuiescence (
                Rows INTEGER CONSTRAINT SelfManagementNormalizationRequired_StopHost_SettleRunsAndApprovals CHECK(Rows=0));
            INSERT INTO SelfManagementNormalizationRequiresQuiescence
            SELECT count(*) FROM AgentInstances owner
            WHERE owner.HarnessManagementJson IS NOT NULL
              AND json_type(owner.HarnessManagementJson,'$.overrides') IS NOT 'object'
              AND (EXISTS (SELECT 1 FROM AgentRuns r WHERE r.AgentInstanceId=owner.InstanceId AND r.Status NOT IN (4,5,6))
                OR EXISTS (SELECT 1 FROM TenantApprovalRequests a WHERE a.InstanceId=owner.InstanceId AND a.Status=0));
            DROP TABLE SelfManagementNormalizationRequiresQuiescence;

            UPDATE AgentInstances SET HarnessManagementJson=json_set(HarnessManagementJson,'$.overrides',json_object(
                'mode',json_object('value',json_extract(HarnessManagementJson,'$.policy.mode')),
                'scopes',json_object('value',json(json_extract(HarnessManagementJson,'$.policy.scopes'))),
                'autoApply',json_object('value',json(COALESCE(json_extract(HarnessManagementJson,'$.policy.managedAutoApply'),'[]'))),
                'configurationAssistance',json_object('value',json(CASE WHEN COALESCE(json_extract(HarnessManagementJson,'$.policy.builtInSkills.configurationAssistance'),1)=1 THEN 'true' ELSE 'false' END)),
                'harnessAuthoring',json_object('value',json(CASE WHEN COALESCE(json_extract(HarnessManagementJson,'$.policy.builtInSkills.harnessAuthoring'),1)=1 THEN 'true' ELSE 'false' END))))
            WHERE HarnessManagementJson IS NOT NULL AND json_type(HarnessManagementJson,'$.overrides') IN ('null')
               OR HarnessManagementJson IS NOT NULL AND json_type(HarnessManagementJson,'$.overrides') IS NULL;
            """);
        // This is a JSON-only migration: no EF model/column/index shape changes.
        migrationBuilder.Sql($"""
            CREATE TEMP TABLE SelfManagementCanonicalPolicyRequired (Rows INTEGER CHECK(Rows=0));
            INSERT INTO SelfManagementCanonicalPolicyRequired SELECT count(*) FROM AgentInstances
            WHERE HarnessManagementJson IS NOT NULL AND ({Invalid("HarnessManagementJson")});
            DROP TABLE SelfManagementCanonicalPolicyRequired;
            CREATE TRIGGER AgentInstances_SelfManagement_Insert BEFORE INSERT ON AgentInstances
            WHEN NEW.HarnessManagementJson IS NOT NULL AND ({Invalid("NEW.HarnessManagementJson")})
            BEGIN SELECT RAISE(ABORT,'SelfManagementCanonicalPolicyRequired'); END;
            CREATE TRIGGER AgentInstances_SelfManagement_Update BEFORE UPDATE OF HarnessManagementJson ON AgentInstances
            WHEN NEW.HarnessManagementJson IS NOT NULL AND ({Invalid("NEW.HarnessManagementJson")})
            BEGIN SELECT RAISE(ABORT,'SelfManagementCanonicalPolicyRequired'); END;
            """);
    }

    // Older binaries can read sparse policies. Never undo normalization or alter consent on downgrade.
    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        DROP TRIGGER IF EXISTS AgentInstances_SelfManagement_Insert;
        DROP TRIGGER IF EXISTS AgentInstances_SelfManagement_Update;
        """);

    private static string Invalid(string json) => $$"""
        CASE WHEN json_valid({{json}})=0 THEN 1 ELSE
        json_type({{json}}) IS NOT 'object'
        OR json_type({{json}},'$.policy') IS NOT 'object'
        OR json_type({{json}},'$.policyRevision') IS NOT 'integer' OR json_extract({{json}},'$.policyRevision')<1
        OR COALESCE(json_type({{json}},'$.policy.frozen'),'missing') NOT IN ('true','false')
        OR json_type({{json}},'$.overrides') IS NOT 'object'
        OR EXISTS (SELECT 1 FROM json_each({{json}}) WHERE key NOT IN ('policy','policyRevision','preparation','instanceChanges','overrides'))
        OR (SELECT count(*) FROM json_each({{json}}))<>(SELECT count(DISTINCT key) FROM json_each({{json}}))
        OR EXISTS (SELECT 1 FROM json_each({{json}},'$.overrides') o WHERE
            o.key NOT IN ('mode','scopes','autoApply','configurationAssistance','harnessAuthoring')
            OR o.type NOT IN ('null','object')
            OR o.type='object' AND (
                (SELECT count(*) FROM json_each(o.value))<>1 OR json_type(o.value,'$.value') IS NULL
                OR o.key='mode' AND (json_type(o.value,'$.value') NOT IN ('text','integer') OR json_extract(o.value,'$.value') NOT IN ('Disabled','Assisted','Managed',0,1,2))
                OR o.key IN ('configurationAssistance','harnessAuthoring') AND json_type(o.value,'$.value') NOT IN ('true','false')
                OR o.key IN ('scopes','autoApply') AND (json_type(o.value,'$.value') IS NOT 'array'
                    OR (SELECT count(*) FROM json_each(o.value,'$.value'))<>(SELECT count(DISTINCT value) FROM json_each(o.value,'$.value'))
                    OR EXISTS (SELECT 1 FROM json_each(o.value,'$.value') GROUP BY CASE value
                        WHEN 'KnowledgeResources' THEN 0 WHEN 'Instructions' THEN 1 WHEN 'ToolSelection' THEN 2
                        WHEN 'Settings' THEN 3 WHEN 'Skills' THEN 4 WHEN 'Resources' THEN 5 WHEN 'Connections' THEN 6
                        WHEN 'ManagementPolicy' THEN 7 WHEN 'AutomationPreferences' THEN 8
                        WHEN 'ConversationPreferences' THEN 0 WHEN 'TextResources' THEN 1 WHEN 'SkillChanges' THEN 2
                        ELSE value END HAVING count(*)>1)
                    OR EXISTS (SELECT 1 FROM json_each(o.value,'$.value') v WHERE
                        v.type NOT IN ('integer','text') OR CASE o.key
                        WHEN 'scopes' THEN v.value NOT IN ('KnowledgeResources','Instructions','ToolSelection','Settings','Skills','Resources','Connections','ManagementPolicy','AutomationPreferences',0,1,2,3,4,5,6,7,8)
                        ELSE v.value NOT IN ('ConversationPreferences','TextResources','SkillChanges',0,1,2) END))))
        OR (SELECT count(*) FROM json_each({{json}},'$.overrides'))<>(SELECT count(DISTINCT key) FROM json_each({{json}},'$.overrides'))
        END
        """;
}
