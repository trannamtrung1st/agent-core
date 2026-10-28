-- Frozen exact predecessor guards and data-preserving table rebuild. Stopped writers and coordinated backup required.

DROP TRIGGER IF EXISTS "TR_SpaceAutomationPins_Approvals_INSERT";

DROP TRIGGER IF EXISTS "TR_SpaceAutomationPins_Automations_INSERT";

DROP TRIGGER IF EXISTS "TR_SpaceAutomationPins_Automations_UPDATE";

DROP TRIGGER IF EXISTS "TR_SpaceAutomationPins_CoreFact_INSERT";

DROP TRIGGER IF EXISTS "TR_SpaceAutomationPins_Occurrences_INSERT";

DROP TRIGGER IF EXISTS "TR_SpaceAutomationPins_Occurrences_UPDATE";

DROP TRIGGER IF EXISTS "TR_SpaceAutomationPins_Runs_INSERT";

DROP TRIGGER IF EXISTS "TR_SpaceAutomationPins_Webhook_INSERT";

DROP TRIGGER IF EXISTS "TR_TenantApprovals_AutomationAdmission_INSERT";

DROP TRIGGER IF EXISTS "TR_TenantApprovals_AutomationAdmission_UPDATE";

DROP TRIGGER IF EXISTS "TR_TenantApprovals_AutomationMutation_INSERT";

DROP TRIGGER IF EXISTS "TR_TenantApprovals_AutomationRead_INSERT";

DROP TRIGGER IF EXISTS "TR_TenantApprovals_IndependentTask_Insert";

DROP TRIGGER IF EXISTS "TR_TenantApprovals_IndependentTask_Update";

DROP TRIGGER IF EXISTS "TR_TenantApprovals_ReportSourceRead_Insert";

DROP TRIGGER IF EXISTS "TR_TenantAutomations_Owner_Update";

DROP TRIGGER IF EXISTS "TR_TenantCompletion_Automation_Insert";

DROP TRIGGER IF EXISTS "TR_TenantCompletion_Automation_Update";

DROP TRIGGER IF EXISTS "TR_TenantCompletion_Occurrence_Insert";

DROP TRIGGER IF EXISTS "TR_TenantCompletion_Occurrence_Update";

DROP TRIGGER IF EXISTS "TR_TenantConversation_Automation_Insert";

DROP TRIGGER IF EXISTS "TR_TenantConversation_Automation_Update";

DROP TRIGGER IF EXISTS "TR_TenantConversation_Occurrence_Insert";

DROP TRIGGER IF EXISTS "TR_TenantConversation_Occurrence_Update";

DROP TRIGGER IF EXISTS "TR_TenantCore_Delivery_Insert";

DROP TRIGGER IF EXISTS "TR_TenantCore_Delivery_Update";

DROP TRIGGER IF EXISTS "TR_TenantCore_Fact_Insert";

DROP TRIGGER IF EXISTS "TR_TenantCore_RunCausation_Insert";

DROP TRIGGER IF EXISTS "TR_TenantEmail_Occurrence_Insert";

DROP TRIGGER IF EXISTS "TR_TenantEvent_Bucket_Insert";

DROP TRIGGER IF EXISTS "TR_TenantEvent_Bucket_Update";

DROP TRIGGER IF EXISTS "TR_TenantEvent_Child_Update";

DROP TRIGGER IF EXISTS "TR_TenantEvent_Occurrence_Insert";

DROP TRIGGER IF EXISTS "TR_TenantOccurrences_Source_Insert";

DROP TRIGGER IF EXISTS "TR_TenantOccurrences_Source_Update";

DROP TRIGGER IF EXISTS "TR_TenantOperationReceipts_AutomationInvocation_INSERT";

DROP TRIGGER IF EXISTS "TR_TenantOperationReceipts_AutomationMutation_INSERT";

DROP TRIGGER IF EXISTS "TR_TenantWebhook_Delivery_Insert";

DROP TRIGGER IF EXISTS "TR_TenantWebhook_Delivery_Update";

DROP TRIGGER IF EXISTS "TR_TenantWebhook_Receipt_Insert";

DROP TRIGGER IF EXISTS "WorkflowAutomationDestination_INSERT";

DROP TRIGGER IF EXISTS "WorkflowAutomationDestination_UPDATE";

DROP TRIGGER IF EXISTS "WorkflowExecutionImmutable";

DROP TRIGGER IF EXISTS "WorkflowExecutionSourceGuard";

DROP TRIGGER IF EXISTS "WorkflowOccurrenceInsert";

DROP TRIGGER IF EXISTS "WorkflowOccurrenceLegacyUpdate";

DROP TRIGGER IF EXISTS "WorkflowOccurrenceUpdate";

CREATE TABLE "WorkflowRebuild_Automations" (
    "AutomationId" TEXT NOT NULL CONSTRAINT "PK_Automations" PRIMARY KEY,
    "TriggerMode" INTEGER NOT NULL,
    "PresetId" TEXT NULL,
    "PresetVersion" INTEGER NULL,
    "ExecutionTargetKind" INTEGER NOT NULL,
    "TargetSessionId" TEXT NULL,
    "WorkflowTargetJson" TEXT NULL,
    "ReportToSessionId" TEXT NULL,
    "AgentInstanceId" TEXT NOT NULL,
    "ProfileId" TEXT NULL,
    "TenantId" TEXT NULL,
    "ExecutorPrincipalId" TEXT NULL,
    "PinnedSpaceId" TEXT NULL,
    "PinnedEnvironmentId" TEXT NULL,
    "Status" INTEGER NOT NULL,
    "Instructions" TEXT NOT NULL,
    "Name" TEXT NOT NULL,
    "NextOccurrenceAtUtc" INTEGER NULL,
    "ExpiresAtUtc" INTEGER NULL,
    "OccurrenceCount" INTEGER NOT NULL,
    "Revision" INTEGER NOT NULL,
    "AuthorizationOrigin" INTEGER NOT NULL,
    "SourceSessionId" TEXT NULL,
    "SourceEventId" TEXT NULL,
    "CreatedAtUtc" INTEGER NOT NULL,
    "UpdatedAtUtc" INTEGER NOT NULL,
    "SuspensionReason" TEXT NULL,
    "ModelPreferenceJson" TEXT NULL,
    "RequiresVision" INTEGER NOT NULL,
    "RequiresTools" INTEGER NOT NULL,
    CONSTRAINT "CK_Automations_Destination" CHECK (((ExecutionTargetKind = 0 AND TargetSessionId IS NULL) OR (ExecutionTargetKind = 1 AND TargetSessionId IS NOT NULL AND ReportToSessionId IS NULL)) AND WorkflowTargetJson IS NULL OR COALESCE((ExecutionTargetKind = 2 AND TenantId IS NOT NULL AND TargetSessionId IS NULL AND ReportToSessionId IS NULL AND json_valid(WorkflowTargetJson) AND json_type(WorkflowTargetJson) = 'object' AND length(json_extract(WorkflowTargetJson,'$.WorkflowId')) = 36 AND json_extract(WorkflowTargetJson,'$.Version') >= 1 AND length(json_extract(WorkflowTargetJson,'$.GraphHash')) = 64 AND json_type(WorkflowTargetJson,'$.InputsJson') = 'text' AND json_valid(json_extract(WorkflowTargetJson,'$.InputsJson')) AND length(CAST(WorkflowTargetJson AS BLOB)) <= 32768),0)),
    CONSTRAINT "CK_Automations_Mode" CHECK (TriggerMode IN (0,1)),
    CONSTRAINT "CK_Automations_Preset" CHECK ((PresetId IS NULL AND PresetVersion IS NULL) OR (PresetId IS NOT NULL AND PresetVersion >= 1)),
    CONSTRAINT "CK_Automations_SpacePins" CHECK ((PinnedSpaceId IS NULL AND PinnedEnvironmentId IS NULL) OR (TenantId IS NOT NULL AND PinnedSpaceId IS NOT NULL AND lower(PinnedSpaceId) <> '00000000-0000-0000-0000-000000000000' AND (PinnedEnvironmentId IS NULL OR lower(PinnedEnvironmentId) <> '00000000-0000-0000-0000-000000000000'))),
    CONSTRAINT "CK_Automations_TenantOwner" CHECK ((TenantId IS NULL AND ExecutorPrincipalId IS NULL AND ProfileId IS NOT NULL) OR (TenantId IS NOT NULL AND ExecutorPrincipalId IS NOT NULL AND ProfileId IS NULL)),
    CONSTRAINT "FK_Automations_SpaceEnvironments_TenantId_PinnedSpaceId_PinnedEnvironmentId" FOREIGN KEY ("TenantId", "PinnedSpaceId", "PinnedEnvironmentId") REFERENCES "SpaceEnvironments" ("TenantId", "SpaceId", "EnvironmentId") ON DELETE RESTRICT,
    CONSTRAINT "FK_Automations_Spaces_TenantId_PinnedSpaceId" FOREIGN KEY ("TenantId", "PinnedSpaceId") REFERENCES "Spaces" ("TenantId", "SpaceId") ON DELETE RESTRICT,
    CONSTRAINT "FK_Automations_SystemIdentities_TenantId_AgentInstanceId_ExecutorPrincipalId" FOREIGN KEY ("TenantId", "AgentInstanceId", "ExecutorPrincipalId") REFERENCES "SystemIdentities" ("TenantId", "AgentInstanceId", "PrincipalId") ON DELETE RESTRICT
);

INSERT INTO "WorkflowRebuild_Automations" ("AutomationId","AgentInstanceId","AuthorizationOrigin","CreatedAtUtc","ExecutionTargetKind","ExecutorPrincipalId","ExpiresAtUtc","Instructions","Name","NextOccurrenceAtUtc","OccurrenceCount","PresetId","PresetVersion","ProfileId","ReportToSessionId","RequiresTools","RequiresVision","Revision","SourceEventId","SourceSessionId","Status","SuspensionReason","TargetSessionId","TenantId","TriggerMode","UpdatedAtUtc","PinnedSpaceId","PinnedEnvironmentId","ModelPreferenceJson") SELECT "AutomationId","AgentInstanceId","AuthorizationOrigin","CreatedAtUtc","ExecutionTargetKind","ExecutorPrincipalId","ExpiresAtUtc","Instructions","Name","NextOccurrenceAtUtc","OccurrenceCount","PresetId","PresetVersion","ProfileId","ReportToSessionId","RequiresTools","RequiresVision","Revision","SourceEventId","SourceSessionId","Status","SuspensionReason","TargetSessionId","TenantId","TriggerMode","UpdatedAtUtc","PinnedSpaceId","PinnedEnvironmentId","ModelPreferenceJson" FROM "Automations";

DROP TABLE "Automations";
ALTER TABLE "WorkflowRebuild_Automations" RENAME TO "Automations";

CREATE INDEX "IX_Automations_AgentInstanceId_ProfileId_Status" ON "Automations" ("AgentInstanceId", "ProfileId", "Status");

CREATE INDEX "IX_Automations_Status_NextOccurrenceAtUtc_AutomationId" ON "Automations" ("Status", "NextOccurrenceAtUtc", "AutomationId");

CREATE INDEX "IX_Automations_TenantId_AgentInstanceId_ExecutorPrincipalId" ON "Automations" ("TenantId", "AgentInstanceId", "ExecutorPrincipalId");

CREATE INDEX "IX_Automations_TenantId_AgentInstanceId_Status" ON "Automations" ("TenantId", "AgentInstanceId", "Status");

CREATE INDEX "IX_Automations_TenantId_PinnedSpaceId_PinnedEnvironmentId" ON "Automations" ("TenantId", "PinnedSpaceId", "PinnedEnvironmentId");

CREATE TABLE "WorkflowRebuild_TriggerOccurrences" (
    "OccurrenceId" TEXT NOT NULL CONSTRAINT "PK_TriggerOccurrences" PRIMARY KEY,
    "TriggerId" TEXT NULL,
    "ExecutionTargetKind" INTEGER NOT NULL,
    "TargetSessionId" TEXT NULL,
    "WorkflowTargetJson" TEXT NULL,
    "ReportToSessionId" TEXT NULL,
    "DedupeKey" TEXT NOT NULL,
    "AutomationId" TEXT NULL,
    "AgentInstanceId" TEXT NOT NULL,
    "ProfileId" TEXT NULL,
    "TenantId" TEXT NULL,
    "ExecutorPrincipalId" TEXT NULL,
    "PinnedSpaceId" TEXT NULL,
    "PinnedEnvironmentId" TEXT NULL,
    "SourceKind" INTEGER NOT NULL,
    "ScheduledAtUtc" INTEGER NULL,
    "ObservedAtUtc" INTEGER NOT NULL,
    "AdmittedAtUtc" INTEGER NOT NULL,
    "EvidenceJson" TEXT NOT NULL,
    "SourceEventId" TEXT NULL,
    "TriggerRevision" INTEGER NULL,
    "Disposition" INTEGER NOT NULL,
    "DispositionReason" TEXT NULL,
    "RoutingRevision" INTEGER NOT NULL,
    "RoutingUpdatedAtUtc" INTEGER NULL,
    "ClaimId" TEXT NULL,
    "ClaimLeaseExpiresAtUtc" INTEGER NULL,
    "ExecutionSessionId" TEXT NULL,
    "AcceptedAgentRunId" TEXT NULL,
    "AcceptedWorkflowExecutionId" TEXT NULL,
    "WorkflowInitiatorPrincipalId" TEXT NULL,
    "LiveSessionId" TEXT NULL,
    "LiveEvaluationCompletedAtUtc" INTEGER NULL,
    "ModelOfferingId" TEXT NULL,
    "ModelConnectionId" TEXT NULL,
    "ExternalModelId" TEXT NULL,
    "ModelReasoningEffort" TEXT NULL,
    "ModelSource" INTEGER NULL,
    "ModelBindingJson" TEXT NULL,
    "ModelDecisionJson" TEXT NULL,
    CONSTRAINT "CK_TriggerOccurrences_Destination" CHECK (((ExecutionTargetKind = 0 AND TargetSessionId IS NULL) OR (ExecutionTargetKind = 1 AND TargetSessionId IS NOT NULL AND ReportToSessionId IS NULL)) AND WorkflowTargetJson IS NULL OR COALESCE((ExecutionTargetKind = 2 AND TenantId IS NOT NULL AND TargetSessionId IS NULL AND ReportToSessionId IS NULL AND json_valid(WorkflowTargetJson) AND json_type(WorkflowTargetJson) = 'object' AND length(json_extract(WorkflowTargetJson,'$.WorkflowId')) = 36 AND json_extract(WorkflowTargetJson,'$.Version') >= 1 AND length(json_extract(WorkflowTargetJson,'$.GraphHash')) = 64 AND json_type(WorkflowTargetJson,'$.InputsJson') = 'text' AND json_valid(json_extract(WorkflowTargetJson,'$.InputsJson')) AND length(CAST(WorkflowTargetJson AS BLOB)) <= 32768),0)),
    CONSTRAINT "CK_TriggerOccurrences_ExecutionLink" CHECK (((ExecutionSessionId IS NULL AND AcceptedAgentRunId IS NULL) OR (ExecutionSessionId IS NOT NULL AND LiveSessionId IS NULL AND AcceptedAgentRunId IS NOT NULL) OR (ExecutionSessionId IS NULL AND LiveSessionId IS NOT NULL AND AcceptedAgentRunId IS NOT NULL AND LiveEvaluationCompletedAtUtc IS NOT NULL)) AND AcceptedWorkflowExecutionId IS NULL AND (ExecutionTargetKind <> 2 OR Disposition <> 6) OR (ExecutionTargetKind = 2 AND AcceptedWorkflowExecutionId IS NOT NULL AND Disposition = 6 AND ExecutionSessionId IS NULL AND AcceptedAgentRunId IS NULL AND LiveSessionId IS NULL AND ModelOfferingId IS NULL)),
    CONSTRAINT "CK_TriggerOccurrences_SpacePins" CHECK ((PinnedSpaceId IS NULL AND PinnedEnvironmentId IS NULL) OR (TenantId IS NOT NULL AND PinnedSpaceId IS NOT NULL AND lower(PinnedSpaceId) <> '00000000-0000-0000-0000-000000000000' AND (PinnedEnvironmentId IS NULL OR lower(PinnedEnvironmentId) <> '00000000-0000-0000-0000-000000000000'))),
    CONSTRAINT "CK_TriggerOccurrences_TenantOwner" CHECK ((TenantId IS NULL AND ExecutorPrincipalId IS NULL AND ProfileId IS NOT NULL) OR (TenantId IS NOT NULL AND ExecutorPrincipalId IS NOT NULL AND ProfileId IS NULL)),
    CONSTRAINT "FK_TriggerOccurrences_AgentRuns_AcceptedAgentRunId" FOREIGN KEY ("AcceptedAgentRunId") REFERENCES "AgentRuns" ("AgentRunId") ON DELETE RESTRICT,
    CONSTRAINT "FK_TriggerOccurrences_Sessions_ExecutionSessionId" FOREIGN KEY ("ExecutionSessionId") REFERENCES "Sessions" ("SessionId") ON DELETE RESTRICT,
    CONSTRAINT "FK_TriggerOccurrences_Sessions_LiveSessionId" FOREIGN KEY ("LiveSessionId") REFERENCES "Sessions" ("SessionId") ON DELETE RESTRICT,
    CONSTRAINT "FK_TriggerOccurrences_SpaceEnvironments_TenantId_PinnedSpaceId_PinnedEnvironmentId" FOREIGN KEY ("TenantId", "PinnedSpaceId", "PinnedEnvironmentId") REFERENCES "SpaceEnvironments" ("TenantId", "SpaceId", "EnvironmentId") ON DELETE RESTRICT,
    CONSTRAINT "FK_TriggerOccurrences_Spaces_TenantId_PinnedSpaceId" FOREIGN KEY ("TenantId", "PinnedSpaceId") REFERENCES "Spaces" ("TenantId", "SpaceId") ON DELETE RESTRICT,
    CONSTRAINT "FK_TriggerOccurrences_SystemIdentities_TenantId_AgentInstanceId_ExecutorPrincipalId" FOREIGN KEY ("TenantId", "AgentInstanceId", "ExecutorPrincipalId") REFERENCES "SystemIdentities" ("TenantId", "AgentInstanceId", "PrincipalId") ON DELETE RESTRICT,
    CONSTRAINT "FK_TriggerOccurrences_TenantWorkflowExecutions_TenantId_AcceptedWorkflowExecutionId" FOREIGN KEY ("TenantId", "AcceptedWorkflowExecutionId") REFERENCES "TenantWorkflowExecutions" ("TenantId", "ExecutionId") ON DELETE RESTRICT
);

INSERT INTO "WorkflowRebuild_TriggerOccurrences" ("OccurrenceId","AcceptedAgentRunId","AdmittedAtUtc","AgentInstanceId","AutomationId","ClaimId","ClaimLeaseExpiresAtUtc","DedupeKey","Disposition","DispositionReason","EvidenceJson","ExecutionSessionId","ExecutionTargetKind","ExecutorPrincipalId","LiveEvaluationCompletedAtUtc","LiveSessionId","ModelOfferingId","ExternalModelId","ModelConnectionId","ModelReasoningEffort","ModelSource","ObservedAtUtc","ProfileId","ReportToSessionId","RoutingRevision","RoutingUpdatedAtUtc","ScheduledAtUtc","SourceEventId","SourceKind","TargetSessionId","TenantId","TriggerId","TriggerRevision","PinnedSpaceId","PinnedEnvironmentId","ModelBindingJson","ModelDecisionJson") SELECT "OccurrenceId","AcceptedAgentRunId","AdmittedAtUtc","AgentInstanceId","AutomationId","ClaimId","ClaimLeaseExpiresAtUtc","DedupeKey","Disposition","DispositionReason","EvidenceJson","ExecutionSessionId","ExecutionTargetKind","ExecutorPrincipalId","LiveEvaluationCompletedAtUtc","LiveSessionId","ModelOfferingId","ExternalModelId","ModelConnectionId","ModelReasoningEffort","ModelSource","ObservedAtUtc","ProfileId","ReportToSessionId","RoutingRevision","RoutingUpdatedAtUtc","ScheduledAtUtc","SourceEventId","SourceKind","TargetSessionId","TenantId","TriggerId","TriggerRevision","PinnedSpaceId","PinnedEnvironmentId","ModelBindingJson","ModelDecisionJson" FROM "TriggerOccurrences";

DROP TABLE "TriggerOccurrences";
ALTER TABLE "WorkflowRebuild_TriggerOccurrences" RENAME TO "TriggerOccurrences";

CREATE UNIQUE INDEX "IX_TriggerOccurrences_AcceptedAgentRunId" ON "TriggerOccurrences" ("AcceptedAgentRunId") WHERE AcceptedAgentRunId IS NOT NULL;

CREATE UNIQUE INDEX "IX_TriggerOccurrences_AgentInstanceId_ProfileId_DedupeKey" ON "TriggerOccurrences" ("AgentInstanceId", "ProfileId", "DedupeKey");

CREATE INDEX "IX_TriggerOccurrences_AgentInstanceId_ProfileId_Disposition" ON "TriggerOccurrences" ("AgentInstanceId", "ProfileId", "Disposition");

CREATE INDEX "IX_TriggerOccurrences_Disposition_ClaimLeaseExpiresAtUtc" ON "TriggerOccurrences" ("Disposition", "ClaimLeaseExpiresAtUtc");

CREATE INDEX "IX_TriggerOccurrences_ExecutionSessionId" ON "TriggerOccurrences" ("ExecutionSessionId");

CREATE INDEX "IX_TriggerOccurrences_LiveSessionId" ON "TriggerOccurrences" ("LiveSessionId");

CREATE UNIQUE INDEX "IX_TriggerOccurrences_TenantId_AcceptedWorkflowExecutionId" ON "TriggerOccurrences" ("TenantId", "AcceptedWorkflowExecutionId") WHERE AcceptedWorkflowExecutionId IS NOT NULL;

CREATE UNIQUE INDEX "IX_TriggerOccurrences_TenantId_AgentInstanceId_DedupeKey" ON "TriggerOccurrences" ("TenantId", "AgentInstanceId", "DedupeKey") WHERE TenantId IS NOT NULL;

CREATE INDEX "IX_TriggerOccurrences_TenantId_AgentInstanceId_Disposition" ON "TriggerOccurrences" ("TenantId", "AgentInstanceId", "Disposition");

CREATE INDEX "IX_TriggerOccurrences_TenantId_AgentInstanceId_ExecutorPrincipalId" ON "TriggerOccurrences" ("TenantId", "AgentInstanceId", "ExecutorPrincipalId");

CREATE INDEX "IX_TriggerOccurrences_TenantId_PinnedSpaceId_PinnedEnvironmentId" ON "TriggerOccurrences" ("TenantId", "PinnedSpaceId", "PinnedEnvironmentId");

CREATE TRIGGER TR_SpaceAutomationPins_Approvals_INSERT BEFORE INSERT ON TenantApprovalRequests BEGIN
SELECT RAISE(ABORT,'Prepared context pins require their closed consent schema') WHERE json_type(NEW.PayloadJson,'$.Operation.AutomationAdmission.Current')='object' AND NOT COALESCE(((json_extract(NEW.PayloadJson,'$.Operation.AutomationAdmission.Current.PinnedSpaceId') IS NULL AND json_extract(NEW.PayloadJson,'$.Operation.AutomationAdmission.Current.PinnedEnvironmentId') IS NULL AND json_extract(NEW.PayloadJson,'$.Operation.AutomationAdmission.Current.SchemaVersion') IS NOT 5) OR (json_extract(NEW.PayloadJson,'$.Operation.AutomationAdmission.Current.SchemaVersion')=5 AND json_type(NEW.PayloadJson,'$.Operation.AutomationAdmission.Current.PinnedSpaceId')='text' AND length(json_extract(NEW.PayloadJson,'$.Operation.AutomationAdmission.Current.PinnedSpaceId'))=36 AND json_extract(NEW.PayloadJson,'$.Operation.AutomationAdmission.Current.PinnedSpaceId')<>'00000000-0000-0000-0000-000000000000' AND (json_extract(NEW.PayloadJson,'$.Operation.AutomationAdmission.Current.PinnedEnvironmentId') IS NULL OR (json_type(NEW.PayloadJson,'$.Operation.AutomationAdmission.Current.PinnedEnvironmentId')='text' AND length(json_extract(NEW.PayloadJson,'$.Operation.AutomationAdmission.Current.PinnedEnvironmentId'))=36 AND json_extract(NEW.PayloadJson,'$.Operation.AutomationAdmission.Current.PinnedEnvironmentId')<>'00000000-0000-0000-0000-000000000000')))),0);
SELECT RAISE(ABORT,'Prepared context pins require their closed consent schema') WHERE json_type(NEW.PayloadJson,'$.Operation.AutomationAdmission.Next')='object' AND NOT COALESCE(((json_extract(NEW.PayloadJson,'$.Operation.AutomationAdmission.Next.PinnedSpaceId') IS NULL AND json_extract(NEW.PayloadJson,'$.Operation.AutomationAdmission.Next.PinnedEnvironmentId') IS NULL AND json_extract(NEW.PayloadJson,'$.Operation.AutomationAdmission.Next.SchemaVersion') IS NOT 5) OR (json_extract(NEW.PayloadJson,'$.Operation.AutomationAdmission.Next.SchemaVersion')=5 AND json_type(NEW.PayloadJson,'$.Operation.AutomationAdmission.Next.PinnedSpaceId')='text' AND length(json_extract(NEW.PayloadJson,'$.Operation.AutomationAdmission.Next.PinnedSpaceId'))=36 AND json_extract(NEW.PayloadJson,'$.Operation.AutomationAdmission.Next.PinnedSpaceId')<>'00000000-0000-0000-0000-000000000000' AND (json_extract(NEW.PayloadJson,'$.Operation.AutomationAdmission.Next.PinnedEnvironmentId') IS NULL OR (json_type(NEW.PayloadJson,'$.Operation.AutomationAdmission.Next.PinnedEnvironmentId')='text' AND length(json_extract(NEW.PayloadJson,'$.Operation.AutomationAdmission.Next.PinnedEnvironmentId'))=36 AND json_extract(NEW.PayloadJson,'$.Operation.AutomationAdmission.Next.PinnedEnvironmentId')<>'00000000-0000-0000-0000-000000000000')))),0);
SELECT RAISE(ABORT,'Prepared context pins require their closed consent schema') WHERE json_type(NEW.PayloadJson,'$.Operation.AutomationMutation.Current')='object' AND NOT COALESCE(((json_extract(NEW.PayloadJson,'$.Operation.AutomationMutation.Current.PinnedSpaceId') IS NULL AND json_extract(NEW.PayloadJson,'$.Operation.AutomationMutation.Current.PinnedEnvironmentId') IS NULL AND json_extract(NEW.PayloadJson,'$.Operation.AutomationMutation.Current.SchemaVersion') IS NOT 5) OR (json_extract(NEW.PayloadJson,'$.Operation.AutomationMutation.Current.SchemaVersion')=5 AND json_type(NEW.PayloadJson,'$.Operation.AutomationMutation.Current.PinnedSpaceId')='text' AND length(json_extract(NEW.PayloadJson,'$.Operation.AutomationMutation.Current.PinnedSpaceId'))=36 AND json_extract(NEW.PayloadJson,'$.Operation.AutomationMutation.Current.PinnedSpaceId')<>'00000000-0000-0000-0000-000000000000' AND (json_extract(NEW.PayloadJson,'$.Operation.AutomationMutation.Current.PinnedEnvironmentId') IS NULL OR (json_type(NEW.PayloadJson,'$.Operation.AutomationMutation.Current.PinnedEnvironmentId')='text' AND length(json_extract(NEW.PayloadJson,'$.Operation.AutomationMutation.Current.PinnedEnvironmentId'))=36 AND json_extract(NEW.PayloadJson,'$.Operation.AutomationMutation.Current.PinnedEnvironmentId')<>'00000000-0000-0000-0000-000000000000')))),0);
SELECT RAISE(ABORT,'Prepared context pins require their closed consent schema') WHERE json_type(NEW.PayloadJson,'$.Operation.AutomationMutation.Proposed')='object' AND NOT COALESCE(((json_extract(NEW.PayloadJson,'$.Operation.AutomationMutation.Proposed.PinnedSpaceId') IS NULL AND json_extract(NEW.PayloadJson,'$.Operation.AutomationMutation.Proposed.PinnedEnvironmentId') IS NULL AND json_extract(NEW.PayloadJson,'$.Operation.AutomationMutation.Proposed.SchemaVersion') IS NOT 5) OR (json_extract(NEW.PayloadJson,'$.Operation.AutomationMutation.Proposed.SchemaVersion')=5 AND json_type(NEW.PayloadJson,'$.Operation.AutomationMutation.Proposed.PinnedSpaceId')='text' AND length(json_extract(NEW.PayloadJson,'$.Operation.AutomationMutation.Proposed.PinnedSpaceId'))=36 AND json_extract(NEW.PayloadJson,'$.Operation.AutomationMutation.Proposed.PinnedSpaceId')<>'00000000-0000-0000-0000-000000000000' AND (json_extract(NEW.PayloadJson,'$.Operation.AutomationMutation.Proposed.PinnedEnvironmentId') IS NULL OR (json_type(NEW.PayloadJson,'$.Operation.AutomationMutation.Proposed.PinnedEnvironmentId')='text' AND length(json_extract(NEW.PayloadJson,'$.Operation.AutomationMutation.Proposed.PinnedEnvironmentId'))=36 AND json_extract(NEW.PayloadJson,'$.Operation.AutomationMutation.Proposed.PinnedEnvironmentId')<>'00000000-0000-0000-0000-000000000000')))),0);
SELECT RAISE(ABORT,'Prepared occurrence pins must match its exact context') WHERE json_type(NEW.PayloadJson,'$.Operation.AutomationAdmission')='object' AND NOT COALESCE(
    json_extract(NEW.PayloadJson,'$.Operation.AutomationAdmission.Current.PinnedSpaceId') IS json_extract(NEW.PayloadJson,'$.Operation.AutomationAdmission.Next.PinnedSpaceId')
    AND json_extract(NEW.PayloadJson,'$.Operation.AutomationAdmission.Current.PinnedEnvironmentId') IS json_extract(NEW.PayloadJson,'$.Operation.AutomationAdmission.Next.PinnedEnvironmentId')
    AND json_extract(NEW.PayloadJson,'$.Operation.AutomationAdmission.Current.PinnedSpaceId') IS json_extract(NEW.PayloadJson,'$.Operation.AutomationAdmission.Occurrence.PinnedSpaceId')
    AND json_extract(NEW.PayloadJson,'$.Operation.AutomationAdmission.Current.PinnedEnvironmentId') IS json_extract(NEW.PayloadJson,'$.Operation.AutomationAdmission.Occurrence.PinnedEnvironmentId')
    AND json_extract(NEW.PayloadJson,'$.Operation.SpaceContext.SpaceId') IS coalesce(json_extract(NEW.PayloadJson,'$.Operation.AutomationAdmission.Current.PinnedSpaceId'),json_extract(NEW.PayloadJson,'$.Operation.SpaceContext.PrivateSpaceId'))
    AND json_extract(NEW.PayloadJson,'$.Operation.SpaceContext.EnvironmentId') IS json_extract(NEW.PayloadJson,'$.Operation.AutomationAdmission.Current.PinnedEnvironmentId'),0);
SELECT RAISE(ABORT,'Prepared context pins differ from their retained source') WHERE json_type(NEW.PayloadJson,'$.Operation.AutomationAdmission')='object' AND (
    NOT EXISTS(SELECT 1 FROM Automations a WHERE a.TenantId=NEW.TenantId AND a.AutomationId=NEW.AutomationId
        AND lower(a.PinnedSpaceId) IS json_extract(NEW.PayloadJson,'$.Operation.AutomationAdmission.Current.PinnedSpaceId')
        AND lower(a.PinnedEnvironmentId) IS json_extract(NEW.PayloadJson,'$.Operation.AutomationAdmission.Current.PinnedEnvironmentId'))
    OR NOT EXISTS(SELECT 1 FROM TriggerOccurrences o WHERE o.TenantId=NEW.TenantId AND o.OccurrenceId=json_extract(NEW.PayloadJson,'$.Operation.AutomationAdmission.Occurrence.OccurrenceId')
        AND lower(o.PinnedSpaceId) IS json_extract(NEW.PayloadJson,'$.Operation.AutomationAdmission.Current.PinnedSpaceId')
        AND lower(o.PinnedEnvironmentId) IS json_extract(NEW.PayloadJson,'$.Operation.AutomationAdmission.Current.PinnedEnvironmentId')));
