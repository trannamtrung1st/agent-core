using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace AgentCore.Infrastructure.Persistence.Migrations;

[DbContext(typeof(AgentCoreDbContext))]
[Migration("20261027010000_UnifiedEvaluations")]
public sealed class UnifiedEvaluations : Migration
{
    protected override void BuildTargetModel(ModelBuilder modelBuilder) => UnifiedEvaluationTargetModel.Build(modelBuilder);
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            CREATE TABLE TenantEvaluationPolicies(TenantId TEXT NOT NULL PRIMARY KEY, Revision INTEGER NOT NULL, Json TEXT NOT NULL,
                CONSTRAINT FK_TenantEvaluationPolicies_Tenants_TenantId FOREIGN KEY(TenantId) REFERENCES Tenants(TenantId) ON DELETE RESTRICT);
            CREATE TABLE EvaluationSuites (
                TenantId TEXT NOT NULL, SuiteId TEXT NOT NULL, Version INTEGER NOT NULL, DraftId TEXT NULL, Json TEXT NOT NULL,
                CONSTRAINT PK_EvaluationSuites PRIMARY KEY(TenantId, SuiteId, Version),
                CONSTRAINT FK_EvaluationSuites_Tenants_TenantId FOREIGN KEY(TenantId) REFERENCES Tenants(TenantId) ON DELETE RESTRICT);
            CREATE INDEX IX_EvaluationSuites_TenantId_DraftId_Version ON EvaluationSuites(TenantId, DraftId, Version);
            CREATE TABLE EvaluationScripts (
                TenantId TEXT NOT NULL, ScriptId TEXT NOT NULL, Version INTEGER NOT NULL, Json TEXT NOT NULL,
                CONSTRAINT PK_EvaluationScripts PRIMARY KEY(TenantId, ScriptId, Version),
                CONSTRAINT FK_EvaluationScripts_Tenants_TenantId FOREIGN KEY(TenantId) REFERENCES Tenants(TenantId) ON DELETE RESTRICT);
            CREATE TABLE EvaluationAttempts (
                TenantId TEXT NOT NULL, AttemptId TEXT NOT NULL, ActorId TEXT NOT NULL, DedupKey TEXT NOT NULL,
                SuiteId TEXT NOT NULL, SuiteVersion INTEGER NOT NULL, SubjectKind INTEGER NOT NULL, SubjectId TEXT NOT NULL,
                Revision INTEGER NOT NULL, CommitSequence INTEGER NULL, Status INTEGER NOT NULL, ExpiresAtUtc INTEGER NOT NULL, Json TEXT NOT NULL,
                CONSTRAINT PK_EvaluationAttempts PRIMARY KEY(TenantId, AttemptId),
                CONSTRAINT FK_EvaluationAttempts_EvaluationSuites_TenantId_SuiteId_SuiteVersion
                    FOREIGN KEY(TenantId, SuiteId, SuiteVersion) REFERENCES EvaluationSuites(TenantId, SuiteId, Version) ON DELETE RESTRICT);
            CREATE INDEX IX_EvaluationAttempts_Status_ExpiresAtUtc ON EvaluationAttempts(Status, ExpiresAtUtc);
            CREATE UNIQUE INDEX IX_EvaluationAttempts_TenantId_ActorId_DedupKey ON EvaluationAttempts(TenantId, ActorId, DedupKey);
            CREATE INDEX IX_EvaluationAttempts_TenantId_SubjectKind_SubjectId_CommitSequence ON EvaluationAttempts(TenantId, SubjectKind, SubjectId, CommitSequence);
            CREATE INDEX IX_EvaluationAttempts_TenantId_SuiteId_SuiteVersion ON EvaluationAttempts(TenantId, SuiteId, SuiteVersion);
            CREATE TRIGGER TR_EvaluationSuites_Immutable_Update BEFORE UPDATE ON EvaluationSuites BEGIN SELECT RAISE(ABORT,'evaluation_version_immutable'); END;
            CREATE TRIGGER TR_EvaluationSuites_Immutable_Delete BEFORE DELETE ON EvaluationSuites BEGIN SELECT RAISE(ABORT,'evaluation_version_immutable'); END;
            CREATE TRIGGER TR_EvaluationScripts_Immutable_Update BEFORE UPDATE ON EvaluationScripts BEGIN SELECT RAISE(ABORT,'script_version_immutable'); END;
            CREATE TRIGGER TR_EvaluationScripts_Immutable_Delete BEFORE DELETE ON EvaluationScripts BEGIN SELECT RAISE(ABORT,'script_version_immutable'); END;
            CREATE TRIGGER TR_EvaluationAttempts_Immutable_Delete BEFORE DELETE ON EvaluationAttempts BEGIN SELECT RAISE(ABORT,'evaluation_attempt_immutable'); END;
            CREATE TRIGGER TR_EvaluationAttempts_Final_Update BEFORE UPDATE ON EvaluationAttempts
                WHEN json_extract(OLD.Json,'$.Status') IN (2,3,4)
                BEGIN SELECT RAISE(ABORT,'evaluation_result_immutable'); END;
            CREATE TRIGGER TR_DefinitionScenarios_NoTenant_Insert BEFORE INSERT ON AgentDefinitionDraftEvaluationScenarios
                WHEN EXISTS(SELECT 1 FROM AgentDefinitionDrafts d JOIN TenantAgentDefinitions t ON t.DefinitionId=d.DefinitionId WHERE d.DraftId=NEW.DraftId)
                BEGIN SELECT RAISE(ABORT,'tenant_evaluation_owner_retired'); END;
            CREATE TRIGGER TR_DefinitionScenarios_NoTenant_Update BEFORE UPDATE ON AgentDefinitionDraftEvaluationScenarios
                WHEN EXISTS(SELECT 1 FROM AgentDefinitionDrafts d JOIN TenantAgentDefinitions t ON t.DefinitionId=d.DefinitionId WHERE d.DraftId=NEW.DraftId)
                BEGIN SELECT RAISE(ABORT,'tenant_evaluation_owner_retired'); END;
            CREATE TRIGGER TR_DefinitionResults_NoTenant_Insert BEFORE INSERT ON AgentDefinitionDraftEvaluationResults
                WHEN EXISTS(SELECT 1 FROM AgentDefinitionDrafts d JOIN TenantAgentDefinitions t ON t.DefinitionId=d.DefinitionId WHERE d.DraftId=NEW.DraftId)
                BEGIN SELECT RAISE(ABORT,'tenant_evaluation_owner_retired'); END;
            INSERT OR IGNORE INTO AccessRolePermissions(RoleId,PermissionKey)
                SELECT r.RoleId,p.PermissionKey FROM AccessRoles r CROSS JOIN (
                    SELECT 'scripts.read' PermissionKey UNION ALL SELECT 'scripts.manage' UNION ALL SELECT 'scripts.execute'
                    UNION ALL SELECT 'evaluations.read' UNION ALL SELECT 'evaluations.manage' UNION ALL SELECT 'evaluations.run' UNION ALL SELECT 'evaluations.review'
                ) p WHERE r.TenantId IS NOT NULL AND r.IsBuiltin=1 AND r.Key='owner';
            INSERT OR IGNORE INTO AccessRolePermissions(RoleId,PermissionKey)
                SELECT r.RoleId,p.PermissionKey FROM AccessRoles r CROSS JOIN (SELECT 'scripts.read' PermissionKey UNION ALL SELECT 'evaluations.read') p
                WHERE r.TenantId IS NOT NULL AND r.IsBuiltin=1 AND r.Key IN ('contributor','reader');
            """);
    }
    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        CREATE TEMP TABLE EvaluationRollbackGuard(Rows INTEGER CONSTRAINT EvaluationRestoreMatchedBackupRequired CHECK(Rows=0));
        INSERT INTO EvaluationRollbackGuard VALUES(1);
        """);
}
