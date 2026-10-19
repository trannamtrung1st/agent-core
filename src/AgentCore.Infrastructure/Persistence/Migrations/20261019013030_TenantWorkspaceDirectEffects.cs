using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgentCore.Infrastructure.Persistence.Migrations;

public partial class TenantWorkspaceDirectEffects : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
            migrationBuilder.Sql("""
                CREATE TRIGGER TR_AgentRuns_DirectWorkspace_Insert
                BEFORE INSERT ON AgentRuns
                BEGIN
                    SELECT RAISE(ABORT, 'invalid direct workspace operation') WHERE NOT (
                        json_type(NEW.PayloadJson, '$.sideEffect.externalEffect.directOperation') IS NULL
                        OR (NEW.TenantId IS NOT NULL AND NEW.ExecutorPrincipalId IS NOT NULL
                            AND json_type(NEW.PayloadJson, '$.sideEffect.externalEffect.directOperation') IS 'object'
                            AND json_type(NEW.PayloadJson, '$.sideEffect.externalEffect.directOperation.operationId') IS 'text'
                            AND length(json_extract(NEW.PayloadJson, '$.sideEffect.externalEffect.directOperation.operationId')) = 36
                            AND json_extract(NEW.PayloadJson, '$.sideEffect.externalEffect.directOperation.operationId') <> '00000000-0000-0000-0000-000000000000'
                            AND json_extract(NEW.PayloadJson, '$.sideEffect.toolCallId') IS ('operation:' || json_extract(NEW.PayloadJson, '$.sideEffect.externalEffect.directOperation.operationId'))
                            AND json_type(NEW.PayloadJson, '$.sideEffect.externalEffect.directOperation.sourceCallId') IS 'text'
                            AND length(json_extract(NEW.PayloadJson, '$.sideEffect.externalEffect.directOperation.sourceCallId')) BETWEEN 1 AND 200
                            AND json_type(NEW.PayloadJson, '$.sideEffect.externalEffect.directOperation.toolName') IS 'text'
                            AND json_extract(NEW.PayloadJson, '$.sideEffect.externalEffect.directOperation.toolName') IN ('workspace.write','workspace.patch','workspace.mkdir','workspace.copy','workspace.move','workspace.delete','workspace.batch')
                            AND json_type(NEW.PayloadJson, '$.sideEffect.externalEffect.directOperation.argumentsJson') IS 'text'
                            AND length(CAST(json_extract(NEW.PayloadJson, '$.sideEffect.externalEffect.directOperation.argumentsJson') AS BLOB)) BETWEEN 2 AND 65536
                            AND json_valid(json_extract(NEW.PayloadJson, '$.sideEffect.externalEffect.directOperation.argumentsJson'))
                            AND json_type(json_extract(NEW.PayloadJson, '$.sideEffect.externalEffect.directOperation.argumentsJson')) IS 'object'
                            AND json_type(NEW.PayloadJson, '$.sideEffect.externalEffect.directOperation.parametersHash') IS 'text'
                            AND length(json_extract(NEW.PayloadJson, '$.sideEffect.externalEffect.directOperation.parametersHash')) = 64
                            AND json_extract(NEW.PayloadJson, '$.sideEffect.externalEffect.directOperation.parametersHash') NOT GLOB '*[^0-9a-f]*'
                            AND json_type(NEW.PayloadJson, '$.sideEffect.externalEffect.directOperation.instanceRevision') IS 'integer'
                            AND json_extract(NEW.PayloadJson, '$.sideEffect.externalEffect.directOperation.instanceRevision') >= 1
                            AND json_type(NEW.PayloadJson, '$.sideEffect.externalEffect.directOperation.personaRevision') IS 'integer'
                            AND json_extract(NEW.PayloadJson, '$.sideEffect.externalEffect.directOperation.personaRevision') >= 1)
                    );
                END;
                """);
            migrationBuilder.Sql("""
                CREATE TRIGGER TR_AgentRuns_DirectWorkspace_Update
                BEFORE UPDATE ON AgentRuns
                BEGIN
                    SELECT RAISE(ABORT, 'invalid direct workspace operation') WHERE NOT (
                        json_type(NEW.PayloadJson, '$.sideEffect.externalEffect.directOperation') IS NULL
                        OR (NEW.TenantId IS NOT NULL AND NEW.ExecutorPrincipalId IS NOT NULL
                            AND json_type(NEW.PayloadJson, '$.sideEffect.externalEffect.directOperation') IS 'object'
                            AND json_type(NEW.PayloadJson, '$.sideEffect.externalEffect.directOperation.operationId') IS 'text'
                            AND length(json_extract(NEW.PayloadJson, '$.sideEffect.externalEffect.directOperation.operationId')) = 36
                            AND json_extract(NEW.PayloadJson, '$.sideEffect.externalEffect.directOperation.operationId') <> '00000000-0000-0000-0000-000000000000'
                            AND json_extract(NEW.PayloadJson, '$.sideEffect.toolCallId') IS ('operation:' || json_extract(NEW.PayloadJson, '$.sideEffect.externalEffect.directOperation.operationId'))
                            AND json_type(NEW.PayloadJson, '$.sideEffect.externalEffect.directOperation.sourceCallId') IS 'text'
                            AND length(json_extract(NEW.PayloadJson, '$.sideEffect.externalEffect.directOperation.sourceCallId')) BETWEEN 1 AND 200
                            AND json_type(NEW.PayloadJson, '$.sideEffect.externalEffect.directOperation.toolName') IS 'text'
                            AND json_extract(NEW.PayloadJson, '$.sideEffect.externalEffect.directOperation.toolName') IN ('workspace.write','workspace.patch','workspace.mkdir','workspace.copy','workspace.move','workspace.delete','workspace.batch')
                            AND json_type(NEW.PayloadJson, '$.sideEffect.externalEffect.directOperation.argumentsJson') IS 'text'
                            AND length(CAST(json_extract(NEW.PayloadJson, '$.sideEffect.externalEffect.directOperation.argumentsJson') AS BLOB)) BETWEEN 2 AND 65536
                            AND json_valid(json_extract(NEW.PayloadJson, '$.sideEffect.externalEffect.directOperation.argumentsJson'))
                            AND json_type(json_extract(NEW.PayloadJson, '$.sideEffect.externalEffect.directOperation.argumentsJson')) IS 'object'
                            AND json_type(NEW.PayloadJson, '$.sideEffect.externalEffect.directOperation.parametersHash') IS 'text'
                            AND length(json_extract(NEW.PayloadJson, '$.sideEffect.externalEffect.directOperation.parametersHash')) = 64
                            AND json_extract(NEW.PayloadJson, '$.sideEffect.externalEffect.directOperation.parametersHash') NOT GLOB '*[^0-9a-f]*'
                            AND json_type(NEW.PayloadJson, '$.sideEffect.externalEffect.directOperation.instanceRevision') IS 'integer'
                            AND json_extract(NEW.PayloadJson, '$.sideEffect.externalEffect.directOperation.instanceRevision') >= 1
                            AND json_type(NEW.PayloadJson, '$.sideEffect.externalEffect.directOperation.personaRevision') IS 'integer'
                            AND json_extract(NEW.PayloadJson, '$.sideEffect.externalEffect.directOperation.personaRevision') >= 1)
                    );
                END;
                """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        // Removing these guards while dispatch evidence remains would permit unsafe replay.
        migrationBuilder.Sql("""
            CREATE TEMP TABLE WorkspaceEffectRollbackGuard (RetainedRows INTEGER CHECK (RetainedRows = 0));
            INSERT INTO WorkspaceEffectRollbackGuard
            SELECT (SELECT COUNT(*) FROM AgentRuns WHERE json_type(PayloadJson, '$.sideEffect.externalEffect.directOperation') IS 'object')
                + (SELECT COUNT(*) FROM TenantOperationReceipts WHERE ReasonCode GLOB 'workspace_mutation_*');
            DROP TABLE WorkspaceEffectRollbackGuard;
            DROP TRIGGER TR_AgentRuns_DirectWorkspace_Insert;
            DROP TRIGGER TR_AgentRuns_DirectWorkspace_Update;
            """);
    }
}
