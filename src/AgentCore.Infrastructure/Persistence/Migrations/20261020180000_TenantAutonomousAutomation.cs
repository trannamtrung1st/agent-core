using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable
namespace AgentCore.Infrastructure.Persistence.Migrations;

// Frozen SQLite rebuilds preserve incumbent consent guards and install the new
// guards after rebuilding their owning tables. No live helper controls this DDL.
public partial class TenantAutonomousAutomation : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("PRAGMA foreign_keys = OFF;", suppressTransaction: true);
        migrationBuilder.Sql(UpgradeSql);
        migrationBuilder.Sql("PRAGMA foreign_keys = ON;", suppressTransaction: true);
    }
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(DowngradeGuardSql);
        migrationBuilder.Sql("PRAGMA foreign_keys = OFF;", suppressTransaction: true);
        migrationBuilder.Sql(DowngradeSql);
        migrationBuilder.Sql("PRAGMA foreign_keys = ON;", suppressTransaction: true);
    }
    private const string UpgradeSql = """
        DROP TRIGGER "TR_Attachments_Tenant_Insert";
        DROP TRIGGER "TR_Attachments_Tenant_Update";
        DROP TRIGGER "TR_TenantApprovals_AccountAction_Insert";
        DROP TRIGGER "TR_TenantApprovals_AccountAction_Update";
        DROP TRIGGER "TR_TenantApprovals_AccountOperationRead_Insert";
        DROP TRIGGER "TR_TenantApprovals_AccountOperationRead_Update";
        DROP TRIGGER "TR_TenantApprovals_AccountRead_Insert";
        DROP TRIGGER "TR_TenantApprovals_AccountRead_Update";
        DROP TRIGGER "TR_TenantApprovals_BackgroundCreation_Update";
        DROP TRIGGER "TR_TenantApprovals_BackgroundReport_Insert";
        DROP TRIGGER "TR_TenantApprovals_BackgroundReport_Update";
        DROP TRIGGER "TR_TenantApprovals_BrowserArtifacts_Insert";
        DROP TRIGGER "TR_TenantApprovals_BrowserArtifacts_Update";
        DROP TRIGGER "TR_TenantApprovals_BrowserFiles_Insert";
        DROP TRIGGER "TR_TenantApprovals_BrowserFiles_Update";
        DROP TRIGGER "TR_TenantApprovals_CompletionRead_Insert";
        DROP TRIGGER "TR_TenantApprovals_CompletionRead_Update";
        DROP TRIGGER "TR_TenantApprovals_ReportSourceRead_Insert";
        DROP TRIGGER "TR_TenantApprovals_ReportSourceRead_Update";
        DROP TRIGGER "TR_TenantReceipts_BrowserArtifacts_Insert";
        DROP TRIGGER "TR_TenantReceipts_BrowserArtifacts_Update";
        DROP INDEX "IX_TenantSessions_TenantId_CreatedByPrincipalId";

        ALTER TABLE "TriggerOccurrences" ADD "ExecutorPrincipalId" TEXT NULL;

        ALTER TABLE "TriggerOccurrences" ADD "TenantId" TEXT NULL;

        ALTER TABLE "Automations" ADD "ExecutorPrincipalId" TEXT NULL;

        ALTER TABLE "Automations" ADD "TenantId" TEXT NULL;

        CREATE UNIQUE INDEX "IX_TriggerOccurrences_TenantId_AgentInstanceId_DedupeKey" ON "TriggerOccurrences" ("TenantId", "AgentInstanceId", "DedupeKey") WHERE TenantId IS NOT NULL;

        CREATE INDEX "IX_TriggerOccurrences_TenantId_AgentInstanceId_Disposition" ON "TriggerOccurrences" ("TenantId", "AgentInstanceId", "Disposition");

        CREATE INDEX "IX_TriggerOccurrences_TenantId_AgentInstanceId_ExecutorPrincipalId" ON "TriggerOccurrences" ("TenantId", "AgentInstanceId", "ExecutorPrincipalId");

        CREATE INDEX "IX_TenantSessions_CreatedByPrincipalId" ON "TenantSessions" ("CreatedByPrincipalId");

        CREATE INDEX "IX_Automations_TenantId_AgentInstanceId_ExecutorPrincipalId" ON "Automations" ("TenantId", "AgentInstanceId", "ExecutorPrincipalId");

        CREATE INDEX "IX_Automations_TenantId_AgentInstanceId_Status" ON "Automations" ("TenantId", "AgentInstanceId", "Status");

        CREATE TABLE "ef_temp_TenantSessions" (
            "SessionId" TEXT NOT NULL CONSTRAINT "PK_TenantSessions" PRIMARY KEY,
            "AgentInstanceId" TEXT NOT NULL,
            "CreatedAt" INTEGER NOT NULL,
            "CreatedByPrincipalId" TEXT NOT NULL,
            "ExecutorPrincipalId" TEXT NOT NULL,
            "TenantId" TEXT NOT NULL,
            CONSTRAINT "AK_TenantSessions_TenantId_SessionId" UNIQUE ("TenantId", "SessionId"),
            CONSTRAINT "AK_TenantSessions_TenantId_SessionId_AgentInstanceId_ExecutorPrincipalId" UNIQUE ("TenantId", "SessionId", "AgentInstanceId", "ExecutorPrincipalId"),
            CONSTRAINT "FK_TenantSessions_Principals_CreatedByPrincipalId" FOREIGN KEY ("CreatedByPrincipalId") REFERENCES "Principals" ("PrincipalId") ON DELETE RESTRICT,
            CONSTRAINT "FK_TenantSessions_Sessions_SessionId_AgentInstanceId" FOREIGN KEY ("SessionId", "AgentInstanceId") REFERENCES "Sessions" ("SessionId", "AgentInstanceId") ON DELETE RESTRICT,
            CONSTRAINT "FK_TenantSessions_SystemIdentities_TenantId_AgentInstanceId_ExecutorPrincipalId" FOREIGN KEY ("TenantId", "AgentInstanceId", "ExecutorPrincipalId") REFERENCES "SystemIdentities" ("TenantId", "AgentInstanceId", "PrincipalId") ON DELETE RESTRICT
        );

        INSERT INTO "ef_temp_TenantSessions" ("SessionId", "AgentInstanceId", "CreatedAt", "CreatedByPrincipalId", "ExecutorPrincipalId", "TenantId")
        SELECT "SessionId", "AgentInstanceId", "CreatedAt", "CreatedByPrincipalId", "ExecutorPrincipalId", "TenantId"
        FROM "TenantSessions";

        CREATE TABLE "ef_temp_TriggerOccurrences" (
            "OccurrenceId" TEXT NOT NULL CONSTRAINT "PK_TriggerOccurrences" PRIMARY KEY,
            "AcceptedAgentRunId" TEXT NULL,
            "AdmittedAtUtc" INTEGER NOT NULL,
            "AgentInstanceId" TEXT NOT NULL,
            "AutomationId" TEXT NULL,
            "ClaimId" TEXT NULL,
            "ClaimLeaseExpiresAtUtc" INTEGER NULL,
            "DedupeKey" TEXT NOT NULL,
            "Disposition" INTEGER NOT NULL,
            "DispositionReason" TEXT NULL,
            "EvidenceJson" TEXT NOT NULL,
            "ExecutionSessionId" TEXT NULL,
            "ExecutionTargetKind" INTEGER NOT NULL,
            "ExecutorPrincipalId" TEXT NULL,
            "LiveEvaluationCompletedAtUtc" INTEGER NULL,
            "LiveSessionId" TEXT NULL,
            "ModelCatalogKey" TEXT NULL,
            "ModelId" TEXT NULL,
            "ModelProviderAlias" TEXT NULL,
            "ModelReasoningEffort" TEXT NULL,
            "ModelSource" INTEGER NULL,
            "ObservedAtUtc" INTEGER NOT NULL,
            "ProfileId" TEXT NULL,
            "ReportToSessionId" TEXT NULL,
            "RoutingRevision" INTEGER NOT NULL,
            "RoutingUpdatedAtUtc" INTEGER NULL,
            "ScheduledAtUtc" INTEGER NULL,
            "SourceEventId" TEXT NULL,
            "SourceKind" INTEGER NOT NULL,
            "TargetSessionId" TEXT NULL,
            "TenantId" TEXT NULL,
            "TriggerId" TEXT NULL,
            "TriggerRevision" INTEGER NULL,
            CONSTRAINT "CK_TriggerOccurrences_Destination" CHECK ((ExecutionTargetKind = 0 AND TargetSessionId IS NULL) OR (ExecutionTargetKind = 1 AND TargetSessionId IS NOT NULL AND ReportToSessionId IS NULL)),
            CONSTRAINT "CK_TriggerOccurrences_ExecutionLink" CHECK ((ExecutionSessionId IS NULL AND AcceptedAgentRunId IS NULL) OR (ExecutionSessionId IS NOT NULL AND LiveSessionId IS NULL AND AcceptedAgentRunId IS NOT NULL) OR (ExecutionSessionId IS NULL AND LiveSessionId IS NOT NULL AND AcceptedAgentRunId IS NOT NULL AND LiveEvaluationCompletedAtUtc IS NOT NULL)),
            CONSTRAINT "CK_TriggerOccurrences_TenantOwner" CHECK ((TenantId IS NULL AND ExecutorPrincipalId IS NULL AND ProfileId IS NOT NULL) OR (TenantId IS NOT NULL AND ExecutorPrincipalId IS NOT NULL AND ProfileId IS NULL)),
            CONSTRAINT "FK_TriggerOccurrences_AgentRuns_AcceptedAgentRunId" FOREIGN KEY ("AcceptedAgentRunId") REFERENCES "AgentRuns" ("AgentRunId") ON DELETE RESTRICT,
            CONSTRAINT "FK_TriggerOccurrences_Sessions_ExecutionSessionId" FOREIGN KEY ("ExecutionSessionId") REFERENCES "Sessions" ("SessionId") ON DELETE RESTRICT,
            CONSTRAINT "FK_TriggerOccurrences_Sessions_LiveSessionId" FOREIGN KEY ("LiveSessionId") REFERENCES "Sessions" ("SessionId") ON DELETE RESTRICT,
            CONSTRAINT "FK_TriggerOccurrences_SystemIdentities_TenantId_AgentInstanceId_ExecutorPrincipalId" FOREIGN KEY ("TenantId", "AgentInstanceId", "ExecutorPrincipalId") REFERENCES "SystemIdentities" ("TenantId", "AgentInstanceId", "PrincipalId") ON DELETE RESTRICT
        );

        INSERT INTO "ef_temp_TriggerOccurrences" ("OccurrenceId", "AcceptedAgentRunId", "AdmittedAtUtc", "AgentInstanceId", "AutomationId", "ClaimId", "ClaimLeaseExpiresAtUtc", "DedupeKey", "Disposition", "DispositionReason", "EvidenceJson", "ExecutionSessionId", "ExecutionTargetKind", "ExecutorPrincipalId", "LiveEvaluationCompletedAtUtc", "LiveSessionId", "ModelCatalogKey", "ModelId", "ModelProviderAlias", "ModelReasoningEffort", "ModelSource", "ObservedAtUtc", "ProfileId", "ReportToSessionId", "RoutingRevision", "RoutingUpdatedAtUtc", "ScheduledAtUtc", "SourceEventId", "SourceKind", "TargetSessionId", "TenantId", "TriggerId", "TriggerRevision")
        SELECT "OccurrenceId", "AcceptedAgentRunId", "AdmittedAtUtc", "AgentInstanceId", "AutomationId", "ClaimId", "ClaimLeaseExpiresAtUtc", "DedupeKey", "Disposition", "DispositionReason", "EvidenceJson", "ExecutionSessionId", "ExecutionTargetKind", "ExecutorPrincipalId", "LiveEvaluationCompletedAtUtc", "LiveSessionId", "ModelCatalogKey", "ModelId", "ModelProviderAlias", "ModelReasoningEffort", "ModelSource", "ObservedAtUtc", "ProfileId", "ReportToSessionId", "RoutingRevision", "RoutingUpdatedAtUtc", "ScheduledAtUtc", "SourceEventId", "SourceKind", "TargetSessionId", "TenantId", "TriggerId", "TriggerRevision"
        FROM "TriggerOccurrences";

        CREATE TABLE "ef_temp_Automations" (
            "AutomationId" TEXT NOT NULL CONSTRAINT "PK_Automations" PRIMARY KEY,
            "AgentInstanceId" TEXT NOT NULL,
            "AuthorizationOrigin" INTEGER NOT NULL,
            "CreatedAtUtc" INTEGER NOT NULL,
            "ExecutionTargetKind" INTEGER NOT NULL,
            "ExecutorPrincipalId" TEXT NULL,
            "ExpiresAtUtc" INTEGER NULL,
            "Instructions" TEXT NOT NULL,
            "ModelOverrideCatalogKey" TEXT NULL,
            "ModelOverrideReasoningEffort" TEXT NULL,
            "Name" TEXT NOT NULL,
            "NextOccurrenceAtUtc" INTEGER NULL,
            "OccurrenceCount" INTEGER NOT NULL,
            "PresetId" TEXT NULL,
            "PresetVersion" INTEGER NULL,
            "ProfileId" TEXT NULL,
            "ReportToSessionId" TEXT NULL,
            "RequiresTools" INTEGER NOT NULL,
            "RequiresVision" INTEGER NOT NULL,
            "Revision" INTEGER NOT NULL,
            "SourceEventId" TEXT NULL,
            "SourceSessionId" TEXT NULL,
            "Status" INTEGER NOT NULL,
            "SuspensionReason" TEXT NULL,
            "TargetSessionId" TEXT NULL,
            "TenantId" TEXT NULL,
            "TriggerMode" INTEGER NOT NULL,
            "UpdatedAtUtc" INTEGER NOT NULL,
            CONSTRAINT "CK_Automations_Destination" CHECK ((ExecutionTargetKind = 0 AND TargetSessionId IS NULL) OR (ExecutionTargetKind = 1 AND TargetSessionId IS NOT NULL AND ReportToSessionId IS NULL)),
            CONSTRAINT "CK_Automations_Mode" CHECK (TriggerMode IN (0,1)),
            CONSTRAINT "CK_Automations_Preset" CHECK ((PresetId IS NULL AND PresetVersion IS NULL) OR (PresetId IS NOT NULL AND PresetVersion >= 1)),
            CONSTRAINT "CK_Automations_TenantOwner" CHECK ((TenantId IS NULL AND ExecutorPrincipalId IS NULL AND ProfileId IS NOT NULL) OR (TenantId IS NOT NULL AND ExecutorPrincipalId IS NOT NULL AND ProfileId IS NULL)),
            CONSTRAINT "FK_Automations_SystemIdentities_TenantId_AgentInstanceId_ExecutorPrincipalId" FOREIGN KEY ("TenantId", "AgentInstanceId", "ExecutorPrincipalId") REFERENCES "SystemIdentities" ("TenantId", "AgentInstanceId", "PrincipalId") ON DELETE RESTRICT
        );

        INSERT INTO "ef_temp_Automations" ("AutomationId", "AgentInstanceId", "AuthorizationOrigin", "CreatedAtUtc", "ExecutionTargetKind", "ExecutorPrincipalId", "ExpiresAtUtc", "Instructions", "ModelOverrideCatalogKey", "ModelOverrideReasoningEffort", "Name", "NextOccurrenceAtUtc", "OccurrenceCount", "PresetId", "PresetVersion", "ProfileId", "ReportToSessionId", "RequiresTools", "RequiresVision", "Revision", "SourceEventId", "SourceSessionId", "Status", "SuspensionReason", "TargetSessionId", "TenantId", "TriggerMode", "UpdatedAtUtc")
        SELECT "AutomationId", "AgentInstanceId", "AuthorizationOrigin", "CreatedAtUtc", "ExecutionTargetKind", "ExecutorPrincipalId", "ExpiresAtUtc", "Instructions", "ModelOverrideCatalogKey", "ModelOverrideReasoningEffort", "Name", "NextOccurrenceAtUtc", "OccurrenceCount", "PresetId", "PresetVersion", "ProfileId", "ReportToSessionId", "RequiresTools", "RequiresVision", "Revision", "SourceEventId", "SourceSessionId", "Status", "SuspensionReason", "TargetSessionId", "TenantId", "TriggerMode", "UpdatedAtUtc"
        FROM "Automations";

        DROP TABLE "TenantSessions";

        ALTER TABLE "ef_temp_TenantSessions" RENAME TO "TenantSessions";

        DROP TABLE "TriggerOccurrences";

        ALTER TABLE "ef_temp_TriggerOccurrences" RENAME TO "TriggerOccurrences";

        DROP TABLE "Automations";

        ALTER TABLE "ef_temp_Automations" RENAME TO "Automations";

        CREATE INDEX "IX_TenantSessions_CreatedByPrincipalId" ON "TenantSessions" ("CreatedByPrincipalId");

        CREATE UNIQUE INDEX "IX_TenantSessions_SessionId_AgentInstanceId" ON "TenantSessions" ("SessionId", "AgentInstanceId");

        CREATE INDEX "IX_TenantSessions_TenantId_AgentInstanceId_ExecutorPrincipalId" ON "TenantSessions" ("TenantId", "AgentInstanceId", "ExecutorPrincipalId");

        CREATE INDEX "IX_TenantSessions_TenantId_AgentInstanceId_SessionId" ON "TenantSessions" ("TenantId", "AgentInstanceId", "SessionId");

        CREATE UNIQUE INDEX "IX_TriggerOccurrences_AcceptedAgentRunId" ON "TriggerOccurrences" ("AcceptedAgentRunId") WHERE AcceptedAgentRunId IS NOT NULL;

        CREATE UNIQUE INDEX "IX_TriggerOccurrences_AgentInstanceId_ProfileId_DedupeKey" ON "TriggerOccurrences" ("AgentInstanceId", "ProfileId", "DedupeKey");

        CREATE INDEX "IX_TriggerOccurrences_AgentInstanceId_ProfileId_Disposition" ON "TriggerOccurrences" ("AgentInstanceId", "ProfileId", "Disposition");

        CREATE INDEX "IX_TriggerOccurrences_Disposition_ClaimLeaseExpiresAtUtc" ON "TriggerOccurrences" ("Disposition", "ClaimLeaseExpiresAtUtc");

        CREATE INDEX "IX_TriggerOccurrences_ExecutionSessionId" ON "TriggerOccurrences" ("ExecutionSessionId");

        CREATE INDEX "IX_TriggerOccurrences_LiveSessionId" ON "TriggerOccurrences" ("LiveSessionId");

        CREATE UNIQUE INDEX "IX_TriggerOccurrences_TenantId_AgentInstanceId_DedupeKey" ON "TriggerOccurrences" ("TenantId", "AgentInstanceId", "DedupeKey") WHERE TenantId IS NOT NULL;

        CREATE INDEX "IX_TriggerOccurrences_TenantId_AgentInstanceId_Disposition" ON "TriggerOccurrences" ("TenantId", "AgentInstanceId", "Disposition");

        CREATE INDEX "IX_TriggerOccurrences_TenantId_AgentInstanceId_ExecutorPrincipalId" ON "TriggerOccurrences" ("TenantId", "AgentInstanceId", "ExecutorPrincipalId");

        CREATE INDEX "IX_Automations_AgentInstanceId_ProfileId_Status" ON "Automations" ("AgentInstanceId", "ProfileId", "Status");

        CREATE INDEX "IX_Automations_Status_NextOccurrenceAtUtc_AutomationId" ON "Automations" ("Status", "NextOccurrenceAtUtc", "AutomationId");

        CREATE INDEX "IX_Automations_TenantId_AgentInstanceId_ExecutorPrincipalId" ON "Automations" ("TenantId", "AgentInstanceId", "ExecutorPrincipalId");

        CREATE INDEX "IX_Automations_TenantId_AgentInstanceId_Status" ON "Automations" ("TenantId", "AgentInstanceId", "Status");
        CREATE TRIGGER TR_TenantApprovals_AccountAction_Insert BEFORE INSERT ON TenantApprovalRequests
        WHEN json_type(NEW.PayloadJson, '$.Operation.AccountAction') IS 'object' AND NOT EXISTS (
            SELECT 1 FROM AgentCredentialBindings b JOIN Credentials c ON c.CredentialId = b.CredentialId
            JOIN TenantCredentials owned ON owned.CredentialId = c.CredentialId AND owned.TenantId = b.TenantId AND owned.DeletedAt IS NULL
            WHERE b.TenantId = NEW.TenantId AND b.AgentInstanceId = NEW.InstanceId AND b.CredentialId = NEW.CredentialId
              AND b.BindingId = json_extract(NEW.PayloadJson, '$.Operation.AccountAction.Credential.BindingId')
              AND b.Reference = json_extract(NEW.PayloadJson, '$.Operation.AccountAction.Credential.Reference')
              AND b.Revision = json_extract(NEW.PayloadJson, '$.Operation.AccountAction.Credential.BindingRevision')
              AND c.Revision = json_extract(NEW.PayloadJson, '$.Operation.AccountAction.Credential.CredentialRevision')
              AND c.SecretRevision = json_extract(NEW.PayloadJson, '$.Operation.AccountAction.Credential.SecretRevision')
              AND c.Purpose = CASE json_extract(NEW.PayloadJson, '$.Operation.AccountAction.Purpose') WHEN 1 THEN 'EmailAccount' WHEN 2 THEN 'GoogleDriveAccount' END)
        BEGIN SELECT RAISE(ABORT, 'exact account action binding required'); END;
        CREATE TRIGGER TR_TenantApprovals_AccountAction_Update BEFORE UPDATE ON TenantApprovalRequests
        WHEN (json_type(OLD.PayloadJson, '$.Operation.AccountAction') IS 'object' OR json_type(NEW.PayloadJson, '$.Operation.AccountAction') IS 'object')
          AND (json_remove(NEW.PayloadJson, '$.Status', '$.Revision') IS NOT json_remove(OLD.PayloadJson, '$.Status', '$.Revision')
            OR NEW.RequestId IS NOT OLD.RequestId OR NEW.TenantId IS NOT OLD.TenantId OR NEW.RunId IS NOT OLD.RunId
            OR NEW.SessionId IS NOT OLD.SessionId OR NEW.InstanceId IS NOT OLD.InstanceId OR NEW.RootActivationId IS NOT OLD.RootActivationId
            OR NEW.ExecutorPrincipalId IS NOT OLD.ExecutorPrincipalId OR NEW.OriginalInitiatorPrincipalId IS NOT OLD.OriginalInitiatorPrincipalId
            OR NEW.OperationId IS NOT OLD.OperationId OR NEW.Fingerprint IS NOT OLD.Fingerprint OR NEW.Permission IS NOT OLD.Permission
            OR NEW.ResourceType IS NOT OLD.ResourceType OR NEW.ResourceId IS NOT OLD.ResourceId OR NEW.CredentialId IS NOT OLD.CredentialId
            OR NEW.Kind IS NOT OLD.Kind OR NEW.CreatedAt IS NOT OLD.CreatedAt OR NEW.ExpiresAt IS NOT OLD.ExpiresAt)
        BEGIN SELECT RAISE(ABORT, 'account action consent is immutable'); END;
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
        CREATE TRIGGER TR_TenantApprovals_AccountRead_Insert BEFORE INSERT ON TenantApprovalRequests
        WHEN json_type(NEW.PayloadJson, '$.Operation.AccountRead') IS 'object' AND NOT EXISTS (
            SELECT 1 FROM AgentCredentialBindings b JOIN Credentials c ON c.CredentialId = b.CredentialId
            JOIN TenantCredentials owned ON owned.CredentialId = c.CredentialId AND owned.TenantId = b.TenantId AND owned.DeletedAt IS NULL
            WHERE b.TenantId = NEW.TenantId AND b.AgentInstanceId = NEW.InstanceId AND b.CredentialId = NEW.CredentialId
              AND b.BindingId = json_extract(NEW.PayloadJson, '$.Operation.AccountRead.Credential.BindingId')
              AND b.Reference = json_extract(NEW.PayloadJson, '$.Operation.AccountRead.Credential.Reference')
              AND b.Revision = json_extract(NEW.PayloadJson, '$.Operation.AccountRead.Credential.BindingRevision')
              AND c.Revision = json_extract(NEW.PayloadJson, '$.Operation.AccountRead.Credential.CredentialRevision')
              AND c.SecretRevision = json_extract(NEW.PayloadJson, '$.Operation.AccountRead.Credential.SecretRevision')
              AND c.Purpose = CASE json_extract(NEW.PayloadJson, '$.Operation.AccountRead.Purpose') WHEN 1 THEN 'EmailAccount' WHEN 2 THEN 'GoogleDriveAccount' END)
        BEGIN SELECT RAISE(ABORT, 'exact account read binding required'); END;
        CREATE TRIGGER TR_TenantApprovals_AccountRead_Update BEFORE UPDATE ON TenantApprovalRequests
        WHEN (json_type(OLD.PayloadJson, '$.Operation.AccountRead') IS 'object' OR json_type(NEW.PayloadJson, '$.Operation.AccountRead') IS 'object')
          AND (json_remove(NEW.PayloadJson, '$.Status', '$.Revision') IS NOT json_remove(OLD.PayloadJson, '$.Status', '$.Revision')
            OR NEW.RequestId IS NOT OLD.RequestId OR NEW.TenantId IS NOT OLD.TenantId OR NEW.RunId IS NOT OLD.RunId
            OR NEW.SessionId IS NOT OLD.SessionId OR NEW.InstanceId IS NOT OLD.InstanceId OR NEW.RootActivationId IS NOT OLD.RootActivationId
            OR NEW.ExecutorPrincipalId IS NOT OLD.ExecutorPrincipalId OR NEW.OriginalInitiatorPrincipalId IS NOT OLD.OriginalInitiatorPrincipalId
            OR NEW.OperationId IS NOT OLD.OperationId OR NEW.Fingerprint IS NOT OLD.Fingerprint OR NEW.Permission IS NOT OLD.Permission
            OR NEW.ResourceType IS NOT OLD.ResourceType OR NEW.ResourceId IS NOT OLD.ResourceId OR NEW.CredentialId IS NOT OLD.CredentialId
            OR NEW.Kind IS NOT OLD.Kind OR NEW.CreatedAt IS NOT OLD.CreatedAt OR NEW.ExpiresAt IS NOT OLD.ExpiresAt)
        BEGIN SELECT RAISE(ABORT, 'account read consent is immutable'); END;
        CREATE TRIGGER TR_TenantApprovals_BackgroundCreation_Update BEFORE UPDATE ON TenantApprovalRequests
        WHEN json_extract(OLD.PayloadJson, '$.Operation.BackgroundStart') IS NOT NULL OR json_extract(NEW.PayloadJson, '$.Operation.BackgroundStart') IS NOT NULL
        BEGIN
            SELECT RAISE(ABORT, 'Exact background creation is immutable') WHERE
                json_extract(NEW.PayloadJson, '$.Operation') IS NOT json_extract(OLD.PayloadJson, '$.Operation')
                OR NEW.RequestId IS NOT OLD.RequestId OR NEW.TenantId IS NOT OLD.TenantId OR NEW.RunId IS NOT OLD.RunId
                OR NEW.SessionId IS NOT OLD.SessionId OR NEW.InstanceId IS NOT OLD.InstanceId OR NEW.RootActivationId IS NOT OLD.RootActivationId
                OR NEW.ExecutorPrincipalId IS NOT OLD.ExecutorPrincipalId OR NEW.OriginalInitiatorPrincipalId IS NOT OLD.OriginalInitiatorPrincipalId
                OR NEW.OperationId IS NOT OLD.OperationId OR NEW.Fingerprint IS NOT OLD.Fingerprint OR NEW.Permission IS NOT OLD.Permission
                OR NEW.ResourceType IS NOT OLD.ResourceType OR NEW.ResourceId IS NOT OLD.ResourceId OR NEW.Kind IS NOT OLD.Kind
                OR NEW.CreatedAt IS NOT OLD.CreatedAt OR NEW.ExpiresAt IS NOT OLD.ExpiresAt;
        END;
        CREATE TRIGGER TR_TenantApprovals_BackgroundReport_Insert BEFORE INSERT ON TenantApprovalRequests
        WHEN json_type(NEW.PayloadJson, '$.Operation.BackgroundReport') IS 'object'
        BEGIN
            SELECT RAISE(ABORT, 'Report approval must reference its exact completed child and owned parent') WHERE NOT EXISTS (
                SELECT 1 FROM AgentRuns child JOIN TenantSessions origin ON origin.TenantId = child.TenantId AND origin.SessionId = child.SessionId
                JOIN TenantSessions parent ON parent.TenantId = origin.TenantId AND parent.AgentInstanceId = origin.AgentInstanceId AND parent.ExecutorPrincipalId = origin.ExecutorPrincipalId
                JOIN Sessions saved ON saved.SessionId = parent.SessionId
                WHERE child.TenantId = NEW.TenantId AND child.AgentRunId = NEW.RunId AND child.SessionId = NEW.SessionId
                AND child.ExecutorPrincipalId = NEW.ExecutorPrincipalId AND child.AgentInstanceId = NEW.InstanceId AND child.RootActivationId = NEW.RootActivationId
                AND child.Status IN (4,5,6) AND child.Revision = json_extract(NEW.PayloadJson, '$.Operation.BackgroundReport.SourceRevision')
                AND parent.SessionId = json_extract(NEW.PayloadJson, '$.Operation.BackgroundReport.ParentSessionId')
                AND saved.Revision = json_extract(NEW.PayloadJson, '$.Operation.BackgroundReport.ParentRevision') AND saved.DurablyDeletedAtUtc IS NULL);
        END;
        CREATE TRIGGER TR_TenantApprovals_BackgroundReport_Update BEFORE UPDATE ON TenantApprovalRequests
        WHEN json_extract(OLD.PayloadJson, '$.Operation.BackgroundReport') IS NOT NULL OR json_extract(NEW.PayloadJson, '$.Operation.BackgroundReport') IS NOT NULL
        BEGIN
            SELECT RAISE(ABORT, 'Exact background creation is immutable') WHERE
                json_extract(NEW.PayloadJson, '$.Operation') IS NOT json_extract(OLD.PayloadJson, '$.Operation')
                OR NEW.RequestId IS NOT OLD.RequestId OR NEW.TenantId IS NOT OLD.TenantId OR NEW.RunId IS NOT OLD.RunId
                OR NEW.SessionId IS NOT OLD.SessionId OR NEW.InstanceId IS NOT OLD.InstanceId OR NEW.RootActivationId IS NOT OLD.RootActivationId
                OR NEW.ExecutorPrincipalId IS NOT OLD.ExecutorPrincipalId OR NEW.OriginalInitiatorPrincipalId IS NOT OLD.OriginalInitiatorPrincipalId
                OR NEW.OperationId IS NOT OLD.OperationId OR NEW.Fingerprint IS NOT OLD.Fingerprint OR NEW.Permission IS NOT OLD.Permission
                OR NEW.ResourceType IS NOT OLD.ResourceType OR NEW.ResourceId IS NOT OLD.ResourceId OR NEW.Kind IS NOT OLD.Kind
                OR NEW.CreatedAt IS NOT OLD.CreatedAt OR NEW.ExpiresAt IS NOT OLD.ExpiresAt;
        END;
        CREATE TRIGGER TR_TenantApprovals_BrowserArtifacts_Insert BEFORE INSERT ON TenantApprovalRequests
        WHEN json_extract(NEW.PayloadJson, '$.Operation.BrowserCommand.Artifacts') IS NOT NULL
        OR json_extract(NEW.PayloadJson, '$.Operation.BrowserCommand.ToolName') IS 'browser.screenshot'
        BEGIN
        SELECT RAISE(ABORT, 'Browser publication requires exact bounded intent') WHERE NOT COALESCE((
        NEW.Permission IN ('browser.use','credentials.use')
        AND json_type(NEW.PayloadJson, '$.Operation.BrowserCommand.ReadOnly') IS 'false'
        AND json_type(NEW.PayloadJson, '$.Operation.BrowserCommand.Artifacts') IS 'object'
        AND (SELECT count(*) FROM json_each(NEW.PayloadJson, '$.Operation.BrowserCommand.Artifacts')) = 8
        AND (SELECT count(DISTINCT key) FROM json_each(NEW.PayloadJson, '$.Operation.BrowserCommand.Artifacts')
        WHERE key IN ('Capture','Downloads','CaptureBytes','DownloadBytes','CapturesPerScope','DownloadsPerScope','CapturePrivacyMode','CapturePrivacyHash')) = 8
        AND json_type(NEW.PayloadJson, '$.Operation.BrowserCommand.Artifacts.Capture') IN ('true','false')
        AND json_type(NEW.PayloadJson, '$.Operation.BrowserCommand.Artifacts.Downloads') IN ('true','false')
        AND (json_extract(NEW.PayloadJson, '$.Operation.BrowserCommand.Artifacts.Capture') = 1
        OR json_extract(NEW.PayloadJson, '$.Operation.BrowserCommand.Artifacts.Downloads') = 1)
        AND json_extract(NEW.PayloadJson, '$.Operation.BrowserCommand.Artifacts.Capture') =
        (json_extract(NEW.PayloadJson, '$.Operation.BrowserCommand.ToolName') = 'browser.screenshot')
        AND json_type(NEW.PayloadJson, '$.Operation.BrowserCommand.Artifacts.CaptureBytes') IS 'integer'
        AND json_extract(NEW.PayloadJson, '$.Operation.BrowserCommand.Artifacts.CaptureBytes') BETWEEN 1 AND 1500000
        AND json_type(NEW.PayloadJson, '$.Operation.BrowserCommand.Artifacts.DownloadBytes') IS 'integer'
        AND json_extract(NEW.PayloadJson, '$.Operation.BrowserCommand.Artifacts.DownloadBytes') BETWEEN 1 AND 5242880
        AND json_type(NEW.PayloadJson, '$.Operation.BrowserCommand.Artifacts.CapturesPerScope') IS 'integer'
        AND json_extract(NEW.PayloadJson, '$.Operation.BrowserCommand.Artifacts.CapturesPerScope') BETWEEN 1 AND 4
        AND json_type(NEW.PayloadJson, '$.Operation.BrowserCommand.Artifacts.DownloadsPerScope') IS 'integer'
        AND json_extract(NEW.PayloadJson, '$.Operation.BrowserCommand.Artifacts.DownloadsPerScope') BETWEEN 1 AND 2
        AND ((json_extract(NEW.PayloadJson, '$.Operation.BrowserCommand.Artifacts.Capture') = 1
        AND json_extract(NEW.PayloadJson, '$.Operation.BrowserCommand.Artifacts.CapturePrivacyMode') IN ('Protected','Unmasked')
        AND json_type(NEW.PayloadJson, '$.Operation.BrowserCommand.Artifacts.CapturePrivacyHash') IS 'text'
        AND length(json_extract(NEW.PayloadJson, '$.Operation.BrowserCommand.Artifacts.CapturePrivacyHash')) = 64
        AND json_extract(NEW.PayloadJson, '$.Operation.BrowserCommand.Artifacts.CapturePrivacyHash') NOT GLOB '*[^0-9a-f]*')
        OR (json_extract(NEW.PayloadJson, '$.Operation.BrowserCommand.Artifacts.Capture') = 0
        AND json_extract(NEW.PayloadJson, '$.Operation.BrowserCommand.Artifacts.CapturePrivacyMode') IS NULL
        AND json_extract(NEW.PayloadJson, '$.Operation.BrowserCommand.Artifacts.CapturePrivacyHash') IS NULL))
        ),0);
        END;
        CREATE TRIGGER TR_TenantApprovals_BrowserArtifacts_Update BEFORE UPDATE ON TenantApprovalRequests
        WHEN json_extract(OLD.PayloadJson, '$.Operation.BrowserCommand.Artifacts') IS NOT
        json_extract(NEW.PayloadJson, '$.Operation.BrowserCommand.Artifacts')
        BEGIN SELECT RAISE(ABORT, 'Browser publication intent is immutable'); END;
        CREATE TRIGGER TR_TenantApprovals_BrowserFiles_Insert BEFORE INSERT ON TenantApprovalRequests
        WHEN json_extract(NEW.PayloadJson, '$.Operation.BrowserCommand.ToolName') IS 'browser.upload'
        OR (json_extract(NEW.PayloadJson, '$.Operation.BrowserCommand.ToolName') IS 'browser.drop' AND
        json_extract(json_extract(NEW.PayloadJson, '$.Operation.BrowserCommand.ArgumentsJson'), '$.artifactId') IS NOT NULL)
        OR json_extract(NEW.PayloadJson, '$.Operation.BrowserCommand.ArtifactSourcesJson') IS NOT NULL
        OR json_extract(NEW.PayloadJson, '$.Operation.BrowserCommand.FileInputOrigin') IS NOT NULL
        BEGIN
        SELECT RAISE(ABORT, 'Browser file transfer requires exact owned Artifact sources') WHERE NOT COALESCE((
        NEW.Kind = 0 AND NEW.Permission = 'browser.use' AND NEW.ResourceType = 'agent' AND NEW.ResourceId = NEW.InstanceId
        AND json_extract(NEW.PayloadJson, '$.Operation.BrowserCommand.ToolName') IN ('browser.upload','browser.drop')
        AND json_type(NEW.PayloadJson, '$.Operation.BrowserCommand.ReadOnly') IS 'false'
        AND json_extract(NEW.PayloadJson, '$.Operation.BrowserCommand.Credential') IS NULL
        AND json_type(NEW.PayloadJson, '$.Operation.BrowserCommand.FileInputOrigin') IS 'text'
        AND length(json_extract(NEW.PayloadJson, '$.Operation.BrowserCommand.FileInputOrigin')) BETWEEN 1 AND 2048
        AND json_type(NEW.PayloadJson, '$.Operation.BrowserCommand.ArtifactSourcesJson') IS 'text'
        AND length(CAST(json_extract(NEW.PayloadJson, '$.Operation.BrowserCommand.ArtifactSourcesJson') AS BLOB)) BETWEEN 1 AND 16384
        AND json_valid(json_extract(NEW.PayloadJson, '$.Operation.BrowserCommand.ArtifactSourcesJson'))
        AND json_type(json_extract(NEW.PayloadJson, '$.Operation.BrowserCommand.ArtifactSourcesJson')) IS 'array'
        AND ((json_extract(NEW.PayloadJson, '$.Operation.BrowserCommand.ToolName') IS 'browser.upload'
        AND json_array_length(json_extract(NEW.PayloadJson, '$.Operation.BrowserCommand.ArtifactSourcesJson')) BETWEEN 1 AND 8
        AND json_array_length(json_extract(NEW.PayloadJson, '$.Operation.BrowserCommand.ArtifactSourcesJson')) =
        json_array_length(json_extract(NEW.PayloadJson, '$.Operation.BrowserCommand.ArgumentsJson'), '$.artifactIds'))
        OR (json_extract(NEW.PayloadJson, '$.Operation.BrowserCommand.ToolName') IS 'browser.drop'
        AND json_array_length(json_extract(NEW.PayloadJson, '$.Operation.BrowserCommand.ArtifactSourcesJson')) = 1
        AND json_type(json_extract(NEW.PayloadJson, '$.Operation.BrowserCommand.ArgumentsJson'), '$.artifactId') IS 'text'
        AND json_type(json_extract(NEW.PayloadJson, '$.Operation.BrowserCommand.ArgumentsJson'), '$.text') IS NULL
        AND json_type(json_extract(NEW.PayloadJson, '$.Operation.BrowserCommand.ArgumentsJson'), '$.mimeType') IS NULL))
        AND NOT EXISTS (SELECT 1 FROM json_each(json_extract(NEW.PayloadJson, '$.Operation.BrowserCommand.ArtifactSourcesJson')) source
        WHERE json_type(source.value) IS NOT 'object'
        OR json_type(source.value, '$.ArtifactId') IS NOT 'text'
        OR json_type(source.value, '$.DisplayName') IS NOT 'text'
        OR json_type(source.value, '$.ContentType') IS NOT 'text'
        OR json_type(source.value, '$.ByteSize') IS NOT 'integer'
        OR json_type(source.value, '$.Sha256Hex') IS NOT 'text'
        OR (SELECT count(*) FROM json_each(source.value)) <> 5
        OR (SELECT count(DISTINCT key) FROM json_each(source.value)
        WHERE key IN ('ArtifactId','DisplayName','ContentType','ByteSize','Sha256Hex')) <> 5
        OR json_extract(source.value, '$.ArtifactId') IS NOT
        CASE WHEN json_extract(NEW.PayloadJson, '$.Operation.BrowserCommand.ToolName') IS 'browser.upload'
        THEN json_extract(json_extract(NEW.PayloadJson, '$.Operation.BrowserCommand.ArgumentsJson'), '$.artifactIds[' || source.key || ']')
        ELSE json_extract(json_extract(NEW.PayloadJson, '$.Operation.BrowserCommand.ArgumentsJson'), '$.artifactId') END
        OR NOT EXISTS (SELECT 1 FROM Artifacts a WHERE a.TenantId = NEW.TenantId AND a.SessionId = NEW.SessionId
        AND a.ArtifactId = json_extract(source.value, '$.ArtifactId') AND a.DisplayName = json_extract(source.value, '$.DisplayName')
        AND a.ContentType = json_extract(source.value, '$.ContentType') AND a.ByteSize = json_extract(source.value, '$.ByteSize')
        AND a.Sha256Hex = json_extract(source.value, '$.Sha256Hex')
        AND EXISTS (SELECT 1 FROM Sessions saved WHERE saved.SessionId = a.SessionId AND saved.DurablyDeletedAtUtc IS NULL)))
        AND (SELECT count(DISTINCT json_extract(value, '$.ArtifactId'))
        FROM json_each(json_extract(NEW.PayloadJson, '$.Operation.BrowserCommand.ArtifactSourcesJson'))) =
        json_array_length(json_extract(NEW.PayloadJson, '$.Operation.BrowserCommand.ArtifactSourcesJson'))
        AND (SELECT sum(json_extract(value, '$.ByteSize'))
        FROM json_each(json_extract(NEW.PayloadJson, '$.Operation.BrowserCommand.ArtifactSourcesJson'))) <= 52428800
        ), 0);
        END;
        CREATE TRIGGER TR_TenantApprovals_BrowserFiles_Update BEFORE UPDATE ON TenantApprovalRequests
        WHEN json_extract(OLD.PayloadJson, '$.Operation.BrowserCommand.ArtifactSourcesJson') IS NOT
        json_extract(NEW.PayloadJson, '$.Operation.BrowserCommand.ArtifactSourcesJson')
        OR json_extract(OLD.PayloadJson, '$.Operation.BrowserCommand.FileInputOrigin') IS NOT
        json_extract(NEW.PayloadJson, '$.Operation.BrowserCommand.FileInputOrigin')
        OR ((json_extract(OLD.PayloadJson, '$.Operation.BrowserCommand.ArtifactSourcesJson') IS NOT NULL
        OR json_extract(NEW.PayloadJson, '$.Operation.BrowserCommand.ArtifactSourcesJson') IS NOT NULL)
        AND (json_extract(OLD.PayloadJson, '$.Operation.BrowserCommand.ToolName') IS NOT json_extract(NEW.PayloadJson, '$.Operation.BrowserCommand.ToolName')
        OR json_extract(OLD.PayloadJson, '$.Operation.BrowserCommand.ArgumentsJson') IS NOT json_extract(NEW.PayloadJson, '$.Operation.BrowserCommand.ArgumentsJson')))
        BEGIN
        SELECT RAISE(ABORT, 'Browser file transfer sources are immutable');
        END;
        CREATE TRIGGER TR_TenantApprovals_CompletionRead_Insert BEFORE INSERT ON TenantApprovalRequests
        WHEN json_type(NEW.PayloadJson, '$.Operation.CompletionRead') IS 'object'
        BEGIN
            SELECT RAISE(ABORT, 'Completion read requires one exact ordinary read Review') WHERE NOT COALESCE((
                NEW.Kind = 0 AND NEW.Status IN (0,1,2,3,4) AND NEW.Revision >= 1 AND NEW.ExpiresAt > NEW.CreatedAt AND length(NEW.Fingerprint) = 64
                AND NEW.Permission = 'agent-runs.read' AND NEW.ResourceType = 'agent-run'
                AND NEW.ResourceId IS json_extract(NEW.PayloadJson, '$.Operation.CompletionRead.SourceRunId')
                AND NEW.OriginalInitiatorPrincipalId IS NOT NULL AND NEW.CredentialId IS NULL AND NEW.SandboxProfileId IS NULL AND NEW.SandboxProfileRevision IS NULL
                AND json_type(NEW.PayloadJson, '$.Operation.CompletionRead') IS 'object'
                AND json_extract(NEW.PayloadJson, '$.Operation.CompletionRead.ParentSessionId') IS NEW.SessionId
                AND json_type(NEW.PayloadJson, '$.Operation.CompletionRead.SourceRevision') IS 'integer'
                AND json_extract(NEW.PayloadJson, '$.Operation.CompletionRead.SourceRevision') >= 1
                AND json_type(NEW.PayloadJson, '$.Operation.CompletionRead.Objective') IS 'text'
                AND length(CAST(json_extract(NEW.PayloadJson, '$.Operation.CompletionRead.Objective') AS BLOB)) BETWEEN 1 AND 4000
                AND json_type(NEW.PayloadJson, '$.Operation.CompletionRead.ObservationJson') IS 'text'
                AND length(CAST(json_extract(NEW.PayloadJson, '$.Operation.CompletionRead.ObservationJson') AS BLOB)) BETWEEN 2 AND 20000
                AND json_valid(json_extract(NEW.PayloadJson, '$.Operation.CompletionRead.ObservationJson'))
                AND json_type(json_extract(NEW.PayloadJson, '$.Operation.CompletionRead.ObservationJson')) IS 'object'
                AND json_extract(json_extract(NEW.PayloadJson, '$.Operation.CompletionRead.ObservationJson'), '$.untrusted') = 1
                AND json_extract(json_extract(NEW.PayloadJson, '$.Operation.CompletionRead.ObservationJson'), '$.tenantCompletionRead') = 1
                AND json_extract(json_extract(NEW.PayloadJson, '$.Operation.CompletionRead.ObservationJson'), '$.backgroundSessionId') IS json_extract(NEW.PayloadJson, '$.Operation.CompletionRead.ChildSessionId')
                AND json_extract(json_extract(NEW.PayloadJson, '$.Operation.CompletionRead.ObservationJson'), '$.parentSessionId') IS NEW.SessionId
                AND json_extract(json_extract(NEW.PayloadJson, '$.Operation.CompletionRead.ObservationJson'), '$.agentRunId') IS json_extract(NEW.PayloadJson, '$.Operation.CompletionRead.InitialRunId')
                AND json_extract(json_extract(NEW.PayloadJson, '$.Operation.CompletionRead.ObservationJson'), '$.sourceAgentRunId') IS NEW.ResourceId
                AND json_extract(json_extract(NEW.PayloadJson, '$.Operation.CompletionRead.ObservationJson'), '$.status') IN ('Completed','Failed','Cancelled')
                AND json_type(NEW.PayloadJson, '$.Operation.CompletionRead.ObservationHash') IS 'text'
                AND length(json_extract(NEW.PayloadJson, '$.Operation.CompletionRead.ObservationHash')) = 64
                AND json_extract(NEW.PayloadJson, '$.Operation.CompletionRead.ObservationHash') NOT GLOB '*[^0-9a-f]*'
                AND json_type(NEW.PayloadJson, '$.Operation.CompletionRead.ArtifactsJson') IS 'text'
                AND length(CAST(json_extract(NEW.PayloadJson, '$.Operation.CompletionRead.ArtifactsJson') AS BLOB)) <= 8192
                AND json_valid(json_extract(NEW.PayloadJson, '$.Operation.CompletionRead.ArtifactsJson'))
                AND json_type(json_extract(NEW.PayloadJson, '$.Operation.CompletionRead.ArtifactsJson')) IS 'array'
                AND json_array_length(json_extract(NEW.PayloadJson, '$.Operation.CompletionRead.ArtifactsJson')) <= 3
                AND length(CAST(NEW.PayloadJson AS BLOB)) <= 131072
                AND json_type(NEW.PayloadJson, '$.Operation.CompletionRead.ChildSessionId') IS 'text' AND length(json_extract(NEW.PayloadJson, '$.Operation.CompletionRead.ChildSessionId')) = 36 AND json_extract(NEW.PayloadJson, '$.Operation.CompletionRead.ChildSessionId') NOT GLOB '*[^0-9a-f-]*' AND substr(json_extract(NEW.PayloadJson, '$.Operation.CompletionRead.ChildSessionId'),9,1) IS '-' AND substr(json_extract(NEW.PayloadJson, '$.Operation.CompletionRead.ChildSessionId'),14,1) IS '-' AND substr(json_extract(NEW.PayloadJson, '$.Operation.CompletionRead.ChildSessionId'),19,1) IS '-' AND substr(json_extract(NEW.PayloadJson, '$.Operation.CompletionRead.ChildSessionId'),24,1) IS '-' AND json_extract(NEW.PayloadJson, '$.Operation.CompletionRead.ChildSessionId') IS NOT '00000000-0000-0000-0000-000000000000' AND json_extract(NEW.PayloadJson, '$.Operation.CompletionRead.ChildSessionId') IS NOT NEW.SessionId
                AND json_type(NEW.PayloadJson, '$.Operation.CompletionRead.InitialRunId') IS 'text' AND length(json_extract(NEW.PayloadJson, '$.Operation.CompletionRead.InitialRunId')) = 36 AND json_extract(NEW.PayloadJson, '$.Operation.CompletionRead.InitialRunId') NOT GLOB '*[^0-9a-f-]*' AND substr(json_extract(NEW.PayloadJson, '$.Operation.CompletionRead.InitialRunId'),9,1) IS '-' AND substr(json_extract(NEW.PayloadJson, '$.Operation.CompletionRead.InitialRunId'),14,1) IS '-' AND substr(json_extract(NEW.PayloadJson, '$.Operation.CompletionRead.InitialRunId'),19,1) IS '-' AND substr(json_extract(NEW.PayloadJson, '$.Operation.CompletionRead.InitialRunId'),24,1) IS '-' AND json_extract(NEW.PayloadJson, '$.Operation.CompletionRead.InitialRunId') IS NOT '00000000-0000-0000-0000-000000000000' AND json_extract(NEW.PayloadJson, '$.Operation.CompletionRead.InitialRunId') IS NOT NEW.SessionId
                AND json_type(NEW.PayloadJson, '$.Operation.CompletionRead.SourceRunId') IS 'text' AND length(json_extract(NEW.PayloadJson, '$.Operation.CompletionRead.SourceRunId')) = 36 AND json_extract(NEW.PayloadJson, '$.Operation.CompletionRead.SourceRunId') NOT GLOB '*[^0-9a-f-]*' AND substr(json_extract(NEW.PayloadJson, '$.Operation.CompletionRead.SourceRunId'),9,1) IS '-' AND substr(json_extract(NEW.PayloadJson, '$.Operation.CompletionRead.SourceRunId'),14,1) IS '-' AND substr(json_extract(NEW.PayloadJson, '$.Operation.CompletionRead.SourceRunId'),19,1) IS '-' AND substr(json_extract(NEW.PayloadJson, '$.Operation.CompletionRead.SourceRunId'),24,1) IS '-' AND json_extract(NEW.PayloadJson, '$.Operation.CompletionRead.SourceRunId') IS NOT '00000000-0000-0000-0000-000000000000' AND json_extract(NEW.PayloadJson, '$.Operation.CompletionRead.SourceRunId') IS NOT NEW.SessionId
                AND json_extract(NEW.PayloadJson, '$.Operation.Persona') IS NULL
                AND json_extract(NEW.PayloadJson, '$.Operation.Preferences') IS NULL
                AND json_extract(NEW.PayloadJson, '$.Operation.CredentialCheck') IS NULL
                AND json_extract(NEW.PayloadJson, '$.Operation.SandboxCommand') IS NULL
                AND json_extract(NEW.PayloadJson, '$.Operation.SandboxControl') IS NULL
                AND json_extract(NEW.PayloadJson, '$.Operation.SandboxRead') IS NULL
                AND json_extract(NEW.PayloadJson, '$.Operation.BrowserCommand') IS NULL
                AND json_extract(NEW.PayloadJson, '$.Operation.CredentialRead') IS NULL
                AND json_extract(NEW.PayloadJson, '$.Operation.ArtifactPublication') IS NULL
                AND json_extract(NEW.PayloadJson, '$.Operation.ArtifactVerification') IS NULL
                AND json_extract(NEW.PayloadJson, '$.Operation.WorkspaceRead') IS NULL
                AND json_extract(NEW.PayloadJson, '$.Operation.WorkspaceMutation') IS NULL
                AND json_extract(NEW.PayloadJson, '$.Operation.AttachmentRead') IS NULL
                AND json_extract(NEW.PayloadJson, '$.Operation.BackgroundStart') IS NULL
                AND json_extract(NEW.PayloadJson, '$.Operation.BackgroundReport') IS NULL),0);
            SELECT RAISE(ABORT, 'Completion read source must be an exact owned terminal child') WHERE NOT EXISTS (
                SELECT 1 FROM TenantSessions child JOIN Sessions saved ON saved.SessionId = child.SessionId
                JOIN AgentRuns initial ON initial.AgentRunId = json_extract(NEW.PayloadJson, '$.Operation.CompletionRead.InitialRunId')
                JOIN AgentRuns source ON source.AgentRunId = json_extract(NEW.PayloadJson, '$.Operation.CompletionRead.SourceRunId')
                WHERE child.TenantId = NEW.TenantId AND child.AgentInstanceId = NEW.InstanceId AND child.ExecutorPrincipalId = NEW.ExecutorPrincipalId
                AND child.SessionId = json_extract(NEW.PayloadJson, '$.Operation.CompletionRead.ChildSessionId') AND saved.DurablyDeletedAtUtc IS NULL
                AND json_extract(saved.OriginJson, '$.originatingSessionId') IS NEW.SessionId
                AND json_extract(saved.OriginJson, '$.initialBackgroundAgentRunId') IS initial.AgentRunId
                AND initial.TenantId = NEW.TenantId AND initial.SessionId = child.SessionId AND initial.AgentInstanceId = NEW.InstanceId AND initial.ExecutorPrincipalId = NEW.ExecutorPrincipalId
                AND json_extract(initial.PayloadJson, '$.admission.activation.kind') = 2 AND json_extract(initial.PayloadJson, '$.admission.outputContract') = 1
                AND source.TenantId = NEW.TenantId AND source.SessionId = child.SessionId AND source.AgentInstanceId = NEW.InstanceId AND source.ExecutorPrincipalId = NEW.ExecutorPrincipalId
                AND source.RootActivationId = initial.RootActivationId AND source.Status IN (4,5,6)
                AND source.Revision = json_extract(NEW.PayloadJson, '$.Operation.CompletionRead.SourceRevision')
                AND json_extract(source.PayloadJson, '$.admission.outputContract') = 1
                AND (json_extract(source.PayloadJson, '$.result.outcomeKind') IS NULL OR json_extract(source.PayloadJson, '$.result.outcomeKind') <> 3)
                AND EXISTS (
                    WITH RECURSIVE lineage(id, depth, seen) AS (
                        SELECT initial.AgentRunId, 0, ',' || initial.AgentRunId || ','
                        UNION ALL
                        SELECT next.AgentRunId, chain.depth + 1, chain.seen || next.AgentRunId || ',' FROM lineage chain
                        JOIN AgentRuns previous ON previous.AgentRunId = chain.id
                        JOIN AgentRuns next ON next.AgentRunId = json_extract(previous.PayloadJson, '$.result.continuationRunId')
                        WHERE chain.depth < 63 AND previous.Status = 4 AND json_extract(previous.PayloadJson, '$.result.outcomeKind') = 3
                        AND instr(chain.seen, ',' || next.AgentRunId || ',') = 0
                        AND next.TenantId = NEW.TenantId AND next.SessionId = child.SessionId AND next.AgentInstanceId = NEW.InstanceId AND next.ExecutorPrincipalId = NEW.ExecutorPrincipalId
                        AND next.RootActivationId = initial.RootActivationId AND json_extract(next.PayloadJson, '$.admission.activation.kind') = 9
                        AND json_extract(next.PayloadJson, '$.admission.outputContract') = 1
                        AND json_extract(next.PayloadJson, '$.admission.approvalResume.sourceRunId') IS previous.AgentRunId
                        AND json_extract(next.PayloadJson, '$.admission.executionContext.parentRunId') IS previous.AgentRunId
                        AND json_extract(next.PayloadJson, '$.admission.activation.sourceAgentRunId') IS previous.AgentRunId
                    ) SELECT 1 FROM lineage WHERE id = source.AgentRunId));
            SELECT RAISE(ABORT, 'Completion metadata must match the exact source') WHERE
                (SELECT count(*) FROM json_each(NEW.PayloadJson, '$.Operation.CompletionRead')) <> 9
                OR EXISTS (SELECT 1 FROM json_each(json_extract(NEW.PayloadJson, '$.Operation.CompletionRead.ArtifactsJson')) file
                    WHERE json_type(file.value) IS NOT 'object' OR (SELECT count(*) FROM json_each(file.value)) <> 6
                    OR json_extract(file.value, '$.SessionId') IS NOT json_extract(NEW.PayloadJson, '$.Operation.CompletionRead.ChildSessionId')
                    OR NOT EXISTS (SELECT 1 FROM Artifacts a JOIN Sessions saved ON saved.SessionId = a.SessionId
                        WHERE a.TenantId = NEW.TenantId AND a.SessionId = json_extract(file.value, '$.SessionId') AND saved.DurablyDeletedAtUtc IS NULL
                        AND a.AgentRunId = json_extract(NEW.PayloadJson, '$.Operation.CompletionRead.SourceRunId')
                        AND a.ArtifactId = json_extract(file.value, '$.ArtifactId') AND a.DisplayName = json_extract(file.value, '$.DisplayName')
                        AND a.ContentType = json_extract(file.value, '$.ContentType') AND a.ByteSize = json_extract(file.value, '$.ByteSize') AND a.Sha256Hex = json_extract(file.value, '$.Sha256Hex')))
                OR (SELECT count(DISTINCT json_extract(value, '$.ArtifactId')) FROM json_each(json_extract(NEW.PayloadJson, '$.Operation.CompletionRead.ArtifactsJson')))
                    <> json_array_length(json_extract(NEW.PayloadJson, '$.Operation.CompletionRead.ArtifactsJson'));
        END;
        CREATE TRIGGER TR_TenantApprovals_CompletionRead_Update BEFORE UPDATE ON TenantApprovalRequests
        WHEN json_extract(OLD.PayloadJson, '$.Operation.CompletionRead') IS NOT NULL OR json_extract(NEW.PayloadJson, '$.Operation.CompletionRead') IS NOT NULL
        BEGIN
            SELECT RAISE(ABORT, 'Completion read evidence is immutable') WHERE
                json_extract(OLD.PayloadJson, '$.Operation') IS NOT json_extract(NEW.PayloadJson, '$.Operation')
                OR OLD.RequestId IS NOT NEW.RequestId OR OLD.TenantId IS NOT NEW.TenantId OR OLD.SessionId IS NOT NEW.SessionId OR OLD.RunId IS NOT NEW.RunId
                OR OLD.RootActivationId IS NOT NEW.RootActivationId OR OLD.InstanceId IS NOT NEW.InstanceId OR OLD.ExecutorPrincipalId IS NOT NEW.ExecutorPrincipalId
                OR OLD.OriginalInitiatorPrincipalId IS NOT NEW.OriginalInitiatorPrincipalId OR OLD.OperationId IS NOT NEW.OperationId
                OR OLD.Fingerprint IS NOT NEW.Fingerprint OR OLD.Permission IS NOT NEW.Permission OR OLD.ResourceType IS NOT NEW.ResourceType
                OR OLD.ResourceId IS NOT NEW.ResourceId OR OLD.Kind IS NOT NEW.Kind OR OLD.CreatedAt IS NOT NEW.CreatedAt OR OLD.ExpiresAt IS NOT NEW.ExpiresAt;
        END;
        CREATE TRIGGER TR_TenantApprovals_ReportSourceRead_Insert BEFORE INSERT ON TenantApprovalRequests
        WHEN json_type(NEW.PayloadJson, '$.Operation.ReportSourceRead') IS 'object'
        BEGIN
                SELECT RAISE(ABORT, 'Report source read requires exact ordinary Review') WHERE NOT COALESCE((
                NEW.Kind = 0 AND NEW.Status IN (0,1,2,3,4) AND NEW.Revision >= 1 AND NEW.ExpiresAt > NEW.CreatedAt AND length(NEW.Fingerprint) = 64
                AND NEW.Permission = 'agent-runs.read' AND NEW.ResourceType = 'agent-run' AND NEW.ResourceId IS NEW.RunId
                AND NEW.OriginalInitiatorPrincipalId IS NOT NULL AND NEW.CredentialId IS NULL AND NEW.SandboxProfileId IS NULL AND NEW.SandboxProfileRevision IS NULL
                AND json_type(NEW.PayloadJson, '$.Operation.ReportSourceRead') IS 'object'
                AND json_extract(NEW.PayloadJson, '$.Operation.ReportSourceRead.ChildSessionId') IS NEW.SessionId
                AND json_extract(NEW.PayloadJson, '$.Operation.ReportSourceRead.SourceRunId') IS NEW.RunId
                AND json_extract(NEW.PayloadJson, '$.Operation.ReportSourceRead.ParentSessionId') IS NOT NEW.SessionId
                AND json_type(NEW.PayloadJson, '$.Operation.ReportSourceRead.SourceRevision') IS 'integer' AND json_extract(NEW.PayloadJson, '$.Operation.ReportSourceRead.SourceRevision') >= 1
                AND json_type(NEW.PayloadJson, '$.Operation.ReportSourceRead.Objective') IS 'text' AND length(CAST(json_extract(NEW.PayloadJson, '$.Operation.ReportSourceRead.Objective') AS BLOB)) BETWEEN 1 AND 4000
                AND json_type(NEW.PayloadJson, '$.Operation.ReportSourceRead.ObservationJson') IS 'text' AND length(CAST(json_extract(NEW.PayloadJson, '$.Operation.ReportSourceRead.ObservationJson') AS BLOB)) BETWEEN 2 AND 8192
                AND json_valid(json_extract(NEW.PayloadJson, '$.Operation.ReportSourceRead.ObservationJson'))
                AND json_extract(json_extract(NEW.PayloadJson, '$.Operation.ReportSourceRead.ObservationJson'), '$.TriggerKind') = 7
                AND json_extract(json_extract(NEW.PayloadJson, '$.Operation.ReportSourceRead.ObservationJson'), '$.InputBoundarySequence') IS NULL
                AND json_type(json_extract(NEW.PayloadJson, '$.Operation.ReportSourceRead.ObservationJson'), '$.Text') IS 'text'
                AND json_valid(json_extract(json_extract(NEW.PayloadJson, '$.Operation.ReportSourceRead.ObservationJson'), '$.Text'))
                AND json_extract(json_extract(json_extract(NEW.PayloadJson, '$.Operation.ReportSourceRead.ObservationJson'), '$.Text'), '$.childSessionId') IS NEW.SessionId
                AND json_extract(json_extract(json_extract(NEW.PayloadJson, '$.Operation.ReportSourceRead.ObservationJson'), '$.Text'), '$.initialAgentRunId') IS NEW.RunId
                AND json_extract(json_extract(json_extract(NEW.PayloadJson, '$.Operation.ReportSourceRead.ObservationJson'), '$.Text'), '$.status') IN ('Completed','Failed','Cancelled')
                AND json_array_length(json_extract(json_extract(json_extract(NEW.PayloadJson, '$.Operation.ReportSourceRead.ObservationJson'), '$.Text'), '$.artifacts')) = 0
                AND json_type(NEW.PayloadJson, '$.Operation.ReportSourceRead.ObservationHash') IS 'text' AND length(json_extract(NEW.PayloadJson, '$.Operation.ReportSourceRead.ObservationHash')) = 64
                AND json_extract(NEW.PayloadJson, '$.Operation.ReportSourceRead.ObservationHash') NOT GLOB '*[^0-9a-f]*'
                AND length(CAST(NEW.PayloadJson AS BLOB)) <= 131072
                AND json_type(NEW.PayloadJson, '$.Operation.ReportSourceRead.ParentSessionId') IS 'text' AND length(json_extract(NEW.PayloadJson, '$.Operation.ReportSourceRead.ParentSessionId')) = 36 AND json_extract(NEW.PayloadJson, '$.Operation.ReportSourceRead.ParentSessionId') NOT GLOB '*[^0-9a-f-]*' AND json_extract(NEW.PayloadJson, '$.Operation.ReportSourceRead.ParentSessionId') IS NOT '00000000-0000-0000-0000-000000000000'
                AND json_type(NEW.PayloadJson, '$.Operation.ReportSourceRead.ChildSessionId') IS 'text' AND length(json_extract(NEW.PayloadJson, '$.Operation.ReportSourceRead.ChildSessionId')) = 36 AND json_extract(NEW.PayloadJson, '$.Operation.ReportSourceRead.ChildSessionId') NOT GLOB '*[^0-9a-f-]*' AND json_extract(NEW.PayloadJson, '$.Operation.ReportSourceRead.ChildSessionId') IS NOT '00000000-0000-0000-0000-000000000000'
                AND json_type(NEW.PayloadJson, '$.Operation.ReportSourceRead.InitialRunId') IS 'text' AND length(json_extract(NEW.PayloadJson, '$.Operation.ReportSourceRead.InitialRunId')) = 36 AND json_extract(NEW.PayloadJson, '$.Operation.ReportSourceRead.InitialRunId') NOT GLOB '*[^0-9a-f-]*' AND json_extract(NEW.PayloadJson, '$.Operation.ReportSourceRead.InitialRunId') IS NOT '00000000-0000-0000-0000-000000000000'
                AND json_type(NEW.PayloadJson, '$.Operation.ReportSourceRead.SourceRunId') IS 'text' AND length(json_extract(NEW.PayloadJson, '$.Operation.ReportSourceRead.SourceRunId')) = 36 AND json_extract(NEW.PayloadJson, '$.Operation.ReportSourceRead.SourceRunId') NOT GLOB '*[^0-9a-f-]*' AND json_extract(NEW.PayloadJson, '$.Operation.ReportSourceRead.SourceRunId') IS NOT '00000000-0000-0000-0000-000000000000'
                AND json_extract(NEW.PayloadJson, '$.Operation.Persona') IS NULL
                AND json_extract(NEW.PayloadJson, '$.Operation.Preferences') IS NULL
                AND json_extract(NEW.PayloadJson, '$.Operation.CredentialCheck') IS NULL
                AND json_extract(NEW.PayloadJson, '$.Operation.SandboxCommand') IS NULL
                AND json_extract(NEW.PayloadJson, '$.Operation.SandboxControl') IS NULL
                AND json_extract(NEW.PayloadJson, '$.Operation.SandboxRead') IS NULL
                AND json_extract(NEW.PayloadJson, '$.Operation.BrowserCommand') IS NULL
                AND json_extract(NEW.PayloadJson, '$.Operation.CredentialRead') IS NULL
                AND json_extract(NEW.PayloadJson, '$.Operation.ArtifactPublication') IS NULL
                AND json_extract(NEW.PayloadJson, '$.Operation.ArtifactVerification') IS NULL
                AND json_extract(NEW.PayloadJson, '$.Operation.WorkspaceRead') IS NULL
                AND json_extract(NEW.PayloadJson, '$.Operation.WorkspaceMutation') IS NULL
                AND json_extract(NEW.PayloadJson, '$.Operation.AttachmentRead') IS NULL
                AND json_extract(NEW.PayloadJson, '$.Operation.BackgroundStart') IS NULL
                AND json_extract(NEW.PayloadJson, '$.Operation.BackgroundReport') IS NULL
                AND json_extract(NEW.PayloadJson, '$.Operation.CompletionRead') IS NULL),0);
            SELECT RAISE(ABORT, 'Completion read source must be an exact owned terminal child') WHERE NOT EXISTS (
                SELECT 1 FROM TenantSessions child JOIN Sessions saved ON saved.SessionId = child.SessionId
                JOIN AgentRuns initial ON initial.AgentRunId = json_extract(NEW.PayloadJson, '$.Operation.ReportSourceRead.InitialRunId')
                JOIN AgentRuns source ON source.AgentRunId = json_extract(NEW.PayloadJson, '$.Operation.ReportSourceRead.SourceRunId')
                WHERE child.TenantId = NEW.TenantId AND child.AgentInstanceId = NEW.InstanceId AND child.ExecutorPrincipalId = NEW.ExecutorPrincipalId
                AND child.SessionId = json_extract(NEW.PayloadJson, '$.Operation.ReportSourceRead.ChildSessionId') AND saved.DurablyDeletedAtUtc IS NULL
                AND json_extract(saved.OriginJson, '$.originatingSessionId') IS json_extract(NEW.PayloadJson, '$.Operation.ReportSourceRead.ParentSessionId')
                AND json_extract(saved.OriginJson, '$.initialBackgroundAgentRunId') IS initial.AgentRunId
                AND initial.TenantId = NEW.TenantId AND initial.SessionId = child.SessionId AND initial.AgentInstanceId = NEW.InstanceId AND initial.ExecutorPrincipalId = NEW.ExecutorPrincipalId
                AND json_extract(initial.PayloadJson, '$.admission.activation.kind') = 2 AND json_extract(initial.PayloadJson, '$.admission.outputContract') = 1
                AND source.TenantId = NEW.TenantId AND source.SessionId = child.SessionId AND source.AgentInstanceId = NEW.InstanceId AND source.ExecutorPrincipalId = NEW.ExecutorPrincipalId
                AND source.AgentRunId IS NEW.RunId AND source.SessionId IS NEW.SessionId AND source.RootActivationId IS NEW.RootActivationId
                AND source.RootActivationId = initial.RootActivationId AND source.Status IN (4,5,6)
                AND source.Revision = json_extract(NEW.PayloadJson, '$.Operation.ReportSourceRead.SourceRevision')
                AND json_extract(source.PayloadJson, '$.admission.outputContract') = 1
                AND (json_extract(source.PayloadJson, '$.result.outcomeKind') IS NULL OR json_extract(source.PayloadJson, '$.result.outcomeKind') <> 3)
                AND EXISTS (SELECT 1 FROM TenantSessions parent JOIN Sessions ps ON ps.SessionId = parent.SessionId WHERE parent.TenantId = NEW.TenantId
                    AND parent.AgentInstanceId = NEW.InstanceId AND parent.ExecutorPrincipalId = NEW.ExecutorPrincipalId AND ps.DurablyDeletedAtUtc IS NULL
                    AND parent.SessionId = json_extract(NEW.PayloadJson, '$.Operation.ReportSourceRead.ParentSessionId'))
                AND EXISTS (
                    WITH RECURSIVE lineage(id, depth, seen) AS (
                        SELECT initial.AgentRunId, 0, ',' || initial.AgentRunId || ','
                        UNION ALL
                        SELECT next.AgentRunId, chain.depth + 1, chain.seen || next.AgentRunId || ',' FROM lineage chain
                        JOIN AgentRuns previous ON previous.AgentRunId = chain.id
                        JOIN AgentRuns next ON next.AgentRunId = json_extract(previous.PayloadJson, '$.result.continuationRunId')
                        WHERE chain.depth < 63 AND previous.Status = 4 AND json_extract(previous.PayloadJson, '$.result.outcomeKind') = 3
                        AND instr(chain.seen, ',' || next.AgentRunId || ',') = 0
                        AND next.TenantId = NEW.TenantId AND next.SessionId = child.SessionId AND next.AgentInstanceId = NEW.InstanceId AND next.ExecutorPrincipalId = NEW.ExecutorPrincipalId
                        AND next.RootActivationId = initial.RootActivationId AND json_extract(next.PayloadJson, '$.admission.activation.kind') = 9
                        AND json_extract(next.PayloadJson, '$.admission.outputContract') = 1
                        AND json_extract(next.PayloadJson, '$.admission.approvalResume.sourceRunId') IS previous.AgentRunId
                        AND json_extract(next.PayloadJson, '$.admission.executionContext.parentRunId') IS previous.AgentRunId
                        AND json_extract(next.PayloadJson, '$.admission.activation.sourceAgentRunId') IS previous.AgentRunId
                    ) SELECT 1 FROM lineage WHERE id = source.AgentRunId));
            SELECT RAISE(ABORT, 'Report source read has an invalid shape') WHERE (SELECT count(*) FROM json_each(NEW.PayloadJson, '$.Operation.ReportSourceRead')) <> 8;
        END;
        CREATE TRIGGER TR_TenantApprovals_ReportSourceRead_Update BEFORE UPDATE ON TenantApprovalRequests
        WHEN json_extract(OLD.PayloadJson, '$.Operation.ReportSourceRead') IS NOT NULL OR json_extract(NEW.PayloadJson, '$.Operation.ReportSourceRead') IS NOT NULL
        BEGIN
            SELECT RAISE(ABORT, 'Report source read evidence is immutable') WHERE
                json_extract(OLD.PayloadJson, '$.Operation') IS NOT json_extract(NEW.PayloadJson, '$.Operation')
                OR OLD.RequestId IS NOT NEW.RequestId OR OLD.TenantId IS NOT NEW.TenantId OR OLD.SessionId IS NOT NEW.SessionId OR OLD.RunId IS NOT NEW.RunId
                OR OLD.RootActivationId IS NOT NEW.RootActivationId OR OLD.InstanceId IS NOT NEW.InstanceId OR OLD.ExecutorPrincipalId IS NOT NEW.ExecutorPrincipalId
                OR OLD.OriginalInitiatorPrincipalId IS NOT NEW.OriginalInitiatorPrincipalId OR OLD.OperationId IS NOT NEW.OperationId
                OR OLD.Fingerprint IS NOT NEW.Fingerprint OR OLD.Permission IS NOT NEW.Permission OR OLD.ResourceType IS NOT NEW.ResourceType
                OR OLD.ResourceId IS NOT NEW.ResourceId OR OLD.Kind IS NOT NEW.Kind OR OLD.CreatedAt IS NOT NEW.CreatedAt OR OLD.ExpiresAt IS NOT NEW.ExpiresAt;
        END;
        CREATE TRIGGER TR_TenantReceipts_BrowserArtifacts_Insert BEFORE INSERT ON TenantOperationReceipts
        WHEN NEW.BrowserArtifactsJson IS NOT NULL
        BEGIN
        SELECT RAISE(ABORT, 'Browser Artifact receipt requires actual owned publication') WHERE NOT COALESCE((
        NEW.RequestId IS NOT NULL AND length(CAST(NEW.BrowserArtifactsJson AS BLOB)) <= 4096
        AND json_valid(NEW.BrowserArtifactsJson) AND json_type(NEW.BrowserArtifactsJson) IS 'object'
        AND (SELECT count(*) FROM json_each(NEW.BrowserArtifactsJson)) = 3
        AND (SELECT count(DISTINCT key) FROM json_each(NEW.BrowserArtifactsJson) WHERE key IN ('status','reasonCode','files')) = 3
        AND json_extract(NEW.BrowserArtifactsJson, '$.status') IN ('published','empty','unavailable')
        AND (json_extract(NEW.BrowserArtifactsJson, '$.reasonCode') IS NULL OR json_extract(NEW.BrowserArtifactsJson, '$.reasonCode') IN
        ('browser_command_not_completed','artifact_publication_unavailable','permission_denied','artifact_quota_exceeded','capture_limit','download_limit','capture_invalid','download_invalid'))
        AND json_type(NEW.BrowserArtifactsJson, '$.files') IS 'array'
        AND json_array_length(NEW.BrowserArtifactsJson, '$.files') <= 3
        AND ((json_extract(NEW.BrowserArtifactsJson, '$.status') = 'published' AND NEW.Outcome = 0
        AND json_array_length(NEW.BrowserArtifactsJson, '$.files') > 0)
        OR (json_extract(NEW.BrowserArtifactsJson, '$.status') = 'empty' AND NEW.Outcome = 0
        AND json_array_length(NEW.BrowserArtifactsJson, '$.files') = 0 AND json_extract(NEW.BrowserArtifactsJson, '$.reasonCode') IS NULL)
        OR (json_extract(NEW.BrowserArtifactsJson, '$.status') = 'unavailable'
        AND json_array_length(NEW.BrowserArtifactsJson, '$.files') = 0 AND json_extract(NEW.BrowserArtifactsJson, '$.reasonCode') IS NOT NULL))
        AND EXISTS (SELECT 1 FROM TenantApprovalRequests request WHERE request.RequestId = NEW.RequestId
        AND json_type(request.PayloadJson, '$.Operation.BrowserCommand.Artifacts') IS 'object')
        AND (SELECT count(*) FROM json_each(NEW.BrowserArtifactsJson, '$.files') WHERE json_extract(value, '$.kind') = 'capture') <= 1
        AND (SELECT count(*) FROM json_each(NEW.BrowserArtifactsJson, '$.files') WHERE json_extract(value, '$.kind') = 'download') <= 2
        AND (SELECT count(DISTINCT json_extract(value, '$.artifactId')) FROM json_each(NEW.BrowserArtifactsJson, '$.files')) = json_array_length(NEW.BrowserArtifactsJson, '$.files')
        AND NOT EXISTS (SELECT 1 FROM json_each(NEW.BrowserArtifactsJson, '$.files') file
        WHERE json_type(file.value) IS NOT 'object' OR (SELECT count(*) FROM json_each(file.value)) <> 2
        OR (SELECT count(DISTINCT key) FROM json_each(file.value) WHERE key IN ('kind','artifactId')) <> 2
        OR json_type(file.value, '$.artifactId') IS NOT 'text' OR json_extract(file.value, '$.kind') NOT IN ('capture','download')
        OR NOT EXISTS (SELECT 1 FROM Artifacts artifact JOIN TenantApprovalRequests request ON request.RequestId = NEW.RequestId
        WHERE artifact.TenantId = NEW.TenantId AND artifact.SessionId = NEW.SessionId AND artifact.AgentRunId = NEW.RunId
        AND artifact.ArtifactId = json_extract(file.value, '$.artifactId')
        AND ((json_extract(file.value, '$.kind') = 'capture'
        AND json_extract(request.PayloadJson, '$.Operation.BrowserCommand.Artifacts.Capture') = 1
        AND artifact.ArtifactId = lower(NEW.OperationId) AND artifact.ContentType IN ('image/png','image/jpeg','image/webp')
        AND artifact.ByteSize BETWEEN 1 AND json_extract(request.PayloadJson, '$.Operation.BrowserCommand.Artifacts.CaptureBytes'))
        OR (json_extract(file.value, '$.kind') = 'download'
        AND json_extract(request.PayloadJson, '$.Operation.BrowserCommand.Artifacts.Downloads') = 1
        AND artifact.ByteSize BETWEEN 0 AND json_extract(request.PayloadJson, '$.Operation.BrowserCommand.Artifacts.DownloadBytes')))))
        ),0);
        END;
        CREATE TRIGGER TR_TenantReceipts_BrowserArtifacts_Update BEFORE UPDATE ON TenantOperationReceipts
        WHEN OLD.BrowserArtifactsJson IS NOT NULL OR NEW.BrowserArtifactsJson IS NOT NULL
        BEGIN SELECT RAISE(ABORT, 'Browser Artifact receipts are immutable'); END;
        CREATE TRIGGER TR_TenantAutomations_Owner_Update BEFORE UPDATE ON Automations
        WHEN (OLD.TenantId IS NOT NULL OR NEW.TenantId IS NOT NULL)
          AND (OLD.TenantId IS NOT NEW.TenantId OR OLD.ExecutorPrincipalId IS NOT NEW.ExecutorPrincipalId
            OR OLD.AgentInstanceId IS NOT NEW.AgentInstanceId OR OLD.ProfileId IS NOT NEW.ProfileId OR OLD.AutomationId IS NOT NEW.AutomationId)
        BEGIN SELECT RAISE(ABORT, 'tenant Automation owner is immutable'); END;
        CREATE TRIGGER TR_TenantOccurrences_Source_Insert BEFORE INSERT ON TriggerOccurrences
        WHEN NEW.TenantId IS NOT NULL
        BEGIN
            SELECT RAISE(ABORT, 'tenant occurrence requires its exact canonical admission') WHERE NOT EXISTS (
                SELECT 1 FROM Automations automation JOIN AgentRuns run ON run.AgentRunId = NEW.AcceptedAgentRunId
                JOIN TenantSessions session ON session.SessionId = NEW.ExecutionSessionId
                JOIN Activations activation ON activation.ActivationId = run.ActivationId
                WHERE automation.TenantId = NEW.TenantId AND automation.AutomationId = NEW.AutomationId
                AND automation.AgentInstanceId = NEW.AgentInstanceId AND automation.ExecutorPrincipalId = NEW.ExecutorPrincipalId
                AND run.TenantId = NEW.TenantId AND run.AgentInstanceId = NEW.AgentInstanceId AND run.ExecutorPrincipalId = NEW.ExecutorPrincipalId
                AND run.SessionId = session.SessionId AND run.RootActivationId = run.ActivationId AND (NEW.SourceKind = 2 OR run.OriginalInitiatorPrincipalId IS NULL) AND run.ProfileId IS NULL
                AND session.TenantId = NEW.TenantId AND session.AgentInstanceId = NEW.AgentInstanceId AND session.ExecutorPrincipalId = NEW.ExecutorPrincipalId
                AND session.CreatedByPrincipalId = NEW.ExecutorPrincipalId AND json_extract(activation.PayloadJson, '$.triggerOccurrenceId') = NEW.OccurrenceId
                AND activation.RootActivationId = activation.ActivationId AND activation.DedupeKey = NEW.DedupeKey
                AND NEW.ProfileId IS NULL AND NEW.SourceKind IN (0,2) AND NEW.Disposition = 6
                AND json_extract(run.PayloadJson, '$.admission.outputContract') = 1
                AND json_extract(run.PayloadJson, '$.admission.executionContext.origin') = CASE NEW.SourceKind WHEN 0 THEN 4 ELSE 7 END);
        END;
        CREATE TRIGGER TR_TenantOccurrences_Source_Update BEFORE UPDATE ON TriggerOccurrences
        WHEN (OLD.TenantId IS NOT NULL OR NEW.TenantId IS NOT NULL)
          AND (OLD.TenantId IS NOT NEW.TenantId OR OLD.ExecutorPrincipalId IS NOT NEW.ExecutorPrincipalId OR OLD.AgentInstanceId IS NOT NEW.AgentInstanceId
            OR OLD.ProfileId IS NOT NEW.ProfileId OR OLD.OccurrenceId IS NOT NEW.OccurrenceId OR OLD.AutomationId IS NOT NEW.AutomationId
            OR OLD.DedupeKey IS NOT NEW.DedupeKey OR OLD.EvidenceJson IS NOT NEW.EvidenceJson OR OLD.SourceKind IS NOT NEW.SourceKind
            OR OLD.TriggerRevision IS NOT NEW.TriggerRevision OR OLD.ScheduledAtUtc IS NOT NEW.ScheduledAtUtc OR OLD.ObservedAtUtc IS NOT NEW.ObservedAtUtc
            OR OLD.AdmittedAtUtc IS NOT NEW.AdmittedAtUtc OR OLD.ExecutionSessionId IS NOT NEW.ExecutionSessionId OR OLD.AcceptedAgentRunId IS NOT NEW.AcceptedAgentRunId
            OR OLD.SourceEventId IS NOT NEW.SourceEventId OR OLD.ModelCatalogKey IS NOT NEW.ModelCatalogKey OR OLD.ModelProviderAlias IS NOT NEW.ModelProviderAlias
            OR OLD.ModelId IS NOT NEW.ModelId OR OLD.ModelReasoningEffort IS NOT NEW.ModelReasoningEffort
            OR OLD.TriggerId IS NOT NEW.TriggerId OR OLD.ModelSource IS NOT NEW.ModelSource OR OLD.ExecutionTargetKind IS NOT NEW.ExecutionTargetKind
            OR OLD.TargetSessionId IS NOT NEW.TargetSessionId OR OLD.ReportToSessionId IS NOT NEW.ReportToSessionId
            OR OLD.Disposition IS NOT NEW.Disposition OR OLD.DispositionReason IS NOT NEW.DispositionReason
            OR OLD.RoutingRevision IS NOT NEW.RoutingRevision OR OLD.RoutingUpdatedAtUtc IS NOT NEW.RoutingUpdatedAtUtc
            OR OLD.ClaimId IS NOT NEW.ClaimId OR OLD.ClaimLeaseExpiresAtUtc IS NOT NEW.ClaimLeaseExpiresAtUtc
            OR OLD.LiveSessionId IS NOT NEW.LiveSessionId OR OLD.LiveEvaluationCompletedAtUtc IS NOT NEW.LiveEvaluationCompletedAtUtc)
        BEGIN SELECT RAISE(ABORT, 'tenant occurrence source is immutable'); END;
        CREATE TRIGGER TR_TenantSessions_Creator_Insert BEFORE INSERT ON TenantSessions
        WHEN NOT EXISTS (SELECT 1 FROM Principals p WHERE p.PrincipalId = NEW.CreatedByPrincipalId AND p.TenantId = NEW.TenantId
            AND (p.Kind = 0 OR p.Kind = 1 AND p.PrincipalId = NEW.ExecutorPrincipalId))
        BEGIN SELECT RAISE(ABORT, 'same tenant Session creator required'); END;
        CREATE TRIGGER TR_TenantSessions_Owner_Update BEFORE UPDATE ON TenantSessions
        WHEN OLD.TenantId IS NOT NEW.TenantId OR OLD.SessionId IS NOT NEW.SessionId OR OLD.AgentInstanceId IS NOT NEW.AgentInstanceId
            OR OLD.ExecutorPrincipalId IS NOT NEW.ExecutorPrincipalId OR OLD.CreatedByPrincipalId IS NOT NEW.CreatedByPrincipalId OR OLD.CreatedAt IS NOT NEW.CreatedAt
        BEGIN SELECT RAISE(ABORT, 'tenant Session owner is immutable'); END;
        CREATE TRIGGER TR_Attachments_Tenant_Insert BEFORE INSERT ON Attachments
        WHEN (NEW.TenantId IS NULL AND EXISTS (SELECT 1 FROM TenantSessions WHERE SessionId = NEW.SessionId))
         OR (NEW.TenantId IS NOT NULL AND (NOT EXISTS (SELECT 1 FROM TenantSessions WHERE TenantId = NEW.TenantId AND SessionId = NEW.SessionId)
         OR NOT EXISTS (SELECT 1 FROM Principals WHERE PrincipalId = NEW.UploadedByPrincipalId AND TenantId = NEW.TenantId AND Kind = 0)
         OR (NEW.State = 'Bound' AND NOT EXISTS (SELECT 1 FROM ConversationEntries WHERE EntryId = NEW.EntryId AND SessionId = NEW.SessionId))))
        BEGIN SELECT RAISE(ABORT, 'Attachment ownership mismatch'); END;
        CREATE TRIGGER TR_Attachments_Tenant_Update BEFORE UPDATE ON Attachments
        WHEN OLD.TenantId IS NOT NEW.TenantId
         OR (NEW.TenantId IS NULL AND EXISTS (SELECT 1 FROM TenantSessions WHERE SessionId = NEW.SessionId))
         OR (OLD.TenantId IS NOT NULL AND (OLD.SessionId IS NOT NEW.SessionId OR OLD.AttachmentId IS NOT NEW.AttachmentId
         OR OLD.UploadRequestId IS NOT NEW.UploadRequestId OR OLD.UploadedByPrincipalId IS NOT NEW.UploadedByPrincipalId
         OR OLD.BlobKey IS NOT NEW.BlobKey OR OLD.DisplayName IS NOT NEW.DisplayName OR OLD.ContentType IS NOT NEW.ContentType
         OR OLD.ByteSize IS NOT NEW.ByteSize OR OLD.Sha256Hex IS NOT NEW.Sha256Hex OR OLD.Readable IS NOT NEW.Readable OR OLD.CreatedAtUtc IS NOT NEW.CreatedAtUtc
         OR (OLD.State = 'Bound' AND (NEW.State IS NOT 'Bound' OR OLD.EntryId IS NOT NEW.EntryId OR OLD.BoundAtUtc IS NOT NEW.BoundAtUtc))
         OR (OLD.State = 'Pending' AND NEW.State = 'Pending' AND NEW.ExpiresAtUtc > OLD.ExpiresAtUtc)
         OR NOT EXISTS (SELECT 1 FROM TenantSessions WHERE TenantId = NEW.TenantId AND SessionId = NEW.SessionId)
         OR NOT EXISTS (SELECT 1 FROM Principals WHERE PrincipalId = NEW.UploadedByPrincipalId AND TenantId = NEW.TenantId AND Kind = 0)
         OR (NEW.State = 'Bound' AND NOT EXISTS (SELECT 1 FROM ConversationEntries WHERE EntryId = NEW.EntryId AND SessionId = NEW.SessionId))))
        BEGIN SELECT RAISE(ABORT, 'Attachment ownership or immutable source mismatch'); END;
        """;
    private const string DowngradeGuardSql = """
        CREATE TEMP TABLE TenantAutomationDowngradeGuard (Rows INTEGER CHECK(Rows=0));
        INSERT INTO TenantAutomationDowngradeGuard SELECT
            (SELECT count(*) FROM Automations WHERE TenantId IS NOT NULL)
            + (SELECT count(*) FROM TriggerOccurrences WHERE TenantId IS NOT NULL)
            + (SELECT count(*) FROM TenantSessions s JOIN Principals p ON p.PrincipalId = s.CreatedByPrincipalId WHERE p.Kind = 1);
        DROP TABLE TenantAutomationDowngradeGuard;
        DROP TRIGGER TR_TenantAutomations_Owner_Update;
        DROP TRIGGER TR_TenantOccurrences_Source_Insert;
        DROP TRIGGER TR_TenantOccurrences_Source_Update;
        DROP TRIGGER TR_TenantSessions_Creator_Insert;
        DROP TRIGGER TR_TenantSessions_Owner_Update;
        """;
    private const string DowngradeSql = """
        DROP TRIGGER "TR_Attachments_Tenant_Insert";
        DROP TRIGGER "TR_Attachments_Tenant_Update";
        DROP TRIGGER "TR_TenantApprovals_AccountAction_Insert";
        DROP TRIGGER "TR_TenantApprovals_AccountAction_Update";
        DROP TRIGGER "TR_TenantApprovals_AccountOperationRead_Insert";
        DROP TRIGGER "TR_TenantApprovals_AccountOperationRead_Update";
        DROP TRIGGER "TR_TenantApprovals_AccountRead_Insert";
        DROP TRIGGER "TR_TenantApprovals_AccountRead_Update";
        DROP TRIGGER "TR_TenantApprovals_BackgroundCreation_Update";
        DROP TRIGGER "TR_TenantApprovals_BackgroundReport_Insert";
        DROP TRIGGER "TR_TenantApprovals_BackgroundReport_Update";
        DROP TRIGGER "TR_TenantApprovals_BrowserArtifacts_Insert";
        DROP TRIGGER "TR_TenantApprovals_BrowserArtifacts_Update";
        DROP TRIGGER "TR_TenantApprovals_BrowserFiles_Insert";
        DROP TRIGGER "TR_TenantApprovals_BrowserFiles_Update";
        DROP TRIGGER "TR_TenantApprovals_CompletionRead_Insert";
        DROP TRIGGER "TR_TenantApprovals_CompletionRead_Update";
        DROP TRIGGER "TR_TenantApprovals_ReportSourceRead_Insert";
        DROP TRIGGER "TR_TenantApprovals_ReportSourceRead_Update";
        DROP TRIGGER "TR_TenantReceipts_BrowserArtifacts_Insert";
        DROP TRIGGER "TR_TenantReceipts_BrowserArtifacts_Update";
        DROP INDEX "IX_TriggerOccurrences_TenantId_AgentInstanceId_DedupeKey";

        DROP INDEX "IX_TriggerOccurrences_TenantId_AgentInstanceId_Disposition";

        DROP INDEX "IX_TriggerOccurrences_TenantId_AgentInstanceId_ExecutorPrincipalId";

        DROP INDEX "IX_TenantSessions_CreatedByPrincipalId";

        DROP INDEX "IX_Automations_TenantId_AgentInstanceId_ExecutorPrincipalId";

        DROP INDEX "IX_Automations_TenantId_AgentInstanceId_Status";

        CREATE INDEX "IX_TenantSessions_TenantId_CreatedByPrincipalId" ON "TenantSessions" ("TenantId", "CreatedByPrincipalId");

        CREATE TABLE "ef_temp_Automations" (
            "AutomationId" TEXT NOT NULL CONSTRAINT "PK_Automations" PRIMARY KEY,
            "AgentInstanceId" TEXT NOT NULL,
            "AuthorizationOrigin" INTEGER NOT NULL,
            "CreatedAtUtc" INTEGER NOT NULL,
            "ExecutionTargetKind" INTEGER NOT NULL,
            "ExpiresAtUtc" INTEGER NULL,
            "Instructions" TEXT NOT NULL,
            "ModelOverrideCatalogKey" TEXT NULL,
            "ModelOverrideReasoningEffort" TEXT NULL,
            "Name" TEXT NOT NULL,
            "NextOccurrenceAtUtc" INTEGER NULL,
            "OccurrenceCount" INTEGER NOT NULL,
            "PresetId" TEXT NULL,
            "PresetVersion" INTEGER NULL,
            "ProfileId" TEXT NOT NULL,
            "ReportToSessionId" TEXT NULL,
            "RequiresTools" INTEGER NOT NULL,
            "RequiresVision" INTEGER NOT NULL,
            "Revision" INTEGER NOT NULL,
            "SourceEventId" TEXT NULL,
            "SourceSessionId" TEXT NULL,
            "Status" INTEGER NOT NULL,
            "SuspensionReason" TEXT NULL,
            "TargetSessionId" TEXT NULL,
            "TriggerMode" INTEGER NOT NULL,
            "UpdatedAtUtc" INTEGER NOT NULL,
            CONSTRAINT "CK_Automations_Destination" CHECK ((ExecutionTargetKind = 0 AND TargetSessionId IS NULL) OR (ExecutionTargetKind = 1 AND TargetSessionId IS NOT NULL AND ReportToSessionId IS NULL)),
            CONSTRAINT "CK_Automations_Mode" CHECK (TriggerMode IN (0,1)),
            CONSTRAINT "CK_Automations_Preset" CHECK ((PresetId IS NULL AND PresetVersion IS NULL) OR (PresetId IS NOT NULL AND PresetVersion >= 1))
        );

        INSERT INTO "ef_temp_Automations" ("AutomationId", "AgentInstanceId", "AuthorizationOrigin", "CreatedAtUtc", "ExecutionTargetKind", "ExpiresAtUtc", "Instructions", "ModelOverrideCatalogKey", "ModelOverrideReasoningEffort", "Name", "NextOccurrenceAtUtc", "OccurrenceCount", "PresetId", "PresetVersion", "ProfileId", "ReportToSessionId", "RequiresTools", "RequiresVision", "Revision", "SourceEventId", "SourceSessionId", "Status", "SuspensionReason", "TargetSessionId", "TriggerMode", "UpdatedAtUtc")
        SELECT "AutomationId", "AgentInstanceId", "AuthorizationOrigin", "CreatedAtUtc", "ExecutionTargetKind", "ExpiresAtUtc", "Instructions", "ModelOverrideCatalogKey", "ModelOverrideReasoningEffort", "Name", "NextOccurrenceAtUtc", "OccurrenceCount", "PresetId", "PresetVersion", IFNULL("ProfileId", ''), "ReportToSessionId", "RequiresTools", "RequiresVision", "Revision", "SourceEventId", "SourceSessionId", "Status", "SuspensionReason", "TargetSessionId", "TriggerMode", "UpdatedAtUtc"
        FROM "Automations";

        CREATE TABLE "ef_temp_TenantSessions" (
            "SessionId" TEXT NOT NULL CONSTRAINT "PK_TenantSessions" PRIMARY KEY,
            "AgentInstanceId" TEXT NOT NULL,
            "CreatedAt" INTEGER NOT NULL,
            "CreatedByPrincipalId" TEXT NOT NULL,
            "ExecutorPrincipalId" TEXT NOT NULL,
            "TenantId" TEXT NOT NULL,
            CONSTRAINT "AK_TenantSessions_TenantId_SessionId" UNIQUE ("TenantId", "SessionId"),
            CONSTRAINT "AK_TenantSessions_TenantId_SessionId_AgentInstanceId_ExecutorPrincipalId" UNIQUE ("TenantId", "SessionId", "AgentInstanceId", "ExecutorPrincipalId"),
            CONSTRAINT "FK_TenantSessions_Sessions_SessionId_AgentInstanceId" FOREIGN KEY ("SessionId", "AgentInstanceId") REFERENCES "Sessions" ("SessionId", "AgentInstanceId") ON DELETE RESTRICT,
            CONSTRAINT "FK_TenantSessions_SystemIdentities_TenantId_AgentInstanceId_ExecutorPrincipalId" FOREIGN KEY ("TenantId", "AgentInstanceId", "ExecutorPrincipalId") REFERENCES "SystemIdentities" ("TenantId", "AgentInstanceId", "PrincipalId") ON DELETE RESTRICT,
            CONSTRAINT "FK_TenantSessions_TenantUsers_TenantId_CreatedByPrincipalId" FOREIGN KEY ("TenantId", "CreatedByPrincipalId") REFERENCES "TenantUsers" ("TenantId", "PrincipalId") ON DELETE RESTRICT
        );

        INSERT INTO "ef_temp_TenantSessions" ("SessionId", "AgentInstanceId", "CreatedAt", "CreatedByPrincipalId", "ExecutorPrincipalId", "TenantId")
        SELECT "SessionId", "AgentInstanceId", "CreatedAt", "CreatedByPrincipalId", "ExecutorPrincipalId", "TenantId"
        FROM "TenantSessions";

        CREATE TABLE "ef_temp_TriggerOccurrences" (
            "OccurrenceId" TEXT NOT NULL CONSTRAINT "PK_TriggerOccurrences" PRIMARY KEY,
            "AcceptedAgentRunId" TEXT NULL,
            "AdmittedAtUtc" INTEGER NOT NULL,
            "AgentInstanceId" TEXT NOT NULL,
            "AutomationId" TEXT NULL,
            "ClaimId" TEXT NULL,
            "ClaimLeaseExpiresAtUtc" INTEGER NULL,
            "DedupeKey" TEXT NOT NULL,
            "Disposition" INTEGER NOT NULL,
            "DispositionReason" TEXT NULL,
            "EvidenceJson" TEXT NOT NULL,
            "ExecutionSessionId" TEXT NULL,
            "ExecutionTargetKind" INTEGER NOT NULL,
            "LiveEvaluationCompletedAtUtc" INTEGER NULL,
            "LiveSessionId" TEXT NULL,
            "ModelCatalogKey" TEXT NULL,
            "ModelId" TEXT NULL,
            "ModelProviderAlias" TEXT NULL,
            "ModelReasoningEffort" TEXT NULL,
            "ModelSource" INTEGER NULL,
            "ObservedAtUtc" INTEGER NOT NULL,
            "ProfileId" TEXT NOT NULL,
            "ReportToSessionId" TEXT NULL,
            "RoutingRevision" INTEGER NOT NULL,
            "RoutingUpdatedAtUtc" INTEGER NULL,
            "ScheduledAtUtc" INTEGER NULL,
            "SourceEventId" TEXT NULL,
            "SourceKind" INTEGER NOT NULL,
            "TargetSessionId" TEXT NULL,
            "TriggerId" TEXT NULL,
            "TriggerRevision" INTEGER NULL,
            CONSTRAINT "CK_TriggerOccurrences_Destination" CHECK ((ExecutionTargetKind = 0 AND TargetSessionId IS NULL) OR (ExecutionTargetKind = 1 AND TargetSessionId IS NOT NULL AND ReportToSessionId IS NULL)),
            CONSTRAINT "CK_TriggerOccurrences_ExecutionLink" CHECK ((ExecutionSessionId IS NULL AND AcceptedAgentRunId IS NULL) OR (ExecutionSessionId IS NOT NULL AND LiveSessionId IS NULL AND AcceptedAgentRunId IS NOT NULL) OR (ExecutionSessionId IS NULL AND LiveSessionId IS NOT NULL AND AcceptedAgentRunId IS NOT NULL AND LiveEvaluationCompletedAtUtc IS NOT NULL)),
            CONSTRAINT "FK_TriggerOccurrences_AgentRuns_AcceptedAgentRunId" FOREIGN KEY ("AcceptedAgentRunId") REFERENCES "AgentRuns" ("AgentRunId") ON DELETE RESTRICT,
            CONSTRAINT "FK_TriggerOccurrences_Sessions_ExecutionSessionId" FOREIGN KEY ("ExecutionSessionId") REFERENCES "Sessions" ("SessionId") ON DELETE RESTRICT,
            CONSTRAINT "FK_TriggerOccurrences_Sessions_LiveSessionId" FOREIGN KEY ("LiveSessionId") REFERENCES "Sessions" ("SessionId") ON DELETE RESTRICT
        );

        INSERT INTO "ef_temp_TriggerOccurrences" ("OccurrenceId", "AcceptedAgentRunId", "AdmittedAtUtc", "AgentInstanceId", "AutomationId", "ClaimId", "ClaimLeaseExpiresAtUtc", "DedupeKey", "Disposition", "DispositionReason", "EvidenceJson", "ExecutionSessionId", "ExecutionTargetKind", "LiveEvaluationCompletedAtUtc", "LiveSessionId", "ModelCatalogKey", "ModelId", "ModelProviderAlias", "ModelReasoningEffort", "ModelSource", "ObservedAtUtc", "ProfileId", "ReportToSessionId", "RoutingRevision", "RoutingUpdatedAtUtc", "ScheduledAtUtc", "SourceEventId", "SourceKind", "TargetSessionId", "TriggerId", "TriggerRevision")
        SELECT "OccurrenceId", "AcceptedAgentRunId", "AdmittedAtUtc", "AgentInstanceId", "AutomationId", "ClaimId", "ClaimLeaseExpiresAtUtc", "DedupeKey", "Disposition", "DispositionReason", "EvidenceJson", "ExecutionSessionId", "ExecutionTargetKind", "LiveEvaluationCompletedAtUtc", "LiveSessionId", "ModelCatalogKey", "ModelId", "ModelProviderAlias", "ModelReasoningEffort", "ModelSource", "ObservedAtUtc", IFNULL("ProfileId", ''), "ReportToSessionId", "RoutingRevision", "RoutingUpdatedAtUtc", "ScheduledAtUtc", "SourceEventId", "SourceKind", "TargetSessionId", "TriggerId", "TriggerRevision"
        FROM "TriggerOccurrences";

        DROP TABLE "Automations";

        ALTER TABLE "ef_temp_Automations" RENAME TO "Automations";

        DROP TABLE "TenantSessions";

        ALTER TABLE "ef_temp_TenantSessions" RENAME TO "TenantSessions";

        DROP TABLE "TriggerOccurrences";

        ALTER TABLE "ef_temp_TriggerOccurrences" RENAME TO "TriggerOccurrences";

        CREATE INDEX "IX_Automations_AgentInstanceId_ProfileId_Status" ON "Automations" ("AgentInstanceId", "ProfileId", "Status");

        CREATE INDEX "IX_Automations_Status_NextOccurrenceAtUtc_AutomationId" ON "Automations" ("Status", "NextOccurrenceAtUtc", "AutomationId");

        CREATE UNIQUE INDEX "IX_TenantSessions_SessionId_AgentInstanceId" ON "TenantSessions" ("SessionId", "AgentInstanceId");

        CREATE INDEX "IX_TenantSessions_TenantId_AgentInstanceId_ExecutorPrincipalId" ON "TenantSessions" ("TenantId", "AgentInstanceId", "ExecutorPrincipalId");

        CREATE INDEX "IX_TenantSessions_TenantId_AgentInstanceId_SessionId" ON "TenantSessions" ("TenantId", "AgentInstanceId", "SessionId");

        CREATE INDEX "IX_TenantSessions_TenantId_CreatedByPrincipalId" ON "TenantSessions" ("TenantId", "CreatedByPrincipalId");

        CREATE UNIQUE INDEX "IX_TriggerOccurrences_AcceptedAgentRunId" ON "TriggerOccurrences" ("AcceptedAgentRunId") WHERE AcceptedAgentRunId IS NOT NULL;

        CREATE UNIQUE INDEX "IX_TriggerOccurrences_AgentInstanceId_ProfileId_DedupeKey" ON "TriggerOccurrences" ("AgentInstanceId", "ProfileId", "DedupeKey");

        CREATE INDEX "IX_TriggerOccurrences_AgentInstanceId_ProfileId_Disposition" ON "TriggerOccurrences" ("AgentInstanceId", "ProfileId", "Disposition");

        CREATE INDEX "IX_TriggerOccurrences_Disposition_ClaimLeaseExpiresAtUtc" ON "TriggerOccurrences" ("Disposition", "ClaimLeaseExpiresAtUtc");

        CREATE INDEX "IX_TriggerOccurrences_ExecutionSessionId" ON "TriggerOccurrences" ("ExecutionSessionId");

        CREATE INDEX "IX_TriggerOccurrences_LiveSessionId" ON "TriggerOccurrences" ("LiveSessionId");
        CREATE TRIGGER TR_TenantApprovals_AccountAction_Insert BEFORE INSERT ON TenantApprovalRequests
        WHEN json_type(NEW.PayloadJson, '$.Operation.AccountAction') IS 'object' AND NOT EXISTS (
            SELECT 1 FROM AgentCredentialBindings b JOIN Credentials c ON c.CredentialId = b.CredentialId
            JOIN TenantCredentials owned ON owned.CredentialId = c.CredentialId AND owned.TenantId = b.TenantId AND owned.DeletedAt IS NULL
            WHERE b.TenantId = NEW.TenantId AND b.AgentInstanceId = NEW.InstanceId AND b.CredentialId = NEW.CredentialId
              AND b.BindingId = json_extract(NEW.PayloadJson, '$.Operation.AccountAction.Credential.BindingId')
              AND b.Reference = json_extract(NEW.PayloadJson, '$.Operation.AccountAction.Credential.Reference')
              AND b.Revision = json_extract(NEW.PayloadJson, '$.Operation.AccountAction.Credential.BindingRevision')
              AND c.Revision = json_extract(NEW.PayloadJson, '$.Operation.AccountAction.Credential.CredentialRevision')
              AND c.SecretRevision = json_extract(NEW.PayloadJson, '$.Operation.AccountAction.Credential.SecretRevision')
              AND c.Purpose = CASE json_extract(NEW.PayloadJson, '$.Operation.AccountAction.Purpose') WHEN 1 THEN 'EmailAccount' WHEN 2 THEN 'GoogleDriveAccount' END)
        BEGIN SELECT RAISE(ABORT, 'exact account action binding required'); END;
        CREATE TRIGGER TR_TenantApprovals_AccountAction_Update BEFORE UPDATE ON TenantApprovalRequests
        WHEN (json_type(OLD.PayloadJson, '$.Operation.AccountAction') IS 'object' OR json_type(NEW.PayloadJson, '$.Operation.AccountAction') IS 'object')
          AND (json_remove(NEW.PayloadJson, '$.Status', '$.Revision') IS NOT json_remove(OLD.PayloadJson, '$.Status', '$.Revision')
            OR NEW.RequestId IS NOT OLD.RequestId OR NEW.TenantId IS NOT OLD.TenantId OR NEW.RunId IS NOT OLD.RunId
            OR NEW.SessionId IS NOT OLD.SessionId OR NEW.InstanceId IS NOT OLD.InstanceId OR NEW.RootActivationId IS NOT OLD.RootActivationId
            OR NEW.ExecutorPrincipalId IS NOT OLD.ExecutorPrincipalId OR NEW.OriginalInitiatorPrincipalId IS NOT OLD.OriginalInitiatorPrincipalId
            OR NEW.OperationId IS NOT OLD.OperationId OR NEW.Fingerprint IS NOT OLD.Fingerprint OR NEW.Permission IS NOT OLD.Permission
            OR NEW.ResourceType IS NOT OLD.ResourceType OR NEW.ResourceId IS NOT OLD.ResourceId OR NEW.CredentialId IS NOT OLD.CredentialId
            OR NEW.Kind IS NOT OLD.Kind OR NEW.CreatedAt IS NOT OLD.CreatedAt OR NEW.ExpiresAt IS NOT OLD.ExpiresAt)
        BEGIN SELECT RAISE(ABORT, 'account action consent is immutable'); END;
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
        CREATE TRIGGER TR_TenantApprovals_AccountRead_Insert BEFORE INSERT ON TenantApprovalRequests
        WHEN json_type(NEW.PayloadJson, '$.Operation.AccountRead') IS 'object' AND NOT EXISTS (
            SELECT 1 FROM AgentCredentialBindings b JOIN Credentials c ON c.CredentialId = b.CredentialId
            JOIN TenantCredentials owned ON owned.CredentialId = c.CredentialId AND owned.TenantId = b.TenantId AND owned.DeletedAt IS NULL
            WHERE b.TenantId = NEW.TenantId AND b.AgentInstanceId = NEW.InstanceId AND b.CredentialId = NEW.CredentialId
              AND b.BindingId = json_extract(NEW.PayloadJson, '$.Operation.AccountRead.Credential.BindingId')
              AND b.Reference = json_extract(NEW.PayloadJson, '$.Operation.AccountRead.Credential.Reference')
              AND b.Revision = json_extract(NEW.PayloadJson, '$.Operation.AccountRead.Credential.BindingRevision')
              AND c.Revision = json_extract(NEW.PayloadJson, '$.Operation.AccountRead.Credential.CredentialRevision')
              AND c.SecretRevision = json_extract(NEW.PayloadJson, '$.Operation.AccountRead.Credential.SecretRevision')
              AND c.Purpose = CASE json_extract(NEW.PayloadJson, '$.Operation.AccountRead.Purpose') WHEN 1 THEN 'EmailAccount' WHEN 2 THEN 'GoogleDriveAccount' END)
        BEGIN SELECT RAISE(ABORT, 'exact account read binding required'); END;
        CREATE TRIGGER TR_TenantApprovals_AccountRead_Update BEFORE UPDATE ON TenantApprovalRequests
        WHEN (json_type(OLD.PayloadJson, '$.Operation.AccountRead') IS 'object' OR json_type(NEW.PayloadJson, '$.Operation.AccountRead') IS 'object')
          AND (json_remove(NEW.PayloadJson, '$.Status', '$.Revision') IS NOT json_remove(OLD.PayloadJson, '$.Status', '$.Revision')
            OR NEW.RequestId IS NOT OLD.RequestId OR NEW.TenantId IS NOT OLD.TenantId OR NEW.RunId IS NOT OLD.RunId
            OR NEW.SessionId IS NOT OLD.SessionId OR NEW.InstanceId IS NOT OLD.InstanceId OR NEW.RootActivationId IS NOT OLD.RootActivationId
            OR NEW.ExecutorPrincipalId IS NOT OLD.ExecutorPrincipalId OR NEW.OriginalInitiatorPrincipalId IS NOT OLD.OriginalInitiatorPrincipalId
            OR NEW.OperationId IS NOT OLD.OperationId OR NEW.Fingerprint IS NOT OLD.Fingerprint OR NEW.Permission IS NOT OLD.Permission
            OR NEW.ResourceType IS NOT OLD.ResourceType OR NEW.ResourceId IS NOT OLD.ResourceId OR NEW.CredentialId IS NOT OLD.CredentialId
            OR NEW.Kind IS NOT OLD.Kind OR NEW.CreatedAt IS NOT OLD.CreatedAt OR NEW.ExpiresAt IS NOT OLD.ExpiresAt)
        BEGIN SELECT RAISE(ABORT, 'account read consent is immutable'); END;
        CREATE TRIGGER TR_TenantApprovals_BackgroundCreation_Update BEFORE UPDATE ON TenantApprovalRequests
        WHEN json_extract(OLD.PayloadJson, '$.Operation.BackgroundStart') IS NOT NULL OR json_extract(NEW.PayloadJson, '$.Operation.BackgroundStart') IS NOT NULL
        BEGIN
            SELECT RAISE(ABORT, 'Exact background creation is immutable') WHERE
                json_extract(NEW.PayloadJson, '$.Operation') IS NOT json_extract(OLD.PayloadJson, '$.Operation')
                OR NEW.RequestId IS NOT OLD.RequestId OR NEW.TenantId IS NOT OLD.TenantId OR NEW.RunId IS NOT OLD.RunId
                OR NEW.SessionId IS NOT OLD.SessionId OR NEW.InstanceId IS NOT OLD.InstanceId OR NEW.RootActivationId IS NOT OLD.RootActivationId
                OR NEW.ExecutorPrincipalId IS NOT OLD.ExecutorPrincipalId OR NEW.OriginalInitiatorPrincipalId IS NOT OLD.OriginalInitiatorPrincipalId
                OR NEW.OperationId IS NOT OLD.OperationId OR NEW.Fingerprint IS NOT OLD.Fingerprint OR NEW.Permission IS NOT OLD.Permission
                OR NEW.ResourceType IS NOT OLD.ResourceType OR NEW.ResourceId IS NOT OLD.ResourceId OR NEW.Kind IS NOT OLD.Kind
                OR NEW.CreatedAt IS NOT OLD.CreatedAt OR NEW.ExpiresAt IS NOT OLD.ExpiresAt;
        END;
        CREATE TRIGGER TR_TenantApprovals_BackgroundReport_Insert BEFORE INSERT ON TenantApprovalRequests
        WHEN json_type(NEW.PayloadJson, '$.Operation.BackgroundReport') IS 'object'
        BEGIN
            SELECT RAISE(ABORT, 'Report approval must reference its exact completed child and owned parent') WHERE NOT EXISTS (
                SELECT 1 FROM AgentRuns child JOIN TenantSessions origin ON origin.TenantId = child.TenantId AND origin.SessionId = child.SessionId
                JOIN TenantSessions parent ON parent.TenantId = origin.TenantId AND parent.AgentInstanceId = origin.AgentInstanceId AND parent.ExecutorPrincipalId = origin.ExecutorPrincipalId
                JOIN Sessions saved ON saved.SessionId = parent.SessionId
                WHERE child.TenantId = NEW.TenantId AND child.AgentRunId = NEW.RunId AND child.SessionId = NEW.SessionId
                AND child.ExecutorPrincipalId = NEW.ExecutorPrincipalId AND child.AgentInstanceId = NEW.InstanceId AND child.RootActivationId = NEW.RootActivationId
                AND child.Status IN (4,5,6) AND child.Revision = json_extract(NEW.PayloadJson, '$.Operation.BackgroundReport.SourceRevision')
                AND parent.SessionId = json_extract(NEW.PayloadJson, '$.Operation.BackgroundReport.ParentSessionId')
                AND saved.Revision = json_extract(NEW.PayloadJson, '$.Operation.BackgroundReport.ParentRevision') AND saved.DurablyDeletedAtUtc IS NULL);
        END;
        CREATE TRIGGER TR_TenantApprovals_BackgroundReport_Update BEFORE UPDATE ON TenantApprovalRequests
        WHEN json_extract(OLD.PayloadJson, '$.Operation.BackgroundReport') IS NOT NULL OR json_extract(NEW.PayloadJson, '$.Operation.BackgroundReport') IS NOT NULL
        BEGIN
            SELECT RAISE(ABORT, 'Exact background creation is immutable') WHERE
                json_extract(NEW.PayloadJson, '$.Operation') IS NOT json_extract(OLD.PayloadJson, '$.Operation')
                OR NEW.RequestId IS NOT OLD.RequestId OR NEW.TenantId IS NOT OLD.TenantId OR NEW.RunId IS NOT OLD.RunId
                OR NEW.SessionId IS NOT OLD.SessionId OR NEW.InstanceId IS NOT OLD.InstanceId OR NEW.RootActivationId IS NOT OLD.RootActivationId
                OR NEW.ExecutorPrincipalId IS NOT OLD.ExecutorPrincipalId OR NEW.OriginalInitiatorPrincipalId IS NOT OLD.OriginalInitiatorPrincipalId
                OR NEW.OperationId IS NOT OLD.OperationId OR NEW.Fingerprint IS NOT OLD.Fingerprint OR NEW.Permission IS NOT OLD.Permission
                OR NEW.ResourceType IS NOT OLD.ResourceType OR NEW.ResourceId IS NOT OLD.ResourceId OR NEW.Kind IS NOT OLD.Kind
                OR NEW.CreatedAt IS NOT OLD.CreatedAt OR NEW.ExpiresAt IS NOT OLD.ExpiresAt;
        END;
        CREATE TRIGGER TR_TenantApprovals_BrowserArtifacts_Insert BEFORE INSERT ON TenantApprovalRequests
        WHEN json_extract(NEW.PayloadJson, '$.Operation.BrowserCommand.Artifacts') IS NOT NULL
        OR json_extract(NEW.PayloadJson, '$.Operation.BrowserCommand.ToolName') IS 'browser.screenshot'
        BEGIN
        SELECT RAISE(ABORT, 'Browser publication requires exact bounded intent') WHERE NOT COALESCE((
        NEW.Permission IN ('browser.use','credentials.use')
        AND json_type(NEW.PayloadJson, '$.Operation.BrowserCommand.ReadOnly') IS 'false'
        AND json_type(NEW.PayloadJson, '$.Operation.BrowserCommand.Artifacts') IS 'object'
        AND (SELECT count(*) FROM json_each(NEW.PayloadJson, '$.Operation.BrowserCommand.Artifacts')) = 8
        AND (SELECT count(DISTINCT key) FROM json_each(NEW.PayloadJson, '$.Operation.BrowserCommand.Artifacts')
        WHERE key IN ('Capture','Downloads','CaptureBytes','DownloadBytes','CapturesPerScope','DownloadsPerScope','CapturePrivacyMode','CapturePrivacyHash')) = 8
        AND json_type(NEW.PayloadJson, '$.Operation.BrowserCommand.Artifacts.Capture') IN ('true','false')
        AND json_type(NEW.PayloadJson, '$.Operation.BrowserCommand.Artifacts.Downloads') IN ('true','false')
        AND (json_extract(NEW.PayloadJson, '$.Operation.BrowserCommand.Artifacts.Capture') = 1
        OR json_extract(NEW.PayloadJson, '$.Operation.BrowserCommand.Artifacts.Downloads') = 1)
        AND json_extract(NEW.PayloadJson, '$.Operation.BrowserCommand.Artifacts.Capture') =
        (json_extract(NEW.PayloadJson, '$.Operation.BrowserCommand.ToolName') = 'browser.screenshot')
        AND json_type(NEW.PayloadJson, '$.Operation.BrowserCommand.Artifacts.CaptureBytes') IS 'integer'
        AND json_extract(NEW.PayloadJson, '$.Operation.BrowserCommand.Artifacts.CaptureBytes') BETWEEN 1 AND 1500000
        AND json_type(NEW.PayloadJson, '$.Operation.BrowserCommand.Artifacts.DownloadBytes') IS 'integer'
        AND json_extract(NEW.PayloadJson, '$.Operation.BrowserCommand.Artifacts.DownloadBytes') BETWEEN 1 AND 5242880
        AND json_type(NEW.PayloadJson, '$.Operation.BrowserCommand.Artifacts.CapturesPerScope') IS 'integer'
        AND json_extract(NEW.PayloadJson, '$.Operation.BrowserCommand.Artifacts.CapturesPerScope') BETWEEN 1 AND 4
        AND json_type(NEW.PayloadJson, '$.Operation.BrowserCommand.Artifacts.DownloadsPerScope') IS 'integer'
        AND json_extract(NEW.PayloadJson, '$.Operation.BrowserCommand.Artifacts.DownloadsPerScope') BETWEEN 1 AND 2
        AND ((json_extract(NEW.PayloadJson, '$.Operation.BrowserCommand.Artifacts.Capture') = 1
        AND json_extract(NEW.PayloadJson, '$.Operation.BrowserCommand.Artifacts.CapturePrivacyMode') IN ('Protected','Unmasked')
        AND json_type(NEW.PayloadJson, '$.Operation.BrowserCommand.Artifacts.CapturePrivacyHash') IS 'text'
        AND length(json_extract(NEW.PayloadJson, '$.Operation.BrowserCommand.Artifacts.CapturePrivacyHash')) = 64
        AND json_extract(NEW.PayloadJson, '$.Operation.BrowserCommand.Artifacts.CapturePrivacyHash') NOT GLOB '*[^0-9a-f]*')
        OR (json_extract(NEW.PayloadJson, '$.Operation.BrowserCommand.Artifacts.Capture') = 0
        AND json_extract(NEW.PayloadJson, '$.Operation.BrowserCommand.Artifacts.CapturePrivacyMode') IS NULL
        AND json_extract(NEW.PayloadJson, '$.Operation.BrowserCommand.Artifacts.CapturePrivacyHash') IS NULL))
        ),0);
        END;
        CREATE TRIGGER TR_TenantApprovals_BrowserArtifacts_Update BEFORE UPDATE ON TenantApprovalRequests
        WHEN json_extract(OLD.PayloadJson, '$.Operation.BrowserCommand.Artifacts') IS NOT
        json_extract(NEW.PayloadJson, '$.Operation.BrowserCommand.Artifacts')
        BEGIN SELECT RAISE(ABORT, 'Browser publication intent is immutable'); END;
        CREATE TRIGGER TR_TenantApprovals_BrowserFiles_Insert BEFORE INSERT ON TenantApprovalRequests
        WHEN json_extract(NEW.PayloadJson, '$.Operation.BrowserCommand.ToolName') IS 'browser.upload'
        OR (json_extract(NEW.PayloadJson, '$.Operation.BrowserCommand.ToolName') IS 'browser.drop' AND
        json_extract(json_extract(NEW.PayloadJson, '$.Operation.BrowserCommand.ArgumentsJson'), '$.artifactId') IS NOT NULL)
        OR json_extract(NEW.PayloadJson, '$.Operation.BrowserCommand.ArtifactSourcesJson') IS NOT NULL
        OR json_extract(NEW.PayloadJson, '$.Operation.BrowserCommand.FileInputOrigin') IS NOT NULL
        BEGIN
        SELECT RAISE(ABORT, 'Browser file transfer requires exact owned Artifact sources') WHERE NOT COALESCE((
        NEW.Kind = 0 AND NEW.Permission = 'browser.use' AND NEW.ResourceType = 'agent' AND NEW.ResourceId = NEW.InstanceId
        AND json_extract(NEW.PayloadJson, '$.Operation.BrowserCommand.ToolName') IN ('browser.upload','browser.drop')
        AND json_type(NEW.PayloadJson, '$.Operation.BrowserCommand.ReadOnly') IS 'false'
        AND json_extract(NEW.PayloadJson, '$.Operation.BrowserCommand.Credential') IS NULL
        AND json_type(NEW.PayloadJson, '$.Operation.BrowserCommand.FileInputOrigin') IS 'text'
        AND length(json_extract(NEW.PayloadJson, '$.Operation.BrowserCommand.FileInputOrigin')) BETWEEN 1 AND 2048
        AND json_type(NEW.PayloadJson, '$.Operation.BrowserCommand.ArtifactSourcesJson') IS 'text'
        AND length(CAST(json_extract(NEW.PayloadJson, '$.Operation.BrowserCommand.ArtifactSourcesJson') AS BLOB)) BETWEEN 1 AND 16384
        AND json_valid(json_extract(NEW.PayloadJson, '$.Operation.BrowserCommand.ArtifactSourcesJson'))
        AND json_type(json_extract(NEW.PayloadJson, '$.Operation.BrowserCommand.ArtifactSourcesJson')) IS 'array'
        AND ((json_extract(NEW.PayloadJson, '$.Operation.BrowserCommand.ToolName') IS 'browser.upload'
        AND json_array_length(json_extract(NEW.PayloadJson, '$.Operation.BrowserCommand.ArtifactSourcesJson')) BETWEEN 1 AND 8
        AND json_array_length(json_extract(NEW.PayloadJson, '$.Operation.BrowserCommand.ArtifactSourcesJson')) =
        json_array_length(json_extract(NEW.PayloadJson, '$.Operation.BrowserCommand.ArgumentsJson'), '$.artifactIds'))
        OR (json_extract(NEW.PayloadJson, '$.Operation.BrowserCommand.ToolName') IS 'browser.drop'
        AND json_array_length(json_extract(NEW.PayloadJson, '$.Operation.BrowserCommand.ArtifactSourcesJson')) = 1
        AND json_type(json_extract(NEW.PayloadJson, '$.Operation.BrowserCommand.ArgumentsJson'), '$.artifactId') IS 'text'
        AND json_type(json_extract(NEW.PayloadJson, '$.Operation.BrowserCommand.ArgumentsJson'), '$.text') IS NULL
        AND json_type(json_extract(NEW.PayloadJson, '$.Operation.BrowserCommand.ArgumentsJson'), '$.mimeType') IS NULL))
        AND NOT EXISTS (SELECT 1 FROM json_each(json_extract(NEW.PayloadJson, '$.Operation.BrowserCommand.ArtifactSourcesJson')) source
        WHERE json_type(source.value) IS NOT 'object'
        OR json_type(source.value, '$.ArtifactId') IS NOT 'text'
        OR json_type(source.value, '$.DisplayName') IS NOT 'text'
        OR json_type(source.value, '$.ContentType') IS NOT 'text'
        OR json_type(source.value, '$.ByteSize') IS NOT 'integer'
        OR json_type(source.value, '$.Sha256Hex') IS NOT 'text'
        OR (SELECT count(*) FROM json_each(source.value)) <> 5
        OR (SELECT count(DISTINCT key) FROM json_each(source.value)
        WHERE key IN ('ArtifactId','DisplayName','ContentType','ByteSize','Sha256Hex')) <> 5
        OR json_extract(source.value, '$.ArtifactId') IS NOT
        CASE WHEN json_extract(NEW.PayloadJson, '$.Operation.BrowserCommand.ToolName') IS 'browser.upload'
        THEN json_extract(json_extract(NEW.PayloadJson, '$.Operation.BrowserCommand.ArgumentsJson'), '$.artifactIds[' || source.key || ']')
        ELSE json_extract(json_extract(NEW.PayloadJson, '$.Operation.BrowserCommand.ArgumentsJson'), '$.artifactId') END
        OR NOT EXISTS (SELECT 1 FROM Artifacts a WHERE a.TenantId = NEW.TenantId AND a.SessionId = NEW.SessionId
        AND a.ArtifactId = json_extract(source.value, '$.ArtifactId') AND a.DisplayName = json_extract(source.value, '$.DisplayName')
        AND a.ContentType = json_extract(source.value, '$.ContentType') AND a.ByteSize = json_extract(source.value, '$.ByteSize')
        AND a.Sha256Hex = json_extract(source.value, '$.Sha256Hex')
        AND EXISTS (SELECT 1 FROM Sessions saved WHERE saved.SessionId = a.SessionId AND saved.DurablyDeletedAtUtc IS NULL)))
        AND (SELECT count(DISTINCT json_extract(value, '$.ArtifactId'))
        FROM json_each(json_extract(NEW.PayloadJson, '$.Operation.BrowserCommand.ArtifactSourcesJson'))) =
        json_array_length(json_extract(NEW.PayloadJson, '$.Operation.BrowserCommand.ArtifactSourcesJson'))
        AND (SELECT sum(json_extract(value, '$.ByteSize'))
        FROM json_each(json_extract(NEW.PayloadJson, '$.Operation.BrowserCommand.ArtifactSourcesJson'))) <= 52428800
        ), 0);
        END;
        CREATE TRIGGER TR_TenantApprovals_BrowserFiles_Update BEFORE UPDATE ON TenantApprovalRequests
        WHEN json_extract(OLD.PayloadJson, '$.Operation.BrowserCommand.ArtifactSourcesJson') IS NOT
        json_extract(NEW.PayloadJson, '$.Operation.BrowserCommand.ArtifactSourcesJson')
        OR json_extract(OLD.PayloadJson, '$.Operation.BrowserCommand.FileInputOrigin') IS NOT
        json_extract(NEW.PayloadJson, '$.Operation.BrowserCommand.FileInputOrigin')
        OR ((json_extract(OLD.PayloadJson, '$.Operation.BrowserCommand.ArtifactSourcesJson') IS NOT NULL
        OR json_extract(NEW.PayloadJson, '$.Operation.BrowserCommand.ArtifactSourcesJson') IS NOT NULL)
        AND (json_extract(OLD.PayloadJson, '$.Operation.BrowserCommand.ToolName') IS NOT json_extract(NEW.PayloadJson, '$.Operation.BrowserCommand.ToolName')
        OR json_extract(OLD.PayloadJson, '$.Operation.BrowserCommand.ArgumentsJson') IS NOT json_extract(NEW.PayloadJson, '$.Operation.BrowserCommand.ArgumentsJson')))
        BEGIN
        SELECT RAISE(ABORT, 'Browser file transfer sources are immutable');
        END;
        CREATE TRIGGER TR_TenantApprovals_CompletionRead_Insert BEFORE INSERT ON TenantApprovalRequests
        WHEN json_type(NEW.PayloadJson, '$.Operation.CompletionRead') IS 'object'
        BEGIN
            SELECT RAISE(ABORT, 'Completion read requires one exact ordinary read Review') WHERE NOT COALESCE((
                NEW.Kind = 0 AND NEW.Status IN (0,1,2,3,4) AND NEW.Revision >= 1 AND NEW.ExpiresAt > NEW.CreatedAt AND length(NEW.Fingerprint) = 64
                AND NEW.Permission = 'agent-runs.read' AND NEW.ResourceType = 'agent-run'
                AND NEW.ResourceId IS json_extract(NEW.PayloadJson, '$.Operation.CompletionRead.SourceRunId')
                AND NEW.OriginalInitiatorPrincipalId IS NOT NULL AND NEW.CredentialId IS NULL AND NEW.SandboxProfileId IS NULL AND NEW.SandboxProfileRevision IS NULL
                AND json_type(NEW.PayloadJson, '$.Operation.CompletionRead') IS 'object'
                AND json_extract(NEW.PayloadJson, '$.Operation.CompletionRead.ParentSessionId') IS NEW.SessionId
                AND json_type(NEW.PayloadJson, '$.Operation.CompletionRead.SourceRevision') IS 'integer'
                AND json_extract(NEW.PayloadJson, '$.Operation.CompletionRead.SourceRevision') >= 1
                AND json_type(NEW.PayloadJson, '$.Operation.CompletionRead.Objective') IS 'text'
                AND length(CAST(json_extract(NEW.PayloadJson, '$.Operation.CompletionRead.Objective') AS BLOB)) BETWEEN 1 AND 4000
                AND json_type(NEW.PayloadJson, '$.Operation.CompletionRead.ObservationJson') IS 'text'
                AND length(CAST(json_extract(NEW.PayloadJson, '$.Operation.CompletionRead.ObservationJson') AS BLOB)) BETWEEN 2 AND 20000
                AND json_valid(json_extract(NEW.PayloadJson, '$.Operation.CompletionRead.ObservationJson'))
                AND json_type(json_extract(NEW.PayloadJson, '$.Operation.CompletionRead.ObservationJson')) IS 'object'
                AND json_extract(json_extract(NEW.PayloadJson, '$.Operation.CompletionRead.ObservationJson'), '$.untrusted') = 1
                AND json_extract(json_extract(NEW.PayloadJson, '$.Operation.CompletionRead.ObservationJson'), '$.tenantCompletionRead') = 1
                AND json_extract(json_extract(NEW.PayloadJson, '$.Operation.CompletionRead.ObservationJson'), '$.backgroundSessionId') IS json_extract(NEW.PayloadJson, '$.Operation.CompletionRead.ChildSessionId')
                AND json_extract(json_extract(NEW.PayloadJson, '$.Operation.CompletionRead.ObservationJson'), '$.parentSessionId') IS NEW.SessionId
                AND json_extract(json_extract(NEW.PayloadJson, '$.Operation.CompletionRead.ObservationJson'), '$.agentRunId') IS json_extract(NEW.PayloadJson, '$.Operation.CompletionRead.InitialRunId')
                AND json_extract(json_extract(NEW.PayloadJson, '$.Operation.CompletionRead.ObservationJson'), '$.sourceAgentRunId') IS NEW.ResourceId
                AND json_extract(json_extract(NEW.PayloadJson, '$.Operation.CompletionRead.ObservationJson'), '$.status') IN ('Completed','Failed','Cancelled')
                AND json_type(NEW.PayloadJson, '$.Operation.CompletionRead.ObservationHash') IS 'text'
                AND length(json_extract(NEW.PayloadJson, '$.Operation.CompletionRead.ObservationHash')) = 64
                AND json_extract(NEW.PayloadJson, '$.Operation.CompletionRead.ObservationHash') NOT GLOB '*[^0-9a-f]*'
                AND json_type(NEW.PayloadJson, '$.Operation.CompletionRead.ArtifactsJson') IS 'text'
                AND length(CAST(json_extract(NEW.PayloadJson, '$.Operation.CompletionRead.ArtifactsJson') AS BLOB)) <= 8192
                AND json_valid(json_extract(NEW.PayloadJson, '$.Operation.CompletionRead.ArtifactsJson'))
                AND json_type(json_extract(NEW.PayloadJson, '$.Operation.CompletionRead.ArtifactsJson')) IS 'array'
                AND json_array_length(json_extract(NEW.PayloadJson, '$.Operation.CompletionRead.ArtifactsJson')) <= 3
                AND length(CAST(NEW.PayloadJson AS BLOB)) <= 131072
                AND json_type(NEW.PayloadJson, '$.Operation.CompletionRead.ChildSessionId') IS 'text' AND length(json_extract(NEW.PayloadJson, '$.Operation.CompletionRead.ChildSessionId')) = 36 AND json_extract(NEW.PayloadJson, '$.Operation.CompletionRead.ChildSessionId') NOT GLOB '*[^0-9a-f-]*' AND substr(json_extract(NEW.PayloadJson, '$.Operation.CompletionRead.ChildSessionId'),9,1) IS '-' AND substr(json_extract(NEW.PayloadJson, '$.Operation.CompletionRead.ChildSessionId'),14,1) IS '-' AND substr(json_extract(NEW.PayloadJson, '$.Operation.CompletionRead.ChildSessionId'),19,1) IS '-' AND substr(json_extract(NEW.PayloadJson, '$.Operation.CompletionRead.ChildSessionId'),24,1) IS '-' AND json_extract(NEW.PayloadJson, '$.Operation.CompletionRead.ChildSessionId') IS NOT '00000000-0000-0000-0000-000000000000' AND json_extract(NEW.PayloadJson, '$.Operation.CompletionRead.ChildSessionId') IS NOT NEW.SessionId
                AND json_type(NEW.PayloadJson, '$.Operation.CompletionRead.InitialRunId') IS 'text' AND length(json_extract(NEW.PayloadJson, '$.Operation.CompletionRead.InitialRunId')) = 36 AND json_extract(NEW.PayloadJson, '$.Operation.CompletionRead.InitialRunId') NOT GLOB '*[^0-9a-f-]*' AND substr(json_extract(NEW.PayloadJson, '$.Operation.CompletionRead.InitialRunId'),9,1) IS '-' AND substr(json_extract(NEW.PayloadJson, '$.Operation.CompletionRead.InitialRunId'),14,1) IS '-' AND substr(json_extract(NEW.PayloadJson, '$.Operation.CompletionRead.InitialRunId'),19,1) IS '-' AND substr(json_extract(NEW.PayloadJson, '$.Operation.CompletionRead.InitialRunId'),24,1) IS '-' AND json_extract(NEW.PayloadJson, '$.Operation.CompletionRead.InitialRunId') IS NOT '00000000-0000-0000-0000-000000000000' AND json_extract(NEW.PayloadJson, '$.Operation.CompletionRead.InitialRunId') IS NOT NEW.SessionId
                AND json_type(NEW.PayloadJson, '$.Operation.CompletionRead.SourceRunId') IS 'text' AND length(json_extract(NEW.PayloadJson, '$.Operation.CompletionRead.SourceRunId')) = 36 AND json_extract(NEW.PayloadJson, '$.Operation.CompletionRead.SourceRunId') NOT GLOB '*[^0-9a-f-]*' AND substr(json_extract(NEW.PayloadJson, '$.Operation.CompletionRead.SourceRunId'),9,1) IS '-' AND substr(json_extract(NEW.PayloadJson, '$.Operation.CompletionRead.SourceRunId'),14,1) IS '-' AND substr(json_extract(NEW.PayloadJson, '$.Operation.CompletionRead.SourceRunId'),19,1) IS '-' AND substr(json_extract(NEW.PayloadJson, '$.Operation.CompletionRead.SourceRunId'),24,1) IS '-' AND json_extract(NEW.PayloadJson, '$.Operation.CompletionRead.SourceRunId') IS NOT '00000000-0000-0000-0000-000000000000' AND json_extract(NEW.PayloadJson, '$.Operation.CompletionRead.SourceRunId') IS NOT NEW.SessionId
                AND json_extract(NEW.PayloadJson, '$.Operation.Persona') IS NULL
                AND json_extract(NEW.PayloadJson, '$.Operation.Preferences') IS NULL
                AND json_extract(NEW.PayloadJson, '$.Operation.CredentialCheck') IS NULL
                AND json_extract(NEW.PayloadJson, '$.Operation.SandboxCommand') IS NULL
                AND json_extract(NEW.PayloadJson, '$.Operation.SandboxControl') IS NULL
                AND json_extract(NEW.PayloadJson, '$.Operation.SandboxRead') IS NULL
                AND json_extract(NEW.PayloadJson, '$.Operation.BrowserCommand') IS NULL
                AND json_extract(NEW.PayloadJson, '$.Operation.CredentialRead') IS NULL
                AND json_extract(NEW.PayloadJson, '$.Operation.ArtifactPublication') IS NULL
                AND json_extract(NEW.PayloadJson, '$.Operation.ArtifactVerification') IS NULL
                AND json_extract(NEW.PayloadJson, '$.Operation.WorkspaceRead') IS NULL
                AND json_extract(NEW.PayloadJson, '$.Operation.WorkspaceMutation') IS NULL
                AND json_extract(NEW.PayloadJson, '$.Operation.AttachmentRead') IS NULL
                AND json_extract(NEW.PayloadJson, '$.Operation.BackgroundStart') IS NULL
                AND json_extract(NEW.PayloadJson, '$.Operation.BackgroundReport') IS NULL),0);
            SELECT RAISE(ABORT, 'Completion read source must be an exact owned terminal child') WHERE NOT EXISTS (
                SELECT 1 FROM TenantSessions child JOIN Sessions saved ON saved.SessionId = child.SessionId
                JOIN AgentRuns initial ON initial.AgentRunId = json_extract(NEW.PayloadJson, '$.Operation.CompletionRead.InitialRunId')
                JOIN AgentRuns source ON source.AgentRunId = json_extract(NEW.PayloadJson, '$.Operation.CompletionRead.SourceRunId')
                WHERE child.TenantId = NEW.TenantId AND child.AgentInstanceId = NEW.InstanceId AND child.ExecutorPrincipalId = NEW.ExecutorPrincipalId
                AND child.SessionId = json_extract(NEW.PayloadJson, '$.Operation.CompletionRead.ChildSessionId') AND saved.DurablyDeletedAtUtc IS NULL
                AND json_extract(saved.OriginJson, '$.originatingSessionId') IS NEW.SessionId
                AND json_extract(saved.OriginJson, '$.initialBackgroundAgentRunId') IS initial.AgentRunId
                AND initial.TenantId = NEW.TenantId AND initial.SessionId = child.SessionId AND initial.AgentInstanceId = NEW.InstanceId AND initial.ExecutorPrincipalId = NEW.ExecutorPrincipalId
                AND json_extract(initial.PayloadJson, '$.admission.activation.kind') = 2 AND json_extract(initial.PayloadJson, '$.admission.outputContract') = 1
                AND source.TenantId = NEW.TenantId AND source.SessionId = child.SessionId AND source.AgentInstanceId = NEW.InstanceId AND source.ExecutorPrincipalId = NEW.ExecutorPrincipalId
                AND source.RootActivationId = initial.RootActivationId AND source.Status IN (4,5,6)
                AND source.Revision = json_extract(NEW.PayloadJson, '$.Operation.CompletionRead.SourceRevision')
                AND json_extract(source.PayloadJson, '$.admission.outputContract') = 1
                AND (json_extract(source.PayloadJson, '$.result.outcomeKind') IS NULL OR json_extract(source.PayloadJson, '$.result.outcomeKind') <> 3)
                AND EXISTS (
                    WITH RECURSIVE lineage(id, depth, seen) AS (
                        SELECT initial.AgentRunId, 0, ',' || initial.AgentRunId || ','
                        UNION ALL
                        SELECT next.AgentRunId, chain.depth + 1, chain.seen || next.AgentRunId || ',' FROM lineage chain
                        JOIN AgentRuns previous ON previous.AgentRunId = chain.id
                        JOIN AgentRuns next ON next.AgentRunId = json_extract(previous.PayloadJson, '$.result.continuationRunId')
                        WHERE chain.depth < 63 AND previous.Status = 4 AND json_extract(previous.PayloadJson, '$.result.outcomeKind') = 3
                        AND instr(chain.seen, ',' || next.AgentRunId || ',') = 0
                        AND next.TenantId = NEW.TenantId AND next.SessionId = child.SessionId AND next.AgentInstanceId = NEW.InstanceId AND next.ExecutorPrincipalId = NEW.ExecutorPrincipalId
                        AND next.RootActivationId = initial.RootActivationId AND json_extract(next.PayloadJson, '$.admission.activation.kind') = 9
                        AND json_extract(next.PayloadJson, '$.admission.outputContract') = 1
                        AND json_extract(next.PayloadJson, '$.admission.approvalResume.sourceRunId') IS previous.AgentRunId
                        AND json_extract(next.PayloadJson, '$.admission.executionContext.parentRunId') IS previous.AgentRunId
                        AND json_extract(next.PayloadJson, '$.admission.activation.sourceAgentRunId') IS previous.AgentRunId
                    ) SELECT 1 FROM lineage WHERE id = source.AgentRunId));
            SELECT RAISE(ABORT, 'Completion metadata must match the exact source') WHERE
                (SELECT count(*) FROM json_each(NEW.PayloadJson, '$.Operation.CompletionRead')) <> 9
                OR EXISTS (SELECT 1 FROM json_each(json_extract(NEW.PayloadJson, '$.Operation.CompletionRead.ArtifactsJson')) file
                    WHERE json_type(file.value) IS NOT 'object' OR (SELECT count(*) FROM json_each(file.value)) <> 6
                    OR json_extract(file.value, '$.SessionId') IS NOT json_extract(NEW.PayloadJson, '$.Operation.CompletionRead.ChildSessionId')
                    OR NOT EXISTS (SELECT 1 FROM Artifacts a JOIN Sessions saved ON saved.SessionId = a.SessionId
                        WHERE a.TenantId = NEW.TenantId AND a.SessionId = json_extract(file.value, '$.SessionId') AND saved.DurablyDeletedAtUtc IS NULL
                        AND a.AgentRunId = json_extract(NEW.PayloadJson, '$.Operation.CompletionRead.SourceRunId')
                        AND a.ArtifactId = json_extract(file.value, '$.ArtifactId') AND a.DisplayName = json_extract(file.value, '$.DisplayName')
                        AND a.ContentType = json_extract(file.value, '$.ContentType') AND a.ByteSize = json_extract(file.value, '$.ByteSize') AND a.Sha256Hex = json_extract(file.value, '$.Sha256Hex')))
                OR (SELECT count(DISTINCT json_extract(value, '$.ArtifactId')) FROM json_each(json_extract(NEW.PayloadJson, '$.Operation.CompletionRead.ArtifactsJson')))
                    <> json_array_length(json_extract(NEW.PayloadJson, '$.Operation.CompletionRead.ArtifactsJson'));
        END;
        CREATE TRIGGER TR_TenantApprovals_CompletionRead_Update BEFORE UPDATE ON TenantApprovalRequests
        WHEN json_extract(OLD.PayloadJson, '$.Operation.CompletionRead') IS NOT NULL OR json_extract(NEW.PayloadJson, '$.Operation.CompletionRead') IS NOT NULL
        BEGIN
            SELECT RAISE(ABORT, 'Completion read evidence is immutable') WHERE
                json_extract(OLD.PayloadJson, '$.Operation') IS NOT json_extract(NEW.PayloadJson, '$.Operation')
                OR OLD.RequestId IS NOT NEW.RequestId OR OLD.TenantId IS NOT NEW.TenantId OR OLD.SessionId IS NOT NEW.SessionId OR OLD.RunId IS NOT NEW.RunId
                OR OLD.RootActivationId IS NOT NEW.RootActivationId OR OLD.InstanceId IS NOT NEW.InstanceId OR OLD.ExecutorPrincipalId IS NOT NEW.ExecutorPrincipalId
                OR OLD.OriginalInitiatorPrincipalId IS NOT NEW.OriginalInitiatorPrincipalId OR OLD.OperationId IS NOT NEW.OperationId
                OR OLD.Fingerprint IS NOT NEW.Fingerprint OR OLD.Permission IS NOT NEW.Permission OR OLD.ResourceType IS NOT NEW.ResourceType
                OR OLD.ResourceId IS NOT NEW.ResourceId OR OLD.Kind IS NOT NEW.Kind OR OLD.CreatedAt IS NOT NEW.CreatedAt OR OLD.ExpiresAt IS NOT NEW.ExpiresAt;
        END;
        CREATE TRIGGER TR_TenantApprovals_ReportSourceRead_Insert BEFORE INSERT ON TenantApprovalRequests
        WHEN json_type(NEW.PayloadJson, '$.Operation.ReportSourceRead') IS 'object'
        BEGIN
                SELECT RAISE(ABORT, 'Report source read requires exact ordinary Review') WHERE NOT COALESCE((
                NEW.Kind = 0 AND NEW.Status IN (0,1,2,3,4) AND NEW.Revision >= 1 AND NEW.ExpiresAt > NEW.CreatedAt AND length(NEW.Fingerprint) = 64
                AND NEW.Permission = 'agent-runs.read' AND NEW.ResourceType = 'agent-run' AND NEW.ResourceId IS NEW.RunId
                AND NEW.OriginalInitiatorPrincipalId IS NOT NULL AND NEW.CredentialId IS NULL AND NEW.SandboxProfileId IS NULL AND NEW.SandboxProfileRevision IS NULL
                AND json_type(NEW.PayloadJson, '$.Operation.ReportSourceRead') IS 'object'
                AND json_extract(NEW.PayloadJson, '$.Operation.ReportSourceRead.ChildSessionId') IS NEW.SessionId
                AND json_extract(NEW.PayloadJson, '$.Operation.ReportSourceRead.SourceRunId') IS NEW.RunId
                AND json_extract(NEW.PayloadJson, '$.Operation.ReportSourceRead.ParentSessionId') IS NOT NEW.SessionId
                AND json_type(NEW.PayloadJson, '$.Operation.ReportSourceRead.SourceRevision') IS 'integer' AND json_extract(NEW.PayloadJson, '$.Operation.ReportSourceRead.SourceRevision') >= 1
                AND json_type(NEW.PayloadJson, '$.Operation.ReportSourceRead.Objective') IS 'text' AND length(CAST(json_extract(NEW.PayloadJson, '$.Operation.ReportSourceRead.Objective') AS BLOB)) BETWEEN 1 AND 4000
                AND json_type(NEW.PayloadJson, '$.Operation.ReportSourceRead.ObservationJson') IS 'text' AND length(CAST(json_extract(NEW.PayloadJson, '$.Operation.ReportSourceRead.ObservationJson') AS BLOB)) BETWEEN 2 AND 8192
                AND json_valid(json_extract(NEW.PayloadJson, '$.Operation.ReportSourceRead.ObservationJson'))
                AND json_extract(json_extract(NEW.PayloadJson, '$.Operation.ReportSourceRead.ObservationJson'), '$.TriggerKind') = 7
                AND json_extract(json_extract(NEW.PayloadJson, '$.Operation.ReportSourceRead.ObservationJson'), '$.InputBoundarySequence') IS NULL
                AND json_type(json_extract(NEW.PayloadJson, '$.Operation.ReportSourceRead.ObservationJson'), '$.Text') IS 'text'
                AND json_valid(json_extract(json_extract(NEW.PayloadJson, '$.Operation.ReportSourceRead.ObservationJson'), '$.Text'))
                AND json_extract(json_extract(json_extract(NEW.PayloadJson, '$.Operation.ReportSourceRead.ObservationJson'), '$.Text'), '$.childSessionId') IS NEW.SessionId
                AND json_extract(json_extract(json_extract(NEW.PayloadJson, '$.Operation.ReportSourceRead.ObservationJson'), '$.Text'), '$.initialAgentRunId') IS NEW.RunId
                AND json_extract(json_extract(json_extract(NEW.PayloadJson, '$.Operation.ReportSourceRead.ObservationJson'), '$.Text'), '$.status') IN ('Completed','Failed','Cancelled')
                AND json_array_length(json_extract(json_extract(json_extract(NEW.PayloadJson, '$.Operation.ReportSourceRead.ObservationJson'), '$.Text'), '$.artifacts')) = 0
                AND json_type(NEW.PayloadJson, '$.Operation.ReportSourceRead.ObservationHash') IS 'text' AND length(json_extract(NEW.PayloadJson, '$.Operation.ReportSourceRead.ObservationHash')) = 64
                AND json_extract(NEW.PayloadJson, '$.Operation.ReportSourceRead.ObservationHash') NOT GLOB '*[^0-9a-f]*'
                AND length(CAST(NEW.PayloadJson AS BLOB)) <= 131072
                AND json_type(NEW.PayloadJson, '$.Operation.ReportSourceRead.ParentSessionId') IS 'text' AND length(json_extract(NEW.PayloadJson, '$.Operation.ReportSourceRead.ParentSessionId')) = 36 AND json_extract(NEW.PayloadJson, '$.Operation.ReportSourceRead.ParentSessionId') NOT GLOB '*[^0-9a-f-]*' AND json_extract(NEW.PayloadJson, '$.Operation.ReportSourceRead.ParentSessionId') IS NOT '00000000-0000-0000-0000-000000000000'
                AND json_type(NEW.PayloadJson, '$.Operation.ReportSourceRead.ChildSessionId') IS 'text' AND length(json_extract(NEW.PayloadJson, '$.Operation.ReportSourceRead.ChildSessionId')) = 36 AND json_extract(NEW.PayloadJson, '$.Operation.ReportSourceRead.ChildSessionId') NOT GLOB '*[^0-9a-f-]*' AND json_extract(NEW.PayloadJson, '$.Operation.ReportSourceRead.ChildSessionId') IS NOT '00000000-0000-0000-0000-000000000000'
                AND json_type(NEW.PayloadJson, '$.Operation.ReportSourceRead.InitialRunId') IS 'text' AND length(json_extract(NEW.PayloadJson, '$.Operation.ReportSourceRead.InitialRunId')) = 36 AND json_extract(NEW.PayloadJson, '$.Operation.ReportSourceRead.InitialRunId') NOT GLOB '*[^0-9a-f-]*' AND json_extract(NEW.PayloadJson, '$.Operation.ReportSourceRead.InitialRunId') IS NOT '00000000-0000-0000-0000-000000000000'
                AND json_type(NEW.PayloadJson, '$.Operation.ReportSourceRead.SourceRunId') IS 'text' AND length(json_extract(NEW.PayloadJson, '$.Operation.ReportSourceRead.SourceRunId')) = 36 AND json_extract(NEW.PayloadJson, '$.Operation.ReportSourceRead.SourceRunId') NOT GLOB '*[^0-9a-f-]*' AND json_extract(NEW.PayloadJson, '$.Operation.ReportSourceRead.SourceRunId') IS NOT '00000000-0000-0000-0000-000000000000'
                AND json_extract(NEW.PayloadJson, '$.Operation.Persona') IS NULL
                AND json_extract(NEW.PayloadJson, '$.Operation.Preferences') IS NULL
                AND json_extract(NEW.PayloadJson, '$.Operation.CredentialCheck') IS NULL
                AND json_extract(NEW.PayloadJson, '$.Operation.SandboxCommand') IS NULL
                AND json_extract(NEW.PayloadJson, '$.Operation.SandboxControl') IS NULL
                AND json_extract(NEW.PayloadJson, '$.Operation.SandboxRead') IS NULL
                AND json_extract(NEW.PayloadJson, '$.Operation.BrowserCommand') IS NULL
                AND json_extract(NEW.PayloadJson, '$.Operation.CredentialRead') IS NULL
                AND json_extract(NEW.PayloadJson, '$.Operation.ArtifactPublication') IS NULL
                AND json_extract(NEW.PayloadJson, '$.Operation.ArtifactVerification') IS NULL
                AND json_extract(NEW.PayloadJson, '$.Operation.WorkspaceRead') IS NULL
                AND json_extract(NEW.PayloadJson, '$.Operation.WorkspaceMutation') IS NULL
                AND json_extract(NEW.PayloadJson, '$.Operation.AttachmentRead') IS NULL
                AND json_extract(NEW.PayloadJson, '$.Operation.BackgroundStart') IS NULL
                AND json_extract(NEW.PayloadJson, '$.Operation.BackgroundReport') IS NULL
                AND json_extract(NEW.PayloadJson, '$.Operation.CompletionRead') IS NULL),0);
            SELECT RAISE(ABORT, 'Completion read source must be an exact owned terminal child') WHERE NOT EXISTS (
                SELECT 1 FROM TenantSessions child JOIN Sessions saved ON saved.SessionId = child.SessionId
                JOIN AgentRuns initial ON initial.AgentRunId = json_extract(NEW.PayloadJson, '$.Operation.ReportSourceRead.InitialRunId')
                JOIN AgentRuns source ON source.AgentRunId = json_extract(NEW.PayloadJson, '$.Operation.ReportSourceRead.SourceRunId')
                WHERE child.TenantId = NEW.TenantId AND child.AgentInstanceId = NEW.InstanceId AND child.ExecutorPrincipalId = NEW.ExecutorPrincipalId
                AND child.SessionId = json_extract(NEW.PayloadJson, '$.Operation.ReportSourceRead.ChildSessionId') AND saved.DurablyDeletedAtUtc IS NULL
                AND json_extract(saved.OriginJson, '$.originatingSessionId') IS json_extract(NEW.PayloadJson, '$.Operation.ReportSourceRead.ParentSessionId')
                AND json_extract(saved.OriginJson, '$.initialBackgroundAgentRunId') IS initial.AgentRunId
                AND initial.TenantId = NEW.TenantId AND initial.SessionId = child.SessionId AND initial.AgentInstanceId = NEW.InstanceId AND initial.ExecutorPrincipalId = NEW.ExecutorPrincipalId
                AND json_extract(initial.PayloadJson, '$.admission.activation.kind') = 2 AND json_extract(initial.PayloadJson, '$.admission.outputContract') = 1
                AND source.TenantId = NEW.TenantId AND source.SessionId = child.SessionId AND source.AgentInstanceId = NEW.InstanceId AND source.ExecutorPrincipalId = NEW.ExecutorPrincipalId
                AND source.AgentRunId IS NEW.RunId AND source.SessionId IS NEW.SessionId AND source.RootActivationId IS NEW.RootActivationId
                AND source.RootActivationId = initial.RootActivationId AND source.Status IN (4,5,6)
                AND source.Revision = json_extract(NEW.PayloadJson, '$.Operation.ReportSourceRead.SourceRevision')
                AND json_extract(source.PayloadJson, '$.admission.outputContract') = 1
                AND (json_extract(source.PayloadJson, '$.result.outcomeKind') IS NULL OR json_extract(source.PayloadJson, '$.result.outcomeKind') <> 3)
                AND EXISTS (SELECT 1 FROM TenantSessions parent JOIN Sessions ps ON ps.SessionId = parent.SessionId WHERE parent.TenantId = NEW.TenantId
                    AND parent.AgentInstanceId = NEW.InstanceId AND parent.ExecutorPrincipalId = NEW.ExecutorPrincipalId AND ps.DurablyDeletedAtUtc IS NULL
                    AND parent.SessionId = json_extract(NEW.PayloadJson, '$.Operation.ReportSourceRead.ParentSessionId'))
                AND EXISTS (
                    WITH RECURSIVE lineage(id, depth, seen) AS (
                        SELECT initial.AgentRunId, 0, ',' || initial.AgentRunId || ','
                        UNION ALL
                        SELECT next.AgentRunId, chain.depth + 1, chain.seen || next.AgentRunId || ',' FROM lineage chain
                        JOIN AgentRuns previous ON previous.AgentRunId = chain.id
                        JOIN AgentRuns next ON next.AgentRunId = json_extract(previous.PayloadJson, '$.result.continuationRunId')
                        WHERE chain.depth < 63 AND previous.Status = 4 AND json_extract(previous.PayloadJson, '$.result.outcomeKind') = 3
                        AND instr(chain.seen, ',' || next.AgentRunId || ',') = 0
                        AND next.TenantId = NEW.TenantId AND next.SessionId = child.SessionId AND next.AgentInstanceId = NEW.InstanceId AND next.ExecutorPrincipalId = NEW.ExecutorPrincipalId
                        AND next.RootActivationId = initial.RootActivationId AND json_extract(next.PayloadJson, '$.admission.activation.kind') = 9
                        AND json_extract(next.PayloadJson, '$.admission.outputContract') = 1
                        AND json_extract(next.PayloadJson, '$.admission.approvalResume.sourceRunId') IS previous.AgentRunId
                        AND json_extract(next.PayloadJson, '$.admission.executionContext.parentRunId') IS previous.AgentRunId
                        AND json_extract(next.PayloadJson, '$.admission.activation.sourceAgentRunId') IS previous.AgentRunId
                    ) SELECT 1 FROM lineage WHERE id = source.AgentRunId));
            SELECT RAISE(ABORT, 'Report source read has an invalid shape') WHERE (SELECT count(*) FROM json_each(NEW.PayloadJson, '$.Operation.ReportSourceRead')) <> 8;
        END;
        CREATE TRIGGER TR_TenantApprovals_ReportSourceRead_Update BEFORE UPDATE ON TenantApprovalRequests
        WHEN json_extract(OLD.PayloadJson, '$.Operation.ReportSourceRead') IS NOT NULL OR json_extract(NEW.PayloadJson, '$.Operation.ReportSourceRead') IS NOT NULL
        BEGIN
            SELECT RAISE(ABORT, 'Report source read evidence is immutable') WHERE
                json_extract(OLD.PayloadJson, '$.Operation') IS NOT json_extract(NEW.PayloadJson, '$.Operation')
                OR OLD.RequestId IS NOT NEW.RequestId OR OLD.TenantId IS NOT NEW.TenantId OR OLD.SessionId IS NOT NEW.SessionId OR OLD.RunId IS NOT NEW.RunId
                OR OLD.RootActivationId IS NOT NEW.RootActivationId OR OLD.InstanceId IS NOT NEW.InstanceId OR OLD.ExecutorPrincipalId IS NOT NEW.ExecutorPrincipalId
                OR OLD.OriginalInitiatorPrincipalId IS NOT NEW.OriginalInitiatorPrincipalId OR OLD.OperationId IS NOT NEW.OperationId
                OR OLD.Fingerprint IS NOT NEW.Fingerprint OR OLD.Permission IS NOT NEW.Permission OR OLD.ResourceType IS NOT NEW.ResourceType
                OR OLD.ResourceId IS NOT NEW.ResourceId OR OLD.Kind IS NOT NEW.Kind OR OLD.CreatedAt IS NOT NEW.CreatedAt OR OLD.ExpiresAt IS NOT NEW.ExpiresAt;
        END;
        CREATE TRIGGER TR_TenantReceipts_BrowserArtifacts_Insert BEFORE INSERT ON TenantOperationReceipts
        WHEN NEW.BrowserArtifactsJson IS NOT NULL
        BEGIN
        SELECT RAISE(ABORT, 'Browser Artifact receipt requires actual owned publication') WHERE NOT COALESCE((
        NEW.RequestId IS NOT NULL AND length(CAST(NEW.BrowserArtifactsJson AS BLOB)) <= 4096
        AND json_valid(NEW.BrowserArtifactsJson) AND json_type(NEW.BrowserArtifactsJson) IS 'object'
        AND (SELECT count(*) FROM json_each(NEW.BrowserArtifactsJson)) = 3
        AND (SELECT count(DISTINCT key) FROM json_each(NEW.BrowserArtifactsJson) WHERE key IN ('status','reasonCode','files')) = 3
        AND json_extract(NEW.BrowserArtifactsJson, '$.status') IN ('published','empty','unavailable')
        AND (json_extract(NEW.BrowserArtifactsJson, '$.reasonCode') IS NULL OR json_extract(NEW.BrowserArtifactsJson, '$.reasonCode') IN
        ('browser_command_not_completed','artifact_publication_unavailable','permission_denied','artifact_quota_exceeded','capture_limit','download_limit','capture_invalid','download_invalid'))
        AND json_type(NEW.BrowserArtifactsJson, '$.files') IS 'array'
        AND json_array_length(NEW.BrowserArtifactsJson, '$.files') <= 3
        AND ((json_extract(NEW.BrowserArtifactsJson, '$.status') = 'published' AND NEW.Outcome = 0
        AND json_array_length(NEW.BrowserArtifactsJson, '$.files') > 0)
        OR (json_extract(NEW.BrowserArtifactsJson, '$.status') = 'empty' AND NEW.Outcome = 0
        AND json_array_length(NEW.BrowserArtifactsJson, '$.files') = 0 AND json_extract(NEW.BrowserArtifactsJson, '$.reasonCode') IS NULL)
        OR (json_extract(NEW.BrowserArtifactsJson, '$.status') = 'unavailable'
        AND json_array_length(NEW.BrowserArtifactsJson, '$.files') = 0 AND json_extract(NEW.BrowserArtifactsJson, '$.reasonCode') IS NOT NULL))
        AND EXISTS (SELECT 1 FROM TenantApprovalRequests request WHERE request.RequestId = NEW.RequestId
        AND json_type(request.PayloadJson, '$.Operation.BrowserCommand.Artifacts') IS 'object')
        AND (SELECT count(*) FROM json_each(NEW.BrowserArtifactsJson, '$.files') WHERE json_extract(value, '$.kind') = 'capture') <= 1
        AND (SELECT count(*) FROM json_each(NEW.BrowserArtifactsJson, '$.files') WHERE json_extract(value, '$.kind') = 'download') <= 2
        AND (SELECT count(DISTINCT json_extract(value, '$.artifactId')) FROM json_each(NEW.BrowserArtifactsJson, '$.files')) = json_array_length(NEW.BrowserArtifactsJson, '$.files')
        AND NOT EXISTS (SELECT 1 FROM json_each(NEW.BrowserArtifactsJson, '$.files') file
        WHERE json_type(file.value) IS NOT 'object' OR (SELECT count(*) FROM json_each(file.value)) <> 2
        OR (SELECT count(DISTINCT key) FROM json_each(file.value) WHERE key IN ('kind','artifactId')) <> 2
        OR json_type(file.value, '$.artifactId') IS NOT 'text' OR json_extract(file.value, '$.kind') NOT IN ('capture','download')
        OR NOT EXISTS (SELECT 1 FROM Artifacts artifact JOIN TenantApprovalRequests request ON request.RequestId = NEW.RequestId
        WHERE artifact.TenantId = NEW.TenantId AND artifact.SessionId = NEW.SessionId AND artifact.AgentRunId = NEW.RunId
        AND artifact.ArtifactId = json_extract(file.value, '$.artifactId')
        AND ((json_extract(file.value, '$.kind') = 'capture'
        AND json_extract(request.PayloadJson, '$.Operation.BrowserCommand.Artifacts.Capture') = 1
        AND artifact.ArtifactId = lower(NEW.OperationId) AND artifact.ContentType IN ('image/png','image/jpeg','image/webp')
        AND artifact.ByteSize BETWEEN 1 AND json_extract(request.PayloadJson, '$.Operation.BrowserCommand.Artifacts.CaptureBytes'))
        OR (json_extract(file.value, '$.kind') = 'download'
        AND json_extract(request.PayloadJson, '$.Operation.BrowserCommand.Artifacts.Downloads') = 1
        AND artifact.ByteSize BETWEEN 0 AND json_extract(request.PayloadJson, '$.Operation.BrowserCommand.Artifacts.DownloadBytes')))))
        ),0);
        END;
        CREATE TRIGGER TR_TenantReceipts_BrowserArtifacts_Update BEFORE UPDATE ON TenantOperationReceipts
        WHEN OLD.BrowserArtifactsJson IS NOT NULL OR NEW.BrowserArtifactsJson IS NOT NULL
        BEGIN SELECT RAISE(ABORT, 'Browser Artifact receipts are immutable'); END;
        CREATE TRIGGER TR_Attachments_Tenant_Insert BEFORE INSERT ON Attachments
        WHEN (NEW.TenantId IS NULL AND EXISTS (SELECT 1 FROM TenantSessions WHERE SessionId = NEW.SessionId))
         OR (NEW.TenantId IS NOT NULL AND (NOT EXISTS (SELECT 1 FROM TenantSessions WHERE TenantId = NEW.TenantId AND SessionId = NEW.SessionId)
         OR NOT EXISTS (SELECT 1 FROM Principals WHERE PrincipalId = NEW.UploadedByPrincipalId AND TenantId = NEW.TenantId AND Kind = 0)
         OR (NEW.State = 'Bound' AND NOT EXISTS (SELECT 1 FROM ConversationEntries WHERE EntryId = NEW.EntryId AND SessionId = NEW.SessionId))))
        BEGIN SELECT RAISE(ABORT, 'Attachment ownership mismatch'); END;
        CREATE TRIGGER TR_Attachments_Tenant_Update BEFORE UPDATE ON Attachments
        WHEN OLD.TenantId IS NOT NEW.TenantId
         OR (NEW.TenantId IS NULL AND EXISTS (SELECT 1 FROM TenantSessions WHERE SessionId = NEW.SessionId))
         OR (OLD.TenantId IS NOT NULL AND (OLD.SessionId IS NOT NEW.SessionId OR OLD.AttachmentId IS NOT NEW.AttachmentId
         OR OLD.UploadRequestId IS NOT NEW.UploadRequestId OR OLD.UploadedByPrincipalId IS NOT NEW.UploadedByPrincipalId
         OR OLD.BlobKey IS NOT NEW.BlobKey OR OLD.DisplayName IS NOT NEW.DisplayName OR OLD.ContentType IS NOT NEW.ContentType
         OR OLD.ByteSize IS NOT NEW.ByteSize OR OLD.Sha256Hex IS NOT NEW.Sha256Hex OR OLD.Readable IS NOT NEW.Readable OR OLD.CreatedAtUtc IS NOT NEW.CreatedAtUtc
         OR (OLD.State = 'Bound' AND (NEW.State IS NOT 'Bound' OR OLD.EntryId IS NOT NEW.EntryId OR OLD.BoundAtUtc IS NOT NEW.BoundAtUtc))
         OR (OLD.State = 'Pending' AND NEW.State = 'Pending' AND NEW.ExpiresAtUtc > OLD.ExpiresAtUtc)
         OR NOT EXISTS (SELECT 1 FROM TenantSessions WHERE TenantId = NEW.TenantId AND SessionId = NEW.SessionId)
         OR NOT EXISTS (SELECT 1 FROM Principals WHERE PrincipalId = NEW.UploadedByPrincipalId AND TenantId = NEW.TenantId AND Kind = 0)
         OR (NEW.State = 'Bound' AND NOT EXISTS (SELECT 1 FROM ConversationEntries WHERE EntryId = NEW.EntryId AND SessionId = NEW.SessionId))))
        BEGIN SELECT RAISE(ABORT, 'Attachment ownership or immutable source mismatch'); END;
        """;
}
