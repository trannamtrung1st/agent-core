using Microsoft.EntityFrameworkCore.Migrations;
#nullable disable
namespace AgentCore.Infrastructure.Persistence.Migrations;

public partial class AutomationChildTriggers : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // All copies, snapshot lineage and schema replacement run in the migration transaction.
        // The deterministic old child ID equals AutomationId, preserving old dedupe keys.
        migrationBuilder.Sql("""
CREATE TEMP TABLE TriggerSeed AS SELECT AutomationId AS TriggerId, AutomationId, TriggerKind AS Kind, 1 AS Enabled, TriggerRevision AS Revision, ScheduleJson, EventId, CoreEventKey, FilterExpression, DispatchMode, DispatchWindowSeconds FROM Automations;
CREATE TABLE Automations_new ("TriggerMode" INTEGER NOT NULL,
"PresetId" TEXT NULL,
"PresetVersion" INTEGER NULL,
"ExecutionTargetKind" INTEGER NOT NULL,
"TargetSessionId" TEXT NULL,
"ReportToSessionId" TEXT NULL,
"AutomationId" TEXT NOT NULL PRIMARY KEY,
"AgentInstanceId" TEXT NOT NULL,
"ProfileId" TEXT NOT NULL,
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
"ModelOverrideCatalogKey" TEXT NULL,
"ModelOverrideReasoningEffort" TEXT NULL,
"RequiresVision" INTEGER NOT NULL,
"RequiresTools" INTEGER NOT NULL,
CONSTRAINT CK_Automations_Destination CHECK ((ExecutionTargetKind = 0 AND TargetSessionId IS NULL) OR (ExecutionTargetKind = 1 AND TargetSessionId IS NOT NULL AND ReportToSessionId IS NULL)),
CONSTRAINT CK_Automations_Preset CHECK ((PresetId IS NULL AND PresetVersion IS NULL) OR (PresetId IS NOT NULL AND PresetVersion >= 1)),
CONSTRAINT CK_Automations_Mode CHECK (TriggerMode IN (0,1)));
INSERT INTO Automations_new (TriggerMode,PresetId,PresetVersion,ExecutionTargetKind,TargetSessionId,ReportToSessionId,AutomationId,AgentInstanceId,ProfileId,Status,Instructions,Name,NextOccurrenceAtUtc,ExpiresAtUtc,OccurrenceCount,Revision,AuthorizationOrigin,SourceSessionId,SourceEventId,CreatedAtUtc,UpdatedAtUtc,SuspensionReason,ModelOverrideCatalogKey,ModelOverrideReasoningEffort,RequiresVision,RequiresTools) SELECT CASE WHEN TriggerKind=0 THEN 0 ELSE 1 END,PresetId,PresetVersion,ExecutionTargetKind,TargetSessionId,ReportToSessionId,AutomationId,AgentInstanceId,ProfileId,Status,Instructions,Name,NextOccurrenceAtUtc,ExpiresAtUtc,OccurrenceCount,Revision,AuthorizationOrigin,SourceSessionId,SourceEventId,CreatedAtUtc,UpdatedAtUtc,SuspensionReason,ModelOverrideCatalogKey,ModelOverrideReasoningEffort,RequiresVision,RequiresTools FROM Automations;
DROP TABLE Automations;
ALTER TABLE Automations_new RENAME TO Automations;
CREATE INDEX IX_Automations_AgentInstanceId_ProfileId_Status ON Automations(AgentInstanceId,ProfileId,Status);
CREATE INDEX IX_Automations_Status_NextOccurrenceAtUtc_AutomationId ON Automations(Status,NextOccurrenceAtUtc,AutomationId);
CREATE TABLE AutomationTriggers (
TriggerId TEXT NOT NULL PRIMARY KEY, AutomationId TEXT NOT NULL REFERENCES Automations(AutomationId) ON DELETE CASCADE,
Kind INTEGER NOT NULL, Enabled INTEGER NOT NULL, Revision INTEGER NOT NULL,
ScheduleJson TEXT NULL, EventId TEXT NULL, CoreEventKey TEXT NULL, FilterExpression TEXT NULL, DispatchMode INTEGER NOT NULL, DispatchWindowSeconds INTEGER NULL,
CONSTRAINT CK_AutomationTriggers_Identity CHECK (length(TriggerId) = 36 AND TriggerId <> '00000000-0000-0000-0000-000000000000' AND Revision >= 1),
CONSTRAINT CK_AutomationTriggers_Source CHECK ((Kind = 0 AND ScheduleJson IS NOT NULL AND EventId IS NULL AND CoreEventKey IS NULL AND FilterExpression IS NULL AND DispatchMode = 0 AND DispatchWindowSeconds IS NULL) OR (Kind = 1 AND ScheduleJson IS NULL AND EventId IS NOT NULL AND CoreEventKey IS NULL) OR (Kind = 2 AND ScheduleJson IS NULL AND EventId IS NULL AND CoreEventKey IN ('run.completed','run.failed','session.completed','session.ended','instance.config_changed','harness.definition_adopted'))),
CONSTRAINT CK_AutomationTriggers_Dispatch CHECK ((DispatchMode = 0 AND DispatchWindowSeconds IS NULL) OR (DispatchMode = 1 AND DispatchWindowSeconds BETWEEN 60 AND 3600)));
INSERT INTO AutomationTriggers SELECT * FROM TriggerSeed;
DROP TABLE TriggerSeed;
CREATE UNIQUE INDEX IX_AutomationTriggers_AutomationId_EventId ON AutomationTriggers(AutomationId,EventId) WHERE EventId IS NOT NULL;
CREATE UNIQUE INDEX IX_AutomationTriggers_AutomationId_CoreEventKey ON AutomationTriggers(AutomationId,CoreEventKey) WHERE CoreEventKey IS NOT NULL;
CREATE UNIQUE INDEX IX_AutomationTriggers_AutomationId_Kind ON AutomationTriggers(AutomationId,Kind) WHERE Kind = 0;
CREATE INDEX IX_AutomationTriggers_EventId_Enabled ON AutomationTriggers(EventId,Enabled);
CREATE TRIGGER AutomationTriggers_Mode_Insert BEFORE INSERT ON AutomationTriggers WHEN (NEW.Kind=0) <> ((SELECT TriggerMode FROM Automations WHERE AutomationId=NEW.AutomationId)=0) BEGIN SELECT RAISE(ABORT,'Trigger mode mismatch'); END;
CREATE TRIGGER AutomationTriggers_Mode_Update BEFORE UPDATE ON AutomationTriggers WHEN (NEW.Kind=0) <> ((SELECT TriggerMode FROM Automations WHERE AutomationId=NEW.AutomationId)=0) BEGIN SELECT RAISE(ABORT,'Trigger mode mismatch'); END;
CREATE TABLE CoreEventDeliveries_new (EventId TEXT NOT NULL,AutomationId TEXT NOT NULL,AgentInstanceId TEXT NOT NULL,ProfileId TEXT NOT NULL,SnapshotJson TEXT NOT NULL,Status INTEGER NOT NULL,DecisionJson TEXT NULL,Code TEXT NULL, TriggerId TEXT NOT NULL, PRIMARY KEY(EventId,TriggerId));
INSERT INTO CoreEventDeliveries_new (EventId,AutomationId,AgentInstanceId,ProfileId,SnapshotJson,Status,DecisionJson,Code,TriggerId) SELECT EventId,AutomationId,AgentInstanceId,ProfileId,CASE WHEN SnapshotJson IS NULL THEN NULL ELSE json_set(SnapshotJson,'$.TriggerId',AutomationId) END,Status,DecisionJson,Code,AutomationId FROM CoreEventDeliveries;
DROP TABLE CoreEventDeliveries; ALTER TABLE CoreEventDeliveries_new RENAME TO CoreEventDeliveries;
CREATE TABLE ExternalEventDeliveries_new (EventId TEXT NOT NULL,AutomationId TEXT NOT NULL,AgentInstanceId TEXT NOT NULL,ProfileId TEXT NOT NULL,SnapshotJson TEXT NULL,Status INTEGER NOT NULL,DecisionJson TEXT NULL, TriggerId TEXT NOT NULL, PRIMARY KEY(EventId,TriggerId));
INSERT INTO ExternalEventDeliveries_new (EventId,AutomationId,AgentInstanceId,ProfileId,SnapshotJson,Status,DecisionJson,TriggerId) SELECT EventId,AutomationId,AgentInstanceId,ProfileId,CASE WHEN SnapshotJson IS NULL THEN NULL ELSE json_set(SnapshotJson,'$.TriggerId',AutomationId) END,Status,DecisionJson,AutomationId FROM ExternalEventDeliveries;
DROP TABLE ExternalEventDeliveries; ALTER TABLE ExternalEventDeliveries_new RENAME TO ExternalEventDeliveries;
CREATE INDEX IX_CoreEventDeliveries_AgentInstanceId_ProfileId_Status ON CoreEventDeliveries(AgentInstanceId,ProfileId,Status);
CREATE INDEX IX_ExternalEventDeliveries_Status_EventId_TriggerId ON ExternalEventDeliveries(Status,EventId,TriggerId);
ALTER TABLE CoreEventBuckets ADD COLUMN TriggerId TEXT NOT NULL DEFAULT '';
UPDATE CoreEventBuckets SET TriggerId=AutomationId, PayloadJson=json_set(PayloadJson,'$.Subscription.TriggerId',AutomationId);
ALTER TABLE TriggerOccurrences ADD COLUMN TriggerId TEXT NULL;
UPDATE TriggerOccurrences SET TriggerId=AutomationId WHERE AutomationId IS NOT NULL AND SourceKind<>2;
-- Add provenance metadata from immutable occurrence evidence, never from today's edited parent.
WITH HistoricalProvenance AS (SELECT OccurrenceId, json_set(EvidenceJson,'$.triggerId',TriggerId,'$.source',
  CASE SourceKind
    WHEN 1 THEN json_object('kind','webhook','eventId',COALESCE(json_extract(EvidenceJson,'$.triggerContext.eventId'),substr(json_extract(EvidenceJson,'$.triggerSummary'),7)))
    WHEN 3 THEN json_object('kind','builtin','key',substr(json_extract(EvidenceJson,'$.triggerSummary'),instr(json_extract(EvidenceJson,'$.triggerSummary'),' · ')+3))
    ELSE NULL END) AS UpgradedEvidence FROM TriggerOccurrences
WHERE TriggerId IS NOT NULL AND json_valid(EvidenceJson) AND json_type(EvidenceJson,'$.triggerSummary')='text')
UPDATE TriggerOccurrences SET EvidenceJson=(SELECT UpgradedEvidence FROM HistoricalProvenance WHERE HistoricalProvenance.OccurrenceId=TriggerOccurrences.OccurrenceId)
WHERE OccurrenceId IN (SELECT OccurrenceId FROM HistoricalProvenance WHERE length(CAST(UpgradedEvidence AS BLOB))<=8192);
-- Full-budget archives retain their original bytes; the owned history projection derives source from that immutable evidence.
""");
    }
    protected override void Down(MigrationBuilder migrationBuilder) => throw new NotSupportedException(
        "Child-trigger schema cannot be downgraded without losing subscriptions. Restore the pre-upgrade backup with the previous application.");
}