SELECT RAISE(ABORT,'Prepared authoring source pins changed') WHERE json_type(NEW.PayloadJson,'$.Operation.AutomationMutation.Current')='object' AND NOT EXISTS(
    SELECT 1 FROM Automations a WHERE a.TenantId=NEW.TenantId AND lower(a.AutomationId)=json_extract(NEW.PayloadJson,'$.Operation.AutomationMutation.Current.AutomationId')
        AND lower(a.PinnedSpaceId) IS json_extract(NEW.PayloadJson,'$.Operation.AutomationMutation.Current.PinnedSpaceId')
        AND lower(a.PinnedEnvironmentId) IS json_extract(NEW.PayloadJson,'$.Operation.AutomationMutation.Current.PinnedEnvironmentId'));
END;

CREATE TRIGGER TR_SpaceAutomationPins_Automations_INSERT BEFORE INSERT ON Automations WHEN NEW.PinnedSpaceId IS NOT NULL AND (1) BEGIN SELECT RAISE(ABORT,'Pinned Automation context is unavailable') WHERE NOT (EXISTS(SELECT 1 FROM Spaces s JOIN SpaceMemberships m ON m.TenantId=s.TenantId AND m.SpaceId=s.SpaceId
    JOIN SystemIdentities i ON i.TenantId=m.TenantId AND i.AgentInstanceId=m.InstanceId AND i.Status=0
    WHERE s.TenantId=NEW.TenantId AND lower(s.SpaceId)=lower(NEW.PinnedSpaceId) AND s.DeletedAt IS NULL AND m.InstanceId=NEW.AgentInstanceId
        AND (s.Kind=1 OR s.OwnerInstanceId=NEW.AgentInstanceId) AND (NEW.PinnedEnvironmentId IS NULL OR (s.EnvironmentsEnabled=1
            AND EXISTS(SELECT 1 FROM SpaceEnvironments e WHERE e.TenantId=s.TenantId AND e.SpaceId=s.SpaceId
                AND lower(e.EnvironmentId)=lower(NEW.PinnedEnvironmentId) AND e.DeletedAt IS NULL))))); END;

CREATE TRIGGER TR_SpaceAutomationPins_Automations_UPDATE BEFORE UPDATE ON Automations WHEN NEW.PinnedSpaceId IS NOT NULL AND (NEW.PinnedSpaceId IS NOT OLD.PinnedSpaceId OR NEW.PinnedEnvironmentId IS NOT OLD.PinnedEnvironmentId OR NEW.Status=0 AND OLD.Status<>0) BEGIN SELECT RAISE(ABORT,'Pinned Automation context is unavailable') WHERE NOT (EXISTS(SELECT 1 FROM Spaces s JOIN SpaceMemberships m ON m.TenantId=s.TenantId AND m.SpaceId=s.SpaceId
    JOIN SystemIdentities i ON i.TenantId=m.TenantId AND i.AgentInstanceId=m.InstanceId AND i.Status=0
    WHERE s.TenantId=NEW.TenantId AND lower(s.SpaceId)=lower(NEW.PinnedSpaceId) AND s.DeletedAt IS NULL AND m.InstanceId=NEW.AgentInstanceId
        AND (s.Kind=1 OR s.OwnerInstanceId=NEW.AgentInstanceId) AND (NEW.PinnedEnvironmentId IS NULL OR (s.EnvironmentsEnabled=1
            AND EXISTS(SELECT 1 FROM SpaceEnvironments e WHERE e.TenantId=s.TenantId AND e.SpaceId=s.SpaceId
                AND lower(e.EnvironmentId)=lower(NEW.PinnedEnvironmentId) AND e.DeletedAt IS NULL))))); END;

CREATE TRIGGER TR_SpaceAutomationPins_CoreFact_INSERT BEFORE INSERT ON CoreEvents WHEN NEW.TenantId IS NOT NULL BEGIN SELECT RAISE(ABORT,'Event receipt pins differ from their saved subscription') WHERE EXISTS(SELECT 1 FROM json_each(NEW.SubscriptionsJson) f WHERE NOT EXISTS(SELECT 1 FROM Automations a WHERE a.TenantId=NEW.TenantId AND a.AutomationId=json_extract(f.value,'$.AutomationId') AND lower(a.PinnedSpaceId) IS json_extract(f.value,'$.PinnedSpaceId') AND lower(a.PinnedEnvironmentId) IS json_extract(f.value,'$.PinnedEnvironmentId'))); END;

CREATE TRIGGER TR_SpaceAutomationPins_Occurrences_INSERT BEFORE INSERT ON TriggerOccurrences WHEN NEW.TenantId IS NOT NULL
BEGIN
    SELECT RAISE(ABORT,'Occurrence pins differ from their saved source') WHERE NEW.AutomationId IS NOT NULL AND NOT EXISTS(
        SELECT 1 FROM Automations a WHERE a.AutomationId=NEW.AutomationId AND a.TenantId=NEW.TenantId
            AND a.PinnedSpaceId IS NEW.PinnedSpaceId AND a.PinnedEnvironmentId IS NEW.PinnedEnvironmentId);
    SELECT RAISE(ABORT,'Occurrence pins differ from their admitted Run') WHERE EXISTS(SELECT 1 FROM AgentRuns r
        WHERE r.AgentRunId=NEW.AcceptedAgentRunId AND NOT (json_extract(r.PayloadJson,'$.admission.configuration.spaceContext.spaceId') IS coalesce(lower(NEW.PinnedSpaceId),json_extract(r.PayloadJson,'$.admission.configuration.spaceContext.privateSpaceId'))
AND json_extract(r.PayloadJson,'$.admission.configuration.spaceContext.environmentId') IS lower(NEW.PinnedEnvironmentId)));
END;

CREATE TRIGGER TR_SpaceAutomationPins_Occurrences_UPDATE BEFORE UPDATE ON TriggerOccurrences WHEN NEW.TenantId IS NOT NULL
BEGIN
    SELECT RAISE(ABORT,'Occurrence context pins are immutable') WHERE NEW.PinnedSpaceId IS NOT OLD.PinnedSpaceId OR NEW.PinnedEnvironmentId IS NOT OLD.PinnedEnvironmentId;
    SELECT RAISE(ABORT,'Occurrence pins differ from their admitted Run') WHERE EXISTS(SELECT 1 FROM AgentRuns r
        WHERE r.AgentRunId=NEW.AcceptedAgentRunId AND NOT (json_extract(r.PayloadJson,'$.admission.configuration.spaceContext.spaceId') IS coalesce(lower(NEW.PinnedSpaceId),json_extract(r.PayloadJson,'$.admission.configuration.spaceContext.privateSpaceId'))
AND json_extract(r.PayloadJson,'$.admission.configuration.spaceContext.environmentId') IS lower(NEW.PinnedEnvironmentId)));
END;

CREATE TRIGGER TR_SpaceAutomationPins_Runs_INSERT BEFORE INSERT ON AgentRuns WHEN NEW.TenantId IS NOT NULL
    AND json_type(NEW.PayloadJson,'$.admission.activation.triggerOccurrenceId')='text'
BEGIN
    SELECT RAISE(ABORT,'Run context differs from its frozen occurrence pins') WHERE EXISTS(SELECT 1 FROM TriggerOccurrences o
        WHERE o.OccurrenceId=json_extract(NEW.PayloadJson,'$.admission.activation.triggerOccurrenceId') AND o.TenantId=NEW.TenantId
            AND (json_extract(NEW.PayloadJson,'$.admission.configuration.spaceContext.spaceId') IS NOT coalesce(lower(o.PinnedSpaceId),json_extract(NEW.PayloadJson,'$.admission.configuration.spaceContext.privateSpaceId'))
                OR json_extract(NEW.PayloadJson,'$.admission.configuration.spaceContext.environmentId') IS NOT lower(o.PinnedEnvironmentId)));
END;

CREATE TRIGGER TR_SpaceAutomationPins_Webhook_INSERT BEFORE INSERT ON ExternalEvents WHEN NEW.TenantId IS NOT NULL BEGIN SELECT RAISE(ABORT,'Event receipt pins differ from their saved subscription') WHERE EXISTS(SELECT 1 FROM json_each(NEW.SubscriptionsJson) f WHERE NOT EXISTS(SELECT 1 FROM Automations a WHERE a.TenantId=NEW.TenantId AND a.AutomationId=json_extract(f.value,'$.AutomationId') AND lower(a.PinnedSpaceId) IS json_extract(f.value,'$.PinnedSpaceId') AND lower(a.PinnedEnvironmentId) IS json_extract(f.value,'$.PinnedEnvironmentId'))); END;

CREATE TRIGGER TR_TenantApprovals_AutomationAdmission_INSERT BEFORE INSERT ON TenantApprovalRequests
WHEN NEW.AutomationId IS NOT NULL
BEGIN
SELECT RAISE(ABORT, 'Automation consent requires its exact reserved source') WHERE NOT EXISTS (
SELECT 1 FROM TriggerOccurrences o JOIN Automations a ON a.AutomationId = o.AutomationId
WHERE o.TenantId = NEW.TenantId AND a.TenantId = NEW.TenantId AND o.AutomationId = NEW.AutomationId
AND o.AgentInstanceId = NEW.InstanceId AND a.AgentInstanceId = NEW.InstanceId
AND o.ExecutorPrincipalId = NEW.ExecutorPrincipalId AND a.ExecutorPrincipalId = NEW.ExecutorPrincipalId
AND o.OccurrenceId = json_extract(NEW.PayloadJson, '$.Operation.AutomationAdmission.Occurrence.OccurrenceId')
AND a.Revision = json_extract(NEW.PayloadJson, '$.Operation.AutomationAdmission.Next.Revision')
AND a.Name IS json_extract(NEW.PayloadJson, '$.Operation.AutomationAdmission.Current.Name')
AND a.Instructions IS json_extract(NEW.PayloadJson, '$.Operation.AutomationAdmission.Current.Instructions')
AND a.OccurrenceCount = json_extract(NEW.PayloadJson, '$.Operation.AutomationAdmission.Next.OccurrenceCount')
AND a.Status = json_extract(NEW.PayloadJson, '$.Operation.AutomationAdmission.Next.Status')
AND json_extract(o.EvidenceJson, '$.instructions') IS a.Instructions
AND json_extract(o.EvidenceJson, '$.automationRevision') = json_extract(NEW.PayloadJson, '$.Operation.AutomationAdmission.Current.Revision')
AND o.ModelOfferingId IS json_extract(NEW.PayloadJson, '$.Operation.AutomationAdmission.Model.OfferingId')
AND o.ModelConnectionId IS json_extract(NEW.PayloadJson, '$.Operation.AutomationAdmission.Model.ConnectionId')
AND o.ExternalModelId IS json_extract(NEW.PayloadJson, '$.Operation.AutomationAdmission.Model.ExternalModelId')
AND o.ModelReasoningEffort IS json_extract(NEW.PayloadJson, '$.Operation.AutomationAdmission.Model.ReasoningEffort')
AND json_extract(o.EvidenceJson, '$.admissionApproval.requestId') IS lower(NEW.RequestId)
AND json_extract(o.EvidenceJson, '$.admissionApproval.requesterPrincipalId') IS lower(NEW.OriginalInitiatorPrincipalId)
AND o.SourceKind = json_extract(NEW.PayloadJson, '$.Operation.AutomationAdmission.Occurrence.SourceKind')
AND o.DedupeKey IS json_extract(NEW.PayloadJson, '$.Operation.AutomationAdmission.Occurrence.DedupeKey')
AND o.Disposition = 3 AND o.DispositionReason = 'admission_approval_pending' AND o.RoutingRevision = 1
AND o.ExecutionSessionId IS NULL AND o.AcceptedAgentRunId IS NULL
AND json_extract(o.EvidenceJson, '$.admissionApproval.sessionId') IS json_extract(NEW.PayloadJson, '$.Operation.AutomationAdmission.SessionId')
AND json_extract(o.EvidenceJson, '$.admissionApproval.runId') IS json_extract(NEW.PayloadJson, '$.Operation.AutomationAdmission.RunId')
AND json_extract(o.EvidenceJson, '$.admissionApproval.activationId') IS json_extract(NEW.PayloadJson, '$.Operation.AutomationAdmission.ActivationId')
AND json_extract(o.EvidenceJson, '$.admissionApproval.entryId') IS json_extract(NEW.PayloadJson, '$.Operation.AutomationAdmission.EntryId')
AND json_extract(o.EvidenceJson, '$.admissionApproval.responseId') IS json_extract(NEW.PayloadJson, '$.Operation.AutomationAdmission.ResponseId')
);
SELECT RAISE(ABORT, 'account-source Review requires actual frozen email facts') WHERE NOT COALESCE((NEW.Permission NOT IN ('credentials.read','credentials.use','email.read') OR (NEW.Kind = 0 AND NOT EXISTS (SELECT 1 FROM json_each(NEW.PayloadJson, '$.Operation.AutomationAdmission.EventSource.EventIds') source WHERE NOT EXISTS (SELECT 1 FROM CoreEvents fact WHERE fact.TenantId = NEW.TenantId AND fact.EventId IS source.value AND fact.EmailWatchEventId IS fact.EventId AND json_extract(fact.PayloadJson, '$.Key') IS 'email.message.received')))),0);
END;

CREATE TRIGGER TR_TenantApprovals_AutomationAdmission_UPDATE BEFORE UPDATE ON TenantApprovalRequests
WHEN NEW.AutomationId IS NOT NULL
BEGIN
SELECT RAISE(ABORT, 'Automation consent requires its exact reserved source') WHERE NOT EXISTS (
SELECT 1 FROM TriggerOccurrences o JOIN Automations a ON a.AutomationId = o.AutomationId
WHERE o.TenantId = NEW.TenantId AND a.TenantId = NEW.TenantId AND o.AutomationId = NEW.AutomationId
AND o.AgentInstanceId = NEW.InstanceId AND a.AgentInstanceId = NEW.InstanceId
AND o.ExecutorPrincipalId = NEW.ExecutorPrincipalId AND a.ExecutorPrincipalId = NEW.ExecutorPrincipalId
AND o.OccurrenceId = json_extract(NEW.PayloadJson, '$.Operation.AutomationAdmission.Occurrence.OccurrenceId')
AND json_extract(o.EvidenceJson, '$.admissionApproval.requestId') IS lower(NEW.RequestId)
AND json_extract(o.EvidenceJson, '$.admissionApproval.requesterPrincipalId') IS lower(NEW.OriginalInitiatorPrincipalId)
AND o.SourceKind = json_extract(NEW.PayloadJson, '$.Operation.AutomationAdmission.Occurrence.SourceKind')
AND o.DedupeKey IS json_extract(NEW.PayloadJson, '$.Operation.AutomationAdmission.Occurrence.DedupeKey')
AND o.Disposition = 3 AND o.DispositionReason = 'admission_approval_pending' AND o.RoutingRevision = 1
AND o.ExecutionSessionId IS NULL AND o.AcceptedAgentRunId IS NULL
AND json_extract(o.EvidenceJson, '$.admissionApproval.sessionId') IS json_extract(NEW.PayloadJson, '$.Operation.AutomationAdmission.SessionId')
AND json_extract(o.EvidenceJson, '$.admissionApproval.runId') IS json_extract(NEW.PayloadJson, '$.Operation.AutomationAdmission.RunId')
AND json_extract(o.EvidenceJson, '$.admissionApproval.activationId') IS json_extract(NEW.PayloadJson, '$.Operation.AutomationAdmission.ActivationId')
AND json_extract(o.EvidenceJson, '$.admissionApproval.entryId') IS json_extract(NEW.PayloadJson, '$.Operation.AutomationAdmission.EntryId')
AND json_extract(o.EvidenceJson, '$.admissionApproval.responseId') IS json_extract(NEW.PayloadJson, '$.Operation.AutomationAdmission.ResponseId')
);
SELECT RAISE(ABORT, 'account-source Review requires actual frozen email facts') WHERE NOT COALESCE((NEW.Permission NOT IN ('credentials.read','credentials.use','email.read') OR (NEW.Kind = 0 AND NOT EXISTS (SELECT 1 FROM json_each(NEW.PayloadJson, '$.Operation.AutomationAdmission.EventSource.EventIds') source WHERE NOT EXISTS (SELECT 1 FROM CoreEvents fact WHERE fact.TenantId = NEW.TenantId AND fact.EventId IS source.value AND fact.EmailWatchEventId IS fact.EventId AND json_extract(fact.PayloadJson, '$.Key') IS 'email.message.received')))),0);
END;

CREATE TRIGGER TR_TenantApprovals_AutomationMutation_INSERT BEFORE INSERT ON TenantApprovalRequests
WHEN json_type(NEW.PayloadJson,'$.Operation.AutomationMutation')='object' AND
 ((json_type(NEW.PayloadJson,'$.Operation.AutomationMutation.Current')='object' AND NOT EXISTS
   (SELECT 1 FROM Automations a WHERE a.TenantId=NEW.TenantId AND a.AgentInstanceId=NEW.InstanceId AND a.ExecutorPrincipalId=NEW.ExecutorPrincipalId
    AND a.AutomationId=NEW.ResourceId AND a.Revision=json_extract(NEW.PayloadJson,'$.Operation.AutomationMutation.Current.Revision')))
 OR (json_type(NEW.PayloadJson,'$.Operation.AutomationMutation.Current') IS NOT 'object' AND EXISTS
   (SELECT 1 FROM Automations a WHERE a.AutomationId=json_extract(NEW.PayloadJson,'$.Operation.AutomationMutation.Proposed.AutomationId'))))
BEGIN SELECT RAISE(ABORT,'Automation mutation requires exact owned current parent or fresh identity'); END;

CREATE TRIGGER TR_TenantApprovals_AutomationRead_INSERT BEFORE INSERT ON TenantApprovalRequests
WHEN json_extract(NEW.PayloadJson,'$.Operation.AutomationRead') IS NOT NULL AND (EXISTS (SELECT 1 FROM json_each(json_extract(NEW.PayloadJson,'$.Operation.AutomationRead.SourcesJson')) source
    WHERE NOT EXISTS (SELECT 1 FROM Automations a WHERE a.TenantId=NEW.TenantId AND a.AgentInstanceId=NEW.InstanceId AND a.ExecutorPrincipalId=NEW.ExecutorPrincipalId
      AND a.AutomationId=json_extract(source.value,'$.AutomationId') AND a.Revision=json_extract(source.value,'$.Revision')))
    OR (SELECT count(DISTINCT json_extract(value,'$.AutomationId')) FROM json_each(json_extract(NEW.PayloadJson,'$.Operation.AutomationRead.SourcesJson')))<>json_array_length(json_extract(NEW.PayloadJson,'$.Operation.AutomationRead.SourcesJson')))
BEGIN SELECT RAISE(ABORT,'Automation read consent requires exact owned current parents'); END;

CREATE TRIGGER TR_TenantApprovals_IndependentTask_Insert BEFORE INSERT ON TenantApprovalRequests
WHEN json_type(NEW.PayloadJson,'$.Operation.SandboxCommand.Evaluation') IS NOT 'object' AND NEW.OriginalInitiatorPrincipalId IS NULL AND json_extract(NEW.PayloadJson, '$.Operation.AutomationAdmission') IS NULL AND NEW.ContentRetiredAt IS NULL AND json_type(NEW.PayloadJson,'$.Operation.TaskDelegation') IS NOT 'object'
BEGIN
SELECT RAISE(ABORT, 'independent task consent requires its exact owned independent lineage') WHERE NOT EXISTS (
WITH RECURSIVE owned AS (
SELECT run.* FROM AgentRuns run
WHERE run.TenantId = NEW.TenantId AND run.AgentInstanceId = NEW.InstanceId AND run.ExecutorPrincipalId = NEW.ExecutorPrincipalId
AND run.RootActivationId = NEW.RootActivationId AND run.OriginalInitiatorPrincipalId IS NULL AND run.ProfileId IS NULL
AND json_extract(run.PayloadJson, '$.admission.executionContext.tenantId') IS lower(NEW.TenantId)
AND json_extract(run.PayloadJson, '$.admission.executionContext.agentPrincipalId') IS lower(NEW.ExecutorPrincipalId)
AND json_extract(run.PayloadJson, '$.admission.executionContext.originalInitiatorPrincipalId') IS NULL
AND json_extract(run.PayloadJson, '$.admission.executionContext.runId') IS run.AgentRunId
AND json_extract(run.PayloadJson, '$.admission.executionContext.sessionId') IS run.SessionId
AND json_extract(run.PayloadJson, '$.admission.executionContext.activationId') IS run.ActivationId
AND json_extract(run.PayloadJson, '$.admission.executionContext.rootActivationId') IS run.RootActivationId
AND json_extract(run.PayloadJson, '$.admission.activation.rootActivationId') IS run.RootActivationId
), lineage(id, depth, seen) AS (
SELECT run.AgentRunId, 0, ',' || run.AgentRunId || ',' FROM owned run
JOIN TenantSessions session ON session.SessionId = run.SessionId
WHERE run.AgentRunId = NEW.RunId AND run.SessionId = NEW.SessionId
AND session.TenantId = NEW.TenantId AND session.AgentInstanceId = NEW.InstanceId AND session.ExecutorPrincipalId = NEW.ExecutorPrincipalId
AND json_extract(NEW.PayloadJson, '$.OriginalInitiatorPrincipalId') IS NULL
UNION ALL
SELECT parent.AgentRunId, chain.depth + 1, chain.seen || parent.AgentRunId || ',' FROM lineage chain
JOIN owned member ON member.AgentRunId = chain.id
JOIN owned parent ON parent.AgentRunId = json_extract(member.PayloadJson, '$.admission.executionContext.parentRunId')
WHERE chain.depth < 63 AND instr(chain.seen, ',' || parent.AgentRunId || ',') = 0
AND json_extract(member.PayloadJson, '$.admission.executionContext.parentActivationId') IS parent.ActivationId
AND json_extract(member.PayloadJson, '$.admission.activation.parentActivationId') IS parent.ActivationId
AND json_extract(member.PayloadJson, '$.admission.activation.sourceAgentRunId') IS parent.AgentRunId
AND json_extract(member.PayloadJson, '$.admission.activation.sourceSessionId') IS parent.SessionId
AND json_extract(member.PayloadJson, '$.admission.outputContract') IN (0,1)
AND json_extract(parent.PayloadJson, '$.admission.outputContract') IN (0,1)
AND ((json_extract(member.PayloadJson, '$.admission.executionContext.origin') = 1
AND json_extract(member.PayloadJson, '$.admission.outputContract') = 1
AND json_extract(member.PayloadJson, '$.admission.activation.kind') = 2 AND member.SessionId <> parent.SessionId
AND json_extract(member.PayloadJson, '$.admission.taskIntent.source') = 1
AND json_extract(member.PayloadJson, '$.admission.taskIntent.sourceRevision') IS parent.AgentRunId)
OR (json_extract(member.PayloadJson, '$.admission.executionContext.origin') = 9
AND json_extract(member.PayloadJson, '$.admission.outputContract') IS json_extract(parent.PayloadJson, '$.admission.outputContract')
AND json_extract(member.PayloadJson, '$.admission.activation.kind') = 9 AND member.SessionId = parent.SessionId
AND parent.Status = 4 AND json_extract(parent.PayloadJson, '$.result.outcomeKind') = 3
AND json_extract(parent.PayloadJson, '$.result.continuationRunId') IS member.AgentRunId
AND json_extract(member.PayloadJson, '$.admission.taskIntent') IS json_extract(parent.PayloadJson, '$.admission.taskIntent')
AND json_extract(member.PayloadJson, '$.admission.approvalResume.sourceRunId') IS parent.AgentRunId
AND json_type(member.PayloadJson, '$.admission.approvalResume.requestId') IS 'text'
AND json_extract(member.PayloadJson, '$.admission.approvalResume.requestId') IS json_extract(parent.PayloadJson, '$.approvalRequestId')
AND EXISTS (SELECT 1 FROM TenantApprovalRequests approval WHERE lower(approval.RequestId) IS json_extract(parent.PayloadJson, '$.approvalRequestId')
AND approval.TenantId = NEW.TenantId AND approval.RunId = parent.AgentRunId AND approval.SessionId = parent.SessionId
AND approval.RootActivationId = NEW.RootActivationId AND approval.ExecutorPrincipalId = NEW.ExecutorPrincipalId AND approval.Status IN (1,2,3,4))))
) SELECT 1 FROM lineage chain JOIN owned root ON root.AgentRunId = chain.id
JOIN TriggerOccurrences occurrence ON occurrence.AcceptedAgentRunId = root.AgentRunId AND occurrence.ExecutionSessionId = root.SessionId
WHERE root.RootActivationId = root.ActivationId AND json_extract(root.PayloadJson, '$.admission.executionContext.origin') = CASE occurrence.SourceKind WHEN 0 THEN 4 WHEN 1 THEN 5 WHEN 3 THEN 6 END
AND json_extract(root.PayloadJson, '$.admission.executionContext.parentRunId') IS NULL
AND json_extract(root.PayloadJson, '$.admission.executionContext.parentActivationId') IS NULL
AND json_extract(root.PayloadJson, '$.admission.taskIntent.source') = 0
AND json_extract(root.PayloadJson, '$.admission.taskIntent.eventSourceKind') IS CASE WHEN occurrence.SourceKind IN (1,3) THEN occurrence.SourceKind ELSE NULL END
AND json_extract(root.PayloadJson, '$.admission.activation.kind') = CASE occurrence.SourceKind WHEN 0 THEN 3 WHEN 1 THEN 4 WHEN 3 THEN 7 END
AND json_extract(root.PayloadJson, '$.admission.activation.triggerOccurrenceId') IS lower(occurrence.OccurrenceId)
AND occurrence.TenantId = NEW.TenantId AND occurrence.AgentInstanceId = NEW.InstanceId AND occurrence.ExecutorPrincipalId = NEW.ExecutorPrincipalId
AND ((occurrence.ExecutionTargetKind = 0 AND json_extract(root.PayloadJson, '$.admission.outputContract') = 1)
OR (occurrence.ExecutionTargetKind = 1 AND occurrence.TargetSessionId IS root.SessionId AND occurrence.ReportToSessionId IS NULL AND json_extract(root.PayloadJson, '$.admission.outputContract') = 0))
AND occurrence.SourceKind IN (0,1,3) AND occurrence.Disposition = 6 AND occurrence.ProfileId IS NULL AND occurrence.AutomationId IS NOT NULL
AND json_extract(root.PayloadJson, '$.admission.taskIntent.text') IS json_extract(occurrence.EvidenceJson, '$.instructions')
AND json_extract(root.PayloadJson, '$.admission.taskIntent.sourceRevision') IS lower(occurrence.AutomationId) || ':' || json_extract(occurrence.EvidenceJson, '$.automationRevision'));
END;

CREATE TRIGGER TR_TenantApprovals_IndependentTask_Update BEFORE UPDATE ON TenantApprovalRequests
WHEN json_type(NEW.PayloadJson,'$.Operation.SandboxCommand.Evaluation') IS NOT 'object' AND NEW.OriginalInitiatorPrincipalId IS NULL AND json_extract(NEW.PayloadJson, '$.Operation.AutomationAdmission') IS NULL AND NEW.ContentRetiredAt IS NULL AND json_type(NEW.PayloadJson,'$.Operation.TaskDelegation') IS NOT 'object'
BEGIN
SELECT RAISE(ABORT, 'independent task consent requires its exact owned independent lineage') WHERE NOT EXISTS (
WITH RECURSIVE owned AS (
SELECT run.* FROM AgentRuns run
WHERE run.TenantId = NEW.TenantId AND run.AgentInstanceId = NEW.InstanceId AND run.ExecutorPrincipalId = NEW.ExecutorPrincipalId
AND run.RootActivationId = NEW.RootActivationId AND run.OriginalInitiatorPrincipalId IS NULL AND run.ProfileId IS NULL
AND json_extract(run.PayloadJson, '$.admission.executionContext.tenantId') IS lower(NEW.TenantId)
AND json_extract(run.PayloadJson, '$.admission.executionContext.agentPrincipalId') IS lower(NEW.ExecutorPrincipalId)
AND json_extract(run.PayloadJson, '$.admission.executionContext.originalInitiatorPrincipalId') IS NULL
AND json_extract(run.PayloadJson, '$.admission.executionContext.runId') IS run.AgentRunId
AND json_extract(run.PayloadJson, '$.admission.executionContext.sessionId') IS run.SessionId
AND json_extract(run.PayloadJson, '$.admission.executionContext.activationId') IS run.ActivationId
AND json_extract(run.PayloadJson, '$.admission.executionContext.rootActivationId') IS run.RootActivationId
AND json_extract(run.PayloadJson, '$.admission.activation.rootActivationId') IS run.RootActivationId
), lineage(id, depth, seen) AS (
SELECT run.AgentRunId, 0, ',' || run.AgentRunId || ',' FROM owned run
JOIN TenantSessions session ON session.SessionId = run.SessionId
WHERE run.AgentRunId = NEW.RunId AND run.SessionId = NEW.SessionId
AND session.TenantId = NEW.TenantId AND session.AgentInstanceId = NEW.InstanceId AND session.ExecutorPrincipalId = NEW.ExecutorPrincipalId
AND json_extract(NEW.PayloadJson, '$.OriginalInitiatorPrincipalId') IS NULL
UNION ALL
SELECT parent.AgentRunId, chain.depth + 1, chain.seen || parent.AgentRunId || ',' FROM lineage chain
JOIN owned member ON member.AgentRunId = chain.id
JOIN owned parent ON parent.AgentRunId = json_extract(member.PayloadJson, '$.admission.executionContext.parentRunId')
WHERE chain.depth < 63 AND instr(chain.seen, ',' || parent.AgentRunId || ',') = 0
AND json_extract(member.PayloadJson, '$.admission.executionContext.parentActivationId') IS parent.ActivationId
AND json_extract(member.PayloadJson, '$.admission.activation.parentActivationId') IS parent.ActivationId
AND json_extract(member.PayloadJson, '$.admission.activation.sourceAgentRunId') IS parent.AgentRunId
AND json_extract(member.PayloadJson, '$.admission.activation.sourceSessionId') IS parent.SessionId
AND json_extract(member.PayloadJson, '$.admission.outputContract') IN (0,1)
AND json_extract(parent.PayloadJson, '$.admission.outputContract') IN (0,1)
AND ((json_extract(member.PayloadJson, '$.admission.executionContext.origin') = 1
AND json_extract(member.PayloadJson, '$.admission.outputContract') = 1
AND json_extract(member.PayloadJson, '$.admission.activation.kind') = 2 AND member.SessionId <> parent.SessionId
AND json_extract(member.PayloadJson, '$.admission.taskIntent.source') = 1
AND json_extract(member.PayloadJson, '$.admission.taskIntent.sourceRevision') IS parent.AgentRunId)
OR (json_extract(member.PayloadJson, '$.admission.executionContext.origin') = 9
AND json_extract(member.PayloadJson, '$.admission.outputContract') IS json_extract(parent.PayloadJson, '$.admission.outputContract')
AND json_extract(member.PayloadJson, '$.admission.activation.kind') = 9 AND member.SessionId = parent.SessionId
AND parent.Status = 4 AND json_extract(parent.PayloadJson, '$.result.outcomeKind') = 3
AND json_extract(parent.PayloadJson, '$.result.continuationRunId') IS member.AgentRunId
AND json_extract(member.PayloadJson, '$.admission.taskIntent') IS json_extract(parent.PayloadJson, '$.admission.taskIntent')
AND json_extract(member.PayloadJson, '$.admission.approvalResume.sourceRunId') IS parent.AgentRunId
AND json_type(member.PayloadJson, '$.admission.approvalResume.requestId') IS 'text'
AND json_extract(member.PayloadJson, '$.admission.approvalResume.requestId') IS json_extract(parent.PayloadJson, '$.approvalRequestId')
AND EXISTS (SELECT 1 FROM TenantApprovalRequests approval WHERE lower(approval.RequestId) IS json_extract(parent.PayloadJson, '$.approvalRequestId')
AND approval.TenantId = NEW.TenantId AND approval.RunId = parent.AgentRunId AND approval.SessionId = parent.SessionId
AND approval.RootActivationId = NEW.RootActivationId AND approval.ExecutorPrincipalId = NEW.ExecutorPrincipalId AND approval.Status IN (1,2,3,4))))
) SELECT 1 FROM lineage chain JOIN owned root ON root.AgentRunId = chain.id
JOIN TriggerOccurrences occurrence ON occurrence.AcceptedAgentRunId = root.AgentRunId AND occurrence.ExecutionSessionId = root.SessionId
WHERE root.RootActivationId = root.ActivationId AND json_extract(root.PayloadJson, '$.admission.executionContext.origin') = CASE occurrence.SourceKind WHEN 0 THEN 4 WHEN 1 THEN 5 WHEN 3 THEN 6 END
AND json_extract(root.PayloadJson, '$.admission.executionContext.parentRunId') IS NULL
AND json_extract(root.PayloadJson, '$.admission.executionContext.parentActivationId') IS NULL
AND json_extract(root.PayloadJson, '$.admission.taskIntent.source') = 0
AND json_extract(root.PayloadJson, '$.admission.taskIntent.eventSourceKind') IS CASE WHEN occurrence.SourceKind IN (1,3) THEN occurrence.SourceKind ELSE NULL END
AND json_extract(root.PayloadJson, '$.admission.activation.kind') = CASE occurrence.SourceKind WHEN 0 THEN 3 WHEN 1 THEN 4 WHEN 3 THEN 7 END
AND json_extract(root.PayloadJson, '$.admission.activation.triggerOccurrenceId') IS lower(occurrence.OccurrenceId)
AND occurrence.TenantId = NEW.TenantId AND occurrence.AgentInstanceId = NEW.InstanceId AND occurrence.ExecutorPrincipalId = NEW.ExecutorPrincipalId
AND ((occurrence.ExecutionTargetKind = 0 AND json_extract(root.PayloadJson, '$.admission.outputContract') = 1)
OR (occurrence.ExecutionTargetKind = 1 AND occurrence.TargetSessionId IS root.SessionId AND occurrence.ReportToSessionId IS NULL AND json_extract(root.PayloadJson, '$.admission.outputContract') = 0))
AND occurrence.SourceKind IN (0,1,3) AND occurrence.Disposition = 6 AND occurrence.ProfileId IS NULL AND occurrence.AutomationId IS NOT NULL
AND json_extract(root.PayloadJson, '$.admission.taskIntent.text') IS json_extract(occurrence.EvidenceJson, '$.instructions')
AND json_extract(root.PayloadJson, '$.admission.taskIntent.sourceRevision') IS lower(occurrence.AutomationId) || ':' || json_extract(occurrence.EvidenceJson, '$.automationRevision'));
END;

