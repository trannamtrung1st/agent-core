using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable
namespace AgentCore.Infrastructure.Persistence.Migrations;

public partial class TenantAccountOperationRead : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        CREATE TRIGGER TR_TenantApprovals_AccountOperationRead_Insert BEFORE INSERT ON TenantApprovalRequests
        WHEN json_type(NEW.PayloadJson, '$.Operation.AccountRead.Source') IS NOT NULL
          OR json_type(json_extract(NEW.PayloadJson, '$.Operation.AccountRead.ArgumentsJson'), '$.operationId') IS NOT NULL
        BEGIN
            SELECT RAISE(ABORT, 'canonical operation read source required') WHERE NOT EXISTS (
                SELECT 1 FROM TenantApprovalRequests original JOIN TenantOperationReceipts receipt ON receipt.RequestId = original.RequestId
                JOIN AgentRuns source ON source.AgentRunId = receipt.RunId
                JOIN TenantSessions session ON session.SessionId = receipt.SessionId AND session.TenantId = receipt.TenantId
                JOIN Sessions retained ON retained.SessionId = session.SessionId
                WHERE original.TenantId = NEW.TenantId AND original.InstanceId = NEW.InstanceId AND original.ExecutorPrincipalId = NEW.ExecutorPrincipalId
                AND lower(original.RequestId) = json_extract(NEW.PayloadJson, '$.Operation.AccountRead.Source.RequestId') AND original.Status = 1
                AND lower(original.OperationId) = json_extract(NEW.PayloadJson, '$.Operation.AccountRead.Source.OperationId')
                AND original.Fingerprint = json_extract(NEW.PayloadJson, '$.Operation.AccountRead.Source.ActionHash')
                AND original.SessionId = json_extract(NEW.PayloadJson, '$.Operation.AccountRead.Source.SessionId')
                AND json_type(original.PayloadJson, '$.Operation.AccountAction') IS 'object'
                AND json_extract(original.PayloadJson, '$.Operation.AccountAction.ToolName') = json_extract(NEW.PayloadJson, '$.Operation.AccountRead.Source.ToolName')
                AND json_extract(NEW.PayloadJson, '$.Operation.AccountRead.Source.ToolName') IN ('drive.create_folder','drive.upload','drive.download','drive.rename','drive.move','drive.trash','drive.trash_batch')
                AND json_extract(original.PayloadJson, '$.Operation.AccountAction.Credential.BindingId') = json_extract(NEW.PayloadJson, '$.Operation.AccountRead.Credential.BindingId')
                AND json_extract(original.PayloadJson, '$.Operation.AccountAction.Credential.CredentialId') = NEW.CredentialId
                AND json_extract(original.PayloadJson, '$.Operation.AccountAction.Credential.BindingRevision') = json_extract(NEW.PayloadJson, '$.Operation.AccountRead.Credential.BindingRevision')
                AND json_extract(original.PayloadJson, '$.Operation.AccountAction.Credential.CredentialRevision') = json_extract(NEW.PayloadJson, '$.Operation.AccountRead.Credential.CredentialRevision')
                AND json_extract(original.PayloadJson, '$.Operation.AccountAction.Credential.AuthEpoch') = json_extract(NEW.PayloadJson, '$.Operation.AccountRead.Credential.AuthEpoch')
                AND receipt.TenantId = NEW.TenantId AND receipt.InstanceId = NEW.InstanceId AND receipt.ExecutorPrincipalId = NEW.ExecutorPrincipalId
                AND receipt.OperationId = original.OperationId AND receipt.ActionHash = original.Fingerprint AND receipt.SessionId = original.SessionId
                AND receipt.RunId = json_extract(NEW.PayloadJson, '$.Operation.AccountRead.Source.RunId') AND receipt.RootActivationId = original.RootActivationId
                AND CASE receipt.Outcome WHEN 0 THEN 'Completed' WHEN 1 THEN 'Denied' WHEN 2 THEN 'Indeterminate' END = json_extract(NEW.PayloadJson, '$.Operation.AccountRead.Source.RecordedOutcome')
                AND source.TenantId = NEW.TenantId AND source.AgentInstanceId = NEW.InstanceId AND source.ExecutorPrincipalId = NEW.ExecutorPrincipalId
                AND source.SessionId = receipt.SessionId AND source.RootActivationId = original.RootActivationId
                AND session.AgentInstanceId = NEW.InstanceId AND session.ExecutorPrincipalId = NEW.ExecutorPrincipalId AND retained.DurablyDeletedAtUtc IS NULL
                AND json_extract(NEW.PayloadJson, '$.Operation.AccountRead.ToolName') IS 'drive.get'
                AND json_extract(NEW.PayloadJson, '$.Operation.AccountRead.Purpose') IS 2
                AND json_type(NEW.PayloadJson, '$.Operation.AccountRead.Source') IS 'object'
                AND (SELECT count(*) FROM json_each(NEW.PayloadJson, '$.Operation.AccountRead.Source')) = 8
                AND json_extract(json_extract(NEW.PayloadJson, '$.Operation.AccountRead.ArgumentsJson'), '$.operationId') = lower(original.OperationId)
                AND json_type(json_extract(NEW.PayloadJson, '$.Operation.AccountRead.ArgumentsJson'), '$.fileId') IS NULL
                AND (SELECT count(*) FROM json_each(json_extract(NEW.PayloadJson, '$.Operation.AccountRead.ArgumentsJson'))) = 2
                AND json_type(NEW.PayloadJson, '$.Operation.AccountRead.Source.NativeReferencesJson') IS 'text'
                AND length(CAST(json_extract(NEW.PayloadJson, '$.Operation.AccountRead.Source.NativeReferencesJson') AS BLOB)) <= 16384
                AND json_valid(json_extract(NEW.PayloadJson, '$.Operation.AccountRead.Source.NativeReferencesJson'))
                AND json_type(json_extract(NEW.PayloadJson, '$.Operation.AccountRead.Source.NativeReferencesJson')) IS 'array'
                AND json_array_length(json_extract(NEW.PayloadJson, '$.Operation.AccountRead.Source.NativeReferencesJson')) BETWEEN 0 AND 20);
            SELECT RAISE(ABORT, 'invalid native operation references') WHERE EXISTS (
                SELECT 1 FROM json_each(json_extract(NEW.PayloadJson, '$.Operation.AccountRead.Source.NativeReferencesJson')) member
                WHERE member.type IS NOT 'object' OR (SELECT count(*) FROM json_each(member.value)) <> 4
                OR json_type(member.value, '$.OperationId') IS NOT 'text' OR length(json_extract(member.value, '$.OperationId')) <> 36
                OR json_extract(member.value, '$.OperationId') GLOB '*[^0-9a-f-]*'
                OR COALESCE(json_type(member.value, '$.FileId'),'') NOT IN ('null','text') OR length(json_extract(member.value, '$.FileId')) > 160
                OR COALESCE(json_type(member.value, '$.EffectHash'),'') NOT IN ('null','text') OR COALESCE(json_type(member.value, '$.SnapshotHash'),'') NOT IN ('null','text')
                OR json_type(member.value, '$.EffectHash') IS NOT json_type(member.value, '$.SnapshotHash')
                OR (json_type(member.value, '$.EffectHash') IS 'text' AND (length(json_extract(member.value, '$.EffectHash')) <> 64
                    OR json_extract(member.value, '$.EffectHash') GLOB '*[^0-9a-f]*' OR length(json_extract(member.value, '$.SnapshotHash')) <> 64
                    OR json_extract(member.value, '$.SnapshotHash') GLOB '*[^0-9a-f]*')));
            SELECT RAISE(ABORT, 'duplicate native operation references') WHERE
                (SELECT count(*) FROM json_each(json_extract(NEW.PayloadJson, '$.Operation.AccountRead.Source.NativeReferencesJson'))) <>
                (SELECT count(DISTINCT json_extract(value, '$.OperationId')) FROM json_each(json_extract(NEW.PayloadJson, '$.Operation.AccountRead.Source.NativeReferencesJson')));
        END;
        CREATE TRIGGER TR_TenantApprovals_AccountOperationRead_Update BEFORE UPDATE ON TenantApprovalRequests
        WHEN json_type(OLD.PayloadJson, '$.Operation.AccountRead.Source') IS NOT NULL
          OR json_type(NEW.PayloadJson, '$.Operation.AccountRead.Source') IS NOT NULL
        BEGIN
            SELECT RAISE(ABORT, 'canonical operation read source is immutable') WHERE
                json_extract(OLD.PayloadJson, '$.Operation.AccountRead') IS NOT json_extract(NEW.PayloadJson, '$.Operation.AccountRead');
        END;
        """);
    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        CREATE TEMP TABLE AccountOperationReadDowngradeGuard (Rows INTEGER CHECK(Rows=0));
        INSERT INTO AccountOperationReadDowngradeGuard SELECT count(*) FROM TenantApprovalRequests
          WHERE json_type(PayloadJson, '$.Operation.AccountRead.Source') IS NOT NULL;
        DROP TABLE AccountOperationReadDowngradeGuard;
        DROP TRIGGER TR_TenantApprovals_AccountOperationRead_Insert;
        DROP TRIGGER TR_TenantApprovals_AccountOperationRead_Update;
        """);
}