CREATE TRIGGER TR_TenantApprovals_ReportSourceRead_Insert BEFORE INSERT ON TenantApprovalRequests
WHEN json_type(NEW.PayloadJson, '$.Operation.ReportSourceRead') IS 'object'
BEGIN
SELECT RAISE(ABORT, 'Report source read requires exact ordinary Review') WHERE NOT COALESCE((
NEW.Kind = 0 AND NEW.Status IN (0,1,2,3,4) AND NEW.Revision >= 1 AND NEW.ExpiresAt > NEW.CreatedAt AND length(NEW.Fingerprint) = 64
AND NEW.Permission = 'agent-runs.read' AND NEW.ResourceType = 'agent-run' AND NEW.ResourceId IS NEW.RunId
AND NEW.CredentialId IS NULL AND NEW.SandboxProfileId IS NULL AND NEW.SandboxProfileRevision IS NULL
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
AND (json_extract(initial.PayloadJson, '$.admission.activation.kind') = 2 OR EXISTS (
SELECT 1 FROM TriggerOccurrences occurrence WHERE occurrence.TenantId = NEW.TenantId
AND occurrence.AgentInstanceId = NEW.InstanceId AND occurrence.ExecutorPrincipalId = NEW.ExecutorPrincipalId
AND occurrence.AcceptedAgentRunId = initial.AgentRunId AND occurrence.ExecutionSessionId = initial.SessionId
AND occurrence.Disposition = 6 AND occurrence.ExecutionTargetKind = 0 AND occurrence.TargetSessionId IS NULL
AND occurrence.ReportToSessionId IS json_extract(NEW.PayloadJson, '$.Operation.ReportSourceRead.ParentSessionId')
AND occurrence.OccurrenceId IS json_extract(initial.PayloadJson, '$.admission.activation.triggerOccurrenceId')
AND occurrence.AutomationId IS json_extract(saved.OriginJson, '$.automationId')
AND occurrence.OccurrenceId IS json_extract(saved.OriginJson, '$.triggerOccurrenceId')
AND initial.RootActivationId = initial.ActivationId AND json_extract(initial.PayloadJson, '$.admission.executionContext.parentRunId') IS NULL)) AND json_extract(initial.PayloadJson, '$.admission.outputContract') = 1
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

CREATE TRIGGER TR_TenantAutomations_Owner_Update BEFORE UPDATE ON Automations
WHEN (OLD.TenantId IS NOT NULL OR NEW.TenantId IS NOT NULL)
AND (OLD.TenantId IS NOT NEW.TenantId OR OLD.ExecutorPrincipalId IS NOT NEW.ExecutorPrincipalId
OR OLD.AgentInstanceId IS NOT NEW.AgentInstanceId OR OLD.ProfileId IS NOT NEW.ProfileId OR OLD.AutomationId IS NOT NEW.AutomationId)
BEGIN SELECT RAISE(ABORT, 'tenant Automation owner is immutable'); END;

CREATE TRIGGER TR_TenantCompletion_Automation_Insert BEFORE INSERT ON Automations
WHEN NEW.TenantId IS NOT NULL AND NEW.ReportToSessionId IS NOT NULL
BEGIN
SELECT RAISE(ABORT, 'tenant completion destination must be an owned conversation') WHERE NOT EXISTS (
SELECT 1 FROM TenantSessions target JOIN Sessions saved ON saved.SessionId = target.SessionId
WHERE target.TenantId = NEW.TenantId AND target.AgentInstanceId = NEW.AgentInstanceId AND target.ExecutorPrincipalId = NEW.ExecutorPrincipalId
AND target.SessionId = NEW.ReportToSessionId AND saved.DurablyDeletedAtUtc IS NULL
AND json_extract(saved.OriginJson, '$.kind') = 0 AND NEW.ExecutionTargetKind = 0 AND NEW.TargetSessionId IS NULL);
END;

CREATE TRIGGER TR_TenantCompletion_Automation_Update BEFORE UPDATE ON Automations
WHEN NEW.TenantId IS NOT NULL AND NEW.ReportToSessionId IS NOT NULL AND (OLD.ReportToSessionId IS NOT NEW.ReportToSessionId OR OLD.TenantId IS NOT NEW.TenantId OR OLD.AgentInstanceId IS NOT NEW.AgentInstanceId OR OLD.ExecutorPrincipalId IS NOT NEW.ExecutorPrincipalId)
BEGIN
SELECT RAISE(ABORT, 'tenant completion destination must be an owned conversation') WHERE NOT EXISTS (
SELECT 1 FROM TenantSessions target JOIN Sessions saved ON saved.SessionId = target.SessionId
WHERE target.TenantId = NEW.TenantId AND target.AgentInstanceId = NEW.AgentInstanceId AND target.ExecutorPrincipalId = NEW.ExecutorPrincipalId
AND target.SessionId = NEW.ReportToSessionId AND saved.DurablyDeletedAtUtc IS NULL
AND json_extract(saved.OriginJson, '$.kind') = 0 AND NEW.ExecutionTargetKind = 0 AND NEW.TargetSessionId IS NULL);
END;

CREATE TRIGGER TR_TenantCompletion_Occurrence_Insert BEFORE INSERT ON TriggerOccurrences
WHEN NEW.TenantId IS NOT NULL
BEGIN
SELECT RAISE(ABORT, 'tenant occurrence destination differs from exact admission') WHERE NOT EXISTS (
SELECT 1 FROM Automations automation WHERE automation.TenantId = NEW.TenantId AND automation.AutomationId = NEW.AutomationId
AND automation.AgentInstanceId = NEW.AgentInstanceId AND automation.ExecutorPrincipalId = NEW.ExecutorPrincipalId
AND automation.ExecutionTargetKind IS NEW.ExecutionTargetKind AND automation.TargetSessionId IS NEW.TargetSessionId
AND automation.ReportToSessionId IS NEW.ReportToSessionId AND automation.WorkflowTargetJson IS NEW.WorkflowTargetJson)
AND (NEW.Disposition = 6 AND 1=1);
SELECT RAISE(ABORT, 'tenant completion origin differs from accepted occurrence') WHERE NEW.Disposition = 6 AND NEW.ExecutionTargetKind <> 2 AND NOT EXISTS (
SELECT 1 FROM Sessions saved WHERE saved.SessionId = NEW.ExecutionSessionId
AND ((NEW.ExecutionTargetKind = 1 AND NEW.TargetSessionId IS NEW.ExecutionSessionId AND NEW.ReportToSessionId IS NULL AND json_extract(saved.OriginJson, '$.kind') = 0) OR (NEW.ExecutionTargetKind = 0 AND json_extract(saved.OriginJson, '$.kind') = 2 AND json_extract(saved.OriginJson, '$.automationId') IS NEW.AutomationId
AND json_extract(saved.OriginJson, '$.triggerOccurrenceId') IS NEW.OccurrenceId
AND json_extract(saved.OriginJson, '$.initialBackgroundAgentRunId') IS NEW.AcceptedAgentRunId
AND json_extract(saved.OriginJson, '$.originatingSessionId') IS NEW.ReportToSessionId
AND json_extract(saved.OriginJson, '$.reportCompletionToOrigin') IS CASE WHEN NEW.ReportToSessionId IS NULL THEN 0 ELSE 1 END)));
END;

CREATE TRIGGER TR_TenantCompletion_Occurrence_Update BEFORE UPDATE ON TriggerOccurrences
WHEN NEW.TenantId IS NOT NULL
BEGIN
SELECT RAISE(ABORT, 'tenant occurrence destination differs from exact admission') WHERE NOT EXISTS (
SELECT 1 FROM Automations automation WHERE automation.TenantId = NEW.TenantId AND automation.AutomationId = NEW.AutomationId
AND automation.AgentInstanceId = NEW.AgentInstanceId AND automation.ExecutorPrincipalId = NEW.ExecutorPrincipalId
AND automation.ExecutionTargetKind IS NEW.ExecutionTargetKind AND automation.TargetSessionId IS NEW.TargetSessionId
AND automation.ReportToSessionId IS NEW.ReportToSessionId AND automation.WorkflowTargetJson IS NEW.WorkflowTargetJson)
AND (NEW.Disposition = 6 AND OLD.Disposition <> 6);
SELECT RAISE(ABORT, 'tenant completion origin differs from accepted occurrence') WHERE NEW.Disposition = 6 AND NEW.ExecutionTargetKind <> 2 AND NOT EXISTS (
SELECT 1 FROM Sessions saved WHERE saved.SessionId = NEW.ExecutionSessionId
AND ((NEW.ExecutionTargetKind = 1 AND NEW.TargetSessionId IS NEW.ExecutionSessionId AND NEW.ReportToSessionId IS NULL AND json_extract(saved.OriginJson, '$.kind') = 0) OR (NEW.ExecutionTargetKind = 0 AND json_extract(saved.OriginJson, '$.kind') = 2 AND json_extract(saved.OriginJson, '$.automationId') IS NEW.AutomationId
AND json_extract(saved.OriginJson, '$.triggerOccurrenceId') IS NEW.OccurrenceId
AND json_extract(saved.OriginJson, '$.initialBackgroundAgentRunId') IS NEW.AcceptedAgentRunId
AND json_extract(saved.OriginJson, '$.originatingSessionId') IS NEW.ReportToSessionId
AND json_extract(saved.OriginJson, '$.reportCompletionToOrigin') IS CASE WHEN NEW.ReportToSessionId IS NULL THEN 0 ELSE 1 END)));
END;

CREATE TRIGGER TR_TenantConversation_Automation_Insert BEFORE INSERT ON Automations WHEN NEW.TenantId IS NOT NULL AND NEW.ExecutionTargetKind = 1
BEGIN
SELECT RAISE(ABORT, 'tenant execution target requires its owned UserChat conversation') WHERE NOT EXISTS (
SELECT 1 FROM TenantSessions target JOIN Sessions saved ON saved.SessionId = target.SessionId WHERE target.TenantId = NEW.TenantId
AND target.AgentInstanceId = NEW.AgentInstanceId AND target.ExecutorPrincipalId = NEW.ExecutorPrincipalId AND target.SessionId IS NEW.TargetSessionId
AND NEW.ReportToSessionId IS NULL AND saved.DurablyDeletedAtUtc IS NULL AND json_extract(saved.OriginJson, '$.kind') = 0);
END;

CREATE TRIGGER TR_TenantConversation_Automation_Update BEFORE UPDATE ON Automations WHEN NEW.TenantId IS NOT NULL AND NEW.ExecutionTargetKind = 1 AND (OLD.TargetSessionId IS NOT NEW.TargetSessionId OR OLD.ExecutionTargetKind IS NOT NEW.ExecutionTargetKind OR OLD.TenantId IS NOT NEW.TenantId OR OLD.AgentInstanceId IS NOT NEW.AgentInstanceId OR OLD.ExecutorPrincipalId IS NOT NEW.ExecutorPrincipalId)
BEGIN
SELECT RAISE(ABORT, 'tenant execution target requires its owned UserChat conversation') WHERE NOT EXISTS (
SELECT 1 FROM TenantSessions target JOIN Sessions saved ON saved.SessionId = target.SessionId WHERE target.TenantId = NEW.TenantId
AND target.AgentInstanceId = NEW.AgentInstanceId AND target.ExecutorPrincipalId = NEW.ExecutorPrincipalId AND target.SessionId IS NEW.TargetSessionId
AND NEW.ReportToSessionId IS NULL AND saved.DurablyDeletedAtUtc IS NULL AND json_extract(saved.OriginJson, '$.kind') = 0);
END;

CREATE TRIGGER TR_TenantConversation_Occurrence_Insert BEFORE INSERT ON TriggerOccurrences
WHEN NEW.TenantId IS NOT NULL AND NEW.ExecutionTargetKind = 1 AND NEW.Disposition = 6
BEGIN
SELECT RAISE(ABORT, 'tenant occurrence requires an exact independent conversation Run') WHERE NOT EXISTS (
SELECT 1 FROM TenantSessions target JOIN Sessions saved ON saved.SessionId = target.SessionId JOIN AgentRuns run ON run.AgentRunId = NEW.AcceptedAgentRunId
WHERE target.TenantId = NEW.TenantId AND target.AgentInstanceId = NEW.AgentInstanceId AND target.ExecutorPrincipalId = NEW.ExecutorPrincipalId
AND target.SessionId IS NEW.TargetSessionId AND NEW.TargetSessionId IS NEW.ExecutionSessionId AND NEW.ReportToSessionId IS NULL
AND json_extract(saved.OriginJson, '$.kind') = 0 AND saved.DurablyDeletedAtUtc IS NULL
AND run.TenantId = NEW.TenantId AND run.AgentInstanceId = NEW.AgentInstanceId AND run.ExecutorPrincipalId = NEW.ExecutorPrincipalId
AND run.SessionId IS target.SessionId AND run.RootActivationId IS run.ActivationId AND json_extract(run.PayloadJson, '$.admission.outputContract') = 0
AND json_extract(run.PayloadJson, '$.admission.activation.triggerOccurrenceId') IS NEW.OccurrenceId
AND json_extract(run.PayloadJson, '$.admission.taskIntent.source') = 0 AND json_extract(run.PayloadJson, '$.admission.taskIntent.text') IS json_extract(NEW.EvidenceJson, '$.instructions')
AND json_extract(run.PayloadJson, '$.admission.taskIntent.sourceRevision') IS NEW.AutomationId || ':' || json_extract(NEW.EvidenceJson, '$.automationRevision'));
END;

CREATE TRIGGER TR_TenantConversation_Occurrence_Update BEFORE UPDATE ON TriggerOccurrences
WHEN NEW.TenantId IS NOT NULL AND NEW.ExecutionTargetKind = 1 AND NEW.Disposition = 6
BEGIN
SELECT RAISE(ABORT, 'tenant occurrence requires an exact independent conversation Run') WHERE NOT EXISTS (
SELECT 1 FROM TenantSessions target JOIN Sessions saved ON saved.SessionId = target.SessionId JOIN AgentRuns run ON run.AgentRunId = NEW.AcceptedAgentRunId
WHERE target.TenantId = NEW.TenantId AND target.AgentInstanceId = NEW.AgentInstanceId AND target.ExecutorPrincipalId = NEW.ExecutorPrincipalId
AND target.SessionId IS NEW.TargetSessionId AND NEW.TargetSessionId IS NEW.ExecutionSessionId AND NEW.ReportToSessionId IS NULL
AND json_extract(saved.OriginJson, '$.kind') = 0 AND saved.DurablyDeletedAtUtc IS NULL
AND run.TenantId = NEW.TenantId AND run.AgentInstanceId = NEW.AgentInstanceId AND run.ExecutorPrincipalId = NEW.ExecutorPrincipalId
AND run.SessionId IS target.SessionId AND run.RootActivationId IS run.ActivationId AND json_extract(run.PayloadJson, '$.admission.outputContract') = 0
AND json_extract(run.PayloadJson, '$.admission.activation.triggerOccurrenceId') IS NEW.OccurrenceId
AND json_extract(run.PayloadJson, '$.admission.taskIntent.source') = 0 AND json_extract(run.PayloadJson, '$.admission.taskIntent.text') IS json_extract(NEW.EvidenceJson, '$.instructions')
AND json_extract(run.PayloadJson, '$.admission.taskIntent.sourceRevision') IS NEW.AutomationId || ':' || json_extract(NEW.EvidenceJson, '$.automationRevision'));
END;

CREATE TRIGGER TR_TenantCore_Delivery_Insert BEFORE INSERT ON CoreEventDeliveries
BEGIN
SELECT RAISE(ABORT, 'delivery requires its exact frozen receipt and occurrence') WHERE NOT COALESCE(((NEW.TenantId IS NULL AND NEW.ExecutorPrincipalId IS NULL AND NEW.ReservedOccurrenceId IS NULL AND NOT EXISTS (SELECT 1 FROM SystemIdentities i WHERE i.Kind = 0 AND i.AgentInstanceId = NEW.AgentInstanceId AND i.TenantId IS NOT NULL) AND NOT EXISTS (SELECT 1 FROM CoreEvents e WHERE e.EventId = NEW.EventId AND e.TenantId IS NOT NULL) AND (NEW.SnapshotJson IS NULL OR NOT EXISTS (SELECT 1 FROM json_tree(NEW.SnapshotJson, '$') WHERE lower(key) IN ('tenantid','executorprincipalid')) AND NOT EXISTS (SELECT 1 FROM json_tree(NEW.SnapshotJson) field JOIN SystemIdentities i ON i.AgentInstanceId = field.value WHERE lower(field.key) = 'agentinstanceid' AND i.Kind = 0 AND i.TenantId IS NOT NULL))) OR (NEW.TenantId IS NOT NULL AND (json_type(NEW.SnapshotJson, '$') IS 'object' AND (SELECT count(*) FROM json_each(NEW.SnapshotJson, '$')) = (10 + CASE WHEN json_type(NEW.SnapshotJson, '$.PinnedSpaceId') IS NULL THEN 0 ELSE 1 END + CASE WHEN json_type(NEW.SnapshotJson, '$.PinnedEnvironmentId') IS NULL THEN 0 ELSE 1 END) AND (SELECT count(DISTINCT key) FROM json_each(NEW.SnapshotJson, '$') WHERE key IN ('AutomationId','Owner','TriggerRevision','FilterExpression','Dispatch','SourceKind','ResourceId','TriggerId','ExpressionVersion','FilterHash')) = 10 AND ((json_type(NEW.SnapshotJson, '$.PinnedSpaceId') IS NULL AND json_type(NEW.SnapshotJson, '$.PinnedEnvironmentId') IS NULL) OR (json_type(NEW.SnapshotJson, '$.PinnedSpaceId')='text' AND length(json_extract(NEW.SnapshotJson, '$.PinnedSpaceId'))=36 AND json_extract(NEW.SnapshotJson, '$.PinnedSpaceId') NOT GLOB '*[^0-9a-f-]*' AND json_extract(NEW.SnapshotJson, '$.PinnedSpaceId')<>'00000000-0000-0000-0000-000000000000' AND substr(json_extract(NEW.SnapshotJson, '$.PinnedSpaceId'),9,1)='-' AND substr(json_extract(NEW.SnapshotJson, '$.PinnedSpaceId'),14,1)='-' AND substr(json_extract(NEW.SnapshotJson, '$.PinnedSpaceId'),19,1)='-' AND substr(json_extract(NEW.SnapshotJson, '$.PinnedSpaceId'),24,1)='-' AND EXISTS(SELECT 1 FROM Spaces ps WHERE lower(ps.TenantId)=lower(json_extract(NEW.SnapshotJson, '$.Owner.TenantId')) AND lower(ps.SpaceId)=json_extract(NEW.SnapshotJson, '$.PinnedSpaceId') AND (ps.Kind=1 OR ps.OwnerInstanceId=json_extract(NEW.SnapshotJson, '$.Owner.AgentInstanceId'))) AND (json_type(NEW.SnapshotJson, '$.PinnedEnvironmentId') IS NULL OR (json_type(NEW.SnapshotJson, '$.PinnedEnvironmentId')='text' AND length(json_extract(NEW.SnapshotJson, '$.PinnedEnvironmentId'))=36 AND json_extract(NEW.SnapshotJson, '$.PinnedEnvironmentId') NOT GLOB '*[^0-9a-f-]*' AND json_extract(NEW.SnapshotJson, '$.PinnedEnvironmentId')<>'00000000-0000-0000-0000-000000000000' AND substr(json_extract(NEW.SnapshotJson, '$.PinnedEnvironmentId'),9,1)='-' AND substr(json_extract(NEW.SnapshotJson, '$.PinnedEnvironmentId'),14,1)='-' AND substr(json_extract(NEW.SnapshotJson, '$.PinnedEnvironmentId'),19,1)='-' AND substr(json_extract(NEW.SnapshotJson, '$.PinnedEnvironmentId'),24,1)='-' AND EXISTS(SELECT 1 FROM SpaceEnvironments pe WHERE lower(pe.TenantId)=lower(json_extract(NEW.SnapshotJson, '$.Owner.TenantId')) AND lower(pe.SpaceId)=json_extract(NEW.SnapshotJson, '$.PinnedSpaceId') AND lower(pe.EnvironmentId)=json_extract(NEW.SnapshotJson, '$.PinnedEnvironmentId'))))))) AND (json_type(NEW.SnapshotJson, '$.Owner') IS 'object' AND (SELECT count(*) FROM json_each(NEW.SnapshotJson, '$.Owner')) = 4 AND (SELECT count(DISTINCT key) FROM json_each(NEW.SnapshotJson, '$.Owner') WHERE key IN ('AgentInstanceId','profileId','TenantId','ExecutorPrincipalId')) = 4) AND json_extract(NEW.SnapshotJson, '$.Owner.AgentInstanceId') IS NEW.AgentInstanceId AND lower(json_extract(NEW.SnapshotJson, '$.Owner.TenantId')) IS lower(NEW.TenantId) AND lower(json_extract(NEW.SnapshotJson, '$.Owner.ExecutorPrincipalId')) IS lower(NEW.ExecutorPrincipalId) AND json_type(NEW.SnapshotJson, '$.Owner.profileId') IS 'null' AND (json_type(NEW.SnapshotJson, '$.Dispatch') IS 'object' AND (SELECT count(*) FROM json_each(NEW.SnapshotJson, '$.Dispatch')) = 2 AND (SELECT count(DISTINCT key) FROM json_each(NEW.SnapshotJson, '$.Dispatch') WHERE key IN ('Mode','WindowSeconds')) = 2) AND json_extract(NEW.SnapshotJson, '$.ExpressionVersion') IS 'js-expression-v1' AND json_extract(NEW.SnapshotJson, '$.TriggerRevision') >= 1 AND (json_type(NEW.SnapshotJson, '$.FilterExpression') IS 'null' OR (json_type(NEW.SnapshotJson, '$.FilterExpression') IS 'text' AND length(json_extract(NEW.SnapshotJson, '$.FilterExpression')) <= 1024)) AND ((json_extract(NEW.SnapshotJson, '$.Dispatch.Mode') = 0 AND json_type(NEW.SnapshotJson, '$.Dispatch.WindowSeconds') IS 'null') OR (json_extract(NEW.SnapshotJson, '$.Dispatch.Mode') = 1 AND json_extract(NEW.SnapshotJson, '$.Dispatch.WindowSeconds') BETWEEN 60 AND 3600)) AND json_extract(NEW.SnapshotJson, '$.AutomationId') IS NEW.AutomationId AND json_extract(NEW.SnapshotJson, '$.TriggerId') IS NEW.TriggerId AND json_extract(NEW.SnapshotJson, '$.SourceKind') = 3 AND EXISTS (SELECT 1 FROM CoreEvents e, json_each(e.SubscriptionsJson) f WHERE e.EventId = NEW.EventId AND e.TenantId = NEW.TenantId AND json(f.value) IS json(NEW.SnapshotJson)) AND NEW.Status BETWEEN 0 AND 9 AND (NEW.Status NOT IN (1,6,8,9) OR json_extract(NEW.DecisionJson, '$.Matched') = 1) AND (NEW.ReservedOccurrenceId IS NULL AND NEW.Status NOT IN (8,9) OR NEW.ReservedOccurrenceId IS NOT NULL AND EXISTS (SELECT 1 FROM TriggerOccurrences o WHERE o.OccurrenceId = NEW.ReservedOccurrenceId AND o.TenantId = NEW.TenantId AND o.AgentInstanceId = NEW.AgentInstanceId AND o.ExecutorPrincipalId = NEW.ExecutorPrincipalId AND o.AutomationId = NEW.AutomationId AND o.TriggerId = NEW.TriggerId AND o.SourceEventId = NEW.EventId AND o.SourceKind = 3 AND ((NEW.Status = 9 AND o.Disposition = 3 AND o.DispositionReason = 'admission_approval_pending') OR (NEW.Status = 8 AND o.Disposition = 6) OR (NEW.Status = 4 AND o.Disposition = 4)))))),0);
END;

CREATE TRIGGER TR_TenantCore_Delivery_Update BEFORE UPDATE ON CoreEventDeliveries
BEGIN
SELECT RAISE(ABORT, 'tenant delivery source is immutable') WHERE (OLD.TenantId IS NOT NULL OR NEW.TenantId IS NOT NULL) AND (OLD.TenantId IS NOT NEW.TenantId OR OLD.ExecutorPrincipalId IS NOT NEW.ExecutorPrincipalId OR OLD.ProfileId IS NOT NEW.ProfileId OR OLD.AgentInstanceId IS NOT NEW.AgentInstanceId OR OLD.AutomationId IS NOT NEW.AutomationId OR OLD.TriggerId IS NOT NEW.TriggerId OR OLD.EventId IS NOT NEW.EventId OR OLD.SnapshotJson IS NOT NEW.SnapshotJson);
SELECT RAISE(ABORT, 'terminal delivery cannot be reopened') WHERE OLD.TenantId IS NOT NULL AND (OLD.ReservedOccurrenceId IS NOT NULL AND OLD.ReservedOccurrenceId IS NOT NEW.ReservedOccurrenceId OR OLD.Status IN (2,4,5,6,7,8) AND (OLD.Status IS NOT NEW.Status OR OLD.DecisionJson IS NOT NEW.DecisionJson OR OLD.Code IS NOT NEW.Code));
SELECT RAISE(ABORT, 'delivery requires its exact frozen receipt and occurrence') WHERE NOT COALESCE(((NEW.TenantId IS NULL AND NEW.ExecutorPrincipalId IS NULL AND NEW.ReservedOccurrenceId IS NULL AND NOT EXISTS (SELECT 1 FROM SystemIdentities i WHERE i.Kind = 0 AND i.AgentInstanceId = NEW.AgentInstanceId AND i.TenantId IS NOT NULL) AND NOT EXISTS (SELECT 1 FROM CoreEvents e WHERE e.EventId = NEW.EventId AND e.TenantId IS NOT NULL) AND (NEW.SnapshotJson IS NULL OR NOT EXISTS (SELECT 1 FROM json_tree(NEW.SnapshotJson, '$') WHERE lower(key) IN ('tenantid','executorprincipalid')) AND NOT EXISTS (SELECT 1 FROM json_tree(NEW.SnapshotJson) field JOIN SystemIdentities i ON i.AgentInstanceId = field.value WHERE lower(field.key) = 'agentinstanceid' AND i.Kind = 0 AND i.TenantId IS NOT NULL))) OR (NEW.TenantId IS NOT NULL AND (json_type(NEW.SnapshotJson, '$') IS 'object' AND (SELECT count(*) FROM json_each(NEW.SnapshotJson, '$')) = (10 + CASE WHEN json_type(NEW.SnapshotJson, '$.PinnedSpaceId') IS NULL THEN 0 ELSE 1 END + CASE WHEN json_type(NEW.SnapshotJson, '$.PinnedEnvironmentId') IS NULL THEN 0 ELSE 1 END) AND (SELECT count(DISTINCT key) FROM json_each(NEW.SnapshotJson, '$') WHERE key IN ('AutomationId','Owner','TriggerRevision','FilterExpression','Dispatch','SourceKind','ResourceId','TriggerId','ExpressionVersion','FilterHash')) = 10 AND ((json_type(NEW.SnapshotJson, '$.PinnedSpaceId') IS NULL AND json_type(NEW.SnapshotJson, '$.PinnedEnvironmentId') IS NULL) OR (json_type(NEW.SnapshotJson, '$.PinnedSpaceId')='text' AND length(json_extract(NEW.SnapshotJson, '$.PinnedSpaceId'))=36 AND json_extract(NEW.SnapshotJson, '$.PinnedSpaceId') NOT GLOB '*[^0-9a-f-]*' AND json_extract(NEW.SnapshotJson, '$.PinnedSpaceId')<>'00000000-0000-0000-0000-000000000000' AND substr(json_extract(NEW.SnapshotJson, '$.PinnedSpaceId'),9,1)='-' AND substr(json_extract(NEW.SnapshotJson, '$.PinnedSpaceId'),14,1)='-' AND substr(json_extract(NEW.SnapshotJson, '$.PinnedSpaceId'),19,1)='-' AND substr(json_extract(NEW.SnapshotJson, '$.PinnedSpaceId'),24,1)='-' AND EXISTS(SELECT 1 FROM Spaces ps WHERE lower(ps.TenantId)=lower(json_extract(NEW.SnapshotJson, '$.Owner.TenantId')) AND lower(ps.SpaceId)=json_extract(NEW.SnapshotJson, '$.PinnedSpaceId') AND (ps.Kind=1 OR ps.OwnerInstanceId=json_extract(NEW.SnapshotJson, '$.Owner.AgentInstanceId'))) AND (json_type(NEW.SnapshotJson, '$.PinnedEnvironmentId') IS NULL OR (json_type(NEW.SnapshotJson, '$.PinnedEnvironmentId')='text' AND length(json_extract(NEW.SnapshotJson, '$.PinnedEnvironmentId'))=36 AND json_extract(NEW.SnapshotJson, '$.PinnedEnvironmentId') NOT GLOB '*[^0-9a-f-]*' AND json_extract(NEW.SnapshotJson, '$.PinnedEnvironmentId')<>'00000000-0000-0000-0000-000000000000' AND substr(json_extract(NEW.SnapshotJson, '$.PinnedEnvironmentId'),9,1)='-' AND substr(json_extract(NEW.SnapshotJson, '$.PinnedEnvironmentId'),14,1)='-' AND substr(json_extract(NEW.SnapshotJson, '$.PinnedEnvironmentId'),19,1)='-' AND substr(json_extract(NEW.SnapshotJson, '$.PinnedEnvironmentId'),24,1)='-' AND EXISTS(SELECT 1 FROM SpaceEnvironments pe WHERE lower(pe.TenantId)=lower(json_extract(NEW.SnapshotJson, '$.Owner.TenantId')) AND lower(pe.SpaceId)=json_extract(NEW.SnapshotJson, '$.PinnedSpaceId') AND lower(pe.EnvironmentId)=json_extract(NEW.SnapshotJson, '$.PinnedEnvironmentId'))))))) AND (json_type(NEW.SnapshotJson, '$.Owner') IS 'object' AND (SELECT count(*) FROM json_each(NEW.SnapshotJson, '$.Owner')) = 4 AND (SELECT count(DISTINCT key) FROM json_each(NEW.SnapshotJson, '$.Owner') WHERE key IN ('AgentInstanceId','profileId','TenantId','ExecutorPrincipalId')) = 4) AND json_extract(NEW.SnapshotJson, '$.Owner.AgentInstanceId') IS NEW.AgentInstanceId AND lower(json_extract(NEW.SnapshotJson, '$.Owner.TenantId')) IS lower(NEW.TenantId) AND lower(json_extract(NEW.SnapshotJson, '$.Owner.ExecutorPrincipalId')) IS lower(NEW.ExecutorPrincipalId) AND json_type(NEW.SnapshotJson, '$.Owner.profileId') IS 'null' AND (json_type(NEW.SnapshotJson, '$.Dispatch') IS 'object' AND (SELECT count(*) FROM json_each(NEW.SnapshotJson, '$.Dispatch')) = 2 AND (SELECT count(DISTINCT key) FROM json_each(NEW.SnapshotJson, '$.Dispatch') WHERE key IN ('Mode','WindowSeconds')) = 2) AND json_extract(NEW.SnapshotJson, '$.ExpressionVersion') IS 'js-expression-v1' AND json_extract(NEW.SnapshotJson, '$.TriggerRevision') >= 1 AND (json_type(NEW.SnapshotJson, '$.FilterExpression') IS 'null' OR (json_type(NEW.SnapshotJson, '$.FilterExpression') IS 'text' AND length(json_extract(NEW.SnapshotJson, '$.FilterExpression')) <= 1024)) AND ((json_extract(NEW.SnapshotJson, '$.Dispatch.Mode') = 0 AND json_type(NEW.SnapshotJson, '$.Dispatch.WindowSeconds') IS 'null') OR (json_extract(NEW.SnapshotJson, '$.Dispatch.Mode') = 1 AND json_extract(NEW.SnapshotJson, '$.Dispatch.WindowSeconds') BETWEEN 60 AND 3600)) AND json_extract(NEW.SnapshotJson, '$.AutomationId') IS NEW.AutomationId AND json_extract(NEW.SnapshotJson, '$.TriggerId') IS NEW.TriggerId AND json_extract(NEW.SnapshotJson, '$.SourceKind') = 3 AND EXISTS (SELECT 1 FROM CoreEvents e, json_each(e.SubscriptionsJson) f WHERE e.EventId = NEW.EventId AND e.TenantId = NEW.TenantId AND json(f.value) IS json(NEW.SnapshotJson)) AND NEW.Status BETWEEN 0 AND 9 AND (NEW.Status NOT IN (1,6,8,9) OR json_extract(NEW.DecisionJson, '$.Matched') = 1) AND (NEW.ReservedOccurrenceId IS NULL AND NEW.Status NOT IN (8,9) OR NEW.ReservedOccurrenceId IS NOT NULL AND EXISTS (SELECT 1 FROM TriggerOccurrences o WHERE o.OccurrenceId = NEW.ReservedOccurrenceId AND o.TenantId = NEW.TenantId AND o.AgentInstanceId = NEW.AgentInstanceId AND o.ExecutorPrincipalId = NEW.ExecutorPrincipalId AND o.AutomationId = NEW.AutomationId AND o.TriggerId = NEW.TriggerId AND o.SourceEventId = NEW.EventId AND o.SourceKind = 3 AND ((NEW.Status = 9 AND o.Disposition = 3 AND o.DispositionReason = 'admission_approval_pending') OR (NEW.Status = 8 AND o.Disposition = 6) OR (NEW.Status = 4 AND o.Disposition = 4)))))),0);
END;

CREATE TRIGGER TR_TenantCore_Fact_Insert BEFORE INSERT ON CoreEvents
BEGIN
SELECT RAISE(ABORT, 'Core facts require a closed actual source and frozen subscriptions') WHERE NOT COALESCE(((NEW.TenantId IS NULL AND NEW.ExecutorPrincipalId IS NULL AND NEW.SubscriptionsJson IS NULL AND NEW.EmailWatchEventId IS NULL AND NOT EXISTS (SELECT 1 FROM SystemIdentities i WHERE i.Kind = 0 AND i.AgentInstanceId = NEW.AgentInstanceId AND i.TenantId IS NOT NULL) AND NOT EXISTS (SELECT 1 FROM json_tree(NEW.PayloadJson, '$') WHERE lower(key) IN ('tenantid','executorprincipalid')) AND NOT EXISTS (SELECT 1 FROM json_tree(NEW.PayloadJson) field JOIN SystemIdentities i ON i.AgentInstanceId = field.value WHERE lower(field.key) = 'agentinstanceid' AND i.Kind = 0 AND i.TenantId IS NOT NULL)) OR (NEW.TenantId IS NOT NULL AND (json_type(NEW.PayloadJson, '$') IS 'object' AND (SELECT count(*) FROM json_each(NEW.PayloadJson, '$')) = 9 AND (SELECT count(DISTINCT key) FROM json_each(NEW.PayloadJson, '$') WHERE key IN ('EventId','DedupeKey','Owner','Key','OccurredAtUtc','DataJson','RootAgentRunId','TriggerDepth','VisitedAutomationIds')) = 9) AND (json_type(NEW.PayloadJson, '$.Owner') IS 'object' AND (SELECT count(*) FROM json_each(NEW.PayloadJson, '$.Owner')) = 4 AND (SELECT count(DISTINCT key) FROM json_each(NEW.PayloadJson, '$.Owner') WHERE key IN ('AgentInstanceId','profileId','TenantId','ExecutorPrincipalId')) = 4) AND json_extract(NEW.PayloadJson, '$.Owner.AgentInstanceId') IS NEW.AgentInstanceId AND lower(json_extract(NEW.PayloadJson, '$.Owner.TenantId')) IS lower(NEW.TenantId) AND lower(json_extract(NEW.PayloadJson, '$.Owner.ExecutorPrincipalId')) IS lower(NEW.ExecutorPrincipalId) AND json_type(NEW.PayloadJson, '$.Owner.profileId') IS 'null' AND NEW.Snapshotted = 1 AND json_type(NEW.PayloadJson, '$.TriggerDepth') IS 'integer' AND json_extract(NEW.PayloadJson, '$.TriggerDepth') BETWEEN 0 AND 4 AND json_type(NEW.PayloadJson, '$.VisitedAutomationIds') IS 'array' AND json_array_length(NEW.PayloadJson, '$.VisitedAutomationIds') <= 4 AND (SELECT count(DISTINCT value) FROM json_each(NEW.PayloadJson, '$.VisitedAutomationIds')) = json_array_length(NEW.PayloadJson, '$.VisitedAutomationIds') AND NOT EXISTS (SELECT 1 FROM json_each(NEW.PayloadJson,'$.VisitedAutomationIds') v WHERE NOT EXISTS(
 SELECT 1 FROM Automations a WHERE a.TenantId=NEW.TenantId AND a.AutomationId=v.value
 AND ((a.AgentInstanceId=NEW.AgentInstanceId AND lower(a.ExecutorPrincipalId)=lower(NEW.ExecutorPrincipalId))
 OR (json_extract(NEW.PayloadJson,'$.Key') IN ('run.completed','run.failed') AND EXISTS(
 SELECT 1 FROM AgentRuns producer
 JOIN TenantDelegatedTasks task ON task.TenantId=producer.TenantId AND task.WorkerSessionId=producer.SessionId
   AND lower(task.TaskId)=lower(json_extract(producer.PayloadJson,'$.admission.delegatedTask.taskId'))
   AND task.WorkerInstanceId=producer.AgentInstanceId AND lower(json_extract(task.Json,'$.Assignment.PrincipalId'))=lower(producer.ExecutorPrincipalId)
 JOIN TenantWorkflowExecutions process ON process.TenantId=task.TenantId AND process.ExecutionId=task.WorkflowExecutionId
 JOIN TenantWorkflowSteps step ON step.TenantId=task.TenantId AND step.ExecutionId=process.ExecutionId
   AND lower(step.OperationId)=lower(json_extract(task.Json,'$.Source.Workflow.OperationId'))
   AND lower(json_extract(step.Json,'$.TaskId'))=lower(task.TaskId) AND json_extract(step.Json,'$.Status')>=1
 WHERE producer.TenantId=NEW.TenantId AND producer.AgentInstanceId=NEW.AgentInstanceId AND producer.ExecutorPrincipalId=NEW.ExecutorPrincipalId
   AND producer.AgentRunId=json_extract(json_extract(NEW.PayloadJson,'$.DataJson'),'$.agentRunId')
   AND json_extract(task.Json,'$.Source.ActorKind')=2
   AND json_extract(task.Json,'$.Source.Workflow.Causation') IS json_extract(process.Json,'$.Source.Causation')
   AND EXISTS(SELECT 1 FROM json_each(process.Json,'$.Source.Causation.VisitedAutomationIds') visited WHERE visited.value=v.value)))))) AND (json_type(NEW.PayloadJson,'$.RootAgentRunId') IS 'null' OR EXISTS(
 SELECT 1 FROM AgentRuns r WHERE r.TenantId=NEW.TenantId AND r.AgentRunId=json_extract(NEW.PayloadJson,'$.RootAgentRunId')
 AND ((r.AgentInstanceId=NEW.AgentInstanceId AND r.ExecutorPrincipalId=NEW.ExecutorPrincipalId)
 OR (json_extract(NEW.PayloadJson,'$.Key') IN ('run.completed','run.failed') AND EXISTS(
   SELECT 1 FROM AgentRuns producer JOIN TenantDelegatedTasks task ON task.TenantId=producer.TenantId
     AND lower(task.TaskId)=lower(coalesce(json_extract(producer.PayloadJson,'$.admission.delegatedTask.taskId'),json_extract(producer.PayloadJson,'$.admission.taskReport.taskId')))
     AND lower(task.RootActivationId)=lower(r.ActivationId)
   WHERE producer.TenantId=NEW.TenantId AND producer.AgentInstanceId=NEW.AgentInstanceId AND producer.ExecutorPrincipalId=NEW.ExecutorPrincipalId
     AND producer.AgentRunId=json_extract(json_extract(NEW.PayloadJson,'$.DataJson'),'$.agentRunId') AND lower(producer.RootActivationId)=lower(r.ActivationId)))
 OR (json_extract(NEW.PayloadJson,'$.Key') IN ('run.completed','run.failed') AND EXISTS(
 SELECT 1 FROM AgentRuns producer
 JOIN TenantDelegatedTasks task ON task.TenantId=producer.TenantId AND task.WorkerSessionId=producer.SessionId
   AND lower(task.TaskId)=lower(json_extract(producer.PayloadJson,'$.admission.delegatedTask.taskId'))
   AND task.WorkerInstanceId=producer.AgentInstanceId AND lower(json_extract(task.Json,'$.Assignment.PrincipalId'))=lower(producer.ExecutorPrincipalId)
 JOIN TenantWorkflowExecutions process ON process.TenantId=task.TenantId AND process.ExecutionId=task.WorkflowExecutionId
 JOIN TenantWorkflowSteps step ON step.TenantId=task.TenantId AND step.ExecutionId=process.ExecutionId
   AND lower(step.OperationId)=lower(json_extract(task.Json,'$.Source.Workflow.OperationId'))
   AND lower(json_extract(step.Json,'$.TaskId'))=lower(task.TaskId) AND json_extract(step.Json,'$.Status')>=1
 WHERE producer.TenantId=NEW.TenantId AND producer.AgentInstanceId=NEW.AgentInstanceId AND producer.ExecutorPrincipalId=NEW.ExecutorPrincipalId
   AND producer.AgentRunId=json_extract(json_extract(NEW.PayloadJson,'$.DataJson'),'$.agentRunId')
   AND json_extract(task.Json,'$.Source.ActorKind')=2
   AND json_extract(task.Json,'$.Source.Workflow.Causation') IS json_extract(process.Json,'$.Source.Causation')
   AND json_extract(process.Json,'$.Source.Causation.RootAgentRunId') IS r.AgentRunId))))) AND CAST(round((julianday(json_extract(NEW.PayloadJson, '$.OccurredAtUtc')) - 2440587.5) * 86400000) AS INTEGER) = NEW.ReceivedAtUtc AND ((json_extract(NEW.PayloadJson, '$.Key') IN ('run.completed','run.failed') AND (json_type(json_extract(NEW.PayloadJson, '$.DataJson'), '$') IS 'object' AND (SELECT count(*) FROM json_each(json_extract(NEW.PayloadJson, '$.DataJson'), '$')) = 5 AND (SELECT count(DISTINCT key) FROM json_each(json_extract(NEW.PayloadJson, '$.DataJson'), '$') WHERE key IN ('agentRunId','sessionId','activationKind','outcomeKind','failureCode')) = 5) AND EXISTS (SELECT 1 FROM AgentRuns r WHERE r.TenantId = NEW.TenantId AND r.AgentInstanceId = NEW.AgentInstanceId AND r.ExecutorPrincipalId = NEW.ExecutorPrincipalId AND r.AgentRunId = json_extract(json_extract(NEW.PayloadJson, '$.DataJson'), '$.agentRunId') AND r.SessionId IS json_extract(json_extract(NEW.PayloadJson, '$.DataJson'), '$.sessionId') AND r.Status = CASE json_extract(NEW.PayloadJson, '$.Key') WHEN 'run.completed' THEN 4 ELSE 5 END AND r.UpdatedAtUtc = NEW.ReceivedAtUtc AND NEW.DedupeKey IS json_extract(NEW.PayloadJson, '$.Key') || ':' || r.AgentRunId AND json_extract(json_extract(NEW.PayloadJson, '$.DataJson'), '$.activationKind') IS CASE json_extract(r.PayloadJson, '$.admission.activation.kind') WHEN 0 THEN 'UserTurn' WHEN 1 THEN 'Initiative' WHEN 2 THEN 'ImmediateBackground' WHEN 3 THEN 'ScheduledWork' WHEN 4 THEN 'ApplicationEvent' WHEN 5 THEN 'ManualBackground' WHEN 6 THEN 'BackgroundCompleted' WHEN 7 THEN 'CoreEvent' WHEN 8 THEN 'ConfigurationContinuation' WHEN 9 THEN 'ApprovalContinuation' WHEN 10 THEN 'ContextContinuation' WHEN 11 THEN 'GoalContinuation' WHEN 12 THEN 'DelegatedTask' WHEN 13 THEN 'TaskCompletion' WHEN 14 THEN 'AgentMessage' END AND json_extract(json_extract(NEW.PayloadJson, '$.DataJson'), '$.outcomeKind') IS CASE json_extract(r.PayloadJson, '$.result.outcomeKind') WHEN 0 THEN 'Response' WHEN 1 THEN 'NoAction' WHEN 2 THEN 'NeedsAttention' WHEN 3 THEN 'Continued' END AND json_extract(json_extract(NEW.PayloadJson, '$.DataJson'), '$.failureCode') IS json_extract(r.PayloadJson, '$.failure.code'))) OR (json_extract(NEW.PayloadJson, '$.Key') IN ('session.completed','session.ended') AND (json_type(json_extract(NEW.PayloadJson, '$.DataJson'), '$') IS 'object' AND (SELECT count(*) FROM json_each(json_extract(NEW.PayloadJson, '$.DataJson'), '$')) = 3 AND (SELECT count(DISTINCT key) FROM json_each(json_extract(NEW.PayloadJson, '$.DataJson'), '$') WHERE key IN ('sessionId','previousLifecycle','lifecycle')) = 3) AND EXISTS (SELECT 1 FROM TenantSessions own JOIN Sessions s ON s.SessionId = own.SessionId JOIN SessionSnapshots ss ON ss.SessionId = s.SessionId WHERE own.TenantId = NEW.TenantId AND own.AgentInstanceId = NEW.AgentInstanceId AND own.ExecutorPrincipalId = NEW.ExecutorPrincipalId AND s.SessionId IS json_extract(json_extract(NEW.PayloadJson, '$.DataJson'), '$.sessionId') AND ss.LifecycleStatus IS CASE json_extract(NEW.PayloadJson, '$.Key') WHEN 'session.completed' THEN 'Completed' ELSE 'Ended' END AND json_extract(json_extract(NEW.PayloadJson, '$.DataJson'), '$.lifecycle') IS ss.LifecycleStatus AND json_extract(json_extract(NEW.PayloadJson, '$.DataJson'), '$.previousLifecycle') IN ('Active','Paused','Completed','Expired','Cancelled','Ended') AND json_extract(json_extract(NEW.PayloadJson, '$.DataJson'), '$.previousLifecycle') IS NOT ss.LifecycleStatus AND s.UpdatedAtUtc = NEW.ReceivedAtUtc AND NEW.DedupeKey IS json_extract(NEW.PayloadJson, '$.Key') || ':' || s.SessionId || ':' || s.Revision)) OR (EXISTS (SELECT 1 FROM AgentInstances i WHERE i.InstanceId = NEW.AgentInstanceId AND i.UpdatedAtUtc = NEW.ReceivedAtUtc AND NEW.DedupeKey IS json_extract(NEW.PayloadJson, '$.Key') || ':' || i.InstanceId || ':' || i.Revision AND json_extract(json_extract(NEW.PayloadJson, '$.DataJson'), '$.agentInstanceId') IS i.InstanceId AND ((json_extract(NEW.PayloadJson, '$.Key') IS 'instance.config_changed' AND (json_type(json_extract(NEW.PayloadJson, '$.DataJson'), '$') IS 'object' AND (SELECT count(*) FROM json_each(json_extract(NEW.PayloadJson, '$.DataJson'), '$')) = 4 AND (SELECT count(DISTINCT key) FROM json_each(json_extract(NEW.PayloadJson, '$.DataJson'), '$') WHERE key IN ('agentInstanceId','revision','changedSections','actor')) = 4) AND json_extract(json_extract(NEW.PayloadJson, '$.DataJson'), '$.revision') = i.Revision AND json_extract(json_extract(NEW.PayloadJson, '$.DataJson'), '$.actor') IS 'guardedWrite' AND json_type(json_extract(NEW.PayloadJson, '$.DataJson'), '$.changedSections') IS 'array' AND json_array_length(json_extract(NEW.PayloadJson, '$.DataJson'), '$.changedSections') BETWEEN 1 AND 4 AND NOT EXISTS (SELECT 1 FROM json_each(json_extract(NEW.PayloadJson, '$.DataJson'), '$.changedSections') c WHERE c.value NOT IN ('persona','unattendedModel','executionBudgets','harnessPolicy'))) OR (json_extract(NEW.PayloadJson, '$.Key') IS 'harness.definition_adopted' AND (json_type(json_extract(NEW.PayloadJson, '$.DataJson'), '$') IS 'object' AND (SELECT count(*) FROM json_each(json_extract(NEW.PayloadJson, '$.DataJson'), '$')) = 4 AND (SELECT count(DISTINCT key) FROM json_each(json_extract(NEW.PayloadJson, '$.DataJson'), '$') WHERE key IN ('agentInstanceId','definitionId','previousVersion','activeVersion')) = 4) AND json_extract(json_extract(NEW.PayloadJson, '$.DataJson'), '$.definitionId') IS i.DefinitionId AND json_extract(json_extract(NEW.PayloadJson, '$.DataJson'), '$.activeVersion') = i.ActiveVersion AND json_extract(json_extract(NEW.PayloadJson, '$.DataJson'), '$.previousVersion') >= 1 AND json_extract(json_extract(NEW.PayloadJson, '$.DataJson'), '$.previousVersion') <> i.ActiveVersion)) AND json_type(NEW.PayloadJson, '$.RootAgentRunId') IS 'null' AND json_extract(NEW.PayloadJson, '$.TriggerDepth') = 0 AND json_array_length(NEW.PayloadJson, '$.VisitedAutomationIds') = 0)) OR (json_extract(NEW.PayloadJson, '$.Key') IS 'email.message.received' AND (json_type(json_extract(NEW.PayloadJson, '$.DataJson'), '$') IS 'object' AND (SELECT count(*) FROM json_each(json_extract(NEW.PayloadJson, '$.DataJson'), '$')) = 4 AND (SELECT count(DISTINCT key) FROM json_each(json_extract(NEW.PayloadJson, '$.DataJson'), '$') WHERE key IN ('accountId','accountRef','messageRef','provider')) = 4) AND length(json_extract(json_extract(NEW.PayloadJson, '$.DataJson'), '$.accountId')) = 36 AND json_extract(json_extract(NEW.PayloadJson, '$.DataJson'), '$.accountId') = lower(json_extract(json_extract(NEW.PayloadJson, '$.DataJson'), '$.accountId')) AND json_extract(json_extract(NEW.PayloadJson, '$.DataJson'), '$.accountId') NOT GLOB '*[^0-9a-f-]*' AND substr(json_extract(json_extract(NEW.PayloadJson, '$.DataJson'), '$.accountId'),9,1) = '-' AND substr(json_extract(json_extract(NEW.PayloadJson, '$.DataJson'), '$.accountId'),14,1) = '-' AND substr(json_extract(json_extract(NEW.PayloadJson, '$.DataJson'), '$.accountId'),19,1) = '-' AND substr(json_extract(json_extract(NEW.PayloadJson, '$.DataJson'), '$.accountId'),24,1) = '-' AND replace(json_extract(json_extract(NEW.PayloadJson, '$.DataJson'), '$.accountId'),'-','') <> '00000000000000000000000000000000' AND json_type(json_extract(NEW.PayloadJson, '$.DataJson'), '$.accountRef') IS 'text' AND length(json_extract(json_extract(NEW.PayloadJson, '$.DataJson'), '$.accountRef')) BETWEEN 1 AND 64 AND json_type(json_extract(NEW.PayloadJson, '$.DataJson'), '$.messageRef') IS 'text' AND length(json_extract(json_extract(NEW.PayloadJson, '$.DataJson'), '$.messageRef')) BETWEEN 1 AND 512 AND NOT EXISTS (SELECT 1 FROM json_each(json_extract(NEW.PayloadJson, '$.DataJson')) field WHERE instr(field.value,char(0)) > 0 OR instr(field.value,char(10)) > 0 OR instr(field.value,char(13)) > 0) AND json_extract(json_extract(NEW.PayloadJson, '$.DataJson'), '$.provider') IN ('Gmail','Custom') AND NEW.EmailWatchEventId IS NEW.EventId AND json_type(NEW.PayloadJson, '$.RootAgentRunId') IS 'null' AND json_extract(NEW.PayloadJson, '$.TriggerDepth') = 0 AND json_array_length(NEW.PayloadJson, '$.VisitedAutomationIds') = 0 AND EXISTS (SELECT 1 FROM EmailWatches w JOIN TenantCredentials own ON own.CredentialId = w.AccountId AND own.TenantId = w.TenantId JOIN Credentials c ON c.CredentialId = own.CredentialId JOIN EmailAccountProfiles p ON p.CredentialId = c.CredentialId AND p.TenantId = own.TenantId JOIN Tenants tenant ON tenant.TenantId = own.TenantId JOIN AgentCredentialBindings b ON b.TenantId = own.TenantId AND b.CredentialId = own.CredentialId JOIN AgentInstances i ON i.InstanceId = b.AgentInstanceId JOIN SystemIdentities identity ON identity.TenantId = own.TenantId AND identity.AgentInstanceId = i.InstanceId WHERE w.TenantId = NEW.TenantId AND w.AccountId IS json_extract(json_extract(NEW.PayloadJson, '$.DataJson'), '$.accountId') AND own.DeletedAt IS NULL AND c.Purpose = 'EmailAccount' AND c.Status = 'Active' AND length(c.ProtectedPayload) > 0 AND tenant.Status = 0 AND w.Enabled = 1 AND json_extract(w.StateJson, '$.Paused') = 0 AND json_type(w.StateJson, '$.Cursor') IS 'object' AND json_extract(w.StateJson, '$.LastCommitHadCursor') = 1 AND json_extract(w.StateJson, '$.LastCommitRuntimeRevision') + 1 = w.RuntimeRevision AND json_extract(w.StateJson, '$.AuthorizationRevision') = c.Revision AND CAST(round((julianday(json_extract(w.StateJson, '$.LastSuccessAtUtc'))-2440587.5)*86400000) AS INTEGER) = NEW.ReceivedAtUtc AND b.AgentInstanceId IS NEW.AgentInstanceId AND b.Reference IS json_extract(json_extract(NEW.PayloadJson, '$.DataJson'), '$.accountRef') AND i.Lifecycle = 'Active' AND identity.Kind = 0 AND identity.PrincipalId = NEW.ExecutorPrincipalId AND json_extract(json_extract(NEW.PayloadJson, '$.DataJson'), '$.provider') IS CASE json_extract(p.ProfileJson, '$.Preset') WHEN 0 THEN 'Gmail' WHEN 1 THEN 'Custom' END AND (json_extract(p.ProfileJson, '$.AuthMethod') = 0 OR json_extract(p.ProfileJson, '$.ConnectionStatus') = 1) AND NEW.DedupeKey IS 'email.message.received:' || replace(lower(NEW.TenantId),'-','') || ':' || replace(w.AccountId,'-','') || ':' || json_extract(w.StateJson, '$.Generation') || ':' || json_extract(json_extract(NEW.PayloadJson, '$.DataJson'), '$.messageRef') || ':' || replace(NEW.AgentInstanceId,'-','')))) AND json_type(NEW.SubscriptionsJson) IS 'array' AND json_array_length(NEW.SubscriptionsJson) <= 1024 AND json_array_length(NEW.SubscriptionsJson) = (SELECT count(*) FROM Automations a JOIN AutomationTriggers t ON t.AutomationId = a.AutomationId WHERE a.TenantId = NEW.TenantId AND a.Status = 0 AND (a.ExpiresAtUtc IS NULL OR a.ExpiresAtUtc > NEW.ReceivedAtUtc) AND t.Enabled = 1 AND t.Kind = 2 AND a.AgentInstanceId = NEW.AgentInstanceId AND a.ExecutorPrincipalId = NEW.ExecutorPrincipalId AND t.CoreEventKey = json_extract(NEW.PayloadJson, '$.Key')) AND (SELECT count(DISTINCT json_extract(value, '$.TriggerId')) FROM json_each(NEW.SubscriptionsJson)) = json_array_length(NEW.SubscriptionsJson) AND NOT EXISTS (SELECT 1 FROM json_each(NEW.SubscriptionsJson) f WHERE NOT COALESCE(((json_type(f.value, '$') IS 'object' AND (SELECT count(*) FROM json_each(f.value, '$')) = (10 + CASE WHEN json_type(f.value, '$.PinnedSpaceId') IS NULL THEN 0 ELSE 1 END + CASE WHEN json_type(f.value, '$.PinnedEnvironmentId') IS NULL THEN 0 ELSE 1 END) AND (SELECT count(DISTINCT key) FROM json_each(f.value, '$') WHERE key IN ('AutomationId','Owner','TriggerRevision','FilterExpression','Dispatch','SourceKind','ResourceId','TriggerId','ExpressionVersion','FilterHash')) = 10 AND ((json_type(f.value, '$.PinnedSpaceId') IS NULL AND json_type(f.value, '$.PinnedEnvironmentId') IS NULL) OR (json_type(f.value, '$.PinnedSpaceId')='text' AND length(json_extract(f.value, '$.PinnedSpaceId'))=36 AND json_extract(f.value, '$.PinnedSpaceId') NOT GLOB '*[^0-9a-f-]*' AND json_extract(f.value, '$.PinnedSpaceId')<>'00000000-0000-0000-0000-000000000000' AND substr(json_extract(f.value, '$.PinnedSpaceId'),9,1)='-' AND substr(json_extract(f.value, '$.PinnedSpaceId'),14,1)='-' AND substr(json_extract(f.value, '$.PinnedSpaceId'),19,1)='-' AND substr(json_extract(f.value, '$.PinnedSpaceId'),24,1)='-' AND EXISTS(SELECT 1 FROM Spaces ps WHERE lower(ps.TenantId)=lower(json_extract(f.value, '$.Owner.TenantId')) AND lower(ps.SpaceId)=json_extract(f.value, '$.PinnedSpaceId') AND (ps.Kind=1 OR ps.OwnerInstanceId=json_extract(f.value, '$.Owner.AgentInstanceId'))) AND (json_type(f.value, '$.PinnedEnvironmentId') IS NULL OR (json_type(f.value, '$.PinnedEnvironmentId')='text' AND length(json_extract(f.value, '$.PinnedEnvironmentId'))=36 AND json_extract(f.value, '$.PinnedEnvironmentId') NOT GLOB '*[^0-9a-f-]*' AND json_extract(f.value, '$.PinnedEnvironmentId')<>'00000000-0000-0000-0000-000000000000' AND substr(json_extract(f.value, '$.PinnedEnvironmentId'),9,1)='-' AND substr(json_extract(f.value, '$.PinnedEnvironmentId'),14,1)='-' AND substr(json_extract(f.value, '$.PinnedEnvironmentId'),19,1)='-' AND substr(json_extract(f.value, '$.PinnedEnvironmentId'),24,1)='-' AND EXISTS(SELECT 1 FROM SpaceEnvironments pe WHERE lower(pe.TenantId)=lower(json_extract(f.value, '$.Owner.TenantId')) AND lower(pe.SpaceId)=json_extract(f.value, '$.PinnedSpaceId') AND lower(pe.EnvironmentId)=json_extract(f.value, '$.PinnedEnvironmentId'))))))) AND (json_type(f.value, '$.Owner') IS 'object' AND (SELECT count(*) FROM json_each(f.value, '$.Owner')) = 4 AND (SELECT count(DISTINCT key) FROM json_each(f.value, '$.Owner') WHERE key IN ('AgentInstanceId','profileId','TenantId','ExecutorPrincipalId')) = 4) AND json_extract(f.value, '$.Owner.AgentInstanceId') IS json_extract(f.value, '$.Owner.AgentInstanceId') AND lower(json_extract(f.value, '$.Owner.TenantId')) IS lower(NEW.TenantId) AND lower(json_extract(f.value, '$.Owner.ExecutorPrincipalId')) IS lower(json_extract(f.value, '$.Owner.ExecutorPrincipalId')) AND json_type(f.value, '$.Owner.profileId') IS 'null' AND (json_type(f.value, '$.Dispatch') IS 'object' AND (SELECT count(*) FROM json_each(f.value, '$.Dispatch')) = 2 AND (SELECT count(DISTINCT key) FROM json_each(f.value, '$.Dispatch') WHERE key IN ('Mode','WindowSeconds')) = 2) AND json_extract(f.value, '$.ExpressionVersion') IS 'js-expression-v1' AND json_extract(f.value, '$.TriggerRevision') >= 1 AND (json_type(f.value, '$.FilterExpression') IS 'null' OR (json_type(f.value, '$.FilterExpression') IS 'text' AND length(json_extract(f.value, '$.FilterExpression')) <= 1024)) AND ((json_extract(f.value, '$.Dispatch.Mode') = 0 AND json_type(f.value, '$.Dispatch.WindowSeconds') IS 'null') OR (json_extract(f.value, '$.Dispatch.Mode') = 1 AND json_extract(f.value, '$.Dispatch.WindowSeconds') BETWEEN 60 AND 3600)) AND json_extract(f.value, '$.SourceKind') = 3 AND json_extract(f.value, '$.ResourceId') IS NULL AND EXISTS (SELECT 1 FROM Automations a JOIN AutomationTriggers t ON t.AutomationId = a.AutomationId WHERE a.TenantId = NEW.TenantId AND a.Status = 0 AND (a.ExpiresAtUtc IS NULL OR a.ExpiresAtUtc > NEW.ReceivedAtUtc) AND t.Enabled = 1 AND t.Kind = 2 AND a.AgentInstanceId = NEW.AgentInstanceId AND a.ExecutorPrincipalId = NEW.ExecutorPrincipalId AND t.CoreEventKey = json_extract(NEW.PayloadJson, '$.Key') AND a.AutomationId IS json_extract(f.value, '$.AutomationId') AND a.AgentInstanceId IS json_extract(f.value, '$.Owner.AgentInstanceId') AND lower(a.ExecutorPrincipalId) IS json_extract(f.value, '$.Owner.ExecutorPrincipalId') AND t.TriggerId IS json_extract(f.value, '$.TriggerId') AND t.Revision = json_extract(f.value, '$.TriggerRevision') AND t.FilterExpression IS json_extract(f.value, '$.FilterExpression') AND t.DispatchMode = json_extract(f.value, '$.Dispatch.Mode') AND t.DispatchWindowSeconds IS json_extract(f.value, '$.Dispatch.WindowSeconds'))),0)))),0);
END;

CREATE TRIGGER TR_TenantCore_RunCausation_Insert BEFORE INSERT ON CoreEvents WHEN NEW.TenantId IS NOT NULL AND json_extract(NEW.PayloadJson,'$.Key') IN ('run.completed','run.failed') BEGIN SELECT RAISE(ABORT,'Core Run causation requires its actual accepted root') WHERE NOT COALESCE((EXISTS (SELECT 1 FROM AgentRuns source JOIN AgentRuns root ON root.ActivationId=source.RootActivationId
    AND root.TenantId=source.TenantId AND root.AgentInstanceId=source.AgentInstanceId AND root.ExecutorPrincipalId=source.ExecutorPrincipalId
    LEFT JOIN TriggerOccurrences o ON o.AcceptedAgentRunId=root.AgentRunId AND o.ExecutionSessionId=root.SessionId AND o.TenantId=root.TenantId
        AND o.AgentInstanceId=root.AgentInstanceId AND o.ExecutorPrincipalId=root.ExecutorPrincipalId AND o.Disposition=6
    WHERE source.TenantId=NEW.TenantId AND source.AgentRunId=json_extract(json_extract(NEW.PayloadJson,'$.DataJson'),'$.agentRunId')
        AND root.RootActivationId=root.ActivationId
        AND ((json_extract(root.PayloadJson,'$.admission.executionContext.origin')=0 AND json_extract(root.PayloadJson,'$.admission.activation.kind')=0)
            OR (o.SourceKind IN (0,1,2,3) AND json_extract(root.PayloadJson,'$.admission.executionContext.origin')=CASE o.SourceKind WHEN 0 THEN 4 WHEN 1 THEN 5 WHEN 2 THEN 7 WHEN 3 THEN 6 END
                AND json_extract(root.PayloadJson,'$.admission.activation.triggerOccurrenceId') IS o.OccurrenceId))
        AND json_extract(NEW.PayloadJson,'$.RootAgentRunId') IS coalesce(json_extract(o.EvidenceJson,'$.triggerContext.causation.rootAgentRunId'),root.AgentRunId)
        AND json_extract(NEW.PayloadJson,'$.TriggerDepth')=coalesce(json_extract(o.EvidenceJson,'$.triggerContext.causation.triggerDepth'),0)
        AND json_extract(NEW.PayloadJson,'$.VisitedAutomationIds') IS coalesce(json_extract(o.EvidenceJson,'$.triggerContext.causation.visitedAutomationIds'),'[]')) OR EXISTS (SELECT 1 FROM AgentRuns source
    JOIN TenantDelegatedTasks task ON task.TenantId=source.TenantId AND lower(task.TaskId)=lower(json_extract(source.PayloadJson,'$.admission.delegatedTask.taskId'))
        AND task.WorkerSessionId=source.SessionId AND task.WorkerInstanceId=source.AgentInstanceId
        AND lower(json_extract(task.Json,'$.Assignment.PrincipalId'))=lower(source.ExecutorPrincipalId)
        AND lower(task.RootActivationId)=lower(source.RootActivationId)
    JOIN AgentRuns initial ON initial.AgentRunId=json_extract(task.Json,'$.InitialWorkerRunId') AND initial.TenantId=task.TenantId
        AND initial.SessionId=source.SessionId AND initial.AgentInstanceId=source.AgentInstanceId AND initial.ExecutorPrincipalId=source.ExecutorPrincipalId
        AND json_extract(initial.PayloadJson,'$.admission.activation.kind')=12 AND json_extract(initial.PayloadJson,'$.admission.executionContext.origin')=12
        AND json_extract(initial.PayloadJson,'$.admission.delegatedTask.assignmentHash')=json_extract(source.PayloadJson,'$.admission.delegatedTask.assignmentHash')
    JOIN AgentRuns root ON root.TenantId=task.TenantId AND lower(root.ActivationId)=lower(task.RootActivationId) AND root.RootActivationId=root.ActivationId
    LEFT JOIN TriggerOccurrences o ON o.AcceptedAgentRunId=root.AgentRunId AND o.ExecutionSessionId=root.SessionId AND o.TenantId=root.TenantId
        AND o.AgentInstanceId=root.AgentInstanceId AND o.ExecutorPrincipalId=root.ExecutorPrincipalId AND o.Disposition=6
    WHERE source.TenantId=NEW.TenantId AND source.AgentRunId=json_extract(json_extract(NEW.PayloadJson,'$.DataJson'),'$.agentRunId')
        AND ((json_extract(root.PayloadJson,'$.admission.executionContext.origin')=0 AND json_extract(root.PayloadJson,'$.admission.activation.kind')=0)
            OR (o.SourceKind IN (0,1,2,3) AND json_extract(root.PayloadJson,'$.admission.executionContext.origin')=CASE o.SourceKind WHEN 0 THEN 4 WHEN 1 THEN 5 WHEN 2 THEN 7 WHEN 3 THEN 6 END)
            OR (json_extract(root.PayloadJson,'$.admission.executionContext.origin')=12 AND json_extract(root.PayloadJson,'$.admission.activation.kind')=12
                AND EXISTS(SELECT 1 FROM TenantDelegatedTasks humanTask WHERE humanTask.TenantId=root.TenantId
                    AND json_extract(humanTask.Json,'$.InitialWorkerRunId')=root.AgentRunId AND json_extract(humanTask.Json,'$.Source.ActorKind')=1
                    AND json_type(humanTask.Json,'$.Source.RunId')='null' AND json_type(humanTask.Json,'$.Source.HumanCommandId')='text')))
        AND json_extract(NEW.PayloadJson,'$.RootAgentRunId') IS coalesce(json_extract(o.EvidenceJson,'$.triggerContext.causation.rootAgentRunId'),root.AgentRunId)
        AND json_extract(NEW.PayloadJson,'$.TriggerDepth')=coalesce(json_extract(o.EvidenceJson,'$.triggerContext.causation.triggerDepth'),0)
        AND json_extract(NEW.PayloadJson,'$.VisitedAutomationIds') IS coalesce(json_extract(o.EvidenceJson,'$.triggerContext.causation.visitedAutomationIds'),'[]')) OR EXISTS (SELECT 1 FROM AgentRuns source
    JOIN TenantTaskNotifications notice ON notice.TenantId=source.TenantId
        AND lower(notice.NotificationId)=lower(json_extract(source.PayloadJson,'$.admission.taskReport.notificationId'))
    JOIN TenantDelegatedTasks task ON task.TenantId=notice.TenantId AND task.TaskId=notice.TaskId
        AND lower(task.TaskId)=lower(json_extract(source.PayloadJson,'$.admission.taskReport.taskId'))
        AND task.Revision=json_extract(source.PayloadJson,'$.admission.taskReport.taskRevision')
        AND lower(task.RootActivationId)=lower(source.RootActivationId)
        AND task.SourceSessionId=source.SessionId
        AND lower(json_extract(task.Json,'$.Source.InstanceId'))=lower(source.AgentInstanceId)
        AND json_extract(task.Json,'$.Request.ReportOnCompletion')=1
        AND lower(notice.TerminalRunId)=lower(json_extract(source.PayloadJson,'$.admission.taskReport.terminalRunId'))
    JOIN TenantSessions parentOwner ON parentOwner.TenantId=source.TenantId
        AND parentOwner.SessionId=source.SessionId AND parentOwner.AgentInstanceId=source.AgentInstanceId AND parentOwner.ExecutorPrincipalId=source.ExecutorPrincipalId
    JOIN AgentRuns report ON report.AgentRunId=json_extract(notice.Json,'$.HandlingRunId')
        AND report.TenantId=source.TenantId AND report.SessionId=source.SessionId AND report.AgentInstanceId=source.AgentInstanceId
        AND report.ExecutorPrincipalId=source.ExecutorPrincipalId AND json_extract(report.PayloadJson,'$.admission.activation.kind')=13
        AND json_extract(report.PayloadJson,'$.admission.taskReport')=json_extract(source.PayloadJson,'$.admission.taskReport')
    JOIN AgentRuns root ON root.TenantId=task.TenantId AND lower(root.ActivationId)=lower(task.RootActivationId) AND root.RootActivationId=root.ActivationId
    LEFT JOIN TriggerOccurrences o ON o.AcceptedAgentRunId=root.AgentRunId AND o.TenantId=root.TenantId AND o.Disposition=6
    WHERE source.TenantId=NEW.TenantId AND source.AgentRunId=json_extract(json_extract(NEW.PayloadJson,'$.DataJson'),'$.agentRunId')
        AND (json_extract(root.PayloadJson,'$.admission.activation.kind')=0
            OR o.SourceKind IN (0,1,2,3)
            OR (json_extract(root.PayloadJson,'$.admission.activation.kind')=12
                AND EXISTS(SELECT 1 FROM TenantDelegatedTasks humanTask WHERE humanTask.TenantId=root.TenantId
                    AND json_extract(humanTask.Json,'$.InitialWorkerRunId')=root.AgentRunId AND json_extract(humanTask.Json,'$.Source.ActorKind')=1
                    AND json_type(humanTask.Json,'$.Source.RunId')='null' AND json_type(humanTask.Json,'$.Source.HumanCommandId')='text')))
        AND json_extract(NEW.PayloadJson,'$.RootAgentRunId') IS coalesce(json_extract(o.EvidenceJson,'$.triggerContext.causation.rootAgentRunId'),root.AgentRunId)
        AND json_extract(NEW.PayloadJson,'$.TriggerDepth')=coalesce(json_extract(o.EvidenceJson,'$.triggerContext.causation.triggerDepth'),0)
        AND json_extract(NEW.PayloadJson,'$.VisitedAutomationIds') IS coalesce(json_extract(o.EvidenceJson,'$.triggerContext.causation.visitedAutomationIds'),'[]')) OR EXISTS(SELECT 1 FROM AgentRuns source
    JOIN AgentRuns root ON root.TenantId=source.TenantId AND root.ActivationId=source.RootActivationId
        AND root.AgentInstanceId=source.AgentInstanceId AND root.ExecutorPrincipalId=source.ExecutorPrincipalId AND root.SessionId=source.SessionId
        AND root.RootActivationId=root.ActivationId AND json_extract(root.PayloadJson,'$.admission.activation.kind')=14
        AND json_extract(root.PayloadJson,'$.admission.executionContext.origin')=14
    JOIN TenantAgentMessages message ON message.TenantId=root.TenantId
        AND lower(message.MessageId)=lower(json_extract(root.PayloadJson,'$.admission.agentMessage.messageId'))
        AND lower(message.RecipientInstanceId)=lower(root.AgentInstanceId)
        AND json_extract(message.ReceiptJson,'$.WakeRunId')=root.AgentRunId AND json_extract(message.ReceiptJson,'$.WakeSessionId')=root.SessionId
        AND json_extract(message.ReceiptJson,'$.WakeHops') BETWEEN 1 AND 2
        AND json_extract(root.PayloadJson,'$.admission.agentMessage')=json_extract(source.PayloadJson,'$.admission.agentMessage')
    JOIN TenantAgentThreads thread ON thread.TenantId=message.TenantId AND thread.ThreadId=message.ThreadId AND thread.TaskId IS NULL
    WHERE source.TenantId=NEW.TenantId AND source.AgentRunId=json_extract(json_extract(NEW.PayloadJson,'$.DataJson'),'$.agentRunId')
        AND json_extract(NEW.PayloadJson,'$.RootAgentRunId') IS root.AgentRunId
        AND json_extract(NEW.PayloadJson,'$.TriggerDepth')=0 AND json_extract(NEW.PayloadJson,'$.VisitedAutomationIds')='[]') OR EXISTS(SELECT 1 FROM AgentRuns source
 JOIN TenantDelegatedTasks task ON task.TenantId=source.TenantId AND lower(task.TaskId)=lower(json_extract(source.PayloadJson,'$.admission.delegatedTask.taskId'))
   AND task.WorkerSessionId=source.SessionId AND task.WorkerInstanceId=source.AgentInstanceId
   AND lower(json_extract(task.Json,'$.Assignment.PrincipalId'))=lower(source.ExecutorPrincipalId)
 JOIN AgentRuns initial ON initial.TenantId=task.TenantId AND initial.AgentRunId=json_extract(task.Json,'$.InitialWorkerRunId')
   AND initial.SessionId=source.SessionId AND initial.AgentInstanceId=source.AgentInstanceId AND initial.ExecutorPrincipalId=source.ExecutorPrincipalId
   AND initial.ActivationId=initial.RootActivationId AND source.RootActivationId=initial.ActivationId
   AND lower(task.RootActivationId)=lower(initial.ActivationId)
 JOIN TenantWorkflowExecutions process ON process.TenantId=task.TenantId AND process.ExecutionId=task.WorkflowExecutionId
 JOIN TenantWorkflowSteps step ON step.TenantId=process.TenantId AND step.ExecutionId=process.ExecutionId
   AND lower(step.OperationId)=lower(json_extract(task.Json,'$.Source.Workflow.OperationId'))
   AND step.NodeId=json_extract(task.Json,'$.Source.Workflow.NodeId') AND json_extract(step.Json,'$.Status')>=1
   AND lower(json_extract(step.Json,'$.TaskId'))=lower(task.TaskId)
 WHERE source.TenantId=NEW.TenantId AND source.AgentRunId=json_extract(json_extract(NEW.PayloadJson,'$.DataJson'),'$.agentRunId')
   AND json_extract(initial.PayloadJson,'$.admission.activation.kind')=12 AND json_extract(initial.PayloadJson,'$.admission.executionContext.origin')=12
   AND lower(json_extract(initial.PayloadJson,'$.admission.activation.sourceEventId'))=lower(step.OperationId)
   AND json_extract(source.PayloadJson,'$.admission.delegatedTask.assignmentHash')=json_extract(initial.PayloadJson,'$.admission.delegatedTask.assignmentHash')
   AND json_extract(task.Json,'$.Source.ActorKind')=2 AND json_type(task.Json,'$.Source.SessionId')='null'
   AND json_type(task.Json,'$.Source.RunId')='null' AND json_type(task.Json,'$.Source.HumanCommandId')='null'
   AND json_extract(task.Json,'$.Source.Workflow.GraphHash')=json_extract(process.Json,'$.GraphHash')
   AND json_extract(task.Json,'$.Source.Workflow.Version')=process.Version
   AND json_extract(process.Json,'$.Source.Kind') IN (0,1,2)
   AND json_extract(task.Json,'$.Source.Workflow.Causation') IS json_extract(process.Json,'$.Source.Causation')
   AND json_extract(NEW.PayloadJson,'$.RootAgentRunId') IS coalesce(json_extract(process.Json,'$.Source.Causation.RootAgentRunId'),initial.AgentRunId)
   AND json_extract(NEW.PayloadJson,'$.TriggerDepth')=coalesce(json_extract(process.Json,'$.Source.Causation.TriggerDepth'),0)
   AND json_extract(NEW.PayloadJson,'$.VisitedAutomationIds') IS coalesce(json_extract(process.Json,'$.Source.Causation.VisitedAutomationIds'),'[]'))),0); END;

CREATE TRIGGER TR_TenantEmail_Occurrence_Insert BEFORE INSERT ON TriggerOccurrences
WHEN NEW.TenantId IS NOT NULL AND NEW.SourceKind = 3 AND EXISTS (SELECT 1 FROM json_each(json_extract(NEW.EvidenceJson, '$.triggerContext.admissionSource'), '$.EventIds') source JOIN CoreEvents fact ON fact.EventId IS source.value AND fact.TenantId = NEW.TenantId WHERE fact.EmailWatchEventId IS NOT NULL)
BEGIN
SELECT RAISE(ABORT, 'email occurrence evidence requires its literal included source facts') WHERE NOT COALESCE((NOT EXISTS (SELECT 1 FROM json_each(json_extract(NEW.EvidenceJson, '$.triggerContext.admissionSource'), '$.EventIds') source WHERE NOT EXISTS (SELECT 1 FROM CoreEvents fact WHERE fact.EventId IS source.value AND fact.TenantId = NEW.TenantId AND fact.EmailWatchEventId IS fact.EventId AND json_extract(fact.PayloadJson, '$.Key') IS 'email.message.received')) AND ((json_extract(json_extract(NEW.EvidenceJson, '$.triggerContext.admissionSource'), '$.IsBucket') = 0 AND EXISTS (SELECT 1 FROM CoreEvents fact WHERE fact.TenantId = NEW.TenantId AND fact.EventId IS NEW.SourceEventId AND json(json_extract(fact.PayloadJson, '$.DataJson')) IS json(json_extract(json_extract(NEW.EvidenceJson, '$.triggerContext'), '$.source')))) OR (json_extract(json_extract(NEW.EvidenceJson, '$.triggerContext.admissionSource'), '$.IsBucket') = 1 AND json_array_length(json_extract(NEW.EvidenceJson, '$.triggerContext'), '$.sources') = json_array_length(json_extract(NEW.EvidenceJson, '$.triggerContext.admissionSource'), '$.EventIds') AND json_extract(json_extract(NEW.EvidenceJson, '$.triggerContext'), '$.count') = json_array_length(json_extract(NEW.EvidenceJson, '$.triggerContext.admissionSource'), '$.EventIds') AND NOT EXISTS (SELECT 1 FROM json_each(json_extract(NEW.EvidenceJson, '$.triggerContext'), '$.sources') retained WHERE NOT COALESCE(((json_type(retained.value, '$') IS 'object' AND (SELECT count(*) FROM json_each(retained.value, '$')) = 2 AND (SELECT count(DISTINCT key) FROM json_each(retained.value, '$') WHERE key IN ('eventId','data')) = 2) AND json_extract(retained.value, '$.eventId') IS json_extract(json_extract(NEW.EvidenceJson, '$.triggerContext.admissionSource'), '$.EventIds[' || retained.key || ']') AND EXISTS (SELECT 1 FROM CoreEvents fact WHERE fact.TenantId = NEW.TenantId AND fact.EventId IS json_extract(retained.value, '$.eventId') AND json(json_extract(fact.PayloadJson, '$.DataJson')) IS json(json_extract(retained.value, '$.data')))),0))))),0);
END;

CREATE TRIGGER TR_TenantEvent_Bucket_Insert BEFORE INSERT ON CoreEventBuckets
BEGIN
SELECT RAISE(ABORT, 'bucket requires its exact bounded matched source facts') WHERE NOT COALESCE(((NEW.TenantId IS NULL AND NEW.ExecutorPrincipalId IS NULL AND NEW.AgentInstanceId IS NULL AND NEW.ReservedOccurrenceId IS NULL AND NOT EXISTS (SELECT 1 FROM json_tree(NEW.PayloadJson, '$') WHERE lower(key) IN ('tenantid','executorprincipalid')) AND NOT EXISTS (SELECT 1 FROM json_tree(NEW.PayloadJson) field JOIN SystemIdentities i ON i.AgentInstanceId = field.value WHERE lower(field.key) = 'agentinstanceid' AND i.Kind = 0 AND i.TenantId IS NOT NULL)) OR (NEW.TenantId IS NOT NULL AND json_type(NEW.PayloadJson) IS 'object' AND (SELECT count(*) FROM json_each(NEW.PayloadJson)) IN (6,7) AND NOT EXISTS (SELECT 1 FROM json_each(NEW.PayloadJson) WHERE key NOT IN ('BucketId','Subscription','DueAtUtc','Sources','Flushed','CompletionCode','ReservedOccurrenceId')) AND (SELECT count(DISTINCT key) FROM json_each(NEW.PayloadJson)) = (SELECT count(*) FROM json_each(NEW.PayloadJson)) AND (json_type(json_extract(NEW.PayloadJson, '$.Subscription'), '$') IS 'object' AND (SELECT count(*) FROM json_each(json_extract(NEW.PayloadJson, '$.Subscription'), '$')) = (10 + CASE WHEN json_type(json_extract(NEW.PayloadJson, '$.Subscription'), '$.PinnedSpaceId') IS NULL THEN 0 ELSE 1 END + CASE WHEN json_type(json_extract(NEW.PayloadJson, '$.Subscription'), '$.PinnedEnvironmentId') IS NULL THEN 0 ELSE 1 END) AND (SELECT count(DISTINCT key) FROM json_each(json_extract(NEW.PayloadJson, '$.Subscription'), '$') WHERE key IN ('AutomationId','Owner','TriggerRevision','FilterExpression','Dispatch','SourceKind','ResourceId','TriggerId','ExpressionVersion','FilterHash')) = 10 AND ((json_type(json_extract(NEW.PayloadJson, '$.Subscription'), '$.PinnedSpaceId') IS NULL AND json_type(json_extract(NEW.PayloadJson, '$.Subscription'), '$.PinnedEnvironmentId') IS NULL) OR (json_type(json_extract(NEW.PayloadJson, '$.Subscription'), '$.PinnedSpaceId')='text' AND length(json_extract(json_extract(NEW.PayloadJson, '$.Subscription'), '$.PinnedSpaceId'))=36 AND json_extract(json_extract(NEW.PayloadJson, '$.Subscription'), '$.PinnedSpaceId') NOT GLOB '*[^0-9a-f-]*' AND json_extract(json_extract(NEW.PayloadJson, '$.Subscription'), '$.PinnedSpaceId')<>'00000000-0000-0000-0000-000000000000' AND substr(json_extract(json_extract(NEW.PayloadJson, '$.Subscription'), '$.PinnedSpaceId'),9,1)='-' AND substr(json_extract(json_extract(NEW.PayloadJson, '$.Subscription'), '$.PinnedSpaceId'),14,1)='-' AND substr(json_extract(json_extract(NEW.PayloadJson, '$.Subscription'), '$.PinnedSpaceId'),19,1)='-' AND substr(json_extract(json_extract(NEW.PayloadJson, '$.Subscription'), '$.PinnedSpaceId'),24,1)='-' AND EXISTS(SELECT 1 FROM Spaces ps WHERE lower(ps.TenantId)=lower(json_extract(json_extract(NEW.PayloadJson, '$.Subscription'), '$.Owner.TenantId')) AND lower(ps.SpaceId)=json_extract(json_extract(NEW.PayloadJson, '$.Subscription'), '$.PinnedSpaceId') AND (ps.Kind=1 OR ps.OwnerInstanceId=json_extract(json_extract(NEW.PayloadJson, '$.Subscription'), '$.Owner.AgentInstanceId'))) AND (json_type(json_extract(NEW.PayloadJson, '$.Subscription'), '$.PinnedEnvironmentId') IS NULL OR (json_type(json_extract(NEW.PayloadJson, '$.Subscription'), '$.PinnedEnvironmentId')='text' AND length(json_extract(json_extract(NEW.PayloadJson, '$.Subscription'), '$.PinnedEnvironmentId'))=36 AND json_extract(json_extract(NEW.PayloadJson, '$.Subscription'), '$.PinnedEnvironmentId') NOT GLOB '*[^0-9a-f-]*' AND json_extract(json_extract(NEW.PayloadJson, '$.Subscription'), '$.PinnedEnvironmentId')<>'00000000-0000-0000-0000-000000000000' AND substr(json_extract(json_extract(NEW.PayloadJson, '$.Subscription'), '$.PinnedEnvironmentId'),9,1)='-' AND substr(json_extract(json_extract(NEW.PayloadJson, '$.Subscription'), '$.PinnedEnvironmentId'),14,1)='-' AND substr(json_extract(json_extract(NEW.PayloadJson, '$.Subscription'), '$.PinnedEnvironmentId'),19,1)='-' AND substr(json_extract(json_extract(NEW.PayloadJson, '$.Subscription'), '$.PinnedEnvironmentId'),24,1)='-' AND EXISTS(SELECT 1 FROM SpaceEnvironments pe WHERE lower(pe.TenantId)=lower(json_extract(json_extract(NEW.PayloadJson, '$.Subscription'), '$.Owner.TenantId')) AND lower(pe.SpaceId)=json_extract(json_extract(NEW.PayloadJson, '$.Subscription'), '$.PinnedSpaceId') AND lower(pe.EnvironmentId)=json_extract(json_extract(NEW.PayloadJson, '$.Subscription'), '$.PinnedEnvironmentId'))))))) AND (json_type(json_extract(NEW.PayloadJson, '$.Subscription'), '$.Owner') IS 'object' AND (SELECT count(*) FROM json_each(json_extract(NEW.PayloadJson, '$.Subscription'), '$.Owner')) = 4 AND (SELECT count(DISTINCT key) FROM json_each(json_extract(NEW.PayloadJson, '$.Subscription'), '$.Owner') WHERE key IN ('AgentInstanceId','profileId','TenantId','ExecutorPrincipalId')) = 4) AND json_extract(json_extract(NEW.PayloadJson, '$.Subscription'), '$.Owner.AgentInstanceId') IS NEW.AgentInstanceId AND lower(json_extract(json_extract(NEW.PayloadJson, '$.Subscription'), '$.Owner.TenantId')) IS lower(NEW.TenantId) AND lower(json_extract(json_extract(NEW.PayloadJson, '$.Subscription'), '$.Owner.ExecutorPrincipalId')) IS lower(NEW.ExecutorPrincipalId) AND json_type(json_extract(NEW.PayloadJson, '$.Subscription'), '$.Owner.profileId') IS 'null' AND (json_type(json_extract(NEW.PayloadJson, '$.Subscription'), '$.Dispatch') IS 'object' AND (SELECT count(*) FROM json_each(json_extract(NEW.PayloadJson, '$.Subscription'), '$.Dispatch')) = 2 AND (SELECT count(DISTINCT key) FROM json_each(json_extract(NEW.PayloadJson, '$.Subscription'), '$.Dispatch') WHERE key IN ('Mode','WindowSeconds')) = 2) AND json_extract(json_extract(NEW.PayloadJson, '$.Subscription'), '$.ExpressionVersion') IS 'js-expression-v1' AND json_extract(json_extract(NEW.PayloadJson, '$.Subscription'), '$.TriggerRevision') >= 1 AND (json_type(json_extract(NEW.PayloadJson, '$.Subscription'), '$.FilterExpression') IS 'null' OR (json_type(json_extract(NEW.PayloadJson, '$.Subscription'), '$.FilterExpression') IS 'text' AND length(json_extract(json_extract(NEW.PayloadJson, '$.Subscription'), '$.FilterExpression')) <= 1024)) AND ((json_extract(json_extract(NEW.PayloadJson, '$.Subscription'), '$.Dispatch.Mode') = 0 AND json_type(json_extract(NEW.PayloadJson, '$.Subscription'), '$.Dispatch.WindowSeconds') IS 'null') OR (json_extract(json_extract(NEW.PayloadJson, '$.Subscription'), '$.Dispatch.Mode') = 1 AND json_extract(json_extract(NEW.PayloadJson, '$.Subscription'), '$.Dispatch.WindowSeconds') BETWEEN 60 AND 3600)) AND json_extract(NEW.PayloadJson, '$.Subscription.AutomationId') IS NEW.AutomationId AND json_extract(NEW.PayloadJson, '$.Subscription.TriggerId') IS NEW.TriggerId AND json_extract(NEW.PayloadJson, '$.Subscription.TriggerRevision') = NEW.TriggerRevision AND json_extract(NEW.PayloadJson, '$.Subscription.Dispatch.Mode') = 1 AND json_extract(NEW.PayloadJson, '$.Flushed') = NEW.Flushed AND json_extract(NEW.PayloadJson, '$.ReservedOccurrenceId') IS NEW.ReservedOccurrenceId AND CAST(round((julianday(json_extract(NEW.PayloadJson, '$.DueAtUtc')) - 2440587.5)*86400000) AS INTEGER) = NEW.DueAtUtc AND json_type(NEW.PayloadJson, '$.Sources') IS 'array' AND json_array_length(NEW.PayloadJson, '$.Sources') BETWEEN 1 AND 24 AND (SELECT count(DISTINCT json_extract(value, '$.EventId')) FROM json_each(NEW.PayloadJson, '$.Sources')) = json_array_length(NEW.PayloadJson, '$.Sources') AND NOT EXISTS (SELECT 1 FROM json_each(NEW.PayloadJson, '$.Sources') e WHERE NOT COALESCE(((json_type(e.value, '$') IS 'object' AND (SELECT count(*) FROM json_each(e.value, '$')) = 8 AND (SELECT count(DISTINCT key) FROM json_each(e.value, '$') WHERE key IN ('EventId','Owner','Key','ReceivedAtUtc','DataJson','RootAgentRunId','TriggerDepth','VisitedAutomationIds')) = 8) AND (json_type(e.value, '$.Owner') IS 'object' AND (SELECT count(*) FROM json_each(e.value, '$.Owner')) = 4 AND (SELECT count(DISTINCT key) FROM json_each(e.value, '$.Owner') WHERE key IN ('AgentInstanceId','profileId','TenantId','ExecutorPrincipalId')) = 4) AND json_extract(e.value, '$.Owner.AgentInstanceId') IS NEW.AgentInstanceId AND lower(json_extract(e.value, '$.Owner.TenantId')) IS lower(NEW.TenantId) AND lower(json_extract(e.value, '$.Owner.ExecutorPrincipalId')) IS lower(NEW.ExecutorPrincipalId) AND json_type(e.value, '$.Owner.profileId') IS 'null' AND CAST(round((julianday(json_extract(e.value, '$.ReceivedAtUtc'))-2440587.5)*86400000) AS INTEGER) < NEW.DueAtUtc AND CAST(round((julianday(json_extract(e.value, '$.ReceivedAtUtc'))-2440587.5)*86400000) AS INTEGER) >= NEW.DueAtUtc - 1000 * json_extract(NEW.PayloadJson, '$.Subscription.Dispatch.WindowSeconds') AND ((json_extract(NEW.PayloadJson, '$.Subscription.SourceKind') = 3 AND EXISTS (SELECT 1 FROM CoreEventDeliveries d JOIN CoreEvents f ON f.EventId = d.EventId WHERE d.TenantId = NEW.TenantId AND d.EventId = json_extract(e.value, '$.EventId') AND d.TriggerId = NEW.TriggerId AND d.Status IN (1,6) AND json(d.SnapshotJson) IS json_extract(NEW.PayloadJson, '$.Subscription') AND json_extract(e.value, '$.Key') IS json_extract(f.PayloadJson, '$.Key') AND json_extract(e.value, '$.DataJson') IS json_extract(f.PayloadJson, '$.DataJson') AND json_extract(e.value, '$.RootAgentRunId') IS json_extract(f.PayloadJson, '$.RootAgentRunId') AND json_extract(e.value, '$.TriggerDepth') IS json_extract(f.PayloadJson, '$.TriggerDepth') AND json_extract(e.value, '$.VisitedAutomationIds') IS json_extract(f.PayloadJson, '$.VisitedAutomationIds') AND CAST(round((julianday(json_extract(e.value, '$.ReceivedAtUtc'))-2440587.5)*86400000) AS INTEGER) = f.ReceivedAtUtc)) OR (json_extract(NEW.PayloadJson, '$.Subscription.SourceKind') = 1 AND EXISTS (SELECT 1 FROM ExternalEventDeliveries d JOIN ExternalEvents f ON f.EventId = d.EventId JOIN WebhookEvents w ON w.ResourceId = f.ResourceId WHERE d.TenantId = NEW.TenantId AND d.EventId = json_extract(e.value, '$.EventId') AND d.TriggerId = NEW.TriggerId AND d.Status IN (0,5) AND json_extract(d.DecisionJson, '$.Matched') = 1 AND json(d.SnapshotJson) IS json_extract(NEW.PayloadJson, '$.Subscription') AND json_extract(e.value, '$.Key') IS w.EventKey AND json(json_extract(e.value, '$.DataJson')) IS json_extract(f.EvidenceJson, '$.data') AND json_type(e.value, '$.RootAgentRunId') IS 'null' AND json_extract(e.value, '$.TriggerDepth') = 0 AND json_array_length(e.value, '$.VisitedAutomationIds') = 0 AND CAST(round((julianday(json_extract(e.value, '$.ReceivedAtUtc'))-2440587.5)*86400000) AS INTEGER) = f.AdmittedAtUtc))) AND NOT EXISTS (SELECT 1 FROM CoreEventBuckets existing, json_each(existing.PayloadJson, '$.Sources') covered WHERE existing.TenantId = NEW.TenantId AND existing.TriggerId = NEW.TriggerId AND existing.BucketId <> NEW.BucketId AND json_extract(covered.value, '$.EventId') IS json_extract(e.value, '$.EventId'))),0)) AND (NEW.ReservedOccurrenceId IS NULL OR EXISTS (SELECT 1 FROM TriggerOccurrences o WHERE o.OccurrenceId = NEW.ReservedOccurrenceId AND o.TenantId = NEW.TenantId AND o.ExecutorPrincipalId = NEW.ExecutorPrincipalId AND o.AgentInstanceId = NEW.AgentInstanceId AND o.AutomationId = NEW.AutomationId AND o.TriggerId = NEW.TriggerId AND o.SourceEventId = NEW.BucketId AND o.SourceKind = json_extract(NEW.PayloadJson, '$.Subscription.SourceKind') AND ((NEW.Flushed = 0 AND o.Disposition = 3 AND o.DispositionReason = 'admission_approval_pending') OR (NEW.Flushed = 1 AND ((o.Disposition = 6 AND json_type(NEW.PayloadJson, '$.CompletionCode') IS 'null') OR (o.Disposition = 4 AND json_type(NEW.PayloadJson, '$.CompletionCode') IS 'text')))))))),0);
END;

CREATE TRIGGER TR_TenantEvent_Bucket_Update BEFORE UPDATE ON CoreEventBuckets
BEGIN
SELECT RAISE(ABORT, 'tenant bucket identity is immutable') WHERE (OLD.TenantId IS NOT NULL OR NEW.TenantId IS NOT NULL) AND (OLD.TenantId IS NOT NEW.TenantId OR OLD.ExecutorPrincipalId IS NOT NEW.ExecutorPrincipalId OR OLD.AgentInstanceId IS NOT NEW.AgentInstanceId OR OLD.BucketId IS NOT NEW.BucketId OR OLD.AutomationId IS NOT NEW.AutomationId OR OLD.TriggerId IS NOT NEW.TriggerId OR OLD.TriggerRevision IS NOT NEW.TriggerRevision OR OLD.DueAtUtc IS NOT NEW.DueAtUtc);
SELECT RAISE(ABORT, 'reserved or flushed bucket membership is immutable') WHERE OLD.TenantId IS NOT NULL AND ((OLD.ReservedOccurrenceId IS NOT NULL OR OLD.Flushed = 1) AND (json_extract(OLD.PayloadJson, '$.Sources') IS NOT json_extract(NEW.PayloadJson, '$.Sources') OR json_extract(OLD.PayloadJson, '$.Subscription') IS NOT json_extract(NEW.PayloadJson, '$.Subscription')) OR OLD.ReservedOccurrenceId IS NOT NULL AND OLD.ReservedOccurrenceId IS NOT NEW.ReservedOccurrenceId OR OLD.Flushed = 1 AND OLD.PayloadJson IS NOT NEW.PayloadJson);
SELECT RAISE(ABORT, 'bucket requires its exact bounded matched source facts') WHERE NOT COALESCE(((NEW.TenantId IS NULL AND NEW.ExecutorPrincipalId IS NULL AND NEW.AgentInstanceId IS NULL AND NEW.ReservedOccurrenceId IS NULL AND NOT EXISTS (SELECT 1 FROM json_tree(NEW.PayloadJson, '$') WHERE lower(key) IN ('tenantid','executorprincipalid')) AND NOT EXISTS (SELECT 1 FROM json_tree(NEW.PayloadJson) field JOIN SystemIdentities i ON i.AgentInstanceId = field.value WHERE lower(field.key) = 'agentinstanceid' AND i.Kind = 0 AND i.TenantId IS NOT NULL)) OR (NEW.TenantId IS NOT NULL AND json_type(NEW.PayloadJson) IS 'object' AND (SELECT count(*) FROM json_each(NEW.PayloadJson)) IN (6,7) AND NOT EXISTS (SELECT 1 FROM json_each(NEW.PayloadJson) WHERE key NOT IN ('BucketId','Subscription','DueAtUtc','Sources','Flushed','CompletionCode','ReservedOccurrenceId')) AND (SELECT count(DISTINCT key) FROM json_each(NEW.PayloadJson)) = (SELECT count(*) FROM json_each(NEW.PayloadJson)) AND (json_type(json_extract(NEW.PayloadJson, '$.Subscription'), '$') IS 'object' AND (SELECT count(*) FROM json_each(json_extract(NEW.PayloadJson, '$.Subscription'), '$')) = (10 + CASE WHEN json_type(json_extract(NEW.PayloadJson, '$.Subscription'), '$.PinnedSpaceId') IS NULL THEN 0 ELSE 1 END + CASE WHEN json_type(json_extract(NEW.PayloadJson, '$.Subscription'), '$.PinnedEnvironmentId') IS NULL THEN 0 ELSE 1 END) AND (SELECT count(DISTINCT key) FROM json_each(json_extract(NEW.PayloadJson, '$.Subscription'), '$') WHERE key IN ('AutomationId','Owner','TriggerRevision','FilterExpression','Dispatch','SourceKind','ResourceId','TriggerId','ExpressionVersion','FilterHash')) = 10 AND ((json_type(json_extract(NEW.PayloadJson, '$.Subscription'), '$.PinnedSpaceId') IS NULL AND json_type(json_extract(NEW.PayloadJson, '$.Subscription'), '$.PinnedEnvironmentId') IS NULL) OR (json_type(json_extract(NEW.PayloadJson, '$.Subscription'), '$.PinnedSpaceId')='text' AND length(json_extract(json_extract(NEW.PayloadJson, '$.Subscription'), '$.PinnedSpaceId'))=36 AND json_extract(json_extract(NEW.PayloadJson, '$.Subscription'), '$.PinnedSpaceId') NOT GLOB '*[^0-9a-f-]*' AND json_extract(json_extract(NEW.PayloadJson, '$.Subscription'), '$.PinnedSpaceId')<>'00000000-0000-0000-0000-000000000000' AND substr(json_extract(json_extract(NEW.PayloadJson, '$.Subscription'), '$.PinnedSpaceId'),9,1)='-' AND substr(json_extract(json_extract(NEW.PayloadJson, '$.Subscription'), '$.PinnedSpaceId'),14,1)='-' AND substr(json_extract(json_extract(NEW.PayloadJson, '$.Subscription'), '$.PinnedSpaceId'),19,1)='-' AND substr(json_extract(json_extract(NEW.PayloadJson, '$.Subscription'), '$.PinnedSpaceId'),24,1)='-' AND EXISTS(SELECT 1 FROM Spaces ps WHERE lower(ps.TenantId)=lower(json_extract(json_extract(NEW.PayloadJson, '$.Subscription'), '$.Owner.TenantId')) AND lower(ps.SpaceId)=json_extract(json_extract(NEW.PayloadJson, '$.Subscription'), '$.PinnedSpaceId') AND (ps.Kind=1 OR ps.OwnerInstanceId=json_extract(json_extract(NEW.PayloadJson, '$.Subscription'), '$.Owner.AgentInstanceId'))) AND (json_type(json_extract(NEW.PayloadJson, '$.Subscription'), '$.PinnedEnvironmentId') IS NULL OR (json_type(json_extract(NEW.PayloadJson, '$.Subscription'), '$.PinnedEnvironmentId')='text' AND length(json_extract(json_extract(NEW.PayloadJson, '$.Subscription'), '$.PinnedEnvironmentId'))=36 AND json_extract(json_extract(NEW.PayloadJson, '$.Subscription'), '$.PinnedEnvironmentId') NOT GLOB '*[^0-9a-f-]*' AND json_extract(json_extract(NEW.PayloadJson, '$.Subscription'), '$.PinnedEnvironmentId')<>'00000000-0000-0000-0000-000000000000' AND substr(json_extract(json_extract(NEW.PayloadJson, '$.Subscription'), '$.PinnedEnvironmentId'),9,1)='-' AND substr(json_extract(json_extract(NEW.PayloadJson, '$.Subscription'), '$.PinnedEnvironmentId'),14,1)='-' AND substr(json_extract(json_extract(NEW.PayloadJson, '$.Subscription'), '$.PinnedEnvironmentId'),19,1)='-' AND substr(json_extract(json_extract(NEW.PayloadJson, '$.Subscription'), '$.PinnedEnvironmentId'),24,1)='-' AND EXISTS(SELECT 1 FROM SpaceEnvironments pe WHERE lower(pe.TenantId)=lower(json_extract(json_extract(NEW.PayloadJson, '$.Subscription'), '$.Owner.TenantId')) AND lower(pe.SpaceId)=json_extract(json_extract(NEW.PayloadJson, '$.Subscription'), '$.PinnedSpaceId') AND lower(pe.EnvironmentId)=json_extract(json_extract(NEW.PayloadJson, '$.Subscription'), '$.PinnedEnvironmentId'))))))) AND (json_type(json_extract(NEW.PayloadJson, '$.Subscription'), '$.Owner') IS 'object' AND (SELECT count(*) FROM json_each(json_extract(NEW.PayloadJson, '$.Subscription'), '$.Owner')) = 4 AND (SELECT count(DISTINCT key) FROM json_each(json_extract(NEW.PayloadJson, '$.Subscription'), '$.Owner') WHERE key IN ('AgentInstanceId','profileId','TenantId','ExecutorPrincipalId')) = 4) AND json_extract(json_extract(NEW.PayloadJson, '$.Subscription'), '$.Owner.AgentInstanceId') IS NEW.AgentInstanceId AND lower(json_extract(json_extract(NEW.PayloadJson, '$.Subscription'), '$.Owner.TenantId')) IS lower(NEW.TenantId) AND lower(json_extract(json_extract(NEW.PayloadJson, '$.Subscription'), '$.Owner.ExecutorPrincipalId')) IS lower(NEW.ExecutorPrincipalId) AND json_type(json_extract(NEW.PayloadJson, '$.Subscription'), '$.Owner.profileId') IS 'null' AND (json_type(json_extract(NEW.PayloadJson, '$.Subscription'), '$.Dispatch') IS 'object' AND (SELECT count(*) FROM json_each(json_extract(NEW.PayloadJson, '$.Subscription'), '$.Dispatch')) = 2 AND (SELECT count(DISTINCT key) FROM json_each(json_extract(NEW.PayloadJson, '$.Subscription'), '$.Dispatch') WHERE key IN ('Mode','WindowSeconds')) = 2) AND json_extract(json_extract(NEW.PayloadJson, '$.Subscription'), '$.ExpressionVersion') IS 'js-expression-v1' AND json_extract(json_extract(NEW.PayloadJson, '$.Subscription'), '$.TriggerRevision') >= 1 AND (json_type(json_extract(NEW.PayloadJson, '$.Subscription'), '$.FilterExpression') IS 'null' OR (json_type(json_extract(NEW.PayloadJson, '$.Subscription'), '$.FilterExpression') IS 'text' AND length(json_extract(json_extract(NEW.PayloadJson, '$.Subscription'), '$.FilterExpression')) <= 1024)) AND ((json_extract(json_extract(NEW.PayloadJson, '$.Subscription'), '$.Dispatch.Mode') = 0 AND json_type(json_extract(NEW.PayloadJson, '$.Subscription'), '$.Dispatch.WindowSeconds') IS 'null') OR (json_extract(json_extract(NEW.PayloadJson, '$.Subscription'), '$.Dispatch.Mode') = 1 AND json_extract(json_extract(NEW.PayloadJson, '$.Subscription'), '$.Dispatch.WindowSeconds') BETWEEN 60 AND 3600)) AND json_extract(NEW.PayloadJson, '$.Subscription.AutomationId') IS NEW.AutomationId AND json_extract(NEW.PayloadJson, '$.Subscription.TriggerId') IS NEW.TriggerId AND json_extract(NEW.PayloadJson, '$.Subscription.TriggerRevision') = NEW.TriggerRevision AND json_extract(NEW.PayloadJson, '$.Subscription.Dispatch.Mode') = 1 AND json_extract(NEW.PayloadJson, '$.Flushed') = NEW.Flushed AND json_extract(NEW.PayloadJson, '$.ReservedOccurrenceId') IS NEW.ReservedOccurrenceId AND CAST(round((julianday(json_extract(NEW.PayloadJson, '$.DueAtUtc')) - 2440587.5)*86400000) AS INTEGER) = NEW.DueAtUtc AND json_type(NEW.PayloadJson, '$.Sources') IS 'array' AND json_array_length(NEW.PayloadJson, '$.Sources') BETWEEN 1 AND 24 AND (SELECT count(DISTINCT json_extract(value, '$.EventId')) FROM json_each(NEW.PayloadJson, '$.Sources')) = json_array_length(NEW.PayloadJson, '$.Sources') AND NOT EXISTS (SELECT 1 FROM json_each(NEW.PayloadJson, '$.Sources') e WHERE NOT COALESCE(((json_type(e.value, '$') IS 'object' AND (SELECT count(*) FROM json_each(e.value, '$')) = 8 AND (SELECT count(DISTINCT key) FROM json_each(e.value, '$') WHERE key IN ('EventId','Owner','Key','ReceivedAtUtc','DataJson','RootAgentRunId','TriggerDepth','VisitedAutomationIds')) = 8) AND (json_type(e.value, '$.Owner') IS 'object' AND (SELECT count(*) FROM json_each(e.value, '$.Owner')) = 4 AND (SELECT count(DISTINCT key) FROM json_each(e.value, '$.Owner') WHERE key IN ('AgentInstanceId','profileId','TenantId','ExecutorPrincipalId')) = 4) AND json_extract(e.value, '$.Owner.AgentInstanceId') IS NEW.AgentInstanceId AND lower(json_extract(e.value, '$.Owner.TenantId')) IS lower(NEW.TenantId) AND lower(json_extract(e.value, '$.Owner.ExecutorPrincipalId')) IS lower(NEW.ExecutorPrincipalId) AND json_type(e.value, '$.Owner.profileId') IS 'null' AND CAST(round((julianday(json_extract(e.value, '$.ReceivedAtUtc'))-2440587.5)*86400000) AS INTEGER) < NEW.DueAtUtc AND CAST(round((julianday(json_extract(e.value, '$.ReceivedAtUtc'))-2440587.5)*86400000) AS INTEGER) >= NEW.DueAtUtc - 1000 * json_extract(NEW.PayloadJson, '$.Subscription.Dispatch.WindowSeconds') AND ((json_extract(NEW.PayloadJson, '$.Subscription.SourceKind') = 3 AND EXISTS (SELECT 1 FROM CoreEventDeliveries d JOIN CoreEvents f ON f.EventId = d.EventId WHERE d.TenantId = NEW.TenantId AND d.EventId = json_extract(e.value, '$.EventId') AND d.TriggerId = NEW.TriggerId AND d.Status IN (1,6) AND json(d.SnapshotJson) IS json_extract(NEW.PayloadJson, '$.Subscription') AND json_extract(e.value, '$.Key') IS json_extract(f.PayloadJson, '$.Key') AND json_extract(e.value, '$.DataJson') IS json_extract(f.PayloadJson, '$.DataJson') AND json_extract(e.value, '$.RootAgentRunId') IS json_extract(f.PayloadJson, '$.RootAgentRunId') AND json_extract(e.value, '$.TriggerDepth') IS json_extract(f.PayloadJson, '$.TriggerDepth') AND json_extract(e.value, '$.VisitedAutomationIds') IS json_extract(f.PayloadJson, '$.VisitedAutomationIds') AND CAST(round((julianday(json_extract(e.value, '$.ReceivedAtUtc'))-2440587.5)*86400000) AS INTEGER) = f.ReceivedAtUtc)) OR (json_extract(NEW.PayloadJson, '$.Subscription.SourceKind') = 1 AND EXISTS (SELECT 1 FROM ExternalEventDeliveries d JOIN ExternalEvents f ON f.EventId = d.EventId JOIN WebhookEvents w ON w.ResourceId = f.ResourceId WHERE d.TenantId = NEW.TenantId AND d.EventId = json_extract(e.value, '$.EventId') AND d.TriggerId = NEW.TriggerId AND d.Status IN (0,5) AND json_extract(d.DecisionJson, '$.Matched') = 1 AND json(d.SnapshotJson) IS json_extract(NEW.PayloadJson, '$.Subscription') AND json_extract(e.value, '$.Key') IS w.EventKey AND json(json_extract(e.value, '$.DataJson')) IS json_extract(f.EvidenceJson, '$.data') AND json_type(e.value, '$.RootAgentRunId') IS 'null' AND json_extract(e.value, '$.TriggerDepth') = 0 AND json_array_length(e.value, '$.VisitedAutomationIds') = 0 AND CAST(round((julianday(json_extract(e.value, '$.ReceivedAtUtc'))-2440587.5)*86400000) AS INTEGER) = f.AdmittedAtUtc))) AND NOT EXISTS (SELECT 1 FROM CoreEventBuckets existing, json_each(existing.PayloadJson, '$.Sources') covered WHERE existing.TenantId = NEW.TenantId AND existing.TriggerId = NEW.TriggerId AND existing.BucketId <> NEW.BucketId AND json_extract(covered.value, '$.EventId') IS json_extract(e.value, '$.EventId'))),0)) AND (NEW.ReservedOccurrenceId IS NULL OR EXISTS (SELECT 1 FROM TriggerOccurrences o WHERE o.OccurrenceId = NEW.ReservedOccurrenceId AND o.TenantId = NEW.TenantId AND o.ExecutorPrincipalId = NEW.ExecutorPrincipalId AND o.AgentInstanceId = NEW.AgentInstanceId AND o.AutomationId = NEW.AutomationId AND o.TriggerId = NEW.TriggerId AND o.SourceEventId = NEW.BucketId AND o.SourceKind = json_extract(NEW.PayloadJson, '$.Subscription.SourceKind') AND ((NEW.Flushed = 0 AND o.Disposition = 3 AND o.DispositionReason = 'admission_approval_pending') OR (NEW.Flushed = 1 AND ((o.Disposition = 6 AND json_type(NEW.PayloadJson, '$.CompletionCode') IS 'null') OR (o.Disposition = 4 AND json_type(NEW.PayloadJson, '$.CompletionCode') IS 'text')))))))),0);
END;

CREATE TRIGGER TR_TenantEvent_Child_Update BEFORE UPDATE ON AutomationTriggers
WHEN EXISTS (SELECT 1 FROM Automations a WHERE a.AutomationId IN (OLD.AutomationId,NEW.AutomationId) AND a.TenantId IS NOT NULL)
BEGIN
SELECT RAISE(ABORT, 'tenant child source is immutable and revisions are exact') WHERE NOT COALESCE((OLD.TriggerId IS NEW.TriggerId AND OLD.AutomationId IS NEW.AutomationId AND OLD.Kind IS NEW.Kind AND OLD.EventId IS NEW.EventId AND OLD.CoreEventKey IS NEW.CoreEventKey AND NEW.Revision = OLD.Revision + CASE WHEN OLD.Enabled IS NEW.Enabled AND OLD.FilterExpression IS NEW.FilterExpression AND OLD.DispatchMode IS NEW.DispatchMode AND OLD.DispatchWindowSeconds IS NEW.DispatchWindowSeconds AND OLD.ScheduleJson IS NEW.ScheduleJson THEN 0 ELSE 1 END),0);
END;

CREATE TRIGGER TR_TenantEvent_Occurrence_Insert BEFORE INSERT ON TriggerOccurrences
WHEN NEW.TenantId IS NOT NULL AND NEW.SourceKind IN (1,3)
BEGIN
SELECT RAISE(ABORT, 'event occurrence requires its actual matched receipt or bucket') WHERE NOT COALESCE((json_type(json_extract(NEW.EvidenceJson, '$.triggerContext.admissionSource')) IS 'object' AND (SELECT count(*) FROM json_each(json_extract(NEW.EvidenceJson, '$.triggerContext.admissionSource'))) = CASE NEW.SourceKind WHEN 1 THEN 10 ELSE 8 END AND (SELECT count(DISTINCT lower(key)) FROM json_each(json_extract(NEW.EvidenceJson, '$.triggerContext.admissionSource'))) = (SELECT count(*) FROM json_each(json_extract(NEW.EvidenceJson, '$.triggerContext.admissionSource'))) AND NOT EXISTS (SELECT 1 FROM json_each(json_extract(NEW.EvidenceJson, '$.triggerContext.admissionSource')) WHERE key NOT IN ('TenantId','TriggerId','SourceKind','SourceId','IsBucket','SourceHash','EventIds','DedupeKey','ResourceRevision','ResourceId')) AND lower(json_extract(json_extract(NEW.EvidenceJson, '$.triggerContext.admissionSource'), '$.TenantId')) IS lower(NEW.TenantId) AND json_extract(json_extract(NEW.EvidenceJson, '$.triggerContext.admissionSource'), '$.TriggerId') IS NEW.TriggerId AND json_extract(json_extract(NEW.EvidenceJson, '$.triggerContext.admissionSource'), '$.SourceKind') = NEW.SourceKind AND json_extract(json_extract(NEW.EvidenceJson, '$.triggerContext.admissionSource'), '$.SourceId') IS NEW.SourceEventId AND json_extract(json_extract(NEW.EvidenceJson, '$.triggerContext.admissionSource'), '$.DedupeKey') IS NEW.DedupeKey AND length(json_extract(json_extract(NEW.EvidenceJson, '$.triggerContext.admissionSource'), '$.SourceHash')) = 64 AND json_extract(json_extract(NEW.EvidenceJson, '$.triggerContext.admissionSource'), '$.SourceHash') NOT GLOB '*[^0-9a-f]*' AND json_array_length(json_extract(NEW.EvidenceJson, '$.triggerContext.admissionSource'), '$.EventIds') BETWEEN 1 AND 24 AND ((json_extract(json_extract(NEW.EvidenceJson, '$.triggerContext.admissionSource'), '$.IsBucket') = 0 AND ((NEW.SourceKind = 3 AND EXISTS (SELECT 1 FROM CoreEventDeliveries d WHERE d.TenantId = NEW.TenantId AND d.ExecutorPrincipalId = NEW.ExecutorPrincipalId AND d.AgentInstanceId = NEW.AgentInstanceId AND d.AutomationId = NEW.AutomationId AND d.TriggerId = NEW.TriggerId AND d.EventId = NEW.SourceEventId AND d.Status = 1 AND d.ReservedOccurrenceId IS NULL AND json_extract(d.DecisionJson, '$.Matched') = 1 AND json_array_length(json_extract(NEW.EvidenceJson, '$.triggerContext.admissionSource'), '$.EventIds') = 1 AND json_extract(json_extract(NEW.EvidenceJson, '$.triggerContext.admissionSource'), '$.EventIds[0]') IS d.EventId)) OR (NEW.SourceKind = 1 AND EXISTS (SELECT 1 FROM ExternalEventDeliveries d JOIN ExternalEvents e ON e.EventId = d.EventId JOIN WebhookEvents w ON w.ResourceId = e.ResourceId WHERE d.TenantId = NEW.TenantId AND d.ExecutorPrincipalId = NEW.ExecutorPrincipalId AND d.AgentInstanceId = NEW.AgentInstanceId AND d.AutomationId = NEW.AutomationId AND d.TriggerId = NEW.TriggerId AND d.EventId = NEW.SourceEventId AND d.Status = 0 AND d.ReservedOccurrenceId IS NULL AND json_extract(d.DecisionJson, '$.Matched') = 1 AND json_array_length(json_extract(NEW.EvidenceJson, '$.triggerContext.admissionSource'), '$.EventIds') = 1 AND json_extract(json_extract(NEW.EvidenceJson, '$.triggerContext.admissionSource'), '$.EventIds[0]') IS d.EventId AND w.TenantId = NEW.TenantId AND w.Status = 0 AND w.ResourceId IS json_extract(json_extract(NEW.EvidenceJson, '$.triggerContext.admissionSource'), '$.ResourceId') AND w.Revision = json_extract(json_extract(NEW.EvidenceJson, '$.triggerContext.admissionSource'), '$.ResourceRevision'))))) OR (json_extract(json_extract(NEW.EvidenceJson, '$.triggerContext.admissionSource'), '$.IsBucket') = 1 AND EXISTS (SELECT 1 FROM CoreEventBuckets b WHERE b.TenantId = NEW.TenantId AND b.AgentInstanceId = NEW.AgentInstanceId AND b.ExecutorPrincipalId = NEW.ExecutorPrincipalId AND b.AutomationId = NEW.AutomationId AND b.TriggerId = NEW.TriggerId AND b.BucketId = NEW.SourceEventId AND b.Flushed = 0 AND b.ReservedOccurrenceId IS NULL AND b.DueAtUtc <= NEW.AdmittedAtUtc AND json_extract(b.PayloadJson, '$.Subscription.SourceKind') = NEW.SourceKind AND ((json_array_length(b.PayloadJson, '$.Sources') = json_array_length(json_extract(NEW.EvidenceJson, '$.triggerContext.admissionSource'), '$.EventIds') AND NOT EXISTS (SELECT 1 FROM json_each(b.PayloadJson, '$.Sources') e WHERE json_extract(e.value, '$.EventId') IS NOT json_extract(json_extract(NEW.EvidenceJson, '$.triggerContext.admissionSource'), '$.EventIds[' || e.key || ']'))) OR (NEW.SourceKind = 3 AND json_array_length(b.PayloadJson, '$.Sources') > 0 AND NOT EXISTS (SELECT 1 FROM json_each(b.PayloadJson, '$.Sources') e WHERE json_extract(e.value, '$.Key') IS NOT 'email.message.received') AND json_array_length(json_extract(NEW.EvidenceJson, '$.triggerContext.admissionSource'), '$.EventIds') = (SELECT count(DISTINCT value) FROM json_each(json_extract(NEW.EvidenceJson, '$.triggerContext.admissionSource'), '$.EventIds')) AND NOT EXISTS (SELECT 1 FROM json_each(json_extract(NEW.EvidenceJson, '$.triggerContext.admissionSource'), '$.EventIds') retained WHERE NOT EXISTS (SELECT 1 FROM json_each(b.PayloadJson, '$.Sources') original WHERE json_extract(original.value, '$.EventId') IS retained.value)) AND NOT EXISTS (SELECT 1 FROM json_each(json_extract(NEW.EvidenceJson, '$.triggerContext.admissionSource'), '$.EventIds') earlier JOIN json_each(json_extract(NEW.EvidenceJson, '$.triggerContext.admissionSource'), '$.EventIds') later ON earlier.key < later.key JOIN json_each(b.PayloadJson, '$.Sources') originalEarlier ON json_extract(originalEarlier.value, '$.EventId') IS earlier.value JOIN json_each(b.PayloadJson, '$.Sources') originalLater ON json_extract(originalLater.value, '$.EventId') IS later.value WHERE originalEarlier.key >= originalLater.key))))))),0);
END;

CREATE TRIGGER TR_TenantOccurrences_Source_Insert BEFORE INSERT ON TriggerOccurrences
WHEN NEW.TenantId IS NOT NULL AND NEW.ExecutionTargetKind <> 2
BEGIN
SELECT RAISE(ABORT, 'tenant occurrence requires exact admission or pending consent') WHERE NOT COALESCE((
(json_extract(NEW.EvidenceJson, '$.admissionApproval') IS NULL AND EXISTS (
SELECT 1 FROM Automations automation JOIN AgentRuns run ON run.AgentRunId = NEW.AcceptedAgentRunId
JOIN TenantSessions session ON session.SessionId = NEW.ExecutionSessionId
JOIN Activations activation ON activation.ActivationId = run.ActivationId
WHERE automation.TenantId = NEW.TenantId AND automation.AutomationId = NEW.AutomationId
AND automation.AgentInstanceId = NEW.AgentInstanceId AND automation.ExecutorPrincipalId = NEW.ExecutorPrincipalId
AND run.TenantId = NEW.TenantId AND run.AgentInstanceId = NEW.AgentInstanceId AND run.ExecutorPrincipalId = NEW.ExecutorPrincipalId
AND run.SessionId = session.SessionId AND run.RootActivationId = run.ActivationId AND (NEW.SourceKind = 2 OR run.OriginalInitiatorPrincipalId IS NULL) AND run.ProfileId IS NULL
AND session.TenantId = NEW.TenantId AND session.AgentInstanceId = NEW.AgentInstanceId AND session.ExecutorPrincipalId = NEW.ExecutorPrincipalId
AND (NEW.ExecutionTargetKind = 1 OR session.CreatedByPrincipalId = NEW.ExecutorPrincipalId) AND json_extract(activation.PayloadJson, '$.triggerOccurrenceId') = NEW.OccurrenceId
AND activation.RootActivationId = activation.ActivationId AND activation.DedupeKey = NEW.DedupeKey
AND NEW.ProfileId IS NULL AND NEW.SourceKind IN (0,1,2,3) AND NEW.Disposition = 6
AND json_extract(run.PayloadJson, '$.admission.outputContract') = CASE NEW.ExecutionTargetKind WHEN 0 THEN 1 WHEN 1 THEN 0 END
AND json_extract(run.PayloadJson, '$.admission.executionContext.origin') = CASE NEW.SourceKind WHEN 0 THEN 4 WHEN 1 THEN 5 WHEN 2 THEN 7 WHEN 3 THEN 6 END))
OR (NEW.Disposition = 3 AND NEW.DispositionReason = 'admission_approval_pending' AND NEW.RoutingRevision = 1
AND NEW.ExecutionSessionId IS NULL AND NEW.AcceptedAgentRunId IS NULL AND NEW.ProfileId IS NULL
AND NEW.SourceKind IN (0,1,2,3) AND json_type(NEW.EvidenceJson, '$.admissionApproval') IS 'object'
AND (SELECT count(*) FROM json_each(NEW.EvidenceJson, '$.admissionApproval')) = 7
AND (SELECT count(DISTINCT key) FROM json_each(NEW.EvidenceJson, '$.admissionApproval') WHERE key IN ('requestId','sessionId','runId','activationId','entryId','responseId','requesterPrincipalId')) = 7
AND json_type(NEW.EvidenceJson, '$.admissionApproval.requestId') IS 'text' AND length(json_extract(NEW.EvidenceJson, '$.admissionApproval.requestId')) = 36 AND json_extract(NEW.EvidenceJson, '$.admissionApproval.requestId') NOT GLOB '*[^0-9a-f-]*' AND json_extract(NEW.EvidenceJson, '$.admissionApproval.requestId') IS NOT '00000000-0000-0000-0000-000000000000' AND substr(json_extract(NEW.EvidenceJson, '$.admissionApproval.requestId'),9,1) IS '-' AND substr(json_extract(NEW.EvidenceJson, '$.admissionApproval.requestId'),14,1) IS '-' AND substr(json_extract(NEW.EvidenceJson, '$.admissionApproval.requestId'),19,1) IS '-' AND substr(json_extract(NEW.EvidenceJson, '$.admissionApproval.requestId'),24,1) IS '-'
AND json_type(NEW.EvidenceJson, '$.admissionApproval.sessionId') IS 'text' AND length(json_extract(NEW.EvidenceJson, '$.admissionApproval.sessionId')) = 36 AND json_extract(NEW.EvidenceJson, '$.admissionApproval.sessionId') NOT GLOB '*[^0-9a-f-]*' AND json_extract(NEW.EvidenceJson, '$.admissionApproval.sessionId') IS NOT '00000000-0000-0000-0000-000000000000' AND substr(json_extract(NEW.EvidenceJson, '$.admissionApproval.sessionId'),9,1) IS '-' AND substr(json_extract(NEW.EvidenceJson, '$.admissionApproval.sessionId'),14,1) IS '-' AND substr(json_extract(NEW.EvidenceJson, '$.admissionApproval.sessionId'),19,1) IS '-' AND substr(json_extract(NEW.EvidenceJson, '$.admissionApproval.sessionId'),24,1) IS '-'
AND json_type(NEW.EvidenceJson, '$.admissionApproval.runId') IS 'text' AND length(json_extract(NEW.EvidenceJson, '$.admissionApproval.runId')) = 36 AND json_extract(NEW.EvidenceJson, '$.admissionApproval.runId') NOT GLOB '*[^0-9a-f-]*' AND json_extract(NEW.EvidenceJson, '$.admissionApproval.runId') IS NOT '00000000-0000-0000-0000-000000000000' AND substr(json_extract(NEW.EvidenceJson, '$.admissionApproval.runId'),9,1) IS '-' AND substr(json_extract(NEW.EvidenceJson, '$.admissionApproval.runId'),14,1) IS '-' AND substr(json_extract(NEW.EvidenceJson, '$.admissionApproval.runId'),19,1) IS '-' AND substr(json_extract(NEW.EvidenceJson, '$.admissionApproval.runId'),24,1) IS '-'
AND json_type(NEW.EvidenceJson, '$.admissionApproval.activationId') IS 'text' AND length(json_extract(NEW.EvidenceJson, '$.admissionApproval.activationId')) = 36 AND json_extract(NEW.EvidenceJson, '$.admissionApproval.activationId') NOT GLOB '*[^0-9a-f-]*' AND json_extract(NEW.EvidenceJson, '$.admissionApproval.activationId') IS NOT '00000000-0000-0000-0000-000000000000' AND substr(json_extract(NEW.EvidenceJson, '$.admissionApproval.activationId'),9,1) IS '-' AND substr(json_extract(NEW.EvidenceJson, '$.admissionApproval.activationId'),14,1) IS '-' AND substr(json_extract(NEW.EvidenceJson, '$.admissionApproval.activationId'),19,1) IS '-' AND substr(json_extract(NEW.EvidenceJson, '$.admissionApproval.activationId'),24,1) IS '-'
AND json_type(NEW.EvidenceJson, '$.admissionApproval.entryId') IS 'text' AND length(json_extract(NEW.EvidenceJson, '$.admissionApproval.entryId')) = 36 AND json_extract(NEW.EvidenceJson, '$.admissionApproval.entryId') NOT GLOB '*[^0-9a-f-]*' AND json_extract(NEW.EvidenceJson, '$.admissionApproval.entryId') IS NOT '00000000-0000-0000-0000-000000000000' AND substr(json_extract(NEW.EvidenceJson, '$.admissionApproval.entryId'),9,1) IS '-' AND substr(json_extract(NEW.EvidenceJson, '$.admissionApproval.entryId'),14,1) IS '-' AND substr(json_extract(NEW.EvidenceJson, '$.admissionApproval.entryId'),19,1) IS '-' AND substr(json_extract(NEW.EvidenceJson, '$.admissionApproval.entryId'),24,1) IS '-'
AND json_type(NEW.EvidenceJson, '$.admissionApproval.responseId') IS 'text' AND length(json_extract(NEW.EvidenceJson, '$.admissionApproval.responseId')) = 36 AND json_extract(NEW.EvidenceJson, '$.admissionApproval.responseId') NOT GLOB '*[^0-9a-f-]*' AND json_extract(NEW.EvidenceJson, '$.admissionApproval.responseId') IS NOT '00000000-0000-0000-0000-000000000000' AND substr(json_extract(NEW.EvidenceJson, '$.admissionApproval.responseId'),9,1) IS '-' AND substr(json_extract(NEW.EvidenceJson, '$.admissionApproval.responseId'),14,1) IS '-' AND substr(json_extract(NEW.EvidenceJson, '$.admissionApproval.responseId'),19,1) IS '-' AND substr(json_extract(NEW.EvidenceJson, '$.admissionApproval.responseId'),24,1) IS '-'
AND json_extract(NEW.EvidenceJson, '$.admissionApproval.requestId') IS NOT json_extract(NEW.EvidenceJson, '$.admissionApproval.sessionId')
AND json_extract(NEW.EvidenceJson, '$.admissionApproval.requestId') IS NOT json_extract(NEW.EvidenceJson, '$.admissionApproval.runId')
AND json_extract(NEW.EvidenceJson, '$.admissionApproval.requestId') IS NOT json_extract(NEW.EvidenceJson, '$.admissionApproval.activationId')
AND json_extract(NEW.EvidenceJson, '$.admissionApproval.requestId') IS NOT json_extract(NEW.EvidenceJson, '$.admissionApproval.entryId')
AND json_extract(NEW.EvidenceJson, '$.admissionApproval.requestId') IS NOT json_extract(NEW.EvidenceJson, '$.admissionApproval.responseId')
AND json_extract(NEW.EvidenceJson, '$.admissionApproval.sessionId') IS NOT json_extract(NEW.EvidenceJson, '$.admissionApproval.runId')
AND json_extract(NEW.EvidenceJson, '$.admissionApproval.sessionId') IS NOT json_extract(NEW.EvidenceJson, '$.admissionApproval.activationId')
AND json_extract(NEW.EvidenceJson, '$.admissionApproval.sessionId') IS NOT json_extract(NEW.EvidenceJson, '$.admissionApproval.entryId')
AND json_extract(NEW.EvidenceJson, '$.admissionApproval.sessionId') IS NOT json_extract(NEW.EvidenceJson, '$.admissionApproval.responseId')
AND json_extract(NEW.EvidenceJson, '$.admissionApproval.runId') IS NOT json_extract(NEW.EvidenceJson, '$.admissionApproval.activationId')
AND json_extract(NEW.EvidenceJson, '$.admissionApproval.runId') IS NOT json_extract(NEW.EvidenceJson, '$.admissionApproval.entryId')
AND json_extract(NEW.EvidenceJson, '$.admissionApproval.runId') IS NOT json_extract(NEW.EvidenceJson, '$.admissionApproval.responseId')
AND json_extract(NEW.EvidenceJson, '$.admissionApproval.activationId') IS NOT json_extract(NEW.EvidenceJson, '$.admissionApproval.entryId')
AND json_extract(NEW.EvidenceJson, '$.admissionApproval.activationId') IS NOT json_extract(NEW.EvidenceJson, '$.admissionApproval.responseId')
AND json_extract(NEW.EvidenceJson, '$.admissionApproval.entryId') IS NOT json_extract(NEW.EvidenceJson, '$.admissionApproval.responseId')
AND EXISTS (SELECT 1 FROM Automations a WHERE a.AutomationId = NEW.AutomationId AND a.TenantId = NEW.TenantId
AND a.AgentInstanceId = NEW.AgentInstanceId AND a.ExecutorPrincipalId = NEW.ExecutorPrincipalId AND a.ProfileId IS NULL)
AND NOT EXISTS (SELECT 1 FROM AgentRuns r WHERE r.AgentRunId = json_extract(NEW.EvidenceJson, '$.admissionApproval.runId'))
AND ((NEW.ExecutionTargetKind = 0 AND NOT EXISTS (SELECT 1 FROM TenantSessions s WHERE s.SessionId = json_extract(NEW.EvidenceJson, '$.admissionApproval.sessionId')))
OR (NEW.ExecutionTargetKind = 1 AND NEW.ReportToSessionId IS NULL AND NEW.TargetSessionId IS json_extract(NEW.EvidenceJson, '$.admissionApproval.sessionId')
AND EXISTS (SELECT 1 FROM TenantSessions s JOIN Sessions saved ON saved.SessionId = s.SessionId WHERE s.TenantId = NEW.TenantId AND s.AgentInstanceId = NEW.AgentInstanceId
AND s.ExecutorPrincipalId = NEW.ExecutorPrincipalId AND s.SessionId IS NEW.TargetSessionId AND saved.DurablyDeletedAtUtc IS NULL AND json_extract(saved.OriginJson, '$.kind') = 0)))
AND ((NEW.SourceKind IN (0,1,3) AND json_extract(NEW.EvidenceJson, '$.admissionApproval.requesterPrincipalId') IS NULL)
OR (NEW.SourceKind = 2 AND EXISTS (SELECT 1 FROM TenantUsers u WHERE u.TenantId = NEW.TenantId AND lower(u.PrincipalId) = json_extract(NEW.EvidenceJson, '$.admissionApproval.requesterPrincipalId')))))),0);
END;

CREATE TRIGGER TR_TenantOccurrences_Source_Update BEFORE UPDATE ON TriggerOccurrences
WHEN (OLD.TenantId IS NOT NULL OR NEW.TenantId IS NOT NULL) AND OLD.ExecutionTargetKind <> 2 AND NEW.ExecutionTargetKind <> 2
BEGIN
SELECT RAISE(ABORT, 'tenant occurrence source is immutable') WHERE 1=1 AND (OLD.TenantId IS NOT NEW.TenantId OR OLD.ExecutorPrincipalId IS NOT NEW.ExecutorPrincipalId OR OLD.AgentInstanceId IS NOT NEW.AgentInstanceId
OR OLD.ProfileId IS NOT NEW.ProfileId OR OLD.OccurrenceId IS NOT NEW.OccurrenceId OR OLD.AutomationId IS NOT NEW.AutomationId
OR OLD.DedupeKey IS NOT NEW.DedupeKey OR OLD.EvidenceJson IS NOT NEW.EvidenceJson OR OLD.SourceKind IS NOT NEW.SourceKind
OR OLD.TriggerRevision IS NOT NEW.TriggerRevision OR OLD.ScheduledAtUtc IS NOT NEW.ScheduledAtUtc OR OLD.ObservedAtUtc IS NOT NEW.ObservedAtUtc
OR OLD.AdmittedAtUtc IS NOT NEW.AdmittedAtUtc
OR OLD.SourceEventId IS NOT NEW.SourceEventId OR OLD.ModelOfferingId IS NOT NEW.ModelOfferingId OR OLD.ModelConnectionId IS NOT NEW.ModelConnectionId
OR OLD.ExternalModelId IS NOT NEW.ExternalModelId OR OLD.ModelReasoningEffort IS NOT NEW.ModelReasoningEffort
OR OLD.TriggerId IS NOT NEW.TriggerId OR OLD.ModelSource IS NOT NEW.ModelSource OR OLD.ExecutionTargetKind IS NOT NEW.ExecutionTargetKind
OR OLD.TargetSessionId IS NOT NEW.TargetSessionId OR OLD.ReportToSessionId IS NOT NEW.ReportToSessionId


OR OLD.ClaimId IS NOT NEW.ClaimId OR OLD.ClaimLeaseExpiresAtUtc IS NOT NEW.ClaimLeaseExpiresAtUtc
OR OLD.LiveSessionId IS NOT NEW.LiveSessionId OR OLD.LiveEvaluationCompletedAtUtc IS NOT NEW.LiveEvaluationCompletedAtUtc);
SELECT RAISE(ABORT, 'tenant occurrence permits only exact consent settlement') WHERE NOT (
OLD.ExecutionSessionId IS NEW.ExecutionSessionId AND OLD.AcceptedAgentRunId IS NEW.AcceptedAgentRunId
AND OLD.Disposition IS NEW.Disposition AND OLD.DispositionReason IS NEW.DispositionReason
AND OLD.RoutingRevision IS NEW.RoutingRevision AND OLD.RoutingUpdatedAtUtc IS NEW.RoutingUpdatedAtUtc)
AND NOT COALESCE((OLD.Disposition = 3 AND OLD.DispositionReason = 'admission_approval_pending' AND OLD.RoutingRevision = 1
AND NEW.RoutingRevision = 2 AND NEW.RoutingUpdatedAtUtc >= OLD.RoutingUpdatedAtUtc
AND EXISTS (SELECT 1 FROM TenantApprovalRequests r
WHERE lower(r.RequestId) = json_extract(NEW.EvidenceJson, '$.admissionApproval.requestId')
AND r.TenantId = NEW.TenantId AND r.InstanceId = NEW.AgentInstanceId AND r.ExecutorPrincipalId = NEW.ExecutorPrincipalId
AND r.AutomationId = NEW.AutomationId
AND ((NEW.Disposition = 4 AND r.Status IN (1,2,3,4) AND NEW.ExecutionSessionId IS NULL AND NEW.AcceptedAgentRunId IS NULL
AND NEW.DispositionReason IS NOT NULL)
OR (NEW.Disposition = 6 AND r.Status = 1 AND NEW.DispositionReason IS NULL
AND NEW.ExecutionSessionId = json_extract(NEW.EvidenceJson, '$.admissionApproval.sessionId')
AND NEW.AcceptedAgentRunId = json_extract(NEW.EvidenceJson, '$.admissionApproval.runId')
AND EXISTS (
SELECT 1 FROM Automations automation JOIN AgentRuns run ON run.AgentRunId = NEW.AcceptedAgentRunId
JOIN TenantSessions session ON session.SessionId = NEW.ExecutionSessionId
JOIN Activations activation ON activation.ActivationId = run.ActivationId
WHERE automation.TenantId = NEW.TenantId AND automation.AutomationId = NEW.AutomationId
AND automation.AgentInstanceId = NEW.AgentInstanceId AND automation.ExecutorPrincipalId = NEW.ExecutorPrincipalId
AND run.TenantId = NEW.TenantId AND run.AgentInstanceId = NEW.AgentInstanceId AND run.ExecutorPrincipalId = NEW.ExecutorPrincipalId
AND run.ActivationId IS json_extract(NEW.EvidenceJson, '$.admissionApproval.activationId')
AND lower(run.OriginalInitiatorPrincipalId) IS json_extract(NEW.EvidenceJson, '$.admissionApproval.requesterPrincipalId')
AND json_extract(run.PayloadJson, '$.admission.responseId') IS json_extract(NEW.EvidenceJson, '$.admissionApproval.responseId')
AND json_extract(run.PayloadJson, '$.admission.taskIntent.text') IS json_extract(NEW.EvidenceJson, '$.instructions')
AND ((NEW.ExecutionTargetKind = 0 AND EXISTS (SELECT 1 FROM ActivationSourceEntries entry WHERE entry.ActivationId = run.ActivationId AND entry.SessionId = run.SessionId
    AND entry.EntryId IS json_extract(NEW.EvidenceJson, '$.admissionApproval.entryId') AND entry.Ordinal = 0))
OR (NEW.ExecutionTargetKind = 1 AND NOT EXISTS (SELECT 1 FROM ActivationSourceEntries entry WHERE entry.ActivationId = run.ActivationId)
AND json_extract(json_extract(run.PayloadJson, '$.admission.activation.evidenceJson'), '$.InputBoundarySequence') IS json_extract(r.PayloadJson, '$.Operation.AutomationAdmission.Conversation.InputBoundarySequence')))
AND run.SessionId = session.SessionId AND run.RootActivationId = run.ActivationId AND (NEW.SourceKind = 2 OR run.OriginalInitiatorPrincipalId IS NULL) AND run.ProfileId IS NULL
AND session.TenantId = NEW.TenantId AND session.AgentInstanceId = NEW.AgentInstanceId AND session.ExecutorPrincipalId = NEW.ExecutorPrincipalId
AND (NEW.ExecutionTargetKind = 1 OR session.CreatedByPrincipalId = NEW.ExecutorPrincipalId) AND json_extract(activation.PayloadJson, '$.triggerOccurrenceId') = NEW.OccurrenceId
AND activation.RootActivationId = activation.ActivationId AND activation.DedupeKey = NEW.DedupeKey
AND NEW.ProfileId IS NULL AND NEW.SourceKind IN (0,1,2,3) AND NEW.Disposition = 6
AND json_extract(run.PayloadJson, '$.admission.outputContract') = CASE NEW.ExecutionTargetKind WHEN 0 THEN 1 WHEN 1 THEN 0 END
AND json_extract(run.PayloadJson, '$.admission.executionContext.origin') = CASE NEW.SourceKind WHEN 0 THEN 4 WHEN 1 THEN 5 WHEN 2 THEN 7 WHEN 3 THEN 6 END))))),0);
END;

CREATE TRIGGER TR_TenantOperationReceipts_AutomationInvocation_INSERT BEFORE INSERT ON TenantOperationReceipts
WHEN json_extract(NEW.AutomationMutationJson,'$.ToolName')='automation.run' AND NOT EXISTS
 (SELECT 1 FROM TriggerOccurrences occurrence JOIN AgentRuns admitted ON admitted.AgentRunId=occurrence.AcceptedAgentRunId
  JOIN AgentRuns parent ON parent.AgentRunId=NEW.RunId
  WHERE occurrence.TenantId=NEW.TenantId AND occurrence.AgentInstanceId=NEW.InstanceId AND occurrence.ExecutorPrincipalId=NEW.ExecutorPrincipalId
  AND occurrence.AutomationId=json_extract(NEW.AutomationMutationJson,'$.AutomationId') AND occurrence.SourceKind=2 AND occurrence.Disposition=6
  AND occurrence.OccurrenceId=json_extract(NEW.AutomationMutationJson,'$.InvocationOccurrenceId')
  AND occurrence.ExecutionSessionId=json_extract(NEW.AutomationMutationJson,'$.InvocationSessionId')
  AND occurrence.AcceptedAgentRunId=json_extract(NEW.AutomationMutationJson,'$.InvocationRunId')
  AND admitted.TenantId=NEW.TenantId AND admitted.AgentInstanceId=NEW.InstanceId AND admitted.ExecutorPrincipalId=NEW.ExecutorPrincipalId
  AND admitted.SessionId=occurrence.ExecutionSessionId AND admitted.OriginalInitiatorPrincipalId=parent.OriginalInitiatorPrincipalId
  AND admitted.RootActivationId=admitted.ActivationId AND json_extract(admitted.PayloadJson,'$.admission.executionContext.origin')=7)
BEGIN SELECT RAISE(ABORT,'Manual invocation receipt requires its exact accepted owned occurrence and Run'); END;

CREATE TRIGGER TR_TenantOperationReceipts_AutomationMutation_INSERT BEFORE INSERT ON TenantOperationReceipts
WHEN NEW.AutomationMutationJson IS NOT NULL AND NOT EXISTS
 (SELECT 1 FROM Automations a WHERE a.TenantId=NEW.TenantId AND a.AgentInstanceId=NEW.InstanceId AND a.ExecutorPrincipalId=NEW.ExecutorPrincipalId
  AND a.AutomationId=json_extract(NEW.AutomationMutationJson,'$.AutomationId') AND a.Revision=json_extract(NEW.AutomationMutationJson,'$.Revision'))
BEGIN SELECT RAISE(ABORT,'Automation mutation receipt requires exact applied owned parent'); END;

CREATE TRIGGER TR_TenantWebhook_Delivery_Insert BEFORE INSERT ON ExternalEventDeliveries
BEGIN
SELECT RAISE(ABORT, 'delivery requires its exact frozen receipt and occurrence') WHERE NOT COALESCE(((NEW.TenantId IS NULL AND NEW.ExecutorPrincipalId IS NULL AND NEW.ReservedOccurrenceId IS NULL AND NOT EXISTS (SELECT 1 FROM SystemIdentities i WHERE i.Kind = 0 AND i.AgentInstanceId = NEW.AgentInstanceId AND i.TenantId IS NOT NULL) AND NOT EXISTS (SELECT 1 FROM ExternalEvents e WHERE e.EventId = NEW.EventId AND e.TenantId IS NOT NULL) AND (NEW.SnapshotJson IS NULL OR NOT EXISTS (SELECT 1 FROM json_tree(NEW.SnapshotJson, '$') WHERE lower(key) IN ('tenantid','executorprincipalid')) AND NOT EXISTS (SELECT 1 FROM json_tree(NEW.SnapshotJson) field JOIN SystemIdentities i ON i.AgentInstanceId = field.value WHERE lower(field.key) = 'agentinstanceid' AND i.Kind = 0 AND i.TenantId IS NOT NULL))) OR (NEW.TenantId IS NOT NULL AND (json_type(NEW.SnapshotJson, '$') IS 'object' AND (SELECT count(*) FROM json_each(NEW.SnapshotJson, '$')) = (10 + CASE WHEN json_type(NEW.SnapshotJson, '$.PinnedSpaceId') IS NULL THEN 0 ELSE 1 END + CASE WHEN json_type(NEW.SnapshotJson, '$.PinnedEnvironmentId') IS NULL THEN 0 ELSE 1 END) AND (SELECT count(DISTINCT key) FROM json_each(NEW.SnapshotJson, '$') WHERE key IN ('AutomationId','Owner','TriggerRevision','FilterExpression','Dispatch','SourceKind','ResourceId','TriggerId','ExpressionVersion','FilterHash')) = 10 AND ((json_type(NEW.SnapshotJson, '$.PinnedSpaceId') IS NULL AND json_type(NEW.SnapshotJson, '$.PinnedEnvironmentId') IS NULL) OR (json_type(NEW.SnapshotJson, '$.PinnedSpaceId')='text' AND length(json_extract(NEW.SnapshotJson, '$.PinnedSpaceId'))=36 AND json_extract(NEW.SnapshotJson, '$.PinnedSpaceId') NOT GLOB '*[^0-9a-f-]*' AND json_extract(NEW.SnapshotJson, '$.PinnedSpaceId')<>'00000000-0000-0000-0000-000000000000' AND substr(json_extract(NEW.SnapshotJson, '$.PinnedSpaceId'),9,1)='-' AND substr(json_extract(NEW.SnapshotJson, '$.PinnedSpaceId'),14,1)='-' AND substr(json_extract(NEW.SnapshotJson, '$.PinnedSpaceId'),19,1)='-' AND substr(json_extract(NEW.SnapshotJson, '$.PinnedSpaceId'),24,1)='-' AND EXISTS(SELECT 1 FROM Spaces ps WHERE lower(ps.TenantId)=lower(json_extract(NEW.SnapshotJson, '$.Owner.TenantId')) AND lower(ps.SpaceId)=json_extract(NEW.SnapshotJson, '$.PinnedSpaceId') AND (ps.Kind=1 OR ps.OwnerInstanceId=json_extract(NEW.SnapshotJson, '$.Owner.AgentInstanceId'))) AND (json_type(NEW.SnapshotJson, '$.PinnedEnvironmentId') IS NULL OR (json_type(NEW.SnapshotJson, '$.PinnedEnvironmentId')='text' AND length(json_extract(NEW.SnapshotJson, '$.PinnedEnvironmentId'))=36 AND json_extract(NEW.SnapshotJson, '$.PinnedEnvironmentId') NOT GLOB '*[^0-9a-f-]*' AND json_extract(NEW.SnapshotJson, '$.PinnedEnvironmentId')<>'00000000-0000-0000-0000-000000000000' AND substr(json_extract(NEW.SnapshotJson, '$.PinnedEnvironmentId'),9,1)='-' AND substr(json_extract(NEW.SnapshotJson, '$.PinnedEnvironmentId'),14,1)='-' AND substr(json_extract(NEW.SnapshotJson, '$.PinnedEnvironmentId'),19,1)='-' AND substr(json_extract(NEW.SnapshotJson, '$.PinnedEnvironmentId'),24,1)='-' AND EXISTS(SELECT 1 FROM SpaceEnvironments pe WHERE lower(pe.TenantId)=lower(json_extract(NEW.SnapshotJson, '$.Owner.TenantId')) AND lower(pe.SpaceId)=json_extract(NEW.SnapshotJson, '$.PinnedSpaceId') AND lower(pe.EnvironmentId)=json_extract(NEW.SnapshotJson, '$.PinnedEnvironmentId'))))))) AND (json_type(NEW.SnapshotJson, '$.Owner') IS 'object' AND (SELECT count(*) FROM json_each(NEW.SnapshotJson, '$.Owner')) = 4 AND (SELECT count(DISTINCT key) FROM json_each(NEW.SnapshotJson, '$.Owner') WHERE key IN ('AgentInstanceId','profileId','TenantId','ExecutorPrincipalId')) = 4) AND json_extract(NEW.SnapshotJson, '$.Owner.AgentInstanceId') IS NEW.AgentInstanceId AND lower(json_extract(NEW.SnapshotJson, '$.Owner.TenantId')) IS lower(NEW.TenantId) AND lower(json_extract(NEW.SnapshotJson, '$.Owner.ExecutorPrincipalId')) IS lower(NEW.ExecutorPrincipalId) AND json_type(NEW.SnapshotJson, '$.Owner.profileId') IS 'null' AND (json_type(NEW.SnapshotJson, '$.Dispatch') IS 'object' AND (SELECT count(*) FROM json_each(NEW.SnapshotJson, '$.Dispatch')) = 2 AND (SELECT count(DISTINCT key) FROM json_each(NEW.SnapshotJson, '$.Dispatch') WHERE key IN ('Mode','WindowSeconds')) = 2) AND json_extract(NEW.SnapshotJson, '$.ExpressionVersion') IS 'js-expression-v1' AND json_extract(NEW.SnapshotJson, '$.TriggerRevision') >= 1 AND (json_type(NEW.SnapshotJson, '$.FilterExpression') IS 'null' OR (json_type(NEW.SnapshotJson, '$.FilterExpression') IS 'text' AND length(json_extract(NEW.SnapshotJson, '$.FilterExpression')) <= 1024)) AND ((json_extract(NEW.SnapshotJson, '$.Dispatch.Mode') = 0 AND json_type(NEW.SnapshotJson, '$.Dispatch.WindowSeconds') IS 'null') OR (json_extract(NEW.SnapshotJson, '$.Dispatch.Mode') = 1 AND json_extract(NEW.SnapshotJson, '$.Dispatch.WindowSeconds') BETWEEN 60 AND 3600)) AND json_extract(NEW.SnapshotJson, '$.AutomationId') IS NEW.AutomationId AND json_extract(NEW.SnapshotJson, '$.TriggerId') IS NEW.TriggerId AND json_extract(NEW.SnapshotJson, '$.SourceKind') = 1 AND EXISTS (SELECT 1 FROM ExternalEvents e, json_each(e.SubscriptionsJson) f WHERE e.EventId = NEW.EventId AND e.TenantId = NEW.TenantId AND json(f.value) IS json(NEW.SnapshotJson)) AND NEW.Status BETWEEN 0 AND 6 AND (NEW.Status NOT IN (1,5,6) OR json_extract(NEW.DecisionJson, '$.Matched') = 1) AND (NEW.ReservedOccurrenceId IS NULL AND NEW.Status NOT IN (1,6) OR NEW.ReservedOccurrenceId IS NOT NULL AND EXISTS (SELECT 1 FROM TriggerOccurrences o WHERE o.OccurrenceId = NEW.ReservedOccurrenceId AND o.TenantId = NEW.TenantId AND o.AgentInstanceId = NEW.AgentInstanceId AND o.ExecutorPrincipalId = NEW.ExecutorPrincipalId AND o.AutomationId = NEW.AutomationId AND o.TriggerId = NEW.TriggerId AND o.SourceEventId = NEW.EventId AND o.SourceKind = 1 AND ((NEW.Status = 6 AND o.Disposition = 3 AND o.DispositionReason = 'admission_approval_pending') OR (NEW.Status = 1 AND o.Disposition = 6) OR (NEW.Status = 2 AND o.Disposition = 4)))))),0);
END;

CREATE TRIGGER TR_TenantWebhook_Delivery_Update BEFORE UPDATE ON ExternalEventDeliveries
BEGIN
SELECT RAISE(ABORT, 'tenant delivery source is immutable') WHERE (OLD.TenantId IS NOT NULL OR NEW.TenantId IS NOT NULL) AND (OLD.TenantId IS NOT NEW.TenantId OR OLD.ExecutorPrincipalId IS NOT NEW.ExecutorPrincipalId OR OLD.ProfileId IS NOT NEW.ProfileId OR OLD.AgentInstanceId IS NOT NEW.AgentInstanceId OR OLD.AutomationId IS NOT NEW.AutomationId OR OLD.TriggerId IS NOT NEW.TriggerId OR OLD.EventId IS NOT NEW.EventId OR OLD.SnapshotJson IS NOT NEW.SnapshotJson);
SELECT RAISE(ABORT, 'terminal delivery cannot be reopened') WHERE OLD.TenantId IS NOT NULL AND (OLD.ReservedOccurrenceId IS NOT NULL AND OLD.ReservedOccurrenceId IS NOT NEW.ReservedOccurrenceId OR OLD.Status IN (1,2,3,5) AND (OLD.Status IS NOT NEW.Status OR OLD.DecisionJson IS NOT NEW.DecisionJson OR OLD.Code IS NOT NEW.Code));
SELECT RAISE(ABORT, 'delivery requires its exact frozen receipt and occurrence') WHERE NOT COALESCE(((NEW.TenantId IS NULL AND NEW.ExecutorPrincipalId IS NULL AND NEW.ReservedOccurrenceId IS NULL AND NOT EXISTS (SELECT 1 FROM SystemIdentities i WHERE i.Kind = 0 AND i.AgentInstanceId = NEW.AgentInstanceId AND i.TenantId IS NOT NULL) AND NOT EXISTS (SELECT 1 FROM ExternalEvents e WHERE e.EventId = NEW.EventId AND e.TenantId IS NOT NULL) AND (NEW.SnapshotJson IS NULL OR NOT EXISTS (SELECT 1 FROM json_tree(NEW.SnapshotJson, '$') WHERE lower(key) IN ('tenantid','executorprincipalid')) AND NOT EXISTS (SELECT 1 FROM json_tree(NEW.SnapshotJson) field JOIN SystemIdentities i ON i.AgentInstanceId = field.value WHERE lower(field.key) = 'agentinstanceid' AND i.Kind = 0 AND i.TenantId IS NOT NULL))) OR (NEW.TenantId IS NOT NULL AND (json_type(NEW.SnapshotJson, '$') IS 'object' AND (SELECT count(*) FROM json_each(NEW.SnapshotJson, '$')) = (10 + CASE WHEN json_type(NEW.SnapshotJson, '$.PinnedSpaceId') IS NULL THEN 0 ELSE 1 END + CASE WHEN json_type(NEW.SnapshotJson, '$.PinnedEnvironmentId') IS NULL THEN 0 ELSE 1 END) AND (SELECT count(DISTINCT key) FROM json_each(NEW.SnapshotJson, '$') WHERE key IN ('AutomationId','Owner','TriggerRevision','FilterExpression','Dispatch','SourceKind','ResourceId','TriggerId','ExpressionVersion','FilterHash')) = 10 AND ((json_type(NEW.SnapshotJson, '$.PinnedSpaceId') IS NULL AND json_type(NEW.SnapshotJson, '$.PinnedEnvironmentId') IS NULL) OR (json_type(NEW.SnapshotJson, '$.PinnedSpaceId')='text' AND length(json_extract(NEW.SnapshotJson, '$.PinnedSpaceId'))=36 AND json_extract(NEW.SnapshotJson, '$.PinnedSpaceId') NOT GLOB '*[^0-9a-f-]*' AND json_extract(NEW.SnapshotJson, '$.PinnedSpaceId')<>'00000000-0000-0000-0000-000000000000' AND substr(json_extract(NEW.SnapshotJson, '$.PinnedSpaceId'),9,1)='-' AND substr(json_extract(NEW.SnapshotJson, '$.PinnedSpaceId'),14,1)='-' AND substr(json_extract(NEW.SnapshotJson, '$.PinnedSpaceId'),19,1)='-' AND substr(json_extract(NEW.SnapshotJson, '$.PinnedSpaceId'),24,1)='-' AND EXISTS(SELECT 1 FROM Spaces ps WHERE lower(ps.TenantId)=lower(json_extract(NEW.SnapshotJson, '$.Owner.TenantId')) AND lower(ps.SpaceId)=json_extract(NEW.SnapshotJson, '$.PinnedSpaceId') AND (ps.Kind=1 OR ps.OwnerInstanceId=json_extract(NEW.SnapshotJson, '$.Owner.AgentInstanceId'))) AND (json_type(NEW.SnapshotJson, '$.PinnedEnvironmentId') IS NULL OR (json_type(NEW.SnapshotJson, '$.PinnedEnvironmentId')='text' AND length(json_extract(NEW.SnapshotJson, '$.PinnedEnvironmentId'))=36 AND json_extract(NEW.SnapshotJson, '$.PinnedEnvironmentId') NOT GLOB '*[^0-9a-f-]*' AND json_extract(NEW.SnapshotJson, '$.PinnedEnvironmentId')<>'00000000-0000-0000-0000-000000000000' AND substr(json_extract(NEW.SnapshotJson, '$.PinnedEnvironmentId'),9,1)='-' AND substr(json_extract(NEW.SnapshotJson, '$.PinnedEnvironmentId'),14,1)='-' AND substr(json_extract(NEW.SnapshotJson, '$.PinnedEnvironmentId'),19,1)='-' AND substr(json_extract(NEW.SnapshotJson, '$.PinnedEnvironmentId'),24,1)='-' AND EXISTS(SELECT 1 FROM SpaceEnvironments pe WHERE lower(pe.TenantId)=lower(json_extract(NEW.SnapshotJson, '$.Owner.TenantId')) AND lower(pe.SpaceId)=json_extract(NEW.SnapshotJson, '$.PinnedSpaceId') AND lower(pe.EnvironmentId)=json_extract(NEW.SnapshotJson, '$.PinnedEnvironmentId'))))))) AND (json_type(NEW.SnapshotJson, '$.Owner') IS 'object' AND (SELECT count(*) FROM json_each(NEW.SnapshotJson, '$.Owner')) = 4 AND (SELECT count(DISTINCT key) FROM json_each(NEW.SnapshotJson, '$.Owner') WHERE key IN ('AgentInstanceId','profileId','TenantId','ExecutorPrincipalId')) = 4) AND json_extract(NEW.SnapshotJson, '$.Owner.AgentInstanceId') IS NEW.AgentInstanceId AND lower(json_extract(NEW.SnapshotJson, '$.Owner.TenantId')) IS lower(NEW.TenantId) AND lower(json_extract(NEW.SnapshotJson, '$.Owner.ExecutorPrincipalId')) IS lower(NEW.ExecutorPrincipalId) AND json_type(NEW.SnapshotJson, '$.Owner.profileId') IS 'null' AND (json_type(NEW.SnapshotJson, '$.Dispatch') IS 'object' AND (SELECT count(*) FROM json_each(NEW.SnapshotJson, '$.Dispatch')) = 2 AND (SELECT count(DISTINCT key) FROM json_each(NEW.SnapshotJson, '$.Dispatch') WHERE key IN ('Mode','WindowSeconds')) = 2) AND json_extract(NEW.SnapshotJson, '$.ExpressionVersion') IS 'js-expression-v1' AND json_extract(NEW.SnapshotJson, '$.TriggerRevision') >= 1 AND (json_type(NEW.SnapshotJson, '$.FilterExpression') IS 'null' OR (json_type(NEW.SnapshotJson, '$.FilterExpression') IS 'text' AND length(json_extract(NEW.SnapshotJson, '$.FilterExpression')) <= 1024)) AND ((json_extract(NEW.SnapshotJson, '$.Dispatch.Mode') = 0 AND json_type(NEW.SnapshotJson, '$.Dispatch.WindowSeconds') IS 'null') OR (json_extract(NEW.SnapshotJson, '$.Dispatch.Mode') = 1 AND json_extract(NEW.SnapshotJson, '$.Dispatch.WindowSeconds') BETWEEN 60 AND 3600)) AND json_extract(NEW.SnapshotJson, '$.AutomationId') IS NEW.AutomationId AND json_extract(NEW.SnapshotJson, '$.TriggerId') IS NEW.TriggerId AND json_extract(NEW.SnapshotJson, '$.SourceKind') = 1 AND EXISTS (SELECT 1 FROM ExternalEvents e, json_each(e.SubscriptionsJson) f WHERE e.EventId = NEW.EventId AND e.TenantId = NEW.TenantId AND json(f.value) IS json(NEW.SnapshotJson)) AND NEW.Status BETWEEN 0 AND 6 AND (NEW.Status NOT IN (1,5,6) OR json_extract(NEW.DecisionJson, '$.Matched') = 1) AND (NEW.ReservedOccurrenceId IS NULL AND NEW.Status NOT IN (1,6) OR NEW.ReservedOccurrenceId IS NOT NULL AND EXISTS (SELECT 1 FROM TriggerOccurrences o WHERE o.OccurrenceId = NEW.ReservedOccurrenceId AND o.TenantId = NEW.TenantId AND o.AgentInstanceId = NEW.AgentInstanceId AND o.ExecutorPrincipalId = NEW.ExecutorPrincipalId AND o.AutomationId = NEW.AutomationId AND o.TriggerId = NEW.TriggerId AND o.SourceEventId = NEW.EventId AND o.SourceKind = 1 AND ((NEW.Status = 6 AND o.Disposition = 3 AND o.DispositionReason = 'admission_approval_pending') OR (NEW.Status = 1 AND o.Disposition = 6) OR (NEW.Status = 2 AND o.Disposition = 4)))))),0);
END;

CREATE TRIGGER TR_TenantWebhook_Receipt_Insert BEFORE INSERT ON ExternalEvents
        WHEN NEW.TenantId IS NOT NULL OR EXISTS (SELECT 1 FROM WebhookEvents s WHERE s.ResourceId = NEW.ResourceId AND s.TenantId IS NOT NULL)
        BEGIN
            SELECT RAISE(ABORT, 'tenant receipt requires its exact active source and bounded evidence') WHERE NOT COALESCE((
                EXISTS (SELECT 1 FROM WebhookEvents s JOIN Tenants t ON t.TenantId = s.TenantId
                    WHERE s.ResourceId = NEW.ResourceId AND s.TenantId = NEW.TenantId AND s.Revision = NEW.SourceRevision AND s.Status = 0 AND t.Status = 0)
                AND NEW.SourceEventId NOT GLOB '*[^!-~]*'
                AND (SELECT count(*) FROM json_each(NEW.EvidenceJson)) = 5
                AND (SELECT count(DISTINCT key) FROM json_each(NEW.EvidenceJson) WHERE key IN ('sourceEventId','data','occurredAtUtc','rootAgentRunId','triggerDepth')) = 5
                AND json_type(NEW.EvidenceJson, '$.rootAgentRunId') IS 'null'
                AND json_type(NEW.EvidenceJson, '$.triggerDepth') IS 'integer'
                AND (json_type(NEW.EvidenceJson, '$.occurredAtUtc') IS 'null' OR json_type(NEW.EvidenceJson, '$.occurredAtUtc') IS 'text')),0);
SELECT RAISE(ABORT, 'Webhook receipt requires its exact frozen subscriptions') WHERE NOT COALESCE((json_type(NEW.SubscriptionsJson) IS 'array' AND json_array_length(NEW.SubscriptionsJson) <= 1024 AND json_array_length(NEW.SubscriptionsJson) = (SELECT count(*) FROM Automations a JOIN AutomationTriggers t ON t.AutomationId = a.AutomationId WHERE a.TenantId = NEW.TenantId AND a.Status = 0 AND (a.ExpiresAtUtc IS NULL OR a.ExpiresAtUtc > NEW.AdmittedAtUtc) AND t.Enabled = 1 AND t.Kind = 1 AND t.EventId = NEW.ResourceId) AND (SELECT count(DISTINCT json_extract(value, '$.TriggerId')) FROM json_each(NEW.SubscriptionsJson)) = json_array_length(NEW.SubscriptionsJson) AND NOT EXISTS (SELECT 1 FROM json_each(NEW.SubscriptionsJson) f WHERE NOT COALESCE(((json_type(f.value, '$') IS 'object' AND (SELECT count(*) FROM json_each(f.value, '$')) = (10 + CASE WHEN json_type(f.value, '$.PinnedSpaceId') IS NULL THEN 0 ELSE 1 END + CASE WHEN json_type(f.value, '$.PinnedEnvironmentId') IS NULL THEN 0 ELSE 1 END) AND (SELECT count(DISTINCT key) FROM json_each(f.value, '$') WHERE key IN ('AutomationId','Owner','TriggerRevision','FilterExpression','Dispatch','SourceKind','ResourceId','TriggerId','ExpressionVersion','FilterHash')) = 10 AND ((json_type(f.value, '$.PinnedSpaceId') IS NULL AND json_type(f.value, '$.PinnedEnvironmentId') IS NULL) OR (json_type(f.value, '$.PinnedSpaceId')='text' AND length(json_extract(f.value, '$.PinnedSpaceId'))=36 AND json_extract(f.value, '$.PinnedSpaceId') NOT GLOB '*[^0-9a-f-]*' AND json_extract(f.value, '$.PinnedSpaceId')<>'00000000-0000-0000-0000-000000000000' AND substr(json_extract(f.value, '$.PinnedSpaceId'),9,1)='-' AND substr(json_extract(f.value, '$.PinnedSpaceId'),14,1)='-' AND substr(json_extract(f.value, '$.PinnedSpaceId'),19,1)='-' AND substr(json_extract(f.value, '$.PinnedSpaceId'),24,1)='-' AND EXISTS(SELECT 1 FROM Spaces ps WHERE lower(ps.TenantId)=lower(json_extract(f.value, '$.Owner.TenantId')) AND lower(ps.SpaceId)=json_extract(f.value, '$.PinnedSpaceId') AND (ps.Kind=1 OR ps.OwnerInstanceId=json_extract(f.value, '$.Owner.AgentInstanceId'))) AND (json_type(f.value, '$.PinnedEnvironmentId') IS NULL OR (json_type(f.value, '$.PinnedEnvironmentId')='text' AND length(json_extract(f.value, '$.PinnedEnvironmentId'))=36 AND json_extract(f.value, '$.PinnedEnvironmentId') NOT GLOB '*[^0-9a-f-]*' AND json_extract(f.value, '$.PinnedEnvironmentId')<>'00000000-0000-0000-0000-000000000000' AND substr(json_extract(f.value, '$.PinnedEnvironmentId'),9,1)='-' AND substr(json_extract(f.value, '$.PinnedEnvironmentId'),14,1)='-' AND substr(json_extract(f.value, '$.PinnedEnvironmentId'),19,1)='-' AND substr(json_extract(f.value, '$.PinnedEnvironmentId'),24,1)='-' AND EXISTS(SELECT 1 FROM SpaceEnvironments pe WHERE lower(pe.TenantId)=lower(json_extract(f.value, '$.Owner.TenantId')) AND lower(pe.SpaceId)=json_extract(f.value, '$.PinnedSpaceId') AND lower(pe.EnvironmentId)=json_extract(f.value, '$.PinnedEnvironmentId'))))))) AND (json_type(f.value, '$.Owner') IS 'object' AND (SELECT count(*) FROM json_each(f.value, '$.Owner')) = 4 AND (SELECT count(DISTINCT key) FROM json_each(f.value, '$.Owner') WHERE key IN ('AgentInstanceId','profileId','TenantId','ExecutorPrincipalId')) = 4) AND json_extract(f.value, '$.Owner.AgentInstanceId') IS json_extract(f.value, '$.Owner.AgentInstanceId') AND lower(json_extract(f.value, '$.Owner.TenantId')) IS lower(NEW.TenantId) AND lower(json_extract(f.value, '$.Owner.ExecutorPrincipalId')) IS lower(json_extract(f.value, '$.Owner.ExecutorPrincipalId')) AND json_type(f.value, '$.Owner.profileId') IS 'null' AND (json_type(f.value, '$.Dispatch') IS 'object' AND (SELECT count(*) FROM json_each(f.value, '$.Dispatch')) = 2 AND (SELECT count(DISTINCT key) FROM json_each(f.value, '$.Dispatch') WHERE key IN ('Mode','WindowSeconds')) = 2) AND json_extract(f.value, '$.ExpressionVersion') IS 'js-expression-v1' AND json_extract(f.value, '$.TriggerRevision') >= 1 AND (json_type(f.value, '$.FilterExpression') IS 'null' OR (json_type(f.value, '$.FilterExpression') IS 'text' AND length(json_extract(f.value, '$.FilterExpression')) <= 1024)) AND ((json_extract(f.value, '$.Dispatch.Mode') = 0 AND json_type(f.value, '$.Dispatch.WindowSeconds') IS 'null') OR (json_extract(f.value, '$.Dispatch.Mode') = 1 AND json_extract(f.value, '$.Dispatch.WindowSeconds') BETWEEN 60 AND 3600)) AND json_extract(f.value, '$.SourceKind') = 1 AND json_extract(f.value, '$.ResourceId') IS NEW.ResourceId AND EXISTS (SELECT 1 FROM Automations a JOIN AutomationTriggers t ON t.AutomationId = a.AutomationId WHERE a.TenantId = NEW.TenantId AND a.Status = 0 AND (a.ExpiresAtUtc IS NULL OR a.ExpiresAtUtc > NEW.AdmittedAtUtc) AND t.Enabled = 1 AND t.Kind = 1 AND t.EventId = NEW.ResourceId AND a.AutomationId IS json_extract(f.value, '$.AutomationId') AND a.AgentInstanceId IS json_extract(f.value, '$.Owner.AgentInstanceId') AND lower(a.ExecutorPrincipalId) IS json_extract(f.value, '$.Owner.ExecutorPrincipalId') AND t.TriggerId IS json_extract(f.value, '$.TriggerId') AND t.Revision = json_extract(f.value, '$.TriggerRevision') AND t.FilterExpression IS json_extract(f.value, '$.FilterExpression') AND t.DispatchMode = json_extract(f.value, '$.Dispatch.Mode') AND t.DispatchWindowSeconds IS json_extract(f.value, '$.Dispatch.WindowSeconds'))),0))),0);
        END;

CREATE TRIGGER WorkflowAutomationDestination_INSERT BEFORE INSERT ON Automations
WHEN NEW.ExecutionTargetKind=2 BEGIN
 SELECT RAISE(ABORT,'Workflow Automation destination requires its exact published version') WHERE NOT COALESCE((
   NEW.TenantId IS NOT NULL AND NEW.TargetSessionId IS NULL AND NEW.ReportToSessionId IS NULL AND NEW.ModelPreferenceJson IS NULL
   AND NEW.RequiresTools=0 AND NEW.RequiresVision=0 AND NEW.PinnedSpaceId IS NULL AND NEW.PinnedEnvironmentId IS NULL
   AND (SELECT count(*) FROM json_each(NEW.WorkflowTargetJson))=4
   AND NOT EXISTS(SELECT 1 FROM json_each(NEW.WorkflowTargetJson) WHERE key NOT IN ('WorkflowId','Version','GraphHash','InputsJson'))
   AND EXISTS(SELECT 1 FROM TenantWorkflowVersions v WHERE v.TenantId=NEW.TenantId
     AND lower(v.WorkflowId)=lower(json_extract(NEW.WorkflowTargetJson,'$.WorkflowId')) AND v.Version=json_extract(NEW.WorkflowTargetJson,'$.Version')
     AND json_extract(v.Json,'$.Graph.Hash')=json_extract(NEW.WorkflowTargetJson,'$.GraphHash')
     AND EXISTS(SELECT 1 FROM json_each(json_extract(v.Json,'$.Graph.CanonicalJson'),'$.allowedOrigins') WHERE value=2))),0);
END;

CREATE TRIGGER WorkflowAutomationDestination_UPDATE BEFORE UPDATE ON Automations
WHEN NEW.ExecutionTargetKind=2 BEGIN
 SELECT RAISE(ABORT,'Workflow Automation destination requires its exact published version') WHERE NOT COALESCE((
   NEW.TenantId IS NOT NULL AND NEW.TargetSessionId IS NULL AND NEW.ReportToSessionId IS NULL AND NEW.ModelPreferenceJson IS NULL
   AND NEW.RequiresTools=0 AND NEW.RequiresVision=0 AND NEW.PinnedSpaceId IS NULL AND NEW.PinnedEnvironmentId IS NULL
   AND (SELECT count(*) FROM json_each(NEW.WorkflowTargetJson))=4
   AND NOT EXISTS(SELECT 1 FROM json_each(NEW.WorkflowTargetJson) WHERE key NOT IN ('WorkflowId','Version','GraphHash','InputsJson'))
   AND EXISTS(SELECT 1 FROM TenantWorkflowVersions v WHERE v.TenantId=NEW.TenantId
     AND lower(v.WorkflowId)=lower(json_extract(NEW.WorkflowTargetJson,'$.WorkflowId')) AND v.Version=json_extract(NEW.WorkflowTargetJson,'$.Version')
     AND json_extract(v.Json,'$.Graph.Hash')=json_extract(NEW.WorkflowTargetJson,'$.GraphHash')
     AND EXISTS(SELECT 1 FROM json_each(json_extract(v.Json,'$.Graph.CanonicalJson'),'$.allowedOrigins') WHERE value=2))),0);
END;

CREATE TRIGGER WorkflowExecutionImmutable BEFORE UPDATE ON TenantWorkflowExecutions BEGIN
 SELECT RAISE(ABORT,'Workflow business source is immutable and terminal state is monotonic') WHERE NOT COALESCE((
   NEW.TenantId=OLD.TenantId AND NEW.ExecutionId=OLD.ExecutionId AND NEW.WorkflowId=OLD.WorkflowId AND NEW.Version=OLD.Version
   AND NEW.SourceScope=OLD.SourceScope AND NEW.RequestKey=OLD.RequestKey AND NEW.CreatedAt=OLD.CreatedAt
   AND NEW.Revision=OLD.Revision+1 AND OLD.Status<8 AND NEW.Status BETWEEN 0 AND 11
   AND json_extract(NEW.Json,'$.Revision')=NEW.Revision AND json_extract(NEW.Json,'$.Status')=NEW.Status
   AND (OLD.Status<8 AND (NEW.Status<8 AND json_extract(NEW.Json,'$.TerminalAt') IS NULL OR NEW.Status>=8 AND json_type(NEW.Json,'$.TerminalAt')='text'))
   AND json_remove(NEW.Json,'$.Revision','$.Status','$.CancellationRequested','$.Cancellation','$.ReasonCode','$.TerminalAt','$.SourceClosure')
     IS json_remove(OLD.Json,'$.Revision','$.Status','$.CancellationRequested','$.Cancellation','$.ReasonCode','$.TerminalAt','$.SourceClosure')
   AND (json_extract(OLD.Json,'$.CancellationRequested')=0 OR json_extract(NEW.Json,'$.CancellationRequested')=1)
   AND (json_extract(OLD.Json,'$.Cancellation') IS NULL OR json_extract(NEW.Json,'$.Cancellation') IS json_extract(OLD.Json,'$.Cancellation'))
   AND (json_extract(OLD.Json,'$.SourceClosure') IS NULL OR json_extract(NEW.Json,'$.SourceClosure') IS json_extract(OLD.Json,'$.SourceClosure'))
   AND (NEW.Status<>8 OR EXISTS(SELECT 1 FROM TenantWorkflowSteps step WHERE step.TenantId=NEW.TenantId AND step.ExecutionId=NEW.ExecutionId
     AND json_extract(step.Json,'$.Kind')=5 AND json_extract(step.Json,'$.Status')=3 AND json_extract(step.Json,'$.ProofAccepted')=1))
   AND (NEW.Status<>10 OR json_extract(NEW.Json,'$.CancellationRequested')=1)),0);
END;

CREATE TRIGGER WorkflowExecutionSourceGuard BEFORE INSERT ON TenantWorkflowExecutions BEGIN
 SELECT RAISE(ABORT,'Workflow requires its genuine human command or currently claimed Run') WHERE NOT COALESCE((
   NEW.Revision=1 AND NEW.Status=0 AND json_valid(NEW.Json) AND json_extract(NEW.Json,'$.SourceClosure') IS NULL
   AND lower(json_extract(NEW.Json,'$.TenantId'))=lower(NEW.TenantId)
   AND lower(json_extract(NEW.Json,'$.ExecutionId'))=lower(NEW.ExecutionId)
   AND lower(json_extract(NEW.Json,'$.WorkflowId'))=lower(NEW.WorkflowId)
   AND json_extract(NEW.Json,'$.Version')=NEW.Version AND json_extract(NEW.Json,'$.Revision')=NEW.Revision
   AND json_extract(NEW.Json,'$.Status')=NEW.Status AND json_extract(NEW.Json,'$.RequestKey')=NEW.RequestKey
   AND json_extract(NEW.Json,'$.Source.RequestScope')=NEW.SourceScope
   AND json_extract(NEW.Json,'$.CancellationRequested')=0 AND json_extract(NEW.Json,'$.TerminalAt') IS NULL
   AND EXISTS(SELECT 1 FROM TenantWorkflowVersions version WHERE version.TenantId=NEW.TenantId AND version.WorkflowId=NEW.WorkflowId AND version.Version=NEW.Version
     AND json_extract(version.Json,'$.Graph.Hash')=json_extract(NEW.Json,'$.GraphHash')
     AND EXISTS(SELECT 1 FROM json_each(json_extract(version.Json,'$.Graph.CanonicalJson'),'$.allowedOrigins') origin WHERE origin.value=json_extract(NEW.Json,'$.Source.Kind')))
   AND ((json_extract(NEW.Json,'$.Source.Kind')=0
     AND lower(json_extract(NEW.Json,'$.Source.SourceId'))=lower(NEW.ExecutionId)
     AND json_extract(NEW.Json,'$.Source.OriginalHumanPrincipalId') IS json_extract(NEW.Json,'$.Source.PrincipalId')
     AND json_extract(NEW.Json,'$.Source.RunId') IS NULL AND json_extract(NEW.Json,'$.Source.InvokingInstanceId') IS NULL
     AND json_extract(NEW.Json,'$.Source.AutomationId') IS NULL AND json_extract(NEW.Json,'$.Source.RootActivationId') IS NULL
     AND json_extract(NEW.Json,'$.Source.Causation') IS NULL AND json_extract(NEW.Json,'$.SourceBudget') IS NULL
     AND EXISTS(SELECT 1 FROM TenantUsers user JOIN Accounts account ON account.AccountId=user.AccountId
       WHERE user.TenantId=NEW.TenantId AND lower(user.PrincipalId)=lower(json_extract(NEW.Json,'$.Source.PrincipalId'))
         AND lower(user.AccountId)=lower(json_extract(NEW.Json,'$.Source.AccountId')) AND user.Status=0 AND account.Status=0))
   OR (json_extract(NEW.Json,'$.Source.Kind')=1 AND json_extract(NEW.Json,'$.Source.AccountId') IS NULL
     AND json_extract(NEW.Json,'$.Source.AutomationId') IS NULL
     AND EXISTS(SELECT 1 FROM AgentRuns source WHERE source.TenantId=NEW.TenantId
       AND source.AgentRunId=json_extract(NEW.Json,'$.Source.RunId') AND source.AgentRunId=json_extract(NEW.Json,'$.Source.SourceId')
       AND source.SessionId=json_extract(NEW.Json,'$.Source.SessionId') AND source.AgentInstanceId=json_extract(NEW.Json,'$.Source.InvokingInstanceId')
       AND lower(source.ExecutorPrincipalId)=lower(json_extract(NEW.Json,'$.Source.PrincipalId'))
       AND lower(source.OriginalInitiatorPrincipalId) IS json_extract(NEW.Json,'$.Source.OriginalHumanPrincipalId')
       AND source.RootActivationId=json_extract(NEW.Json,'$.Source.RootActivationId')
       AND source.Status=1 AND source.LeaseExpiresAtUtc>NEW.CreatedAt AND json_type(source.PayloadJson,'$.claim.generation')='text'
       AND json_extract(source.PayloadJson,'$.cancellationRequested')=0))
   OR (json_extract(NEW.Json,'$.Source.Kind')=2 AND json_extract(NEW.Json,'$.Source.AccountId') IS NULL
     AND json_extract(NEW.Json,'$.Source.RunId') IS NULL AND json_extract(NEW.Json,'$.Source.SessionId') IS NULL
     AND json_extract(NEW.Json,'$.Source.RootActivationId') IS NULL AND NEW.RequestKey='automation-occurrence'
     AND EXISTS(SELECT 1 FROM TriggerOccurrences occurrence WHERE occurrence.TenantId=NEW.TenantId
       AND lower(occurrence.OccurrenceId)=lower(json_extract(NEW.Json,'$.Source.SourceId'))
       AND lower(occurrence.AutomationId)=lower(json_extract(NEW.Json,'$.Source.AutomationId'))
       AND lower(occurrence.AgentInstanceId)=lower(json_extract(NEW.Json,'$.Source.InvokingInstanceId'))
       AND lower(occurrence.ExecutorPrincipalId)=lower(json_extract(NEW.Json,'$.Source.PrincipalId'))
       AND lower(occurrence.WorkflowInitiatorPrincipalId) IS json_extract(NEW.Json,'$.Source.OriginalHumanPrincipalId')
       AND occurrence.ExecutionTargetKind=2 AND occurrence.Disposition=0 AND occurrence.RoutingRevision=0
       AND occurrence.AcceptedWorkflowExecutionId IS NULL AND occurrence.AcceptedAgentRunId IS NULL AND occurrence.ExecutionSessionId IS NULL
       AND lower(json_extract(occurrence.WorkflowTargetJson,'$.WorkflowId'))=lower(NEW.WorkflowId)
       AND json_extract(occurrence.WorkflowTargetJson,'$.Version')=NEW.Version
       AND json_extract(occurrence.WorkflowTargetJson,'$.GraphHash')=json_extract(NEW.Json,'$.GraphHash')
       AND json_extract(occurrence.WorkflowTargetJson,'$.InputsJson')=json_extract(NEW.Json,'$.InputsJson'))))) ,0);
END;

CREATE TRIGGER WorkflowOccurrenceInsert BEFORE INSERT ON TriggerOccurrences WHEN NEW.ExecutionTargetKind=2 BEGIN
 SELECT RAISE(ABORT,'Workflow occurrence requires the actual pending source') WHERE NOT COALESCE((
   NEW.Disposition=0 AND NEW.RoutingRevision=0 AND NEW.DispositionReason IS NULL AND NEW.ProfileId IS NULL
   AND NEW.ExecutionSessionId IS NULL AND NEW.AcceptedAgentRunId IS NULL AND NEW.AcceptedWorkflowExecutionId IS NULL AND NEW.LiveSessionId IS NULL
   AND NEW.LiveEvaluationCompletedAtUtc IS NULL AND NEW.ModelOfferingId IS NULL AND NEW.ModelConnectionId IS NULL AND NEW.ExternalModelId IS NULL
   AND NEW.ClaimId IS NULL AND NEW.ClaimLeaseExpiresAtUtc IS NULL AND NEW.SourceKind IN(0,1,2,3)
   AND NEW.PinnedSpaceId IS NULL AND NEW.PinnedEnvironmentId IS NULL
   AND json_extract(NEW.EvidenceJson,'$.admissionApproval') IS NULL
   AND EXISTS(SELECT 1 FROM Automations a WHERE a.TenantId=NEW.TenantId AND a.AutomationId=NEW.AutomationId AND a.Status=0
     AND a.AgentInstanceId=NEW.AgentInstanceId AND a.ExecutorPrincipalId=NEW.ExecutorPrincipalId
     AND a.ExecutionTargetKind=2 AND a.WorkflowTargetJson=NEW.WorkflowTargetJson
     AND json_extract(NEW.EvidenceJson,'$.automationRevision')=a.Revision)
   AND ((NEW.SourceKind=2 AND EXISTS(SELECT 1 FROM TenantUsers u JOIN Accounts a ON a.AccountId=u.AccountId
     WHERE u.TenantId=NEW.TenantId AND u.PrincipalId=NEW.WorkflowInitiatorPrincipalId AND u.Status=0 AND a.Status=0))
     OR (NEW.SourceKind<>2 AND NEW.WorkflowInitiatorPrincipalId IS NULL))),0);
END;

CREATE TRIGGER WorkflowOccurrenceLegacyUpdate BEFORE UPDATE ON TriggerOccurrences WHEN OLD.ExecutionTargetKind<>2 AND (NEW.ExecutionTargetKind=2 OR OLD.WorkflowTargetJson IS NOT NEW.WorkflowTargetJson OR OLD.AcceptedWorkflowExecutionId IS NOT NEW.AcceptedWorkflowExecutionId OR OLD.WorkflowInitiatorPrincipalId IS NOT NEW.WorkflowInitiatorPrincipalId) BEGIN SELECT RAISE(ABORT,'Legacy occurrence cannot acquire a Workflow source'); END;

CREATE TRIGGER WorkflowOccurrenceUpdate BEFORE UPDATE ON TriggerOccurrences WHEN OLD.ExecutionTargetKind=2 OR NEW.ExecutionTargetKind=2 BEGIN
 SELECT RAISE(ABORT,'Workflow occurrence source is immutable') WHERE OLD."OccurrenceId" IS NOT NEW."OccurrenceId" OR OLD."AcceptedAgentRunId" IS NOT NEW."AcceptedAgentRunId" OR OLD."AdmittedAtUtc" IS NOT NEW."AdmittedAtUtc" OR OLD."AgentInstanceId" IS NOT NEW."AgentInstanceId" OR OLD."AutomationId" IS NOT NEW."AutomationId" OR OLD."ClaimId" IS NOT NEW."ClaimId" OR OLD."ClaimLeaseExpiresAtUtc" IS NOT NEW."ClaimLeaseExpiresAtUtc" OR OLD."DedupeKey" IS NOT NEW."DedupeKey" OR OLD."EvidenceJson" IS NOT NEW."EvidenceJson" OR OLD."ExecutionSessionId" IS NOT NEW."ExecutionSessionId" OR OLD."ExecutionTargetKind" IS NOT NEW."ExecutionTargetKind" OR OLD."ExecutorPrincipalId" IS NOT NEW."ExecutorPrincipalId" OR OLD."LiveEvaluationCompletedAtUtc" IS NOT NEW."LiveEvaluationCompletedAtUtc" OR OLD."LiveSessionId" IS NOT NEW."LiveSessionId" OR OLD."ModelOfferingId" IS NOT NEW."ModelOfferingId" OR OLD."ExternalModelId" IS NOT NEW."ExternalModelId" OR OLD."ModelConnectionId" IS NOT NEW."ModelConnectionId" OR OLD."ModelReasoningEffort" IS NOT NEW."ModelReasoningEffort" OR OLD."ModelSource" IS NOT NEW."ModelSource" OR OLD."ObservedAtUtc" IS NOT NEW."ObservedAtUtc" OR OLD."ProfileId" IS NOT NEW."ProfileId" OR OLD."ReportToSessionId" IS NOT NEW."ReportToSessionId" OR OLD."ScheduledAtUtc" IS NOT NEW."ScheduledAtUtc" OR OLD."SourceEventId" IS NOT NEW."SourceEventId" OR OLD."SourceKind" IS NOT NEW."SourceKind" OR OLD."TargetSessionId" IS NOT NEW."TargetSessionId" OR OLD."TenantId" IS NOT NEW."TenantId" OR OLD."TriggerId" IS NOT NEW."TriggerId" OR OLD."TriggerRevision" IS NOT NEW."TriggerRevision" OR OLD."PinnedSpaceId" IS NOT NEW."PinnedSpaceId" OR OLD."PinnedEnvironmentId" IS NOT NEW."PinnedEnvironmentId" OR OLD."ModelBindingJson" IS NOT NEW."ModelBindingJson" OR OLD."ModelDecisionJson" IS NOT NEW."ModelDecisionJson" OR OLD."WorkflowTargetJson" IS NOT NEW."WorkflowTargetJson" OR OLD."WorkflowInitiatorPrincipalId" IS NOT NEW."WorkflowInitiatorPrincipalId";
 SELECT RAISE(ABORT,'Workflow occurrence permits only its exact admission') WHERE NOT (
  OLD.Disposition IS NEW.Disposition AND OLD.DispositionReason IS NEW.DispositionReason AND OLD.RoutingRevision IS NEW.RoutingRevision
  AND OLD.RoutingUpdatedAtUtc IS NEW.RoutingUpdatedAtUtc AND OLD.AcceptedWorkflowExecutionId IS NEW.AcceptedWorkflowExecutionId)
 AND NOT COALESCE((OLD.Disposition=0 AND OLD.RoutingRevision=0 AND OLD.AcceptedWorkflowExecutionId IS NULL AND NEW.Disposition=6
  AND NEW.RoutingRevision=1 AND NEW.DispositionReason IS NULL AND NEW.RoutingUpdatedAtUtc>=NEW.AdmittedAtUtc
  AND EXISTS(SELECT 1 FROM TenantWorkflowExecutions e WHERE e.TenantId=NEW.TenantId AND e.ExecutionId=NEW.AcceptedWorkflowExecutionId
    AND json_extract(e.Json,'$.Source.Kind')=2 AND lower(json_extract(e.Json,'$.Source.SourceId'))=lower(NEW.OccurrenceId)
    AND lower(json_extract(e.Json,'$.Source.AutomationId'))=lower(NEW.AutomationId)
    AND json_extract(e.Json,'$.InputsJson')=json_extract(NEW.WorkflowTargetJson,'$.InputsJson'))),0);
END;
