using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgentCore.Infrastructure.Persistence.Migrations;

// Freeze the table and every affected existing guard. EF's automatic SQLite CHECK
// rebuild would discard triggers. No helper can change this migration's history.
public partial class TenantContinuityRead : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => Rebuild(migrationBuilder, Upgrade);
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            CREATE TEMP TABLE ContinuityReadDowngradeGuard (Rows INTEGER CHECK(Rows=0));
            INSERT INTO ContinuityReadDowngradeGuard SELECT count(*) FROM TenantApprovalRequests WHERE json_type(PayloadJson, '$.Operation.ContinuityRead') IS NOT NULL;
            DROP TABLE ContinuityReadDowngradeGuard;
            """);
        Rebuild(migrationBuilder, Downgrade);
    }
    private static void Rebuild(MigrationBuilder migrationBuilder, string sql)
    {
        migrationBuilder.Sql("PRAGMA foreign_keys = 0;", suppressTransaction: true);
        migrationBuilder.Sql(sql);
        migrationBuilder.Sql("PRAGMA foreign_keys = 1;", suppressTransaction: true);
    }
    private const string Upgrade = """
        DROP TRIGGER IF EXISTS "TR_TenantApprovals_AccountAction_Insert";
        DROP TRIGGER IF EXISTS "TR_TenantApprovals_AccountAction_Update";
        DROP TRIGGER IF EXISTS "TR_TenantApprovals_AccountOperationRead_Insert";
        DROP TRIGGER IF EXISTS "TR_TenantApprovals_AccountOperationRead_Update";
        DROP TRIGGER IF EXISTS "TR_TenantApprovals_AccountRead_Insert";
        DROP TRIGGER IF EXISTS "TR_TenantApprovals_AccountRead_Update";
        DROP TRIGGER IF EXISTS "TR_TenantApprovals_AutomationAdmission_INSERT";
        DROP TRIGGER IF EXISTS "TR_TenantApprovals_AutomationAdmission_Immutable";
        DROP TRIGGER IF EXISTS "TR_TenantApprovals_AutomationAdmission_UPDATE";
        DROP TRIGGER IF EXISTS "TR_TenantApprovals_BackgroundCreation_Update";
        DROP TRIGGER IF EXISTS "TR_TenantApprovals_BackgroundReport_Insert";
        DROP TRIGGER IF EXISTS "TR_TenantApprovals_BackgroundReport_Update";
        DROP TRIGGER IF EXISTS "TR_TenantApprovals_BrowserArtifacts_Insert";
        DROP TRIGGER IF EXISTS "TR_TenantApprovals_BrowserArtifacts_Update";
        DROP TRIGGER IF EXISTS "TR_TenantApprovals_BrowserFiles_Insert";
        DROP TRIGGER IF EXISTS "TR_TenantApprovals_BrowserFiles_Update";
        DROP TRIGGER IF EXISTS "TR_TenantApprovals_CompletionRead_Insert";
        DROP TRIGGER IF EXISTS "TR_TenantApprovals_CompletionRead_Update";
        DROP TRIGGER IF EXISTS "TR_TenantApprovals_IndependentTask_Insert";
        DROP TRIGGER IF EXISTS "TR_TenantApprovals_IndependentTask_Update";
        DROP TRIGGER IF EXISTS "TR_TenantApprovals_Memory_Insert";
        DROP TRIGGER IF EXISTS "TR_TenantApprovals_Memory_Update";
        DROP TRIGGER IF EXISTS "TR_TenantApprovals_ReportSourceRead_Insert";
        DROP TRIGGER IF EXISTS "TR_TenantApprovals_ReportSourceRead_Update";
        DROP TRIGGER IF EXISTS "TR_TenantOccurrences_Source_Update";
        DROP TRIGGER IF EXISTS "TR_TenantReceipts_BrowserArtifacts_Insert";
        DROP TRIGGER IF EXISTS "TR_TenantApprovals_Continuity_Insert";
        DROP TRIGGER IF EXISTS "TR_TenantApprovals_Continuity_Update";
        CREATE TABLE "TenantApprovalRequests_ContinuityRebuild" (
            "RequestId" TEXT NOT NULL CONSTRAINT "PK_TenantApprovalRequests" PRIMARY KEY,
            "TenantId" TEXT NOT NULL,
            "RunId" TEXT NULL,
            "SessionId" TEXT NULL,
            "AutomationId" TEXT NULL,
            "InstanceId" TEXT NOT NULL,
            "RootActivationId" TEXT NOT NULL,
            "ExecutorPrincipalId" TEXT NOT NULL,
            "OriginalInitiatorPrincipalId" TEXT NULL,
            "OperationId" TEXT NOT NULL,
            "Fingerprint" TEXT NOT NULL,
            "Permission" TEXT NOT NULL,
            "ResourceType" TEXT NOT NULL,
            "ResourceId" TEXT NOT NULL,
            "CredentialId" TEXT NULL,
            "SandboxProfileId" TEXT NULL,
            "SandboxProfileRevision" INTEGER NULL,
            "Kind" INTEGER NOT NULL,
            "Status" INTEGER NOT NULL,
            "Revision" INTEGER NOT NULL,
            "CreatedAt" INTEGER NOT NULL,
            "ExpiresAt" INTEGER NOT NULL,
            "PayloadJson" TEXT NOT NULL,
            CONSTRAINT "AK_TenantApprovalRequests_RequestId_RootActivationId_InstanceId" UNIQUE ("RequestId", "RootActivationId", "InstanceId"),
            CONSTRAINT "AK_TenantApprovalRequests_TenantId_RequestId" UNIQUE ("TenantId", "RequestId"),
            CONSTRAINT "AK_TenantApprovalRequests_TenantId_RequestId_ExecutorPrincipalId_OperationId_Fingerprint" UNIQUE ("TenantId", "RequestId", "ExecutorPrincipalId", "OperationId", "Fingerprint"),
            CONSTRAINT "CK_TenantApprovalRequest_Values" CHECK ((((((RunId IS NOT NULL AND SessionId IS NOT NULL AND AutomationId IS NULL AND json_extract(PayloadJson, '$.Operation.AutomationAdmission') IS NULL AND (((((((((((((((((((((((((((((((((Kind IN (0,1) AND Status IN (0,1,2,3,4) AND Revision >= 1 AND ExpiresAt > CreatedAt AND length(Fingerprint) = 64 AND ((Permission = 'agents.update' AND ResourceType = 'agent' AND ResourceId = InstanceId AND CredentialId IS NULL AND SandboxProfileId IS NULL AND SandboxProfileRevision IS NULL AND length(PayloadJson) <= 131072) OR (Permission = 'credentials.use' AND ResourceType = 'credential' AND CredentialId IS NOT NULL AND ResourceId = CredentialId AND SandboxProfileId IS NULL AND SandboxProfileRevision IS NULL AND length(PayloadJson) <= 131072) OR (Permission = 'sandbox.execute' AND Kind = 0 AND ResourceType = 'agent' AND ResourceId = InstanceId AND CredentialId IS NULL AND SandboxProfileId IS NOT NULL AND SandboxProfileRevision IS NOT NULL AND SandboxProfileRevision > 0 AND json_type(PayloadJson, '$.Operation.SandboxCommand.ProfileId') IS 'text' AND json_extract(PayloadJson, '$.Operation.SandboxCommand.ProfileId') IS SandboxProfileId AND json_extract(PayloadJson, '$.Operation.SandboxCommand.ProfileRevision') IS SandboxProfileRevision AND length(PayloadJson) <= 1048576) OR (Permission = 'sandbox.manage' AND Kind = 0 AND ResourceType = 'agent' AND ResourceId = InstanceId AND CredentialId IS NULL AND SandboxProfileId IS NOT NULL AND SandboxProfileRevision IS NOT NULL AND SandboxProfileRevision > 0 AND json_type(PayloadJson, '$.Operation.SandboxControl.ProfileId') IS 'text' AND json_extract(PayloadJson, '$.Operation.SandboxControl.ProfileId') IS SandboxProfileId AND json_extract(PayloadJson, '$.Operation.SandboxControl.ProfileRevision') IS SandboxProfileRevision AND length(PayloadJson) <= 131072) OR (Permission = 'sandbox.read' AND Kind = 0 AND ResourceType = 'agent' AND ResourceId = InstanceId AND CredentialId IS NULL AND SandboxProfileId IS NULL AND SandboxProfileRevision IS NULL AND json_type(PayloadJson, '$.Operation.SandboxRead') IS 'object' AND json_extract(PayloadJson, '$.Operation.SandboxRead.SessionId') IS SessionId AND json_extract(PayloadJson, '$.Operation.SandboxRead.Kind') IN (0,1,2) AND json_type(PayloadJson, '$.Operation.SandboxRead.ProfileReferencesJson') IS 'text' AND length(json_extract(PayloadJson, '$.Operation.SandboxRead.ProfileReferencesJson')) <= 4096 AND json_valid(json_extract(PayloadJson, '$.Operation.SandboxRead.ProfileReferencesJson')) AND json_type(json_extract(PayloadJson, '$.Operation.SandboxRead.ProfileReferencesJson')) IS 'array' AND json_array_length(json_extract(PayloadJson, '$.Operation.SandboxRead.ProfileReferencesJson')) <= 18 AND length(PayloadJson) <= 131072) OR (Permission IN ('browser.read','browser.use') AND Kind = 0 AND ResourceType = 'agent' AND ResourceId = InstanceId AND CredentialId IS NULL AND SandboxProfileId IS NULL AND SandboxProfileRevision IS NULL AND json_type(PayloadJson, '$.Operation.BrowserCommand') IS 'object' AND json_extract(PayloadJson, '$.Operation.Persona') IS NULL AND json_extract(PayloadJson, '$.Operation.Preferences') IS NULL AND json_extract(PayloadJson, '$.Operation.CredentialCheck') IS NULL AND json_extract(PayloadJson, '$.Operation.SandboxCommand') IS NULL AND json_extract(PayloadJson, '$.Operation.SandboxControl') IS NULL AND json_extract(PayloadJson, '$.Operation.SandboxRead') IS NULL AND json_extract(PayloadJson, '$.Operation.BrowserCommand.SessionId') IS SessionId AND json_type(PayloadJson, '$.Operation.BrowserCommand.ToolName') IS 'text' AND json_type(PayloadJson, '$.Operation.BrowserCommand.ReadOnly') IS CASE WHEN Permission = 'browser.read' THEN 'true' ELSE 'false' END AND json_type(PayloadJson, '$.Operation.BrowserCommand.ArgumentsJson') IS 'text' AND json_valid(json_extract(PayloadJson, '$.Operation.BrowserCommand.ArgumentsJson')) AND length(json_extract(PayloadJson, '$.Operation.BrowserCommand.ArgumentsJson')) <= 16384 AND json_type(PayloadJson, '$.Operation.BrowserCommand.TargetJson') IS 'text' AND json_valid(json_extract(PayloadJson, '$.Operation.BrowserCommand.TargetJson')) AND length(json_extract(PayloadJson, '$.Operation.BrowserCommand.TargetJson')) <= 4096 AND json_type(PayloadJson, '$.Operation.BrowserCommand.CommandHash') IS 'text' AND length(json_extract(PayloadJson, '$.Operation.BrowserCommand.CommandHash')) = 64 AND length(PayloadJson) <= 262144) OR (Kind = 0 AND SandboxProfileId IS NULL AND SandboxProfileRevision IS NULL AND json_type(PayloadJson, '$.Operation.CredentialRead') IS 'object' AND json_extract(PayloadJson, '$.Operation.CredentialRead.SessionId') IS SessionId AND json_type(PayloadJson, '$.Operation.CredentialRead.ArgumentsJson') IS 'text' AND length(json_extract(PayloadJson, '$.Operation.CredentialRead.ArgumentsJson')) <= 4096 AND json_valid(json_extract(PayloadJson, '$.Operation.CredentialRead.ArgumentsJson')) AND json_type(PayloadJson, '$.Operation.CredentialRead.ReferencesJson') IS 'text' AND length(json_extract(PayloadJson, '$.Operation.CredentialRead.ReferencesJson')) <= 65536 AND json_valid(json_extract(PayloadJson, '$.Operation.CredentialRead.ReferencesJson')) AND json_type(json_extract(PayloadJson, '$.Operation.CredentialRead.ReferencesJson')) IS 'array' AND json_array_length(json_extract(PayloadJson, '$.Operation.CredentialRead.ReferencesJson')) <= 101 AND ((Permission = 'credentials.read' AND ResourceType = 'credential' AND CredentialId IS NOT NULL AND ResourceId = CredentialId AND json_extract(PayloadJson, '$.Operation.CredentialRead.CredentialId') IS CredentialId AND json_extract(json_extract(PayloadJson, '$.Operation.CredentialRead.ReferencesJson'), '$[0].CredentialId') IS CredentialId) OR (Permission = 'sessions.read' AND ResourceType = 'session' AND ResourceId = SessionId AND CredentialId IS NULL AND json_extract(PayloadJson, '$.Operation.CredentialRead.CredentialId') IS NULL AND json_array_length(json_extract(PayloadJson, '$.Operation.CredentialRead.ReferencesJson')) = 0)) AND json_extract(PayloadJson, '$.Operation.Persona') IS NULL AND json_extract(PayloadJson, '$.Operation.Preferences') IS NULL AND json_extract(PayloadJson, '$.Operation.CredentialCheck') IS NULL AND json_extract(PayloadJson, '$.Operation.SandboxCommand') IS NULL AND json_extract(PayloadJson, '$.Operation.SandboxControl') IS NULL AND json_extract(PayloadJson, '$.Operation.SandboxRead') IS NULL AND json_extract(PayloadJson, '$.Operation.BrowserCommand') IS NULL AND length(PayloadJson) <= 262144))) AND json_extract(PayloadJson, '$.Operation.ArtifactPublication') IS NULL) OR (Kind IN (0,1) AND Status IN (0,1,2,3,4) AND Revision >= 1 AND ExpiresAt > CreatedAt AND length(Fingerprint) = 64 AND Permission = 'artifacts.create' AND ResourceType = 'session' AND ResourceId = SessionId AND CredentialId IS NULL AND SandboxProfileId IS NULL AND SandboxProfileRevision IS NULL AND json_type(PayloadJson, '$.Operation.ArtifactPublication') IS 'object' AND json_extract(PayloadJson, '$.Operation.ArtifactPublication.SessionId') IS SessionId AND json_type(PayloadJson, '$.Operation.ArtifactPublication.ArgumentsJson') IS 'text' AND length(CAST(json_extract(PayloadJson, '$.Operation.ArtifactPublication.ArgumentsJson') AS BLOB)) <= 65536 AND json_valid(json_extract(PayloadJson, '$.Operation.ArtifactPublication.ArgumentsJson')) AND json_type(json_extract(PayloadJson, '$.Operation.ArtifactPublication.ArgumentsJson')) IS 'object' AND json_type(json_extract(PayloadJson, '$.Operation.ArtifactPublication.ArgumentsJson'), '$.displayName') IS 'text' AND json_type(json_extract(PayloadJson, '$.Operation.ArtifactPublication.ArgumentsJson'), '$.contentType') IS 'text' AND json_type(json_extract(PayloadJson, '$.Operation.ArtifactPublication.ArgumentsJson'), '$.content') IS 'text' AND length(CAST(json_extract(json_extract(PayloadJson, '$.Operation.ArtifactPublication.ArgumentsJson'), '$.content') AS BLOB)) <= 32768 AND json_extract(PayloadJson, '$.Operation.Persona') IS NULL AND json_extract(PayloadJson, '$.Operation.Preferences') IS NULL AND json_extract(PayloadJson, '$.Operation.CredentialCheck') IS NULL AND json_extract(PayloadJson, '$.Operation.SandboxCommand') IS NULL AND json_extract(PayloadJson, '$.Operation.SandboxControl') IS NULL AND json_extract(PayloadJson, '$.Operation.SandboxRead') IS NULL AND json_extract(PayloadJson, '$.Operation.BrowserCommand') IS NULL AND json_extract(PayloadJson, '$.Operation.CredentialRead') IS NULL AND length(CAST(PayloadJson AS BLOB)) <= 524288)) AND json_extract(PayloadJson, '$.Operation.ArtifactVerification') IS NULL) OR (Kind = 0 AND Status IN (0,1,2,3,4) AND Revision >= 1 AND ExpiresAt > CreatedAt AND length(Fingerprint) = 64 AND Permission = 'artifacts.read' AND ResourceType = 'artifact' AND CredentialId IS NULL AND SandboxProfileId IS NULL AND SandboxProfileRevision IS NULL AND json_type(PayloadJson, '$.Operation.ArtifactVerification') IS 'object' AND json_extract(PayloadJson, '$.Operation.ArtifactVerification.SessionId') IS SessionId AND json_extract(PayloadJson, '$.Operation.ArtifactVerification.ArtifactId') IS ResourceId AND json_type(PayloadJson, '$.Operation.ArtifactVerification.DisplayName') IS 'text' AND length(json_extract(PayloadJson, '$.Operation.ArtifactVerification.DisplayName')) BETWEEN 1 AND 200 AND json_type(PayloadJson, '$.Operation.ArtifactVerification.ContentType') IS 'text' AND length(json_extract(PayloadJson, '$.Operation.ArtifactVerification.ContentType')) BETWEEN 1 AND 200 AND json_type(PayloadJson, '$.Operation.ArtifactVerification.ByteSize') IS 'integer' AND json_extract(PayloadJson, '$.Operation.ArtifactVerification.ByteSize') >= 0 AND json_type(PayloadJson, '$.Operation.ArtifactVerification.Sha256Hex') IS 'text' AND length(json_extract(PayloadJson, '$.Operation.ArtifactVerification.Sha256Hex')) = 64 AND json_extract(PayloadJson, '$.Operation.ArtifactVerification.Sha256Hex') NOT GLOB '*[^0-9a-f]*' AND json_extract(PayloadJson, '$.Operation.Persona') IS NULL AND json_extract(PayloadJson, '$.Operation.Preferences') IS NULL AND json_extract(PayloadJson, '$.Operation.CredentialCheck') IS NULL AND json_extract(PayloadJson, '$.Operation.SandboxCommand') IS NULL AND json_extract(PayloadJson, '$.Operation.SandboxControl') IS NULL AND json_extract(PayloadJson, '$.Operation.SandboxRead') IS NULL AND json_extract(PayloadJson, '$.Operation.BrowserCommand') IS NULL AND json_extract(PayloadJson, '$.Operation.CredentialRead') IS NULL AND json_extract(PayloadJson, '$.Operation.ArtifactPublication') IS NULL AND length(CAST(PayloadJson AS BLOB)) <= 131072)) AND json_extract(PayloadJson, '$.Operation.WorkspaceRead') IS NULL) OR (Kind = 0 AND Status IN (0,1,2,3,4) AND Revision >= 1 AND ExpiresAt > CreatedAt AND length(Fingerprint) = 64 AND ((Permission = 'workspaces.read' AND ResourceType = 'workspace' AND json_extract(PayloadJson, '$.Operation.WorkspaceRead.ResourceKey') IS NULL AND json_extract(PayloadJson, '$.Operation.WorkspaceRead.ResourceRevision') IS NULL) OR (Permission = 'resources.read' AND ResourceType = 'resource'
        AND json_type(PayloadJson, '$.Operation.WorkspaceRead.ResourceKey') IS 'text'
        AND ((length(json_extract(PayloadJson, '$.Operation.WorkspaceRead.ResourceKey')) = 45 AND substr(json_extract(PayloadJson, '$.Operation.WorkspaceRead.ResourceKey'),1,9) IS 'instance:')
            OR (length(json_extract(PayloadJson, '$.Operation.WorkspaceRead.ResourceKey')) = 47 AND substr(json_extract(PayloadJson, '$.Operation.WorkspaceRead.ResourceKey'),1,11) IS 'definition:'))
        AND substr(json_extract(PayloadJson, '$.Operation.WorkspaceRead.ResourceKey'),-36) NOT GLOB '*[^0-9a-f-]*'
        AND substr(json_extract(PayloadJson, '$.Operation.WorkspaceRead.ResourceKey'),-28,1) IS '-'
        AND substr(json_extract(PayloadJson, '$.Operation.WorkspaceRead.ResourceKey'),-23,1) IS '-'
        AND substr(json_extract(PayloadJson, '$.Operation.WorkspaceRead.ResourceKey'),-18,1) IS '-'
        AND substr(json_extract(PayloadJson, '$.Operation.WorkspaceRead.ResourceKey'),-13,1) IS '-'
        AND json_type(PayloadJson, '$.Operation.WorkspaceRead.ResourceRevision') IS 'integer'
        AND json_extract(PayloadJson, '$.Operation.WorkspaceRead.ResourceRevision') >= 0
        AND json_type(PayloadJson, '$.Operation.WorkspaceRead.Working') IS 'false'
        AND json_extract(PayloadJson, '$.Operation.WorkspaceRead.ToolName') IN ('workspace.read','office.document.inspect','office.spreadsheet.inspect')
        AND (substr(json_extract(json_extract(PayloadJson, '$.Operation.WorkspaceRead.ArgumentsJson'), '$.path'),1,17) IS '/agent/resources/'
            OR substr(json_extract(json_extract(PayloadJson, '$.Operation.WorkspaceRead.ArgumentsJson'), '$.path'),1,26) IS '/agent/instance/resources/'))) AND CredentialId IS NULL AND SandboxProfileId IS NULL AND SandboxProfileRevision IS NULL AND json_type(PayloadJson, '$.Operation.WorkspaceRead') IS 'object' AND json_extract(PayloadJson, '$.Operation.WorkspaceRead.SessionId') IS SessionId AND (json_type(PayloadJson, '$.Operation.WorkspaceRead.Working') IS 'true' OR json_type(PayloadJson, '$.Operation.WorkspaceRead.Working') IS 'false') AND ((Permission = 'workspaces.read' AND ResourceId IS CASE WHEN json_extract(PayloadJson, '$.Operation.WorkspaceRead.Working') = 1 THEN 'working:' || SessionId ELSE 'home:' || InstanceId END) OR (Permission = 'resources.read' AND ResourceId IS InstanceId || ':' || json_extract(PayloadJson, '$.Operation.WorkspaceRead.ResourceKey'))) AND json_type(PayloadJson, '$.Operation.WorkspaceRead.ToolName') IS 'text' AND json_extract(PayloadJson, '$.Operation.WorkspaceRead.ToolName') IN ('workspace.read','workspace.list','workspace.search','office.document.inspect','office.spreadsheet.inspect') AND json_type(PayloadJson, '$.Operation.WorkspaceRead.ArgumentsJson') IS 'text' AND length(CAST(json_extract(PayloadJson, '$.Operation.WorkspaceRead.ArgumentsJson') AS BLOB)) <= 16384 AND json_valid(json_extract(PayloadJson, '$.Operation.WorkspaceRead.ArgumentsJson')) AND json_type(json_extract(PayloadJson, '$.Operation.WorkspaceRead.ArgumentsJson')) IS 'object' AND json_type(json_extract(PayloadJson, '$.Operation.WorkspaceRead.ArgumentsJson'), '$.path') IS 'text' AND length(json_extract(json_extract(PayloadJson, '$.Operation.WorkspaceRead.ArgumentsJson'), '$.path')) BETWEEN 1 AND 4095 AND json_type(PayloadJson, '$.Operation.WorkspaceRead.ObservationHash') IS 'text' AND length(json_extract(PayloadJson, '$.Operation.WorkspaceRead.ObservationHash')) = 64 AND json_extract(PayloadJson, '$.Operation.WorkspaceRead.ObservationHash') NOT GLOB '*[^0-9a-f]*' AND length(CAST(PayloadJson AS BLOB)) <= 131072 AND json_extract(PayloadJson, '$.Operation.Persona') IS NULL AND json_extract(PayloadJson, '$.Operation.Preferences') IS NULL AND json_extract(PayloadJson, '$.Operation.CredentialCheck') IS NULL AND json_extract(PayloadJson, '$.Operation.SandboxCommand') IS NULL AND json_extract(PayloadJson, '$.Operation.SandboxControl') IS NULL AND json_extract(PayloadJson, '$.Operation.SandboxRead') IS NULL AND json_extract(PayloadJson, '$.Operation.BrowserCommand') IS NULL AND json_extract(PayloadJson, '$.Operation.CredentialRead') IS NULL AND json_extract(PayloadJson, '$.Operation.ArtifactPublication') IS NULL AND json_extract(PayloadJson, '$.Operation.ArtifactVerification') IS NULL)) AND json_extract(PayloadJson, '$.Operation.WorkspaceMutation') IS NULL) OR COALESCE((Kind IN (0,1) AND Status IN (0,1,2,3,4) AND Revision >= 1 AND ExpiresAt > CreatedAt AND length(Fingerprint) = 64
        AND Permission IN ('workspaces.write','workspaces.delete') AND ResourceType = 'workspace'
        AND CredentialId IS NULL AND SandboxProfileId IS NULL AND SandboxProfileRevision IS NULL
        AND json_type(PayloadJson, '$.Operation.WorkspaceMutation') IS 'object'
        AND json_extract(PayloadJson, '$.Operation.WorkspaceMutation.SessionId') IS SessionId
        AND json_type(PayloadJson, '$.Operation.WorkspaceMutation.Working') IN ('true','false')
        AND ResourceId IS CASE WHEN json_extract(PayloadJson, '$.Operation.WorkspaceMutation.Working') = 1 THEN 'working:' || SessionId ELSE 'home:' || InstanceId END
        AND json_extract(PayloadJson, '$.Operation.WorkspaceMutation.Permission') IS Permission
        AND json_extract(PayloadJson, '$.Operation.WorkspaceMutation.ToolName') IN ('workspace.write','workspace.patch','workspace.mkdir','workspace.copy','workspace.move','workspace.delete','workspace.batch','office.document.create','office.document.edit','office.spreadsheet.create','office.spreadsheet.edit')
        AND (json_extract(PayloadJson, '$.Operation.WorkspaceMutation.ToolName') = 'workspace.batch'
            OR Permission IS CASE WHEN json_extract(PayloadJson, '$.Operation.WorkspaceMutation.ToolName') IN ('workspace.move','workspace.delete') THEN 'workspaces.delete' ELSE 'workspaces.write' END)
        AND json_type(PayloadJson, '$.Operation.WorkspaceMutation.ArgumentsJson') IS 'text'
        AND length(CAST(json_extract(PayloadJson, '$.Operation.WorkspaceMutation.ArgumentsJson') AS BLOB)) BETWEEN 2 AND 65536
        AND json_valid(json_extract(PayloadJson, '$.Operation.WorkspaceMutation.ArgumentsJson'))
        AND json_type(json_extract(PayloadJson, '$.Operation.WorkspaceMutation.ArgumentsJson')) IS 'object'
        AND (json_extract(PayloadJson, '$.Operation.WorkspaceMutation.ToolName') = 'workspace.batch' OR (
            CASE json_extract(PayloadJson, '$.Operation.WorkspaceMutation.ToolName')
                WHEN 'workspace.copy' THEN json_extract(json_extract(PayloadJson, '$.Operation.WorkspaceMutation.ArgumentsJson'), '$.destination')
                WHEN 'workspace.move' THEN json_extract(json_extract(PayloadJson, '$.Operation.WorkspaceMutation.ArgumentsJson'), '$.source')
                ELSE json_extract(json_extract(PayloadJson, '$.Operation.WorkspaceMutation.ArgumentsJson'), '$.path') END
            LIKE CASE WHEN json_extract(PayloadJson, '$.Operation.WorkspaceMutation.Working') = 1 THEN '/working/%' ELSE '/home/%' END))
        AND json_type(PayloadJson, '$.Operation.WorkspaceMutation.ObservationHash') IS 'text'
        AND length(json_extract(PayloadJson, '$.Operation.WorkspaceMutation.ObservationHash')) = 64
        AND json_extract(PayloadJson, '$.Operation.WorkspaceMutation.ObservationHash') NOT GLOB '*[^0-9a-f]*'
        AND json_type(PayloadJson, '$.Operation.WorkspaceMutation.TargetsJson') IS 'text'
        AND length(json_extract(PayloadJson, '$.Operation.WorkspaceMutation.TargetsJson')) BETWEEN 2 AND 4096
        AND json_valid(json_extract(PayloadJson, '$.Operation.WorkspaceMutation.TargetsJson'))
        AND json_type(json_extract(PayloadJson, '$.Operation.WorkspaceMutation.TargetsJson')) IS 'array'
        AND json_array_length(json_extract(PayloadJson, '$.Operation.WorkspaceMutation.TargetsJson')) BETWEEN 1 AND 6
        AND json_extract(PayloadJson, '$.Operation.Persona') IS NULL
        AND json_extract(PayloadJson, '$.Operation.Preferences') IS NULL
        AND json_extract(PayloadJson, '$.Operation.CredentialCheck') IS NULL
        AND json_extract(PayloadJson, '$.Operation.SandboxCommand') IS NULL
        AND json_extract(PayloadJson, '$.Operation.SandboxControl') IS NULL
        AND json_extract(PayloadJson, '$.Operation.SandboxRead') IS NULL
        AND json_extract(PayloadJson, '$.Operation.BrowserCommand') IS NULL
        AND json_extract(PayloadJson, '$.Operation.CredentialRead') IS NULL
        AND json_extract(PayloadJson, '$.Operation.ArtifactPublication') IS NULL
        AND json_extract(PayloadJson, '$.Operation.ArtifactVerification') IS NULL
        AND json_extract(PayloadJson, '$.Operation.WorkspaceRead') IS NULL
        AND length(CAST(PayloadJson AS BLOB)) <= 524288), 0)) AND json_extract(PayloadJson, '$.Operation.ArtifactPublication.Source') IS NULL) OR COALESCE((Kind IN (0,1) AND Status IN (0,1,2,3,4) AND Revision >= 1 AND ExpiresAt > CreatedAt AND length(Fingerprint) = 64
        AND Permission = 'artifacts.create' AND ResourceType = 'session' AND ResourceId IS SessionId
        AND CredentialId IS NULL AND SandboxProfileId IS NULL AND SandboxProfileRevision IS NULL
        AND json_type(PayloadJson, '$.Operation.ArtifactPublication') IS 'object'
        AND json_extract(PayloadJson, '$.Operation.ArtifactPublication.SessionId') IS SessionId
        AND json_type(PayloadJson, '$.Operation.ArtifactPublication.Source') IS 'object'
        AND json_type(PayloadJson, '$.Operation.ArtifactPublication.Source.Working') IN ('true','false')
        AND json_type(PayloadJson, '$.Operation.ArtifactPublication.Source.Path') IS 'text'
        AND length(CAST(json_extract(PayloadJson, '$.Operation.ArtifactPublication.Source.Path') AS BLOB)) BETWEEN 7 AND 4095
        AND ((json_extract(PayloadJson, '$.Operation.ArtifactPublication.Source.ResourceKey') IS NULL
            AND json_extract(PayloadJson, '$.Operation.ArtifactPublication.Source.ResourceRevision') IS NULL
            AND CASE WHEN json_extract(PayloadJson, '$.Operation.ArtifactPublication.Source.Working') = 1
            THEN substr(json_extract(PayloadJson, '$.Operation.ArtifactPublication.Source.Path'),1,9) IS '/working/'
            ELSE substr(json_extract(PayloadJson, '$.Operation.ArtifactPublication.Source.Path'),1,6) IS '/home/' END)
            OR (json_type(PayloadJson, '$.Operation.ArtifactPublication.Source.Working') IS 'false'
                AND json_type(PayloadJson, '$.Operation.ArtifactPublication.Source.ResourceKey') IS 'text'
                AND ((length(json_extract(PayloadJson, '$.Operation.ArtifactPublication.Source.ResourceKey')) = 45 AND substr(json_extract(PayloadJson, '$.Operation.ArtifactPublication.Source.ResourceKey'),1,9) IS 'instance:')
                    OR (length(json_extract(PayloadJson, '$.Operation.ArtifactPublication.Source.ResourceKey')) = 47 AND substr(json_extract(PayloadJson, '$.Operation.ArtifactPublication.Source.ResourceKey'),1,11) IS 'definition:'))
                AND json_type(PayloadJson, '$.Operation.ArtifactPublication.Source.ResourceRevision') IS 'integer'
                AND json_extract(PayloadJson, '$.Operation.ArtifactPublication.Source.ResourceRevision') >= 0
                AND (substr(json_extract(PayloadJson, '$.Operation.ArtifactPublication.Source.Path'),1,17) IS '/agent/resources/'
                    OR substr(json_extract(PayloadJson, '$.Operation.ArtifactPublication.Source.Path'),1,26) IS '/agent/instance/resources/')))
        AND json_type(PayloadJson, '$.Operation.ArtifactPublication.Source.ContentType') IS 'text'
        AND length(json_extract(PayloadJson, '$.Operation.ArtifactPublication.Source.ContentType')) BETWEEN 1 AND 200
        AND json_type(PayloadJson, '$.Operation.ArtifactPublication.Source.ByteSize') IS 'integer'
        AND json_extract(PayloadJson, '$.Operation.ArtifactPublication.Source.ByteSize') BETWEEN 0 AND 52428800
        AND json_type(PayloadJson, '$.Operation.ArtifactPublication.Source.Sha256Hex') IS 'text'
        AND length(json_extract(PayloadJson, '$.Operation.ArtifactPublication.Source.Sha256Hex')) = 64
        AND json_extract(PayloadJson, '$.Operation.ArtifactPublication.Source.Sha256Hex') NOT GLOB '*[^0-9a-f]*'
        AND json_type(PayloadJson, '$.Operation.ArtifactPublication.Source.ObservationHash') IS 'text'
        AND length(json_extract(PayloadJson, '$.Operation.ArtifactPublication.Source.ObservationHash')) = 64
        AND json_extract(PayloadJson, '$.Operation.ArtifactPublication.Source.ObservationHash') NOT GLOB '*[^0-9a-f]*'
        AND json_type(PayloadJson, '$.Operation.ArtifactPublication.ArgumentsJson') IS 'text'
        AND length(CAST(json_extract(PayloadJson, '$.Operation.ArtifactPublication.ArgumentsJson') AS BLOB)) BETWEEN 2 AND 65536
        AND json_valid(json_extract(PayloadJson, '$.Operation.ArtifactPublication.ArgumentsJson'))
        AND json_type(json_extract(PayloadJson, '$.Operation.ArtifactPublication.ArgumentsJson')) IS 'object'
        AND json_type(json_extract(PayloadJson, '$.Operation.ArtifactPublication.ArgumentsJson'), '$.path') IS 'text'
        AND json_extract(json_extract(PayloadJson, '$.Operation.ArtifactPublication.ArgumentsJson'), '$.path') IS json_extract(PayloadJson, '$.Operation.ArtifactPublication.Source.Path')
        AND json_type(json_extract(PayloadJson, '$.Operation.ArtifactPublication.ArgumentsJson'), '$.displayName') IS 'text'
        AND length(json_extract(json_extract(PayloadJson, '$.Operation.ArtifactPublication.ArgumentsJson'), '$.displayName')) BETWEEN 1 AND 200
        AND (json_type(json_extract(PayloadJson, '$.Operation.ArtifactPublication.ArgumentsJson'), '$.contentType') IS NULL OR (
            json_type(json_extract(PayloadJson, '$.Operation.ArtifactPublication.ArgumentsJson'), '$.contentType') IS 'text'
            AND length(json_extract(json_extract(PayloadJson, '$.Operation.ArtifactPublication.ArgumentsJson'), '$.contentType')) BETWEEN 1 AND 200))
        AND json_type(json_extract(PayloadJson, '$.Operation.ArtifactPublication.ArgumentsJson'), '$.content') IS NULL
        AND json_extract(PayloadJson, '$.Operation.Persona') IS NULL
        AND json_extract(PayloadJson, '$.Operation.Preferences') IS NULL
        AND json_extract(PayloadJson, '$.Operation.CredentialCheck') IS NULL
        AND json_extract(PayloadJson, '$.Operation.SandboxCommand') IS NULL
        AND json_extract(PayloadJson, '$.Operation.SandboxControl') IS NULL
        AND json_extract(PayloadJson, '$.Operation.SandboxRead') IS NULL
        AND json_extract(PayloadJson, '$.Operation.BrowserCommand') IS NULL
        AND json_extract(PayloadJson, '$.Operation.CredentialRead') IS NULL
        AND json_extract(PayloadJson, '$.Operation.ArtifactVerification') IS NULL
        AND json_extract(PayloadJson, '$.Operation.WorkspaceRead') IS NULL
        AND json_extract(PayloadJson, '$.Operation.WorkspaceMutation') IS NULL
        AND length(CAST(PayloadJson AS BLOB)) <= 524288), 0)) AND json_extract(PayloadJson, '$.Operation.WorkspaceRead.ResourceMembersJson') IS NULL) OR COALESCE((Kind = 0 AND Status IN (0,1,2,3,4) AND Revision >= 1 AND ExpiresAt > CreatedAt AND length(Fingerprint) = 64
        AND Permission = 'sessions.read' AND ResourceType = 'session' AND ResourceId IS SessionId
        AND CredentialId IS NULL AND SandboxProfileId IS NULL AND SandboxProfileRevision IS NULL
        AND json_type(PayloadJson, '$.Operation.WorkspaceRead') IS 'object'
        AND json_extract(PayloadJson, '$.Operation.WorkspaceRead.SessionId') IS SessionId
        AND json_type(PayloadJson, '$.Operation.WorkspaceRead.Working') IS 'false'
        AND json_extract(PayloadJson, '$.Operation.WorkspaceRead.ResourceKey') IS NULL
        AND json_extract(PayloadJson, '$.Operation.WorkspaceRead.ResourceRevision') IS NULL
        AND json_extract(PayloadJson, '$.Operation.WorkspaceRead.ToolName') IN ('workspace.list','workspace.search')
        AND json_type(PayloadJson, '$.Operation.WorkspaceRead.ArgumentsJson') IS 'text'
        AND length(CAST(json_extract(PayloadJson, '$.Operation.WorkspaceRead.ArgumentsJson') AS BLOB)) <= 16384
        AND json_valid(json_extract(PayloadJson, '$.Operation.WorkspaceRead.ArgumentsJson'))
        AND json_type(json_extract(PayloadJson, '$.Operation.WorkspaceRead.ArgumentsJson'), '$.path') IS 'text'
        AND (json_extract(json_extract(PayloadJson, '$.Operation.WorkspaceRead.ArgumentsJson'), '$.path') IN ('/agent/resources','/agent/instance/resources')
            OR substr(json_extract(json_extract(PayloadJson, '$.Operation.WorkspaceRead.ArgumentsJson'), '$.path'),1,17) IS '/agent/resources/'
            OR substr(json_extract(json_extract(PayloadJson, '$.Operation.WorkspaceRead.ArgumentsJson'), '$.path'),1,26) IS '/agent/instance/resources/')
        AND json_type(PayloadJson, '$.Operation.WorkspaceRead.ResourceMembersJson') IS 'text'
        AND length(CAST(json_extract(PayloadJson, '$.Operation.WorkspaceRead.ResourceMembersJson') AS BLOB)) <= 65536
        AND json_valid(json_extract(PayloadJson, '$.Operation.WorkspaceRead.ResourceMembersJson'))
        AND json_type(json_extract(PayloadJson, '$.Operation.WorkspaceRead.ResourceMembersJson')) IS 'array'
        AND json_array_length(json_extract(PayloadJson, '$.Operation.WorkspaceRead.ResourceMembersJson')) <= 257
        AND json_type(PayloadJson, '$.Operation.WorkspaceRead.ObservationHash') IS 'text'
        AND length(json_extract(PayloadJson, '$.Operation.WorkspaceRead.ObservationHash')) = 64
        AND json_extract(PayloadJson, '$.Operation.WorkspaceRead.ObservationHash') NOT GLOB '*[^0-9a-f]*'
        AND json_extract(PayloadJson, '$.Operation.Persona') IS NULL
        AND json_extract(PayloadJson, '$.Operation.Preferences') IS NULL
        AND json_extract(PayloadJson, '$.Operation.CredentialCheck') IS NULL
        AND json_extract(PayloadJson, '$.Operation.SandboxCommand') IS NULL
        AND json_extract(PayloadJson, '$.Operation.SandboxControl') IS NULL
        AND json_extract(PayloadJson, '$.Operation.SandboxRead') IS NULL
        AND json_extract(PayloadJson, '$.Operation.BrowserCommand') IS NULL
        AND json_extract(PayloadJson, '$.Operation.CredentialRead') IS NULL
        AND json_extract(PayloadJson, '$.Operation.ArtifactPublication') IS NULL
        AND json_extract(PayloadJson, '$.Operation.ArtifactVerification') IS NULL
        AND json_extract(PayloadJson, '$.Operation.WorkspaceMutation') IS NULL
        AND length(CAST(PayloadJson AS BLOB)) <= 131072),0)) AND json_extract(PayloadJson, '$.Operation.WorkspaceRead.Knowledge') IS NULL) OR COALESCE((Kind = 0 AND Status IN (0,1,2,3,4) AND Revision >= 1 AND ExpiresAt > CreatedAt AND length(Fingerprint) = 64
        AND Permission = 'resources.read' AND ResourceType = 'resource'
        AND ResourceId IS InstanceId || ':' || json_extract(PayloadJson, '$.Operation.WorkspaceRead.ResourceKey')
        AND CredentialId IS NULL AND SandboxProfileId IS NULL AND SandboxProfileRevision IS NULL
        AND json_type(PayloadJson, '$.Operation.WorkspaceRead') IS 'object'
        AND json_extract(PayloadJson, '$.Operation.WorkspaceRead.SessionId') IS SessionId
        AND json_type(PayloadJson, '$.Operation.WorkspaceRead.Working') IS 'false'
        AND json_extract(PayloadJson, '$.Operation.WorkspaceRead.ToolName') IS 'knowledge.retrieve'
        AND json_type(PayloadJson, '$.Operation.WorkspaceRead.ResourceKey') IS 'text'
        AND ((length(json_extract(PayloadJson, '$.Operation.WorkspaceRead.ResourceKey')) = 45 AND substr(json_extract(PayloadJson, '$.Operation.WorkspaceRead.ResourceKey'),1,9) IS 'instance:')
            OR (length(json_extract(PayloadJson, '$.Operation.WorkspaceRead.ResourceKey')) = 47 AND substr(json_extract(PayloadJson, '$.Operation.WorkspaceRead.ResourceKey'),1,11) IS 'definition:'))
        AND json_type(PayloadJson, '$.Operation.WorkspaceRead.ResourceRevision') IS 'integer'
        AND json_extract(PayloadJson, '$.Operation.WorkspaceRead.ResourceRevision') >= 0
        AND json_extract(PayloadJson, '$.Operation.WorkspaceRead.ResourceMembersJson') IS NULL
        AND json_type(PayloadJson, '$.Operation.WorkspaceRead.ArgumentsJson') IS 'text'
        AND length(CAST(json_extract(PayloadJson, '$.Operation.WorkspaceRead.ArgumentsJson') AS BLOB)) <= 16384
        AND json_valid(json_extract(PayloadJson, '$.Operation.WorkspaceRead.ArgumentsJson'))
        AND json_type(json_extract(PayloadJson, '$.Operation.WorkspaceRead.ArgumentsJson'), '$.identity') IS 'text'
        AND json_type(PayloadJson, '$.Operation.WorkspaceRead.Knowledge') IS 'object'
        AND json_type(PayloadJson, '$.Operation.WorkspaceRead.Knowledge.Identity') IS 'text'
        AND length(json_extract(PayloadJson, '$.Operation.WorkspaceRead.Knowledge.Identity')) BETWEEN 1 AND 240
        AND json_extract(PayloadJson, '$.Operation.WorkspaceRead.Knowledge.Identity') IS json_extract(json_extract(PayloadJson, '$.Operation.WorkspaceRead.ArgumentsJson'), '$.identity')
        AND json_type(PayloadJson, '$.Operation.WorkspaceRead.Knowledge.Title') IS 'text'
        AND length(json_extract(PayloadJson, '$.Operation.WorkspaceRead.Knowledge.Title')) BETWEEN 1 AND 512
        AND json_type(PayloadJson, '$.Operation.WorkspaceRead.Knowledge.Citation') IS 'text'
        AND length(json_extract(PayloadJson, '$.Operation.WorkspaceRead.Knowledge.Citation')) BETWEEN 1 AND 1024
        AND json_type(PayloadJson, '$.Operation.WorkspaceRead.Knowledge.Path') IS 'text'
        AND length(CAST(json_extract(PayloadJson, '$.Operation.WorkspaceRead.Knowledge.Path') AS BLOB)) BETWEEN 1 AND 4095
        AND (substr(json_extract(PayloadJson, '$.Operation.WorkspaceRead.Knowledge.Path'),1,17) IS '/agent/resources/'
            OR substr(json_extract(PayloadJson, '$.Operation.WorkspaceRead.Knowledge.Path'),1,26) IS '/agent/instance/resources/')
        AND json_type(PayloadJson, '$.Operation.WorkspaceRead.ObservationHash') IS 'text'
        AND length(json_extract(PayloadJson, '$.Operation.WorkspaceRead.ObservationHash')) = 64
        AND json_extract(PayloadJson, '$.Operation.WorkspaceRead.ObservationHash') NOT GLOB '*[^0-9a-f]*'
        AND json_extract(PayloadJson, '$.Operation.Persona') IS NULL
        AND json_extract(PayloadJson, '$.Operation.Preferences') IS NULL
        AND json_extract(PayloadJson, '$.Operation.CredentialCheck') IS NULL
        AND json_extract(PayloadJson, '$.Operation.SandboxCommand') IS NULL
        AND json_extract(PayloadJson, '$.Operation.SandboxControl') IS NULL
        AND json_extract(PayloadJson, '$.Operation.SandboxRead') IS NULL
        AND json_extract(PayloadJson, '$.Operation.BrowserCommand') IS NULL
        AND json_extract(PayloadJson, '$.Operation.CredentialRead') IS NULL
        AND json_extract(PayloadJson, '$.Operation.ArtifactPublication') IS NULL
        AND json_extract(PayloadJson, '$.Operation.ArtifactVerification') IS NULL
        AND json_extract(PayloadJson, '$.Operation.WorkspaceMutation') IS NULL
        AND length(CAST(PayloadJson AS BLOB)) <= 131072),0)) AND json_extract(PayloadJson, '$.Operation.AttachmentRead') IS NULL) OR COALESCE((Kind = 0 AND Status IN (0,1,2,3,4) AND Revision >= 1 AND ExpiresAt > CreatedAt AND length(Fingerprint) = 64
        AND Permission = 'attachments.read' AND ResourceType = 'attachment'
        AND CredentialId IS NULL AND SandboxProfileId IS NULL AND SandboxProfileRevision IS NULL
        AND json_type(PayloadJson, '$.Operation.AttachmentRead') IS 'object'
        AND json_extract(PayloadJson, '$.Operation.AttachmentRead.SessionId') IS SessionId
        AND json_extract(PayloadJson, '$.Operation.AttachmentRead.AttachmentId') IS ResourceId
        AND length(ResourceId) = 36 AND ResourceId NOT GLOB '*[^0-9a-f-]*'
        AND substr(ResourceId,9,1) IS '-' AND substr(ResourceId,14,1) IS '-' AND substr(ResourceId,19,1) IS '-' AND substr(ResourceId,24,1) IS '-'
        AND json_type(PayloadJson, '$.Operation.AttachmentRead.EntryId') IS 'text'
        AND length(json_extract(PayloadJson, '$.Operation.AttachmentRead.EntryId')) = 36
        AND json_type(PayloadJson, '$.Operation.AttachmentRead.DisplayName') IS 'text'
        AND length(json_extract(PayloadJson, '$.Operation.AttachmentRead.DisplayName')) BETWEEN 1 AND 200
        AND json_type(PayloadJson, '$.Operation.AttachmentRead.ContentType') IS 'text'
        AND length(json_extract(PayloadJson, '$.Operation.AttachmentRead.ContentType')) BETWEEN 1 AND 200
        AND json_type(PayloadJson, '$.Operation.AttachmentRead.ByteSize') IS 'integer'
        AND json_extract(PayloadJson, '$.Operation.AttachmentRead.ByteSize') BETWEEN 0 AND 26214400
        AND json_type(PayloadJson, '$.Operation.AttachmentRead.Sha256Hex') IS 'text'
        AND length(json_extract(PayloadJson, '$.Operation.AttachmentRead.Sha256Hex')) = 64
        AND json_extract(PayloadJson, '$.Operation.AttachmentRead.Sha256Hex') NOT GLOB '*[^0-9a-f]*'
        AND (json_extract(PayloadJson, '$.Operation.AttachmentRead.ProcessorVersion') IS NULL OR
            (json_type(PayloadJson, '$.Operation.AttachmentRead.ProcessorVersion') IS 'text'
                AND length(json_extract(PayloadJson, '$.Operation.AttachmentRead.ProcessorVersion')) BETWEEN 1 AND 128))
        AND json_type(PayloadJson, '$.Operation.AttachmentRead.Vision') IN ('true','false')
        AND json_type(PayloadJson, '$.Operation.AttachmentRead.ArgumentsJson') IS 'text'
        AND length(CAST(json_extract(PayloadJson, '$.Operation.AttachmentRead.ArgumentsJson') AS BLOB)) <= 4096
        AND json_valid(json_extract(PayloadJson, '$.Operation.AttachmentRead.ArgumentsJson'))
        AND json_type(json_extract(PayloadJson, '$.Operation.AttachmentRead.ArgumentsJson')) IS 'object'
        AND json_extract(json_extract(PayloadJson, '$.Operation.AttachmentRead.ArgumentsJson'), '$.attachmentId') IS ResourceId
        AND json_extract(PayloadJson, '$.Operation.Persona') IS NULL
        AND json_extract(PayloadJson, '$.Operation.Preferences') IS NULL
        AND json_extract(PayloadJson, '$.Operation.CredentialCheck') IS NULL
        AND json_extract(PayloadJson, '$.Operation.SandboxCommand') IS NULL
        AND json_extract(PayloadJson, '$.Operation.SandboxControl') IS NULL
        AND json_extract(PayloadJson, '$.Operation.SandboxRead') IS NULL
        AND json_extract(PayloadJson, '$.Operation.BrowserCommand') IS NULL
        AND json_extract(PayloadJson, '$.Operation.CredentialRead') IS NULL
        AND json_extract(PayloadJson, '$.Operation.ArtifactPublication') IS NULL
        AND json_extract(PayloadJson, '$.Operation.ArtifactVerification') IS NULL
        AND json_extract(PayloadJson, '$.Operation.WorkspaceRead') IS NULL
        AND json_extract(PayloadJson, '$.Operation.WorkspaceMutation') IS NULL
        AND length(CAST(PayloadJson AS BLOB)) <= 131072),0)) AND json_extract(PayloadJson, '$.Operation.ArtifactPublication.Source.AttachmentId') IS NULL AND json_extract(PayloadJson, '$.Operation.ArtifactPublication.Source.EntryId') IS NULL AND json_extract(PayloadJson, '$.Operation.ArtifactPublication.Source.AttachmentName') IS NULL) OR COALESCE((Kind IN (0,1) AND Status IN (0,1,2,3,4) AND Revision >= 1 AND ExpiresAt > CreatedAt AND length(Fingerprint) = 64
        AND Permission = 'artifacts.create' AND ResourceType = 'session' AND ResourceId IS SessionId
        AND CredentialId IS NULL AND SandboxProfileId IS NULL AND SandboxProfileRevision IS NULL
        AND json_type(PayloadJson, '$.Operation.ArtifactPublication') IS 'object'
        AND json_extract(PayloadJson, '$.Operation.ArtifactPublication.SessionId') IS SessionId
        AND json_type(PayloadJson, '$.Operation.ArtifactPublication.Source') IS 'object'
        AND json_type(PayloadJson, '$.Operation.ArtifactPublication.Source.Working') IN ('true','false')
        AND json_type(PayloadJson, '$.Operation.ArtifactPublication.Source.Path') IS 'text'
        AND length(CAST(json_extract(PayloadJson, '$.Operation.ArtifactPublication.Source.Path') AS BLOB)) BETWEEN 7 AND 4095
        AND json_type(PayloadJson, '$.Operation.ArtifactPublication.Source.Working') IS 'true'
        AND json_extract(PayloadJson, '$.Operation.ArtifactPublication.Source.ResourceKey') IS NULL
        AND json_extract(PayloadJson, '$.Operation.ArtifactPublication.Source.ResourceRevision') IS NULL
        AND json_type(PayloadJson, '$.Operation.ArtifactPublication.Source.AttachmentId') IS 'text'
        AND length(json_extract(PayloadJson, '$.Operation.ArtifactPublication.Source.AttachmentId')) = 36
        AND json_extract(PayloadJson, '$.Operation.ArtifactPublication.Source.AttachmentId') NOT GLOB '*[^0-9a-f-]*'
        AND json_extract(PayloadJson, '$.Operation.ArtifactPublication.Source.AttachmentId') <> '00000000-0000-0000-0000-000000000000'
        AND substr(json_extract(PayloadJson, '$.Operation.ArtifactPublication.Source.AttachmentId'),9,1) IS '-'
        AND substr(json_extract(PayloadJson, '$.Operation.ArtifactPublication.Source.AttachmentId'),14,1) IS '-'
        AND substr(json_extract(PayloadJson, '$.Operation.ArtifactPublication.Source.AttachmentId'),19,1) IS '-'
        AND substr(json_extract(PayloadJson, '$.Operation.ArtifactPublication.Source.AttachmentId'),24,1) IS '-'
        AND json_type(PayloadJson, '$.Operation.ArtifactPublication.Source.AttachmentName') IS 'text'
        AND length(json_extract(PayloadJson, '$.Operation.ArtifactPublication.Source.AttachmentName')) BETWEEN 1 AND 200
        AND json_type(PayloadJson, '$.Operation.ArtifactPublication.Source.EntryId') IS 'text'
        AND length(json_extract(PayloadJson, '$.Operation.ArtifactPublication.Source.EntryId')) = 36
        AND json_extract(PayloadJson, '$.Operation.ArtifactPublication.Source.EntryId') <> '00000000-0000-0000-0000-000000000000'
        AND length(replace(json_extract(PayloadJson, '$.Operation.ArtifactPublication.Source.AttachmentId'),'-','')) = 32 AND replace(json_extract(PayloadJson, '$.Operation.ArtifactPublication.Source.AttachmentId'),'-','') NOT GLOB '*[^0-9a-f]*'
        AND length(replace(json_extract(PayloadJson, '$.Operation.ArtifactPublication.Source.EntryId'),'-','')) = 32 AND replace(json_extract(PayloadJson, '$.Operation.ArtifactPublication.Source.EntryId'),'-','') NOT GLOB '*[^0-9a-f]*'
        AND substr(json_extract(PayloadJson, '$.Operation.ArtifactPublication.Source.EntryId'),9,1) IS '-'
        AND substr(json_extract(PayloadJson, '$.Operation.ArtifactPublication.Source.EntryId'),14,1) IS '-'
        AND substr(json_extract(PayloadJson, '$.Operation.ArtifactPublication.Source.EntryId'),19,1) IS '-'
        AND substr(json_extract(PayloadJson, '$.Operation.ArtifactPublication.Source.EntryId'),24,1) IS '-'
        AND json_extract(PayloadJson, '$.Operation.ArtifactPublication.Source.Path') IS '/attachments/' || json_extract(PayloadJson, '$.Operation.ArtifactPublication.Source.AttachmentId')
        AND json_type(PayloadJson, '$.Operation.ArtifactPublication.Source.ContentType') IS 'text'
        AND length(json_extract(PayloadJson, '$.Operation.ArtifactPublication.Source.ContentType')) BETWEEN 1 AND 200
        AND json_type(PayloadJson, '$.Operation.ArtifactPublication.Source.ByteSize') IS 'integer'
        AND json_extract(PayloadJson, '$.Operation.ArtifactPublication.Source.ByteSize') BETWEEN 0 AND 26214400
        AND json_type(PayloadJson, '$.Operation.ArtifactPublication.Source.Sha256Hex') IS 'text'
        AND length(json_extract(PayloadJson, '$.Operation.ArtifactPublication.Source.Sha256Hex')) = 64
        AND json_extract(PayloadJson, '$.Operation.ArtifactPublication.Source.Sha256Hex') NOT GLOB '*[^0-9a-f]*'
        AND json_type(PayloadJson, '$.Operation.ArtifactPublication.Source.ObservationHash') IS 'text'
        AND length(json_extract(PayloadJson, '$.Operation.ArtifactPublication.Source.ObservationHash')) = 64
        AND json_extract(PayloadJson, '$.Operation.ArtifactPublication.Source.ObservationHash') NOT GLOB '*[^0-9a-f]*'
        AND json_type(PayloadJson, '$.Operation.ArtifactPublication.ArgumentsJson') IS 'text'
        AND length(CAST(json_extract(PayloadJson, '$.Operation.ArtifactPublication.ArgumentsJson') AS BLOB)) BETWEEN 2 AND 65536
        AND json_valid(json_extract(PayloadJson, '$.Operation.ArtifactPublication.ArgumentsJson'))
        AND json_type(json_extract(PayloadJson, '$.Operation.ArtifactPublication.ArgumentsJson')) IS 'object'
        AND json_type(json_extract(PayloadJson, '$.Operation.ArtifactPublication.ArgumentsJson'), '$.path') IS 'text'
        AND json_extract(json_extract(PayloadJson, '$.Operation.ArtifactPublication.ArgumentsJson'), '$.path') IS json_extract(PayloadJson, '$.Operation.ArtifactPublication.Source.Path')
        AND json_type(json_extract(PayloadJson, '$.Operation.ArtifactPublication.ArgumentsJson'), '$.displayName') IS 'text'
        AND length(json_extract(json_extract(PayloadJson, '$.Operation.ArtifactPublication.ArgumentsJson'), '$.displayName')) BETWEEN 1 AND 200
        AND (json_type(json_extract(PayloadJson, '$.Operation.ArtifactPublication.ArgumentsJson'), '$.contentType') IS NULL OR (
            json_type(json_extract(PayloadJson, '$.Operation.ArtifactPublication.ArgumentsJson'), '$.contentType') IS 'text'
            AND length(json_extract(json_extract(PayloadJson, '$.Operation.ArtifactPublication.ArgumentsJson'), '$.contentType')) BETWEEN 1 AND 200))
        AND json_type(json_extract(PayloadJson, '$.Operation.ArtifactPublication.ArgumentsJson'), '$.content') IS NULL
        AND json_extract(PayloadJson, '$.Operation.Persona') IS NULL
        AND json_extract(PayloadJson, '$.Operation.Preferences') IS NULL
        AND json_extract(PayloadJson, '$.Operation.CredentialCheck') IS NULL
        AND json_extract(PayloadJson, '$.Operation.SandboxCommand') IS NULL
        AND json_extract(PayloadJson, '$.Operation.SandboxControl') IS NULL
        AND json_extract(PayloadJson, '$.Operation.SandboxRead') IS NULL
        AND json_extract(PayloadJson, '$.Operation.BrowserCommand') IS NULL
        AND json_extract(PayloadJson, '$.Operation.CredentialRead') IS NULL
        AND json_extract(PayloadJson, '$.Operation.ArtifactVerification') IS NULL
        AND json_extract(PayloadJson, '$.Operation.WorkspaceRead') IS NULL
        AND json_extract(PayloadJson, '$.Operation.WorkspaceMutation') IS NULL
        AND length(CAST(PayloadJson AS BLOB)) <= 524288
        AND json_extract(PayloadJson, '$.Operation.AttachmentRead') IS NULL),0)) AND json_extract(PayloadJson, '$.Operation.WorkspaceRead.AttachmentMembersJson') IS NULL) OR COALESCE((Kind = 0 AND Status IN (0,1,2,3,4) AND Revision >= 1 AND ExpiresAt > CreatedAt AND length(Fingerprint) = 64
        AND Permission = 'sessions.read' AND ResourceType = 'session' AND ResourceId IS SessionId
        AND CredentialId IS NULL AND SandboxProfileId IS NULL AND SandboxProfileRevision IS NULL
        AND json_type(PayloadJson, '$.Operation.WorkspaceRead') IS 'object'
        AND json_extract(PayloadJson, '$.Operation.WorkspaceRead.SessionId') IS SessionId
        AND json_type(PayloadJson, '$.Operation.WorkspaceRead.Working') IS 'false'
        AND json_extract(PayloadJson, '$.Operation.WorkspaceRead.ResourceKey') IS NULL
        AND json_extract(PayloadJson, '$.Operation.WorkspaceRead.ResourceRevision') IS NULL
        AND json_extract(PayloadJson, '$.Operation.WorkspaceRead.ToolName') IN ('workspace.list','workspace.search')
        AND json_type(PayloadJson, '$.Operation.WorkspaceRead.ArgumentsJson') IS 'text'
        AND length(CAST(json_extract(PayloadJson, '$.Operation.WorkspaceRead.ArgumentsJson') AS BLOB)) <= 16384
        AND json_valid(json_extract(PayloadJson, '$.Operation.WorkspaceRead.ArgumentsJson'))
        AND json_type(json_extract(PayloadJson, '$.Operation.WorkspaceRead.ArgumentsJson'), '$.path') IS 'text'
        AND json_extract(json_extract(PayloadJson, '$.Operation.WorkspaceRead.ArgumentsJson'), '$.path') IS '/attachments'
        AND json_extract(PayloadJson, '$.Operation.WorkspaceRead.ResourceMembersJson') IS NULL
        AND json_extract(PayloadJson, '$.Operation.WorkspaceRead.Knowledge') IS NULL
        AND json_extract(PayloadJson, '$.Operation.AttachmentRead') IS NULL
        AND json_type(PayloadJson, '$.Operation.WorkspaceRead.AttachmentMembersJson') IS 'text'
        AND length(CAST(json_extract(PayloadJson, '$.Operation.WorkspaceRead.AttachmentMembersJson') AS BLOB)) <= 65536
        AND json_valid(json_extract(PayloadJson, '$.Operation.WorkspaceRead.AttachmentMembersJson'))
        AND json_type(json_extract(PayloadJson, '$.Operation.WorkspaceRead.AttachmentMembersJson')) IS 'array'
        AND json_array_length(json_extract(PayloadJson, '$.Operation.WorkspaceRead.AttachmentMembersJson')) <= 257
        AND json_type(PayloadJson, '$.Operation.WorkspaceRead.ObservationHash') IS 'text'
        AND length(json_extract(PayloadJson, '$.Operation.WorkspaceRead.ObservationHash')) = 64
        AND json_extract(PayloadJson, '$.Operation.WorkspaceRead.ObservationHash') NOT GLOB '*[^0-9a-f]*'
        AND json_extract(PayloadJson, '$.Operation.Persona') IS NULL
        AND json_extract(PayloadJson, '$.Operation.Preferences') IS NULL
        AND json_extract(PayloadJson, '$.Operation.CredentialCheck') IS NULL
        AND json_extract(PayloadJson, '$.Operation.SandboxCommand') IS NULL
        AND json_extract(PayloadJson, '$.Operation.SandboxControl') IS NULL
        AND json_extract(PayloadJson, '$.Operation.SandboxRead') IS NULL
        AND json_extract(PayloadJson, '$.Operation.BrowserCommand') IS NULL
        AND json_extract(PayloadJson, '$.Operation.CredentialRead') IS NULL
        AND json_extract(PayloadJson, '$.Operation.ArtifactPublication') IS NULL
        AND json_extract(PayloadJson, '$.Operation.ArtifactVerification') IS NULL
        AND json_extract(PayloadJson, '$.Operation.WorkspaceMutation') IS NULL
        AND length(CAST(PayloadJson AS BLOB)) <= 131072),0)) AND json_extract(PayloadJson, '$.Operation.BackgroundStart') IS NULL) OR COALESCE((Kind IN (0,1) AND Status IN (0,1,2,3,4) AND Revision >= 1 AND ExpiresAt > CreatedAt AND length(Fingerprint) = 64
        AND Permission IN ('sessions.create','agent-runs.create') AND ResourceType = 'tenant' AND ResourceId IS lower(TenantId)
        AND CredentialId IS NULL AND SandboxProfileId IS NULL AND SandboxProfileRevision IS NULL
        AND json_type(PayloadJson, '$.Operation.BackgroundStart') IS 'object'
        AND json_extract(PayloadJson, '$.Operation.BackgroundStart.TenantId') IS lower(TenantId)
        AND json_extract(PayloadJson, '$.Operation.BackgroundStart.SessionId') IS SessionId
        AND json_extract(PayloadJson, '$.Operation.BackgroundStart.Permission') IS Permission
        AND json_type(PayloadJson, '$.Operation.BackgroundStart.Objective') IS 'text'
        AND length(json_extract(PayloadJson, '$.Operation.BackgroundStart.Objective')) BETWEEN 1 AND 4000
        AND json_type(PayloadJson, '$.Operation.BackgroundStart.Title') IS 'text'
        AND length(json_extract(PayloadJson, '$.Operation.BackgroundStart.Title')) BETWEEN 1 AND 80
        AND json_type(PayloadJson, '$.Operation.BackgroundStart.ReportCompletion') IN ('true','false')
        AND json_type(PayloadJson, '$.Operation.BackgroundStart.ConfigurationHash') IS 'text'
        AND length(json_extract(PayloadJson, '$.Operation.BackgroundStart.ConfigurationHash')) = 64
        AND json_extract(PayloadJson, '$.Operation.BackgroundStart.ConfigurationHash') NOT GLOB '*[^0-9a-f]*'
        AND json_type(PayloadJson, '$.Operation.BackgroundStart.Model') IS 'object'
        AND json_type(PayloadJson, '$.Operation.BackgroundStart.Model.CatalogKey') IS 'text'
        AND length(json_extract(PayloadJson, '$.Operation.BackgroundStart.Model.CatalogKey')) BETWEEN 1 AND 128
        AND json_type(PayloadJson, '$.Operation.BackgroundStart.Model.ProviderAlias') IS 'text'
        AND length(json_extract(PayloadJson, '$.Operation.BackgroundStart.Model.ProviderAlias')) BETWEEN 1 AND 128
        AND json_type(PayloadJson, '$.Operation.BackgroundStart.Model.ModelId') IS 'text'
        AND length(json_extract(PayloadJson, '$.Operation.BackgroundStart.Model.ModelId')) BETWEEN 1 AND 128
        AND length(CAST(PayloadJson AS BLOB)) <= 131072 AND json_type(PayloadJson, '$.Operation.BackgroundStart.ChildSessionId') IS 'text' AND length(json_extract(PayloadJson, '$.Operation.BackgroundStart.ChildSessionId')) = 36 AND json_extract(PayloadJson, '$.Operation.BackgroundStart.ChildSessionId') NOT GLOB '*[^0-9a-f-]*' AND substr(json_extract(PayloadJson, '$.Operation.BackgroundStart.ChildSessionId'),9,1) IS '-' AND substr(json_extract(PayloadJson, '$.Operation.BackgroundStart.ChildSessionId'),14,1) IS '-' AND substr(json_extract(PayloadJson, '$.Operation.BackgroundStart.ChildSessionId'),19,1) IS '-' AND substr(json_extract(PayloadJson, '$.Operation.BackgroundStart.ChildSessionId'),24,1) IS '-' AND json_extract(PayloadJson, '$.Operation.BackgroundStart.ChildSessionId') IS NOT SessionId AND json_extract(PayloadJson, '$.Operation.BackgroundStart.ChildSessionId') IS NOT lower(TenantId) AND json_type(PayloadJson, '$.Operation.BackgroundStart.EntryId') IS 'text' AND length(json_extract(PayloadJson, '$.Operation.BackgroundStart.EntryId')) = 36 AND json_extract(PayloadJson, '$.Operation.BackgroundStart.EntryId') NOT GLOB '*[^0-9a-f-]*' AND substr(json_extract(PayloadJson, '$.Operation.BackgroundStart.EntryId'),9,1) IS '-' AND substr(json_extract(PayloadJson, '$.Operation.BackgroundStart.EntryId'),14,1) IS '-' AND substr(json_extract(PayloadJson, '$.Operation.BackgroundStart.EntryId'),19,1) IS '-' AND substr(json_extract(PayloadJson, '$.Operation.BackgroundStart.EntryId'),24,1) IS '-' AND json_extract(PayloadJson, '$.Operation.BackgroundStart.EntryId') IS NOT SessionId AND json_extract(PayloadJson, '$.Operation.BackgroundStart.EntryId') IS NOT lower(TenantId) AND json_type(PayloadJson, '$.Operation.BackgroundStart.ActivationId') IS 'text' AND length(json_extract(PayloadJson, '$.Operation.BackgroundStart.ActivationId')) = 36 AND json_extract(PayloadJson, '$.Operation.BackgroundStart.ActivationId') NOT GLOB '*[^0-9a-f-]*' AND substr(json_extract(PayloadJson, '$.Operation.BackgroundStart.ActivationId'),9,1) IS '-' AND substr(json_extract(PayloadJson, '$.Operation.BackgroundStart.ActivationId'),14,1) IS '-' AND substr(json_extract(PayloadJson, '$.Operation.BackgroundStart.ActivationId'),19,1) IS '-' AND substr(json_extract(PayloadJson, '$.Operation.BackgroundStart.ActivationId'),24,1) IS '-' AND json_extract(PayloadJson, '$.Operation.BackgroundStart.ActivationId') IS NOT SessionId AND json_extract(PayloadJson, '$.Operation.BackgroundStart.ActivationId') IS NOT lower(TenantId) AND json_type(PayloadJson, '$.Operation.BackgroundStart.RunId') IS 'text' AND length(json_extract(PayloadJson, '$.Operation.BackgroundStart.RunId')) = 36 AND json_extract(PayloadJson, '$.Operation.BackgroundStart.RunId') NOT GLOB '*[^0-9a-f-]*' AND substr(json_extract(PayloadJson, '$.Operation.BackgroundStart.RunId'),9,1) IS '-' AND substr(json_extract(PayloadJson, '$.Operation.BackgroundStart.RunId'),14,1) IS '-' AND substr(json_extract(PayloadJson, '$.Operation.BackgroundStart.RunId'),19,1) IS '-' AND substr(json_extract(PayloadJson, '$.Operation.BackgroundStart.RunId'),24,1) IS '-' AND json_extract(PayloadJson, '$.Operation.BackgroundStart.RunId') IS NOT SessionId AND json_extract(PayloadJson, '$.Operation.BackgroundStart.RunId') IS NOT lower(TenantId) AND json_type(PayloadJson, '$.Operation.BackgroundStart.ResponseId') IS 'text' AND length(json_extract(PayloadJson, '$.Operation.BackgroundStart.ResponseId')) = 36 AND json_extract(PayloadJson, '$.Operation.BackgroundStart.ResponseId') NOT GLOB '*[^0-9a-f-]*' AND substr(json_extract(PayloadJson, '$.Operation.BackgroundStart.ResponseId'),9,1) IS '-' AND substr(json_extract(PayloadJson, '$.Operation.BackgroundStart.ResponseId'),14,1) IS '-' AND substr(json_extract(PayloadJson, '$.Operation.BackgroundStart.ResponseId'),19,1) IS '-' AND substr(json_extract(PayloadJson, '$.Operation.BackgroundStart.ResponseId'),24,1) IS '-' AND json_extract(PayloadJson, '$.Operation.BackgroundStart.ResponseId') IS NOT SessionId AND json_extract(PayloadJson, '$.Operation.BackgroundStart.ResponseId') IS NOT lower(TenantId) AND json_extract(PayloadJson, '$.Operation.BackgroundStart.ChildSessionId') IS NOT json_extract(PayloadJson, '$.Operation.BackgroundStart.EntryId') AND json_extract(PayloadJson, '$.Operation.BackgroundStart.ChildSessionId') IS NOT json_extract(PayloadJson, '$.Operation.BackgroundStart.ActivationId') AND json_extract(PayloadJson, '$.Operation.BackgroundStart.ChildSessionId') IS NOT json_extract(PayloadJson, '$.Operation.BackgroundStart.RunId') AND json_extract(PayloadJson, '$.Operation.BackgroundStart.ChildSessionId') IS NOT json_extract(PayloadJson, '$.Operation.BackgroundStart.ResponseId') AND json_extract(PayloadJson, '$.Operation.BackgroundStart.EntryId') IS NOT json_extract(PayloadJson, '$.Operation.BackgroundStart.ActivationId') AND json_extract(PayloadJson, '$.Operation.BackgroundStart.EntryId') IS NOT json_extract(PayloadJson, '$.Operation.BackgroundStart.RunId') AND json_extract(PayloadJson, '$.Operation.BackgroundStart.EntryId') IS NOT json_extract(PayloadJson, '$.Operation.BackgroundStart.ResponseId') AND json_extract(PayloadJson, '$.Operation.BackgroundStart.ActivationId') IS NOT json_extract(PayloadJson, '$.Operation.BackgroundStart.RunId') AND json_extract(PayloadJson, '$.Operation.BackgroundStart.ActivationId') IS NOT json_extract(PayloadJson, '$.Operation.BackgroundStart.ResponseId') AND json_extract(PayloadJson, '$.Operation.BackgroundStart.RunId') IS NOT json_extract(PayloadJson, '$.Operation.BackgroundStart.ResponseId') AND json_extract(PayloadJson, '$.Operation.Persona') IS NULL AND json_extract(PayloadJson, '$.Operation.Preferences') IS NULL AND json_extract(PayloadJson, '$.Operation.CredentialCheck') IS NULL AND json_extract(PayloadJson, '$.Operation.SandboxCommand') IS NULL AND json_extract(PayloadJson, '$.Operation.SandboxControl') IS NULL AND json_extract(PayloadJson, '$.Operation.SandboxRead') IS NULL AND json_extract(PayloadJson, '$.Operation.BrowserCommand') IS NULL AND json_extract(PayloadJson, '$.Operation.CredentialRead') IS NULL AND json_extract(PayloadJson, '$.Operation.ArtifactPublication') IS NULL AND json_extract(PayloadJson, '$.Operation.ArtifactVerification') IS NULL AND json_extract(PayloadJson, '$.Operation.WorkspaceRead') IS NULL AND json_extract(PayloadJson, '$.Operation.WorkspaceMutation') IS NULL AND json_extract(PayloadJson, '$.Operation.AttachmentRead') IS NULL),0)) AND json_extract(PayloadJson, '$.Operation.BackgroundReport') IS NULL) OR COALESCE((Kind IN (0,1) AND Status IN (0,1,2,3,4) AND Revision >= 1 AND ExpiresAt > CreatedAt AND length(Fingerprint) = 64
        AND Permission = 'agent-runs.create' AND ResourceType = 'tenant' AND ResourceId IS lower(TenantId)
        AND CredentialId IS NULL AND SandboxProfileId IS NULL AND SandboxProfileRevision IS NULL
        AND json_type(PayloadJson, '$.Operation.BackgroundReport') IS 'object'
        AND json_extract(PayloadJson, '$.Operation.BackgroundReport.TenantId') IS lower(TenantId)
        AND json_extract(PayloadJson, '$.Operation.BackgroundReport.SessionId') IS SessionId
        AND json_extract(PayloadJson, '$.Operation.BackgroundReport.ChildRunId') IS RunId
        AND json_type(PayloadJson, '$.Operation.BackgroundReport.SourceRevision') IS 'integer'
        AND json_extract(PayloadJson, '$.Operation.BackgroundReport.SourceRevision') >= 1
        AND json_type(PayloadJson, '$.Operation.BackgroundReport.ParentRevision') IS 'integer'
        AND json_extract(PayloadJson, '$.Operation.BackgroundReport.ParentRevision') >= 1
        AND json_type(PayloadJson, '$.Operation.BackgroundReport.InputBoundarySequence') IS 'integer'
        AND json_extract(PayloadJson, '$.Operation.BackgroundReport.InputBoundarySequence') >= 0
        AND json_type(PayloadJson, '$.Operation.BackgroundReport.EvidenceJson') IS 'text'
        AND json_valid(json_extract(PayloadJson, '$.Operation.BackgroundReport.EvidenceJson'))
        AND json_type(PayloadJson, '$.Operation.BackgroundReport.ConfigurationHash') IS 'text'
        AND length(json_extract(PayloadJson, '$.Operation.BackgroundReport.ConfigurationHash')) = 64
        AND json_extract(PayloadJson, '$.Operation.BackgroundReport.ConfigurationHash') NOT GLOB '*[^0-9a-f]*'
        AND json_type(PayloadJson, '$.Operation.BackgroundReport.Model') IS 'object'
        AND length(CAST(PayloadJson AS BLOB)) <= 131072 AND json_type(PayloadJson, '$.Operation.BackgroundReport.ParentSessionId') IS 'text' AND length(json_extract(PayloadJson, '$.Operation.BackgroundReport.ParentSessionId')) = 36 AND json_extract(PayloadJson, '$.Operation.BackgroundReport.ParentSessionId') NOT GLOB '*[^0-9a-f-]*' AND substr(json_extract(PayloadJson, '$.Operation.BackgroundReport.ParentSessionId'),9,1) IS '-' AND substr(json_extract(PayloadJson, '$.Operation.BackgroundReport.ParentSessionId'),14,1) IS '-' AND substr(json_extract(PayloadJson, '$.Operation.BackgroundReport.ParentSessionId'),19,1) IS '-' AND substr(json_extract(PayloadJson, '$.Operation.BackgroundReport.ParentSessionId'),24,1) IS '-' AND json_extract(PayloadJson, '$.Operation.BackgroundReport.ParentSessionId') IS NOT SessionId AND json_extract(PayloadJson, '$.Operation.BackgroundReport.ParentSessionId') IS NOT RunId AND json_extract(PayloadJson, '$.Operation.BackgroundReport.ParentSessionId') IS NOT lower(TenantId) AND json_type(PayloadJson, '$.Operation.BackgroundReport.ActivationId') IS 'text' AND length(json_extract(PayloadJson, '$.Operation.BackgroundReport.ActivationId')) = 36 AND json_extract(PayloadJson, '$.Operation.BackgroundReport.ActivationId') NOT GLOB '*[^0-9a-f-]*' AND substr(json_extract(PayloadJson, '$.Operation.BackgroundReport.ActivationId'),9,1) IS '-' AND substr(json_extract(PayloadJson, '$.Operation.BackgroundReport.ActivationId'),14,1) IS '-' AND substr(json_extract(PayloadJson, '$.Operation.BackgroundReport.ActivationId'),19,1) IS '-' AND substr(json_extract(PayloadJson, '$.Operation.BackgroundReport.ActivationId'),24,1) IS '-' AND json_extract(PayloadJson, '$.Operation.BackgroundReport.ActivationId') IS NOT SessionId AND json_extract(PayloadJson, '$.Operation.BackgroundReport.ActivationId') IS NOT RunId AND json_extract(PayloadJson, '$.Operation.BackgroundReport.ActivationId') IS NOT lower(TenantId) AND json_type(PayloadJson, '$.Operation.BackgroundReport.RunId') IS 'text' AND length(json_extract(PayloadJson, '$.Operation.BackgroundReport.RunId')) = 36 AND json_extract(PayloadJson, '$.Operation.BackgroundReport.RunId') NOT GLOB '*[^0-9a-f-]*' AND substr(json_extract(PayloadJson, '$.Operation.BackgroundReport.RunId'),9,1) IS '-' AND substr(json_extract(PayloadJson, '$.Operation.BackgroundReport.RunId'),14,1) IS '-' AND substr(json_extract(PayloadJson, '$.Operation.BackgroundReport.RunId'),19,1) IS '-' AND substr(json_extract(PayloadJson, '$.Operation.BackgroundReport.RunId'),24,1) IS '-' AND json_extract(PayloadJson, '$.Operation.BackgroundReport.RunId') IS NOT SessionId AND json_extract(PayloadJson, '$.Operation.BackgroundReport.RunId') IS NOT RunId AND json_extract(PayloadJson, '$.Operation.BackgroundReport.RunId') IS NOT lower(TenantId) AND json_type(PayloadJson, '$.Operation.BackgroundReport.ResponseId') IS 'text' AND length(json_extract(PayloadJson, '$.Operation.BackgroundReport.ResponseId')) = 36 AND json_extract(PayloadJson, '$.Operation.BackgroundReport.ResponseId') NOT GLOB '*[^0-9a-f-]*' AND substr(json_extract(PayloadJson, '$.Operation.BackgroundReport.ResponseId'),9,1) IS '-' AND substr(json_extract(PayloadJson, '$.Operation.BackgroundReport.ResponseId'),14,1) IS '-' AND substr(json_extract(PayloadJson, '$.Operation.BackgroundReport.ResponseId'),19,1) IS '-' AND substr(json_extract(PayloadJson, '$.Operation.BackgroundReport.ResponseId'),24,1) IS '-' AND json_extract(PayloadJson, '$.Operation.BackgroundReport.ResponseId') IS NOT SessionId AND json_extract(PayloadJson, '$.Operation.BackgroundReport.ResponseId') IS NOT RunId AND json_extract(PayloadJson, '$.Operation.BackgroundReport.ResponseId') IS NOT lower(TenantId) AND json_extract(PayloadJson, '$.Operation.BackgroundReport.ParentSessionId') IS NOT json_extract(PayloadJson, '$.Operation.BackgroundReport.ActivationId') AND json_extract(PayloadJson, '$.Operation.BackgroundReport.ParentSessionId') IS NOT json_extract(PayloadJson, '$.Operation.BackgroundReport.RunId') AND json_extract(PayloadJson, '$.Operation.BackgroundReport.ParentSessionId') IS NOT json_extract(PayloadJson, '$.Operation.BackgroundReport.ResponseId') AND json_extract(PayloadJson, '$.Operation.BackgroundReport.ActivationId') IS NOT json_extract(PayloadJson, '$.Operation.BackgroundReport.RunId') AND json_extract(PayloadJson, '$.Operation.BackgroundReport.ActivationId') IS NOT json_extract(PayloadJson, '$.Operation.BackgroundReport.ResponseId') AND json_extract(PayloadJson, '$.Operation.BackgroundReport.RunId') IS NOT json_extract(PayloadJson, '$.Operation.BackgroundReport.ResponseId') AND json_type(PayloadJson, '$.Operation.BackgroundReport.Model.CatalogKey') IS 'text' AND length(json_extract(PayloadJson, '$.Operation.BackgroundReport.Model.CatalogKey')) BETWEEN 1 AND 128 AND json_type(PayloadJson, '$.Operation.BackgroundReport.Model.ProviderAlias') IS 'text' AND length(json_extract(PayloadJson, '$.Operation.BackgroundReport.Model.ProviderAlias')) BETWEEN 1 AND 128 AND json_type(PayloadJson, '$.Operation.BackgroundReport.Model.ModelId') IS 'text' AND length(json_extract(PayloadJson, '$.Operation.BackgroundReport.Model.ModelId')) BETWEEN 1 AND 128 AND json_extract(PayloadJson, '$.Operation.Persona') IS NULL AND json_extract(PayloadJson, '$.Operation.Preferences') IS NULL AND json_extract(PayloadJson, '$.Operation.CredentialCheck') IS NULL AND json_extract(PayloadJson, '$.Operation.SandboxCommand') IS NULL AND json_extract(PayloadJson, '$.Operation.SandboxControl') IS NULL AND json_extract(PayloadJson, '$.Operation.SandboxRead') IS NULL AND json_extract(PayloadJson, '$.Operation.BrowserCommand') IS NULL AND json_extract(PayloadJson, '$.Operation.CredentialRead') IS NULL AND json_extract(PayloadJson, '$.Operation.ArtifactPublication') IS NULL AND json_extract(PayloadJson, '$.Operation.ArtifactVerification') IS NULL AND json_extract(PayloadJson, '$.Operation.WorkspaceRead') IS NULL AND json_extract(PayloadJson, '$.Operation.WorkspaceMutation') IS NULL AND json_extract(PayloadJson, '$.Operation.AttachmentRead') IS NULL AND json_extract(PayloadJson, '$.Operation.BackgroundStart') IS NULL),0)) AND json_extract(PayloadJson, '$.Operation.CompletionRead') IS NULL) OR COALESCE((Kind = 0 AND Status IN (0,1,2,3,4) AND Revision >= 1 AND ExpiresAt > CreatedAt AND length(Fingerprint) = 64
        AND Permission = 'agent-runs.read' AND ResourceType = 'agent-run'
        AND ResourceId IS json_extract(PayloadJson, '$.Operation.CompletionRead.SourceRunId')
        AND CredentialId IS NULL AND SandboxProfileId IS NULL AND SandboxProfileRevision IS NULL
        AND json_type(PayloadJson, '$.Operation.CompletionRead') IS 'object'
        AND json_extract(PayloadJson, '$.Operation.CompletionRead.ParentSessionId') IS SessionId
        AND json_type(PayloadJson, '$.Operation.CompletionRead.SourceRevision') IS 'integer'
        AND json_extract(PayloadJson, '$.Operation.CompletionRead.SourceRevision') >= 1
        AND json_type(PayloadJson, '$.Operation.CompletionRead.Objective') IS 'text'
        AND length(CAST(json_extract(PayloadJson, '$.Operation.CompletionRead.Objective') AS BLOB)) BETWEEN 1 AND 4000
        AND json_type(PayloadJson, '$.Operation.CompletionRead.ObservationJson') IS 'text'
        AND length(CAST(json_extract(PayloadJson, '$.Operation.CompletionRead.ObservationJson') AS BLOB)) BETWEEN 2 AND 20000
        AND json_valid(json_extract(PayloadJson, '$.Operation.CompletionRead.ObservationJson'))
        AND json_type(json_extract(PayloadJson, '$.Operation.CompletionRead.ObservationJson')) IS 'object'
        AND json_extract(json_extract(PayloadJson, '$.Operation.CompletionRead.ObservationJson'), '$.untrusted') = 1
        AND json_extract(json_extract(PayloadJson, '$.Operation.CompletionRead.ObservationJson'), '$.tenantCompletionRead') = 1
        AND json_extract(json_extract(PayloadJson, '$.Operation.CompletionRead.ObservationJson'), '$.backgroundSessionId') IS json_extract(PayloadJson, '$.Operation.CompletionRead.ChildSessionId')
        AND json_extract(json_extract(PayloadJson, '$.Operation.CompletionRead.ObservationJson'), '$.parentSessionId') IS SessionId
        AND json_extract(json_extract(PayloadJson, '$.Operation.CompletionRead.ObservationJson'), '$.agentRunId') IS json_extract(PayloadJson, '$.Operation.CompletionRead.InitialRunId')
        AND json_extract(json_extract(PayloadJson, '$.Operation.CompletionRead.ObservationJson'), '$.sourceAgentRunId') IS ResourceId
        AND json_extract(json_extract(PayloadJson, '$.Operation.CompletionRead.ObservationJson'), '$.status') IN ('Completed','Failed','Cancelled')
        AND json_type(PayloadJson, '$.Operation.CompletionRead.ObservationHash') IS 'text'
        AND length(json_extract(PayloadJson, '$.Operation.CompletionRead.ObservationHash')) = 64
        AND json_extract(PayloadJson, '$.Operation.CompletionRead.ObservationHash') NOT GLOB '*[^0-9a-f]*'
        AND json_type(PayloadJson, '$.Operation.CompletionRead.ArtifactsJson') IS 'text'
        AND length(CAST(json_extract(PayloadJson, '$.Operation.CompletionRead.ArtifactsJson') AS BLOB)) <= 8192
        AND json_valid(json_extract(PayloadJson, '$.Operation.CompletionRead.ArtifactsJson'))
        AND json_type(json_extract(PayloadJson, '$.Operation.CompletionRead.ArtifactsJson')) IS 'array'
        AND json_array_length(json_extract(PayloadJson, '$.Operation.CompletionRead.ArtifactsJson')) <= 3
        AND length(CAST(PayloadJson AS BLOB)) <= 131072
        AND json_type(PayloadJson, '$.Operation.CompletionRead.ChildSessionId') IS 'text' AND length(json_extract(PayloadJson, '$.Operation.CompletionRead.ChildSessionId')) = 36 AND json_extract(PayloadJson, '$.Operation.CompletionRead.ChildSessionId') NOT GLOB '*[^0-9a-f-]*' AND substr(json_extract(PayloadJson, '$.Operation.CompletionRead.ChildSessionId'),9,1) IS '-' AND substr(json_extract(PayloadJson, '$.Operation.CompletionRead.ChildSessionId'),14,1) IS '-' AND substr(json_extract(PayloadJson, '$.Operation.CompletionRead.ChildSessionId'),19,1) IS '-' AND substr(json_extract(PayloadJson, '$.Operation.CompletionRead.ChildSessionId'),24,1) IS '-' AND json_extract(PayloadJson, '$.Operation.CompletionRead.ChildSessionId') IS NOT '00000000-0000-0000-0000-000000000000' AND json_extract(PayloadJson, '$.Operation.CompletionRead.ChildSessionId') IS NOT SessionId
        AND json_type(PayloadJson, '$.Operation.CompletionRead.InitialRunId') IS 'text' AND length(json_extract(PayloadJson, '$.Operation.CompletionRead.InitialRunId')) = 36 AND json_extract(PayloadJson, '$.Operation.CompletionRead.InitialRunId') NOT GLOB '*[^0-9a-f-]*' AND substr(json_extract(PayloadJson, '$.Operation.CompletionRead.InitialRunId'),9,1) IS '-' AND substr(json_extract(PayloadJson, '$.Operation.CompletionRead.InitialRunId'),14,1) IS '-' AND substr(json_extract(PayloadJson, '$.Operation.CompletionRead.InitialRunId'),19,1) IS '-' AND substr(json_extract(PayloadJson, '$.Operation.CompletionRead.InitialRunId'),24,1) IS '-' AND json_extract(PayloadJson, '$.Operation.CompletionRead.InitialRunId') IS NOT '00000000-0000-0000-0000-000000000000' AND json_extract(PayloadJson, '$.Operation.CompletionRead.InitialRunId') IS NOT SessionId
        AND json_type(PayloadJson, '$.Operation.CompletionRead.SourceRunId') IS 'text' AND length(json_extract(PayloadJson, '$.Operation.CompletionRead.SourceRunId')) = 36 AND json_extract(PayloadJson, '$.Operation.CompletionRead.SourceRunId') NOT GLOB '*[^0-9a-f-]*' AND substr(json_extract(PayloadJson, '$.Operation.CompletionRead.SourceRunId'),9,1) IS '-' AND substr(json_extract(PayloadJson, '$.Operation.CompletionRead.SourceRunId'),14,1) IS '-' AND substr(json_extract(PayloadJson, '$.Operation.CompletionRead.SourceRunId'),19,1) IS '-' AND substr(json_extract(PayloadJson, '$.Operation.CompletionRead.SourceRunId'),24,1) IS '-' AND json_extract(PayloadJson, '$.Operation.CompletionRead.SourceRunId') IS NOT '00000000-0000-0000-0000-000000000000' AND json_extract(PayloadJson, '$.Operation.CompletionRead.SourceRunId') IS NOT SessionId
        AND json_extract(PayloadJson, '$.Operation.Persona') IS NULL
        AND json_extract(PayloadJson, '$.Operation.Preferences') IS NULL
        AND json_extract(PayloadJson, '$.Operation.CredentialCheck') IS NULL
        AND json_extract(PayloadJson, '$.Operation.SandboxCommand') IS NULL
        AND json_extract(PayloadJson, '$.Operation.SandboxControl') IS NULL
        AND json_extract(PayloadJson, '$.Operation.SandboxRead') IS NULL
        AND json_extract(PayloadJson, '$.Operation.BrowserCommand') IS NULL
        AND json_extract(PayloadJson, '$.Operation.CredentialRead') IS NULL
        AND json_extract(PayloadJson, '$.Operation.ArtifactPublication') IS NULL
        AND json_extract(PayloadJson, '$.Operation.ArtifactVerification') IS NULL
        AND json_extract(PayloadJson, '$.Operation.WorkspaceRead') IS NULL
        AND json_extract(PayloadJson, '$.Operation.WorkspaceMutation') IS NULL
        AND json_extract(PayloadJson, '$.Operation.AttachmentRead') IS NULL
        AND json_extract(PayloadJson, '$.Operation.BackgroundStart') IS NULL
        AND json_extract(PayloadJson, '$.Operation.BackgroundReport') IS NULL),0)) AND json_extract(PayloadJson, '$.Operation.ReportSourceRead') IS NULL) OR COALESCE((Kind = 0 AND Status IN (0,1,2,3,4) AND Revision >= 1 AND ExpiresAt > CreatedAt AND length(Fingerprint) = 64
        AND Permission = 'agent-runs.read' AND ResourceType = 'agent-run' AND ResourceId IS RunId
        AND CredentialId IS NULL AND SandboxProfileId IS NULL AND SandboxProfileRevision IS NULL
        AND json_type(PayloadJson, '$.Operation.ReportSourceRead') IS 'object'
        AND json_extract(PayloadJson, '$.Operation.ReportSourceRead.ChildSessionId') IS SessionId
        AND json_extract(PayloadJson, '$.Operation.ReportSourceRead.SourceRunId') IS RunId
        AND json_extract(PayloadJson, '$.Operation.ReportSourceRead.ParentSessionId') IS NOT SessionId
        AND json_type(PayloadJson, '$.Operation.ReportSourceRead.SourceRevision') IS 'integer' AND json_extract(PayloadJson, '$.Operation.ReportSourceRead.SourceRevision') >= 1
        AND json_type(PayloadJson, '$.Operation.ReportSourceRead.Objective') IS 'text' AND length(CAST(json_extract(PayloadJson, '$.Operation.ReportSourceRead.Objective') AS BLOB)) BETWEEN 1 AND 4000
        AND json_type(PayloadJson, '$.Operation.ReportSourceRead.ObservationJson') IS 'text' AND length(CAST(json_extract(PayloadJson, '$.Operation.ReportSourceRead.ObservationJson') AS BLOB)) BETWEEN 2 AND 8192
        AND json_valid(json_extract(PayloadJson, '$.Operation.ReportSourceRead.ObservationJson'))
        AND json_extract(json_extract(PayloadJson, '$.Operation.ReportSourceRead.ObservationJson'), '$.TriggerKind') = 7
        AND json_extract(json_extract(PayloadJson, '$.Operation.ReportSourceRead.ObservationJson'), '$.InputBoundarySequence') IS NULL
        AND json_type(json_extract(PayloadJson, '$.Operation.ReportSourceRead.ObservationJson'), '$.Text') IS 'text'
        AND json_valid(json_extract(json_extract(PayloadJson, '$.Operation.ReportSourceRead.ObservationJson'), '$.Text'))
        AND json_extract(json_extract(json_extract(PayloadJson, '$.Operation.ReportSourceRead.ObservationJson'), '$.Text'), '$.childSessionId') IS SessionId
        AND json_extract(json_extract(json_extract(PayloadJson, '$.Operation.ReportSourceRead.ObservationJson'), '$.Text'), '$.initialAgentRunId') IS RunId
        AND json_extract(json_extract(json_extract(PayloadJson, '$.Operation.ReportSourceRead.ObservationJson'), '$.Text'), '$.status') IN ('Completed','Failed','Cancelled')
        AND json_array_length(json_extract(json_extract(json_extract(PayloadJson, '$.Operation.ReportSourceRead.ObservationJson'), '$.Text'), '$.artifacts')) = 0
        AND json_type(PayloadJson, '$.Operation.ReportSourceRead.ObservationHash') IS 'text' AND length(json_extract(PayloadJson, '$.Operation.ReportSourceRead.ObservationHash')) = 64
        AND json_extract(PayloadJson, '$.Operation.ReportSourceRead.ObservationHash') NOT GLOB '*[^0-9a-f]*'
        AND length(CAST(PayloadJson AS BLOB)) <= 131072
        AND json_type(PayloadJson, '$.Operation.ReportSourceRead.ParentSessionId') IS 'text' AND length(json_extract(PayloadJson, '$.Operation.ReportSourceRead.ParentSessionId')) = 36 AND json_extract(PayloadJson, '$.Operation.ReportSourceRead.ParentSessionId') NOT GLOB '*[^0-9a-f-]*' AND json_extract(PayloadJson, '$.Operation.ReportSourceRead.ParentSessionId') IS NOT '00000000-0000-0000-0000-000000000000'
        AND json_type(PayloadJson, '$.Operation.ReportSourceRead.ChildSessionId') IS 'text' AND length(json_extract(PayloadJson, '$.Operation.ReportSourceRead.ChildSessionId')) = 36 AND json_extract(PayloadJson, '$.Operation.ReportSourceRead.ChildSessionId') NOT GLOB '*[^0-9a-f-]*' AND json_extract(PayloadJson, '$.Operation.ReportSourceRead.ChildSessionId') IS NOT '00000000-0000-0000-0000-000000000000'
        AND json_type(PayloadJson, '$.Operation.ReportSourceRead.InitialRunId') IS 'text' AND length(json_extract(PayloadJson, '$.Operation.ReportSourceRead.InitialRunId')) = 36 AND json_extract(PayloadJson, '$.Operation.ReportSourceRead.InitialRunId') NOT GLOB '*[^0-9a-f-]*' AND json_extract(PayloadJson, '$.Operation.ReportSourceRead.InitialRunId') IS NOT '00000000-0000-0000-0000-000000000000'
        AND json_type(PayloadJson, '$.Operation.ReportSourceRead.SourceRunId') IS 'text' AND length(json_extract(PayloadJson, '$.Operation.ReportSourceRead.SourceRunId')) = 36 AND json_extract(PayloadJson, '$.Operation.ReportSourceRead.SourceRunId') NOT GLOB '*[^0-9a-f-]*' AND json_extract(PayloadJson, '$.Operation.ReportSourceRead.SourceRunId') IS NOT '00000000-0000-0000-0000-000000000000'
        AND json_extract(PayloadJson, '$.Operation.Persona') IS NULL
        AND json_extract(PayloadJson, '$.Operation.Preferences') IS NULL
        AND json_extract(PayloadJson, '$.Operation.CredentialCheck') IS NULL
        AND json_extract(PayloadJson, '$.Operation.SandboxCommand') IS NULL
        AND json_extract(PayloadJson, '$.Operation.SandboxControl') IS NULL
        AND json_extract(PayloadJson, '$.Operation.SandboxRead') IS NULL
        AND json_extract(PayloadJson, '$.Operation.BrowserCommand') IS NULL
        AND json_extract(PayloadJson, '$.Operation.CredentialRead') IS NULL
        AND json_extract(PayloadJson, '$.Operation.ArtifactPublication') IS NULL
        AND json_extract(PayloadJson, '$.Operation.ArtifactVerification') IS NULL
        AND json_extract(PayloadJson, '$.Operation.WorkspaceRead') IS NULL
        AND json_extract(PayloadJson, '$.Operation.WorkspaceMutation') IS NULL
        AND json_extract(PayloadJson, '$.Operation.AttachmentRead') IS NULL
        AND json_extract(PayloadJson, '$.Operation.BackgroundStart') IS NULL
        AND json_extract(PayloadJson, '$.Operation.BackgroundReport') IS NULL
        AND json_extract(PayloadJson, '$.Operation.CompletionRead') IS NULL),0)) AND json_extract(PayloadJson, '$.Operation.AccountRead') IS NULL) OR COALESCE((Kind IN (0,1) AND Status IN (0,1,2,3,4) AND Revision >= 1 AND ExpiresAt > CreatedAt AND length(Fingerprint) = 64
        AND Permission = 'credentials.use' AND ResourceType = 'credential' AND CredentialId IS NOT NULL AND ResourceId IS CredentialId
        AND SandboxProfileId IS NULL AND SandboxProfileRevision IS NULL
        AND json_type(PayloadJson, '$.Operation.AccountRead') IS 'object'
        AND json_extract(PayloadJson, '$.Operation.AccountRead.SessionId') IS SessionId
        AND json_extract(PayloadJson, '$.Operation.AccountRead.Credential.CredentialId') IS CredentialId
        AND json_type(PayloadJson, '$.Operation.AccountRead.Credential.BindingId') IS 'text'
        AND length(json_extract(PayloadJson, '$.Operation.AccountRead.Credential.BindingId')) = 36
        AND json_type(PayloadJson, '$.Operation.AccountRead.Credential.Reference') IS 'text'
        AND length(json_extract(PayloadJson, '$.Operation.AccountRead.Credential.Reference')) BETWEEN 1 AND 120
        AND json_type(PayloadJson, '$.Operation.AccountRead.Credential.BindingRevision') IS 'integer' AND json_extract(PayloadJson, '$.Operation.AccountRead.Credential.BindingRevision') >= 1
        AND json_type(PayloadJson, '$.Operation.AccountRead.Credential.CredentialRevision') IS 'integer' AND json_extract(PayloadJson, '$.Operation.AccountRead.Credential.CredentialRevision') >= 1
        AND json_type(PayloadJson, '$.Operation.AccountRead.Credential.SecretRevision') IS 'integer' AND json_extract(PayloadJson, '$.Operation.AccountRead.Credential.SecretRevision') >= CASE WHEN json_extract(PayloadJson, '$.Operation.AccountRead.Purpose') = 1 THEN 0 ELSE 1 END
        AND json_type(PayloadJson, '$.Operation.AccountRead.Credential.AuthEpoch') IS 'integer' AND json_extract(PayloadJson, '$.Operation.AccountRead.Credential.AuthEpoch') >= CASE WHEN json_extract(PayloadJson, '$.Operation.AccountRead.Purpose') = 1 THEN 0 ELSE 1 END
        AND json_type(PayloadJson, '$.Operation.AccountRead.ArgumentsJson') IS 'text' AND length(CAST(json_extract(PayloadJson, '$.Operation.AccountRead.ArgumentsJson') AS BLOB)) <= 8192
        AND json_valid(json_extract(PayloadJson, '$.Operation.AccountRead.ArgumentsJson'))
        AND json_type(json_extract(PayloadJson, '$.Operation.AccountRead.ArgumentsJson')) IS 'object'
        AND json_extract(json_extract(PayloadJson, '$.Operation.AccountRead.ArgumentsJson'), '$.accountRef') IS json_extract(PayloadJson, '$.Operation.AccountRead.Credential.Reference')
        AND ((json_extract(PayloadJson, '$.Operation.AccountRead.Purpose') = 1 AND json_extract(PayloadJson, '$.Operation.AccountRead.ToolName') IN ('email.search','email.read'))
          OR (json_extract(PayloadJson, '$.Operation.AccountRead.Purpose') = 2 AND json_extract(PayloadJson, '$.Operation.AccountRead.ToolName') IN ('drive.list','drive.search','drive.get')))
        AND length(CAST(PayloadJson AS BLOB)) <= 131072
        AND json_extract(PayloadJson, '$.Operation.Persona') IS NULL
        AND json_extract(PayloadJson, '$.Operation.Preferences') IS NULL
        AND json_extract(PayloadJson, '$.Operation.CredentialCheck') IS NULL
        AND json_extract(PayloadJson, '$.Operation.SandboxCommand') IS NULL
        AND json_extract(PayloadJson, '$.Operation.SandboxControl') IS NULL
        AND json_extract(PayloadJson, '$.Operation.SandboxRead') IS NULL
        AND json_extract(PayloadJson, '$.Operation.BrowserCommand') IS NULL
        AND json_extract(PayloadJson, '$.Operation.CredentialRead') IS NULL
        AND json_extract(PayloadJson, '$.Operation.ArtifactPublication') IS NULL
        AND json_extract(PayloadJson, '$.Operation.ArtifactVerification') IS NULL
        AND json_extract(PayloadJson, '$.Operation.WorkspaceRead') IS NULL
        AND json_extract(PayloadJson, '$.Operation.WorkspaceMutation') IS NULL
        AND json_extract(PayloadJson, '$.Operation.AttachmentRead') IS NULL
        AND json_extract(PayloadJson, '$.Operation.BackgroundStart') IS NULL
        AND json_extract(PayloadJson, '$.Operation.BackgroundReport') IS NULL
        AND json_extract(PayloadJson, '$.Operation.CompletionRead') IS NULL
        AND json_extract(PayloadJson, '$.Operation.ReportSourceRead') IS NULL),0)) AND json_extract(PayloadJson, '$.Operation.AccountAction') IS NULL) OR COALESCE((Kind IN (0,1) AND Status IN (0,1,2,3,4) AND Revision >= 1 AND ExpiresAt > CreatedAt AND length(Fingerprint) = 64
        AND CredentialId IS NOT NULL
        AND json_extract(PayloadJson, '$.Operation.AccountAction.Permission') IS Permission
        AND ((Permission IN ('email.draft','email.send','drive.write','drive.delete') AND ResourceType = 'credential' AND ResourceId IS CredentialId)
          OR (Permission = 'workspaces.write' AND ResourceType = 'workspace' AND ResourceId IS CASE
            WHEN substr(json_extract(json_extract(PayloadJson, '$.Operation.AccountAction.ArgumentsJson'),'$.path'),1,9) = '/working/' THEN 'working:' || SessionId ELSE 'home:' || InstanceId END))
        AND SandboxProfileId IS NULL AND SandboxProfileRevision IS NULL
        AND json_type(PayloadJson, '$.Operation.AccountAction') IS 'object'
        AND json_extract(PayloadJson, '$.Operation.AccountAction.SessionId') IS SessionId
        AND json_extract(PayloadJson, '$.Operation.AccountAction.Credential.CredentialId') IS CredentialId
        AND json_type(PayloadJson, '$.Operation.AccountAction.Credential.BindingId') IS 'text'
        AND length(json_extract(PayloadJson, '$.Operation.AccountAction.Credential.BindingId')) = 36
        AND json_type(PayloadJson, '$.Operation.AccountAction.Credential.Reference') IS 'text'
        AND length(json_extract(PayloadJson, '$.Operation.AccountAction.Credential.Reference')) BETWEEN 1 AND 120
        AND json_type(PayloadJson, '$.Operation.AccountAction.Credential.BindingRevision') IS 'integer' AND json_extract(PayloadJson, '$.Operation.AccountAction.Credential.BindingRevision') >= 1
        AND json_type(PayloadJson, '$.Operation.AccountAction.Credential.CredentialRevision') IS 'integer' AND json_extract(PayloadJson, '$.Operation.AccountAction.Credential.CredentialRevision') >= 1
        AND json_type(PayloadJson, '$.Operation.AccountAction.Credential.SecretRevision') IS 'integer' AND json_extract(PayloadJson, '$.Operation.AccountAction.Credential.SecretRevision') >= CASE WHEN json_extract(PayloadJson, '$.Operation.AccountAction.Purpose') = 1 THEN 0 ELSE 1 END
        AND json_type(PayloadJson, '$.Operation.AccountAction.Credential.AuthEpoch') IS 'integer' AND json_extract(PayloadJson, '$.Operation.AccountAction.Credential.AuthEpoch') >= CASE WHEN json_extract(PayloadJson, '$.Operation.AccountAction.Purpose') = 1 THEN 0 ELSE 1 END
        AND json_type(PayloadJson, '$.Operation.AccountAction.ArgumentsJson') IS 'text' AND length(CAST(json_extract(PayloadJson, '$.Operation.AccountAction.ArgumentsJson') AS BLOB)) <= 49152
        AND json_valid(json_extract(PayloadJson, '$.Operation.AccountAction.ArgumentsJson'))
        AND json_type(json_extract(PayloadJson, '$.Operation.AccountAction.ArgumentsJson')) IS 'object'
        AND json_extract(json_extract(PayloadJson, '$.Operation.AccountAction.ArgumentsJson'), '$.accountRef') IS json_extract(PayloadJson, '$.Operation.AccountAction.Credential.Reference')
        AND ((json_extract(PayloadJson, '$.Operation.AccountAction.Purpose') = 1
            AND ((Permission = 'email.draft' AND json_extract(PayloadJson, '$.Operation.AccountAction.ToolName') = 'email.create_draft')
              OR (Permission = 'email.send' AND json_extract(PayloadJson, '$.Operation.AccountAction.ToolName') = 'email.send'))
            AND json_type(PayloadJson, '$.Operation.AccountAction.Draft') IS 'object'
            AND json_type(PayloadJson, '$.Operation.AccountAction.Draft.DraftId') IS 'text' AND length(json_extract(PayloadJson, '$.Operation.AccountAction.Draft.DraftId')) BETWEEN 1 AND 200
            AND json_type(PayloadJson, '$.Operation.AccountAction.Draft.RecipientsJson') IS 'text' AND json_valid(json_extract(PayloadJson, '$.Operation.AccountAction.Draft.RecipientsJson'))
            AND json_type(PayloadJson, '$.Operation.AccountAction.Draft.Subject') IS 'text' AND length(json_extract(PayloadJson, '$.Operation.AccountAction.Draft.Subject')) <= 500
            AND json_type(PayloadJson, '$.Operation.AccountAction.Draft.Body') IS 'text' AND length(json_extract(PayloadJson, '$.Operation.AccountAction.Draft.Body')) <= 32000
            AND json_type(PayloadJson, '$.Operation.AccountAction.Draft.Sender') IS 'text' AND length(json_extract(PayloadJson, '$.Operation.AccountAction.Draft.Sender')) BETWEEN 3 AND 320
            AND json_extract(PayloadJson, '$.Operation.AccountAction.Draft.Revision') >= 1
            AND json_extract(PayloadJson, '$.Operation.AccountAction.RemoteSourcesJson') = '[]'
            AND json_extract(PayloadJson, '$.Operation.AccountAction.UploadSource') IS NULL AND json_extract(PayloadJson, '$.Operation.AccountAction.DestinationObservationHash') IS NULL)
          OR (json_extract(PayloadJson, '$.Operation.AccountAction.Purpose') = 2
            AND json_extract(PayloadJson, '$.Operation.AccountAction.Draft') IS NULL
            AND ((Permission = 'drive.write' AND json_extract(PayloadJson, '$.Operation.AccountAction.ToolName') IN ('drive.create_folder','drive.upload','drive.rename','drive.move'))
              OR (Permission = 'drive.delete' AND json_extract(PayloadJson, '$.Operation.AccountAction.ToolName') IN ('drive.trash','drive.trash_batch'))
              OR (Permission = 'workspaces.write' AND json_extract(PayloadJson, '$.Operation.AccountAction.ToolName') = 'drive.download'))))
        AND json_type(PayloadJson, '$.Operation.AccountAction.RemoteSourcesJson') IS 'text'
        AND length(CAST(json_extract(PayloadJson, '$.Operation.AccountAction.RemoteSourcesJson') AS BLOB)) <= 32768
        AND json_valid(json_extract(PayloadJson, '$.Operation.AccountAction.RemoteSourcesJson'))
        AND json_type(json_extract(PayloadJson, '$.Operation.AccountAction.RemoteSourcesJson')) IS 'array'
        AND json_array_length(json_extract(PayloadJson, '$.Operation.AccountAction.RemoteSourcesJson')) <= 21
        AND (json_extract(PayloadJson, '$.Operation.AccountAction.ToolName') <> 'drive.upload' OR json_type(PayloadJson, '$.Operation.AccountAction.UploadSource') IS 'object')
        AND (json_extract(PayloadJson, '$.Operation.AccountAction.ToolName') <> 'drive.download' OR (json_type(PayloadJson, '$.Operation.AccountAction.DestinationObservationHash') IS 'text'
            AND length(json_extract(PayloadJson, '$.Operation.AccountAction.DestinationObservationHash')) = 64 AND json_extract(PayloadJson, '$.Operation.AccountAction.DestinationObservationHash') NOT GLOB '*[^0-9a-f]*'))
        AND length(CAST(PayloadJson AS BLOB)) <= 131072
        AND json_extract(PayloadJson, '$.Operation.Persona') IS NULL
        AND json_extract(PayloadJson, '$.Operation.Preferences') IS NULL
        AND json_extract(PayloadJson, '$.Operation.CredentialCheck') IS NULL
        AND json_extract(PayloadJson, '$.Operation.SandboxCommand') IS NULL
        AND json_extract(PayloadJson, '$.Operation.SandboxControl') IS NULL
        AND json_extract(PayloadJson, '$.Operation.SandboxRead') IS NULL
        AND json_extract(PayloadJson, '$.Operation.BrowserCommand') IS NULL
        AND json_extract(PayloadJson, '$.Operation.CredentialRead') IS NULL
        AND json_extract(PayloadJson, '$.Operation.ArtifactPublication') IS NULL
        AND json_extract(PayloadJson, '$.Operation.ArtifactVerification') IS NULL
        AND json_extract(PayloadJson, '$.Operation.WorkspaceRead') IS NULL
        AND json_extract(PayloadJson, '$.Operation.WorkspaceMutation') IS NULL
        AND json_extract(PayloadJson, '$.Operation.AttachmentRead') IS NULL
        AND json_extract(PayloadJson, '$.Operation.BackgroundStart') IS NULL
        AND json_extract(PayloadJson, '$.Operation.BackgroundReport') IS NULL
        AND json_extract(PayloadJson, '$.Operation.CompletionRead') IS NULL
        AND json_extract(PayloadJson, '$.Operation.ReportSourceRead') IS NULL
        AND json_extract(PayloadJson, '$.Operation.AccountRead') IS NULL),0))) OR COALESCE((Kind IN (0,1) AND Status IN (0,1,2,3,4) AND Revision >= 1 AND ExpiresAt > CreatedAt AND ExpiresAt - CreatedAt <= 86400000 AND length(Fingerprint) = 64
        AND RunId IS NULL AND SessionId IS NULL AND AutomationId IS NOT NULL
        AND CredentialId IS NULL AND SandboxProfileId IS NULL AND SandboxProfileRevision IS NULL
        AND Permission IN ('automations.run','sessions.create','agent-runs.create','sessions.read','agents.chat','webhooks.read','core-events.read','credentials.read','credentials.use','email.read')
        AND (Permission NOT IN ('sessions.read','agents.chat','webhooks.read','core-events.read','credentials.read','credentials.use','email.read') OR Kind = 0)
        AND ResourceType IS CASE Permission WHEN 'automations.run' THEN 'automation' WHEN 'sessions.read' THEN 'session' WHEN 'agents.chat' THEN 'agent' WHEN 'webhooks.read' THEN 'webhook' WHEN 'core-events.read' THEN 'event' WHEN 'credentials.read' THEN 'credential' WHEN 'credentials.use' THEN 'credential' WHEN 'email.read' THEN 'credential' ELSE 'tenant' END
        AND ResourceId IS CASE Permission WHEN 'automations.run' THEN AutomationId WHEN 'sessions.read' THEN json_extract(PayloadJson, '$.Operation.AutomationAdmission.SessionId') WHEN 'agents.chat' THEN InstanceId WHEN 'webhooks.read' THEN json_extract(PayloadJson, '$.Operation.AutomationAdmission.EventSource.ResourceId') WHEN 'core-events.read' THEN json_extract(PayloadJson, '$.Operation.AutomationAdmission.EventSource.EventIds[0]') WHEN 'credentials.read' THEN min(coalesce(json_extract(json_extract(PayloadJson, '$.Operation.AutomationAdmission.Occurrence.EvidenceJson'), '$.triggerContext.source.accountId'),'ffffffff-ffff-ffff-ffff-ffffffffffff'),coalesce(json_extract(json_extract(PayloadJson, '$.Operation.AutomationAdmission.Occurrence.EvidenceJson'), '$.triggerContext.sources[0].data.accountId'),'ffffffff-ffff-ffff-ffff-ffffffffffff'),coalesce(json_extract(json_extract(PayloadJson, '$.Operation.AutomationAdmission.Occurrence.EvidenceJson'), '$.triggerContext.sources[1].data.accountId'),'ffffffff-ffff-ffff-ffff-ffffffffffff'),coalesce(json_extract(json_extract(PayloadJson, '$.Operation.AutomationAdmission.Occurrence.EvidenceJson'), '$.triggerContext.sources[2].data.accountId'),'ffffffff-ffff-ffff-ffff-ffffffffffff'),coalesce(json_extract(json_extract(PayloadJson, '$.Operation.AutomationAdmission.Occurrence.EvidenceJson'), '$.triggerContext.sources[3].data.accountId'),'ffffffff-ffff-ffff-ffff-ffffffffffff'),coalesce(json_extract(json_extract(PayloadJson, '$.Operation.AutomationAdmission.Occurrence.EvidenceJson'), '$.triggerContext.sources[4].data.accountId'),'ffffffff-ffff-ffff-ffff-ffffffffffff'),coalesce(json_extract(json_extract(PayloadJson, '$.Operation.AutomationAdmission.Occurrence.EvidenceJson'), '$.triggerContext.sources[5].data.accountId'),'ffffffff-ffff-ffff-ffff-ffffffffffff'),coalesce(json_extract(json_extract(PayloadJson, '$.Operation.AutomationAdmission.Occurrence.EvidenceJson'), '$.triggerContext.sources[6].data.accountId'),'ffffffff-ffff-ffff-ffff-ffffffffffff'),coalesce(json_extract(json_extract(PayloadJson, '$.Operation.AutomationAdmission.Occurrence.EvidenceJson'), '$.triggerContext.sources[7].data.accountId'),'ffffffff-ffff-ffff-ffff-ffffffffffff'),coalesce(json_extract(json_extract(PayloadJson, '$.Operation.AutomationAdmission.Occurrence.EvidenceJson'), '$.triggerContext.sources[8].data.accountId'),'ffffffff-ffff-ffff-ffff-ffffffffffff'),coalesce(json_extract(json_extract(PayloadJson, '$.Operation.AutomationAdmission.Occurrence.EvidenceJson'), '$.triggerContext.sources[9].data.accountId'),'ffffffff-ffff-ffff-ffff-ffffffffffff'),coalesce(json_extract(json_extract(PayloadJson, '$.Operation.AutomationAdmission.Occurrence.EvidenceJson'), '$.triggerContext.sources[10].data.accountId'),'ffffffff-ffff-ffff-ffff-ffffffffffff'),coalesce(json_extract(json_extract(PayloadJson, '$.Operation.AutomationAdmission.Occurrence.EvidenceJson'), '$.triggerContext.sources[11].data.accountId'),'ffffffff-ffff-ffff-ffff-ffffffffffff'),coalesce(json_extract(json_extract(PayloadJson, '$.Operation.AutomationAdmission.Occurrence.EvidenceJson'), '$.triggerContext.sources[12].data.accountId'),'ffffffff-ffff-ffff-ffff-ffffffffffff'),coalesce(json_extract(json_extract(PayloadJson, '$.Operation.AutomationAdmission.Occurrence.EvidenceJson'), '$.triggerContext.sources[13].data.accountId'),'ffffffff-ffff-ffff-ffff-ffffffffffff'),coalesce(json_extract(json_extract(PayloadJson, '$.Operation.AutomationAdmission.Occurrence.EvidenceJson'), '$.triggerContext.sources[14].data.accountId'),'ffffffff-ffff-ffff-ffff-ffffffffffff'),coalesce(json_extract(json_extract(PayloadJson, '$.Operation.AutomationAdmission.Occurrence.EvidenceJson'), '$.triggerContext.sources[15].data.accountId'),'ffffffff-ffff-ffff-ffff-ffffffffffff'),coalesce(json_extract(json_extract(PayloadJson, '$.Operation.AutomationAdmission.Occurrence.EvidenceJson'), '$.triggerContext.sources[16].data.accountId'),'ffffffff-ffff-ffff-ffff-ffffffffffff'),coalesce(json_extract(json_extract(PayloadJson, '$.Operation.AutomationAdmission.Occurrence.EvidenceJson'), '$.triggerContext.sources[17].data.accountId'),'ffffffff-ffff-ffff-ffff-ffffffffffff'),coalesce(json_extract(json_extract(PayloadJson, '$.Operation.AutomationAdmission.Occurrence.EvidenceJson'), '$.triggerContext.sources[18].data.accountId'),'ffffffff-ffff-ffff-ffff-ffffffffffff'),coalesce(json_extract(json_extract(PayloadJson, '$.Operation.AutomationAdmission.Occurrence.EvidenceJson'), '$.triggerContext.sources[19].data.accountId'),'ffffffff-ffff-ffff-ffff-ffffffffffff'),coalesce(json_extract(json_extract(PayloadJson, '$.Operation.AutomationAdmission.Occurrence.EvidenceJson'), '$.triggerContext.sources[20].data.accountId'),'ffffffff-ffff-ffff-ffff-ffffffffffff'),coalesce(json_extract(json_extract(PayloadJson, '$.Operation.AutomationAdmission.Occurrence.EvidenceJson'), '$.triggerContext.sources[21].data.accountId'),'ffffffff-ffff-ffff-ffff-ffffffffffff'),coalesce(json_extract(json_extract(PayloadJson, '$.Operation.AutomationAdmission.Occurrence.EvidenceJson'), '$.triggerContext.sources[22].data.accountId'),'ffffffff-ffff-ffff-ffff-ffffffffffff'),coalesce(json_extract(json_extract(PayloadJson, '$.Operation.AutomationAdmission.Occurrence.EvidenceJson'), '$.triggerContext.sources[23].data.accountId'),'ffffffff-ffff-ffff-ffff-ffffffffffff')) WHEN 'credentials.use' THEN min(coalesce(json_extract(json_extract(PayloadJson, '$.Operation.AutomationAdmission.Occurrence.EvidenceJson'), '$.triggerContext.source.accountId'),'ffffffff-ffff-ffff-ffff-ffffffffffff'),coalesce(json_extract(json_extract(PayloadJson, '$.Operation.AutomationAdmission.Occurrence.EvidenceJson'), '$.triggerContext.sources[0].data.accountId'),'ffffffff-ffff-ffff-ffff-ffffffffffff'),coalesce(json_extract(json_extract(PayloadJson, '$.Operation.AutomationAdmission.Occurrence.EvidenceJson'), '$.triggerContext.sources[1].data.accountId'),'ffffffff-ffff-ffff-ffff-ffffffffffff'),coalesce(json_extract(json_extract(PayloadJson, '$.Operation.AutomationAdmission.Occurrence.EvidenceJson'), '$.triggerContext.sources[2].data.accountId'),'ffffffff-ffff-ffff-ffff-ffffffffffff'),coalesce(json_extract(json_extract(PayloadJson, '$.Operation.AutomationAdmission.Occurrence.EvidenceJson'), '$.triggerContext.sources[3].data.accountId'),'ffffffff-ffff-ffff-ffff-ffffffffffff'),coalesce(json_extract(json_extract(PayloadJson, '$.Operation.AutomationAdmission.Occurrence.EvidenceJson'), '$.triggerContext.sources[4].data.accountId'),'ffffffff-ffff-ffff-ffff-ffffffffffff'),coalesce(json_extract(json_extract(PayloadJson, '$.Operation.AutomationAdmission.Occurrence.EvidenceJson'), '$.triggerContext.sources[5].data.accountId'),'ffffffff-ffff-ffff-ffff-ffffffffffff'),coalesce(json_extract(json_extract(PayloadJson, '$.Operation.AutomationAdmission.Occurrence.EvidenceJson'), '$.triggerContext.sources[6].data.accountId'),'ffffffff-ffff-ffff-ffff-ffffffffffff'),coalesce(json_extract(json_extract(PayloadJson, '$.Operation.AutomationAdmission.Occurrence.EvidenceJson'), '$.triggerContext.sources[7].data.accountId'),'ffffffff-ffff-ffff-ffff-ffffffffffff'),coalesce(json_extract(json_extract(PayloadJson, '$.Operation.AutomationAdmission.Occurrence.EvidenceJson'), '$.triggerContext.sources[8].data.accountId'),'ffffffff-ffff-ffff-ffff-ffffffffffff'),coalesce(json_extract(json_extract(PayloadJson, '$.Operation.AutomationAdmission.Occurrence.EvidenceJson'), '$.triggerContext.sources[9].data.accountId'),'ffffffff-ffff-ffff-ffff-ffffffffffff'),coalesce(json_extract(json_extract(PayloadJson, '$.Operation.AutomationAdmission.Occurrence.EvidenceJson'), '$.triggerContext.sources[10].data.accountId'),'ffffffff-ffff-ffff-ffff-ffffffffffff'),coalesce(json_extract(json_extract(PayloadJson, '$.Operation.AutomationAdmission.Occurrence.EvidenceJson'), '$.triggerContext.sources[11].data.accountId'),'ffffffff-ffff-ffff-ffff-ffffffffffff'),coalesce(json_extract(json_extract(PayloadJson, '$.Operation.AutomationAdmission.Occurrence.EvidenceJson'), '$.triggerContext.sources[12].data.accountId'),'ffffffff-ffff-ffff-ffff-ffffffffffff'),coalesce(json_extract(json_extract(PayloadJson, '$.Operation.AutomationAdmission.Occurrence.EvidenceJson'), '$.triggerContext.sources[13].data.accountId'),'ffffffff-ffff-ffff-ffff-ffffffffffff'),coalesce(json_extract(json_extract(PayloadJson, '$.Operation.AutomationAdmission.Occurrence.EvidenceJson'), '$.triggerContext.sources[14].data.accountId'),'ffffffff-ffff-ffff-ffff-ffffffffffff'),coalesce(json_extract(json_extract(PayloadJson, '$.Operation.AutomationAdmission.Occurrence.EvidenceJson'), '$.triggerContext.sources[15].data.accountId'),'ffffffff-ffff-ffff-ffff-ffffffffffff'),coalesce(json_extract(json_extract(PayloadJson, '$.Operation.AutomationAdmission.Occurrence.EvidenceJson'), '$.triggerContext.sources[16].data.accountId'),'ffffffff-ffff-ffff-ffff-ffffffffffff'),coalesce(json_extract(json_extract(PayloadJson, '$.Operation.AutomationAdmission.Occurrence.EvidenceJson'), '$.triggerContext.sources[17].data.accountId'),'ffffffff-ffff-ffff-ffff-ffffffffffff'),coalesce(json_extract(json_extract(PayloadJson, '$.Operation.AutomationAdmission.Occurrence.EvidenceJson'), '$.triggerContext.sources[18].data.accountId'),'ffffffff-ffff-ffff-ffff-ffffffffffff'),coalesce(json_extract(json_extract(PayloadJson, '$.Operation.AutomationAdmission.Occurrence.EvidenceJson'), '$.triggerContext.sources[19].data.accountId'),'ffffffff-ffff-ffff-ffff-ffffffffffff'),coalesce(json_extract(json_extract(PayloadJson, '$.Operation.AutomationAdmission.Occurrence.EvidenceJson'), '$.triggerContext.sources[20].data.accountId'),'ffffffff-ffff-ffff-ffff-ffffffffffff'),coalesce(json_extract(json_extract(PayloadJson, '$.Operation.AutomationAdmission.Occurrence.EvidenceJson'), '$.triggerContext.sources[21].data.accountId'),'ffffffff-ffff-ffff-ffff-ffffffffffff'),coalesce(json_extract(json_extract(PayloadJson, '$.Operation.AutomationAdmission.Occurrence.EvidenceJson'), '$.triggerContext.sources[22].data.accountId'),'ffffffff-ffff-ffff-ffff-ffffffffffff'),coalesce(json_extract(json_extract(PayloadJson, '$.Operation.AutomationAdmission.Occurrence.EvidenceJson'), '$.triggerContext.sources[23].data.accountId'),'ffffffff-ffff-ffff-ffff-ffffffffffff')) WHEN 'email.read' THEN min(coalesce(json_extract(json_extract(PayloadJson, '$.Operation.AutomationAdmission.Occurrence.EvidenceJson'), '$.triggerContext.source.accountId'),'ffffffff-ffff-ffff-ffff-ffffffffffff'),coalesce(json_extract(json_extract(PayloadJson, '$.Operation.AutomationAdmission.Occurrence.EvidenceJson'), '$.triggerContext.sources[0].data.accountId'),'ffffffff-ffff-ffff-ffff-ffffffffffff'),coalesce(json_extract(json_extract(PayloadJson, '$.Operation.AutomationAdmission.Occurrence.EvidenceJson'), '$.triggerContext.sources[1].data.accountId'),'ffffffff-ffff-ffff-ffff-ffffffffffff'),coalesce(json_extract(json_extract(PayloadJson, '$.Operation.AutomationAdmission.Occurrence.EvidenceJson'), '$.triggerContext.sources[2].data.accountId'),'ffffffff-ffff-ffff-ffff-ffffffffffff'),coalesce(json_extract(json_extract(PayloadJson, '$.Operation.AutomationAdmission.Occurrence.EvidenceJson'), '$.triggerContext.sources[3].data.accountId'),'ffffffff-ffff-ffff-ffff-ffffffffffff'),coalesce(json_extract(json_extract(PayloadJson, '$.Operation.AutomationAdmission.Occurrence.EvidenceJson'), '$.triggerContext.sources[4].data.accountId'),'ffffffff-ffff-ffff-ffff-ffffffffffff'),coalesce(json_extract(json_extract(PayloadJson, '$.Operation.AutomationAdmission.Occurrence.EvidenceJson'), '$.triggerContext.sources[5].data.accountId'),'ffffffff-ffff-ffff-ffff-ffffffffffff'),coalesce(json_extract(json_extract(PayloadJson, '$.Operation.AutomationAdmission.Occurrence.EvidenceJson'), '$.triggerContext.sources[6].data.accountId'),'ffffffff-ffff-ffff-ffff-ffffffffffff'),coalesce(json_extract(json_extract(PayloadJson, '$.Operation.AutomationAdmission.Occurrence.EvidenceJson'), '$.triggerContext.sources[7].data.accountId'),'ffffffff-ffff-ffff-ffff-ffffffffffff'),coalesce(json_extract(json_extract(PayloadJson, '$.Operation.AutomationAdmission.Occurrence.EvidenceJson'), '$.triggerContext.sources[8].data.accountId'),'ffffffff-ffff-ffff-ffff-ffffffffffff'),coalesce(json_extract(json_extract(PayloadJson, '$.Operation.AutomationAdmission.Occurrence.EvidenceJson'), '$.triggerContext.sources[9].data.accountId'),'ffffffff-ffff-ffff-ffff-ffffffffffff'),coalesce(json_extract(json_extract(PayloadJson, '$.Operation.AutomationAdmission.Occurrence.EvidenceJson'), '$.triggerContext.sources[10].data.accountId'),'ffffffff-ffff-ffff-ffff-ffffffffffff'),coalesce(json_extract(json_extract(PayloadJson, '$.Operation.AutomationAdmission.Occurrence.EvidenceJson'), '$.triggerContext.sources[11].data.accountId'),'ffffffff-ffff-ffff-ffff-ffffffffffff'),coalesce(json_extract(json_extract(PayloadJson, '$.Operation.AutomationAdmission.Occurrence.EvidenceJson'), '$.triggerContext.sources[12].data.accountId'),'ffffffff-ffff-ffff-ffff-ffffffffffff'),coalesce(json_extract(json_extract(PayloadJson, '$.Operation.AutomationAdmission.Occurrence.EvidenceJson'), '$.triggerContext.sources[13].data.accountId'),'ffffffff-ffff-ffff-ffff-ffffffffffff'),coalesce(json_extract(json_extract(PayloadJson, '$.Operation.AutomationAdmission.Occurrence.EvidenceJson'), '$.triggerContext.sources[14].data.accountId'),'ffffffff-ffff-ffff-ffff-ffffffffffff'),coalesce(json_extract(json_extract(PayloadJson, '$.Operation.AutomationAdmission.Occurrence.EvidenceJson'), '$.triggerContext.sources[15].data.accountId'),'ffffffff-ffff-ffff-ffff-ffffffffffff'),coalesce(json_extract(json_extract(PayloadJson, '$.Operation.AutomationAdmission.Occurrence.EvidenceJson'), '$.triggerContext.sources[16].data.accountId'),'ffffffff-ffff-ffff-ffff-ffffffffffff'),coalesce(json_extract(json_extract(PayloadJson, '$.Operation.AutomationAdmission.Occurrence.EvidenceJson'), '$.triggerContext.sources[17].data.accountId'),'ffffffff-ffff-ffff-ffff-ffffffffffff'),coalesce(json_extract(json_extract(PayloadJson, '$.Operation.AutomationAdmission.Occurrence.EvidenceJson'), '$.triggerContext.sources[18].data.accountId'),'ffffffff-ffff-ffff-ffff-ffffffffffff'),coalesce(json_extract(json_extract(PayloadJson, '$.Operation.AutomationAdmission.Occurrence.EvidenceJson'), '$.triggerContext.sources[19].data.accountId'),'ffffffff-ffff-ffff-ffff-ffffffffffff'),coalesce(json_extract(json_extract(PayloadJson, '$.Operation.AutomationAdmission.Occurrence.EvidenceJson'), '$.triggerContext.sources[20].data.accountId'),'ffffffff-ffff-ffff-ffff-ffffffffffff'),coalesce(json_extract(json_extract(PayloadJson, '$.Operation.AutomationAdmission.Occurrence.EvidenceJson'), '$.triggerContext.sources[21].data.accountId'),'ffffffff-ffff-ffff-ffff-ffffffffffff'),coalesce(json_extract(json_extract(PayloadJson, '$.Operation.AutomationAdmission.Occurrence.EvidenceJson'), '$.triggerContext.sources[22].data.accountId'),'ffffffff-ffff-ffff-ffff-ffffffffffff'),coalesce(json_extract(json_extract(PayloadJson, '$.Operation.AutomationAdmission.Occurrence.EvidenceJson'), '$.triggerContext.sources[23].data.accountId'),'ffffffff-ffff-ffff-ffff-ffffffffffff')) ELSE lower(TenantId) END
        AND json_type(PayloadJson, '$.Operation.AutomationAdmission') IS 'object'
        AND ((json_extract(PayloadJson, '$.Operation.AutomationAdmission.Occurrence.ExecutionTarget.Kind') = 0
                  AND json_extract(PayloadJson, '$.Operation.AutomationAdmission.Occurrence.ExecutionTarget.SessionId') IS NULL
                  AND json_type(PayloadJson, '$.Operation.AutomationAdmission.Current.TargetSessionId') IS NULL
                  AND json_type(PayloadJson, '$.Operation.AutomationAdmission.Next.TargetSessionId') IS NULL
                  AND json_extract(PayloadJson, '$.Operation.AutomationAdmission.Conversation') IS NULL
                  AND json_extract(PayloadJson, '$.Operation.AutomationAdmission.Current.ReportToSessionId') IS json_extract(PayloadJson, '$.Operation.AutomationAdmission.Next.ReportToSessionId')
                  AND json_extract(PayloadJson, '$.Operation.AutomationAdmission.Current.ReportToSessionId') IS json_extract(PayloadJson, '$.Operation.AutomationAdmission.Occurrence.CompletionDelivery.SessionId')
                  AND ((json_extract(PayloadJson, '$.Operation.AutomationAdmission.Current.ReportToSessionId') IS NULL
                    AND json_type(PayloadJson, '$.Operation.AutomationAdmission.Current.ReportToSessionId') IS NULL
                    AND json_type(PayloadJson, '$.Operation.AutomationAdmission.Next.ReportToSessionId') IS NULL
                    AND json_extract(PayloadJson, '$.Operation.AutomationAdmission.Current.SchemaVersion') IS NOT 3
                    AND json_extract(PayloadJson, '$.Operation.AutomationAdmission.Next.SchemaVersion') IS NOT 3)
                  OR (json_type(PayloadJson, '$.Operation.AutomationAdmission.Current.ReportToSessionId') IS 'text'
                    AND length(json_extract(PayloadJson, '$.Operation.AutomationAdmission.Current.ReportToSessionId')) = 36
                    AND json_extract(PayloadJson, '$.Operation.AutomationAdmission.Current.ReportToSessionId') NOT GLOB '*[^0-9a-f-]*'
                    AND json_extract(PayloadJson, '$.Operation.AutomationAdmission.Current.ReportToSessionId') <> '00000000-0000-0000-0000-000000000000'
                    AND substr(json_extract(PayloadJson, '$.Operation.AutomationAdmission.Current.ReportToSessionId'),9,1) = '-'
                    AND substr(json_extract(PayloadJson, '$.Operation.AutomationAdmission.Current.ReportToSessionId'),14,1) = '-'
                    AND substr(json_extract(PayloadJson, '$.Operation.AutomationAdmission.Current.ReportToSessionId'),19,1) = '-'
                    AND substr(json_extract(PayloadJson, '$.Operation.AutomationAdmission.Current.ReportToSessionId'),24,1) = '-'
                    AND json_extract(PayloadJson, '$.Operation.AutomationAdmission.Current.SchemaVersion') = 3
                    AND json_extract(PayloadJson, '$.Operation.AutomationAdmission.Next.SchemaVersion') = 3)))
                OR (json_extract(PayloadJson, '$.Operation.AutomationAdmission.Occurrence.ExecutionTarget.Kind') = 1
                  AND json_extract(PayloadJson, '$.Operation.AutomationAdmission.Occurrence.ExecutionTarget.SessionId') IS json_extract(PayloadJson, '$.Operation.AutomationAdmission.SessionId')
                  AND json_extract(PayloadJson, '$.Operation.AutomationAdmission.Current.TargetSessionId') IS json_extract(PayloadJson, '$.Operation.AutomationAdmission.SessionId')
                  AND json_extract(PayloadJson, '$.Operation.AutomationAdmission.Next.TargetSessionId') IS json_extract(PayloadJson, '$.Operation.AutomationAdmission.SessionId')
                  AND json_extract(PayloadJson, '$.Operation.AutomationAdmission.Current.SchemaVersion') = 4
                  AND json_extract(PayloadJson, '$.Operation.AutomationAdmission.Next.SchemaVersion') = 4
                  AND json_type(PayloadJson, '$.Operation.AutomationAdmission.Current.ReportToSessionId') IS NULL
                  AND json_type(PayloadJson, '$.Operation.AutomationAdmission.Next.ReportToSessionId') IS NULL
                  AND json_extract(PayloadJson, '$.Operation.AutomationAdmission.Occurrence.CompletionDelivery.SessionId') IS NULL
                  AND json_type(PayloadJson, '$.Operation.AutomationAdmission.Conversation') IS 'object'
                  AND json_extract(PayloadJson, '$.Operation.AutomationAdmission.Conversation.SessionRevision') >= 1
                  AND json_extract(PayloadJson, '$.Operation.AutomationAdmission.Conversation.InputBoundarySequence') >= 0))
        AND json_extract(PayloadJson, '$.Operation.AutomationAdmission.TenantId') IS lower(TenantId)
        AND json_extract(PayloadJson, '$.Operation.AutomationAdmission.Permission') IS Permission
        AND json_extract(PayloadJson, '$.Operation.AutomationAdmission.RequestId') IS lower(RequestId)
        AND json_extract(PayloadJson, '$.Operation.AutomationAdmission.Current.AutomationId') IS AutomationId
        AND json_extract(PayloadJson, '$.Operation.AutomationAdmission.Next.AutomationId') IS AutomationId
        AND json_extract(PayloadJson, '$.Operation.AutomationAdmission.Current.Status') = 0
        AND json_extract(PayloadJson, '$.Operation.AutomationAdmission.Next.Status') IN (0,1)
        AND json_extract(PayloadJson, '$.Operation.AutomationAdmission.Next.Revision') = json_extract(PayloadJson, '$.Operation.AutomationAdmission.Current.Revision') + 1
        AND json_extract(PayloadJson, '$.Operation.AutomationAdmission.Current.Owner.TenantId') IS lower(TenantId)
        AND json_extract(PayloadJson, '$.Operation.AutomationAdmission.Current.Owner.AgentInstanceId') IS InstanceId
        AND json_extract(PayloadJson, '$.Operation.AutomationAdmission.Current.Owner.ExecutorPrincipalId') IS lower(ExecutorPrincipalId)
        AND json_extract(PayloadJson, '$.Operation.AutomationAdmission.Current.Owner.profileId') IS NULL
        AND json_extract(PayloadJson, '$.Operation.AutomationAdmission.Next.Owner') IS json_extract(PayloadJson, '$.Operation.AutomationAdmission.Current.Owner')
        AND json_extract(PayloadJson, '$.Operation.AutomationAdmission.Occurrence.Owner') IS json_extract(PayloadJson, '$.Operation.AutomationAdmission.Current.Owner')
        AND json_extract(PayloadJson, '$.Operation.AutomationAdmission.Occurrence.AutomationId') IS AutomationId
        AND json_extract(PayloadJson, '$.Operation.AutomationAdmission.Occurrence.SourceKind') IN (0,1,2,3)
        AND json_extract(PayloadJson, '$.Operation.AutomationAdmission.Occurrence.Disposition') = 0
        AND json_extract(PayloadJson, '$.Operation.AutomationAdmission.Occurrence.RoutingRevision') = 0
        AND json_extract(PayloadJson, '$.Operation.AutomationAdmission.Occurrence.ExecutionSessionId') IS NULL
        AND json_extract(PayloadJson, '$.Operation.AutomationAdmission.Occurrence.AcceptedAgentRunId') IS NULL
        AND ((json_extract(PayloadJson, '$.Operation.AutomationAdmission.Occurrence.SourceKind') IN (0,1,3) AND OriginalInitiatorPrincipalId IS NULL)
          OR (json_extract(PayloadJson, '$.Operation.AutomationAdmission.Occurrence.SourceKind') = 2 AND OriginalInitiatorPrincipalId IS NOT NULL))
        AND json_extract(PayloadJson, '$.Operation.AutomationAdmission.ManualInitiatorPrincipalId') IS lower(OriginalInitiatorPrincipalId)
        AND json_extract(PayloadJson, '$.Operation.AutomationAdmission.RunId') IS json_extract(PayloadJson, '$.RunId')
        AND json_extract(PayloadJson, '$.Operation.AutomationAdmission.SessionId') IS json_extract(PayloadJson, '$.SessionId')
        AND json_extract(PayloadJson, '$.Operation.AutomationAdmission.ActivationId') IS RootActivationId
        AND json_type(PayloadJson, '$.Operation.AutomationAdmission.ConfigurationHash') IS 'text'
        AND length(json_extract(PayloadJson, '$.Operation.AutomationAdmission.ConfigurationHash')) = 64
        AND json_extract(PayloadJson, '$.Operation.AutomationAdmission.ConfigurationHash') NOT GLOB '*[^0-9a-f]*'
        AND json_type(PayloadJson, '$.Operation.AutomationAdmission.Model') IS 'object'
        AND length(CAST(PayloadJson AS BLOB)) <= CASE WHEN json_type(PayloadJson, '$.Operation.AutomationAdmission.EventSource') IS 'object' THEN 1048576 ELSE 262144 END AND ((json_extract(PayloadJson, '$.Operation.AutomationAdmission.Occurrence.SourceKind') IN (0,2)
          AND json_extract(PayloadJson, '$.Operation.AutomationAdmission.EventSource') IS NULL
          AND Permission NOT IN ('webhooks.read','core-events.read','credentials.read','credentials.use','email.read'))
        OR (json_extract(PayloadJson, '$.Operation.AutomationAdmission.Occurrence.SourceKind') IN (1,3)
          AND json_type(PayloadJson, '$.Operation.AutomationAdmission.EventSource') IS 'object'
          AND json_extract(PayloadJson, '$.Operation.AutomationAdmission.EventSource.TenantId') IS lower(TenantId)
          AND json_extract(PayloadJson, '$.Operation.AutomationAdmission.EventSource.SourceKind') = json_extract(PayloadJson, '$.Operation.AutomationAdmission.Occurrence.SourceKind')
          AND json_extract(PayloadJson, '$.Operation.AutomationAdmission.EventSource.TriggerId') IS json_extract(PayloadJson, '$.Operation.AutomationAdmission.Occurrence.TriggerId')
          AND json_extract(PayloadJson, '$.Operation.AutomationAdmission.EventSource.SourceId') IS json_extract(PayloadJson, '$.Operation.AutomationAdmission.Occurrence.SourceEventId')
          AND json_extract(PayloadJson, '$.Operation.AutomationAdmission.EventSource.DedupeKey') IS json_extract(PayloadJson, '$.Operation.AutomationAdmission.Occurrence.DedupeKey')
          AND json_type(PayloadJson, '$.Operation.AutomationAdmission.EventSource.IsBucket') IN ('true','false')
          AND json_type(PayloadJson, '$.Operation.AutomationAdmission.EventSource.EventIds') IS 'array'
          AND json_array_length(PayloadJson, '$.Operation.AutomationAdmission.EventSource.EventIds') BETWEEN 1 AND 24
          AND (json_extract(PayloadJson, '$.Operation.AutomationAdmission.EventSource.IsBucket') = 1
            OR (json_array_length(PayloadJson, '$.Operation.AutomationAdmission.EventSource.EventIds') = 1
                AND json_extract(PayloadJson, '$.Operation.AutomationAdmission.EventSource.EventIds[0]') IS json_extract(PayloadJson, '$.Operation.AutomationAdmission.EventSource.SourceId')))
          AND length(json_extract(PayloadJson, '$.Operation.AutomationAdmission.EventSource.SourceHash')) = 64
          AND json_extract(PayloadJson, '$.Operation.AutomationAdmission.EventSource.SourceHash') NOT GLOB '*[^0-9a-f]*'
          AND json_extract(PayloadJson, '$.Operation.AutomationAdmission.Current.SchemaVersion') IN (2,3,4)
          AND json_extract(PayloadJson, '$.Operation.AutomationAdmission.Next.SchemaVersion') IN (2,3,4)
          AND ((json_extract(PayloadJson, '$.Operation.AutomationAdmission.EventSource.SourceKind') = 1
            AND json_extract(PayloadJson, '$.Operation.AutomationAdmission.EventSource.ResourceRevision') >= 1
            AND length(json_extract(PayloadJson, '$.Operation.AutomationAdmission.EventSource.ResourceId')) = 36
            AND Permission NOT IN ('core-events.read','credentials.read','credentials.use','email.read'))
           OR (json_extract(PayloadJson, '$.Operation.AutomationAdmission.EventSource.SourceKind') = 3
            AND json_extract(PayloadJson, '$.Operation.AutomationAdmission.EventSource.ResourceRevision') IS NULL
            AND json_extract(PayloadJson, '$.Operation.AutomationAdmission.EventSource.ResourceId') IS NULL
            AND Permission <> 'webhooks.read')))) AND json_type(PayloadJson, '$.Operation.AutomationAdmission.RequestId') IS 'text' AND length(json_extract(PayloadJson, '$.Operation.AutomationAdmission.RequestId')) = 36 AND json_extract(PayloadJson, '$.Operation.AutomationAdmission.RequestId') NOT GLOB '*[^0-9a-f-]*' AND json_extract(PayloadJson, '$.Operation.AutomationAdmission.RequestId') IS NOT '00000000-0000-0000-0000-000000000000' AND substr(json_extract(PayloadJson, '$.Operation.AutomationAdmission.RequestId'),9,1) IS '-' AND substr(json_extract(PayloadJson, '$.Operation.AutomationAdmission.RequestId'),14,1) IS '-' AND substr(json_extract(PayloadJson, '$.Operation.AutomationAdmission.RequestId'),19,1) IS '-' AND substr(json_extract(PayloadJson, '$.Operation.AutomationAdmission.RequestId'),24,1) IS '-' AND json_type(PayloadJson, '$.Operation.AutomationAdmission.SessionId') IS 'text' AND length(json_extract(PayloadJson, '$.Operation.AutomationAdmission.SessionId')) = 36 AND json_extract(PayloadJson, '$.Operation.AutomationAdmission.SessionId') NOT GLOB '*[^0-9a-f-]*' AND json_extract(PayloadJson, '$.Operation.AutomationAdmission.SessionId') IS NOT '00000000-0000-0000-0000-000000000000' AND substr(json_extract(PayloadJson, '$.Operation.AutomationAdmission.SessionId'),9,1) IS '-' AND substr(json_extract(PayloadJson, '$.Operation.AutomationAdmission.SessionId'),14,1) IS '-' AND substr(json_extract(PayloadJson, '$.Operation.AutomationAdmission.SessionId'),19,1) IS '-' AND substr(json_extract(PayloadJson, '$.Operation.AutomationAdmission.SessionId'),24,1) IS '-' AND json_type(PayloadJson, '$.Operation.AutomationAdmission.EntryId') IS 'text' AND length(json_extract(PayloadJson, '$.Operation.AutomationAdmission.EntryId')) = 36 AND json_extract(PayloadJson, '$.Operation.AutomationAdmission.EntryId') NOT GLOB '*[^0-9a-f-]*' AND json_extract(PayloadJson, '$.Operation.AutomationAdmission.EntryId') IS NOT '00000000-0000-0000-0000-000000000000' AND substr(json_extract(PayloadJson, '$.Operation.AutomationAdmission.EntryId'),9,1) IS '-' AND substr(json_extract(PayloadJson, '$.Operation.AutomationAdmission.EntryId'),14,1) IS '-' AND substr(json_extract(PayloadJson, '$.Operation.AutomationAdmission.EntryId'),19,1) IS '-' AND substr(json_extract(PayloadJson, '$.Operation.AutomationAdmission.EntryId'),24,1) IS '-' AND json_type(PayloadJson, '$.Operation.AutomationAdmission.ActivationId') IS 'text' AND length(json_extract(PayloadJson, '$.Operation.AutomationAdmission.ActivationId')) = 36 AND json_extract(PayloadJson, '$.Operation.AutomationAdmission.ActivationId') NOT GLOB '*[^0-9a-f-]*' AND json_extract(PayloadJson, '$.Operation.AutomationAdmission.ActivationId') IS NOT '00000000-0000-0000-0000-000000000000' AND substr(json_extract(PayloadJson, '$.Operation.AutomationAdmission.ActivationId'),9,1) IS '-' AND substr(json_extract(PayloadJson, '$.Operation.AutomationAdmission.ActivationId'),14,1) IS '-' AND substr(json_extract(PayloadJson, '$.Operation.AutomationAdmission.ActivationId'),19,1) IS '-' AND substr(json_extract(PayloadJson, '$.Operation.AutomationAdmission.ActivationId'),24,1) IS '-' AND json_type(PayloadJson, '$.Operation.AutomationAdmission.RunId') IS 'text' AND length(json_extract(PayloadJson, '$.Operation.AutomationAdmission.RunId')) = 36 AND json_extract(PayloadJson, '$.Operation.AutomationAdmission.RunId') NOT GLOB '*[^0-9a-f-]*' AND json_extract(PayloadJson, '$.Operation.AutomationAdmission.RunId') IS NOT '00000000-0000-0000-0000-000000000000' AND substr(json_extract(PayloadJson, '$.Operation.AutomationAdmission.RunId'),9,1) IS '-' AND substr(json_extract(PayloadJson, '$.Operation.AutomationAdmission.RunId'),14,1) IS '-' AND substr(json_extract(PayloadJson, '$.Operation.AutomationAdmission.RunId'),19,1) IS '-' AND substr(json_extract(PayloadJson, '$.Operation.AutomationAdmission.RunId'),24,1) IS '-' AND json_type(PayloadJson, '$.Operation.AutomationAdmission.ResponseId') IS 'text' AND length(json_extract(PayloadJson, '$.Operation.AutomationAdmission.ResponseId')) = 36 AND json_extract(PayloadJson, '$.Operation.AutomationAdmission.ResponseId') NOT GLOB '*[^0-9a-f-]*' AND json_extract(PayloadJson, '$.Operation.AutomationAdmission.ResponseId') IS NOT '00000000-0000-0000-0000-000000000000' AND substr(json_extract(PayloadJson, '$.Operation.AutomationAdmission.ResponseId'),9,1) IS '-' AND substr(json_extract(PayloadJson, '$.Operation.AutomationAdmission.ResponseId'),14,1) IS '-' AND substr(json_extract(PayloadJson, '$.Operation.AutomationAdmission.ResponseId'),19,1) IS '-' AND substr(json_extract(PayloadJson, '$.Operation.AutomationAdmission.ResponseId'),24,1) IS '-' AND json_extract(PayloadJson, '$.Operation.AutomationAdmission.RequestId') IS NOT json_extract(PayloadJson, '$.Operation.AutomationAdmission.SessionId') AND json_extract(PayloadJson, '$.Operation.AutomationAdmission.RequestId') IS NOT json_extract(PayloadJson, '$.Operation.AutomationAdmission.EntryId') AND json_extract(PayloadJson, '$.Operation.AutomationAdmission.RequestId') IS NOT json_extract(PayloadJson, '$.Operation.AutomationAdmission.ActivationId') AND json_extract(PayloadJson, '$.Operation.AutomationAdmission.RequestId') IS NOT json_extract(PayloadJson, '$.Operation.AutomationAdmission.RunId') AND json_extract(PayloadJson, '$.Operation.AutomationAdmission.RequestId') IS NOT json_extract(PayloadJson, '$.Operation.AutomationAdmission.ResponseId') AND json_extract(PayloadJson, '$.Operation.AutomationAdmission.SessionId') IS NOT json_extract(PayloadJson, '$.Operation.AutomationAdmission.EntryId') AND json_extract(PayloadJson, '$.Operation.AutomationAdmission.SessionId') IS NOT json_extract(PayloadJson, '$.Operation.AutomationAdmission.ActivationId') AND json_extract(PayloadJson, '$.Operation.AutomationAdmission.SessionId') IS NOT json_extract(PayloadJson, '$.Operation.AutomationAdmission.RunId') AND json_extract(PayloadJson, '$.Operation.AutomationAdmission.SessionId') IS NOT json_extract(PayloadJson, '$.Operation.AutomationAdmission.ResponseId') AND json_extract(PayloadJson, '$.Operation.AutomationAdmission.EntryId') IS NOT json_extract(PayloadJson, '$.Operation.AutomationAdmission.ActivationId') AND json_extract(PayloadJson, '$.Operation.AutomationAdmission.EntryId') IS NOT json_extract(PayloadJson, '$.Operation.AutomationAdmission.RunId') AND json_extract(PayloadJson, '$.Operation.AutomationAdmission.EntryId') IS NOT json_extract(PayloadJson, '$.Operation.AutomationAdmission.ResponseId') AND json_extract(PayloadJson, '$.Operation.AutomationAdmission.ActivationId') IS NOT json_extract(PayloadJson, '$.Operation.AutomationAdmission.RunId') AND json_extract(PayloadJson, '$.Operation.AutomationAdmission.ActivationId') IS NOT json_extract(PayloadJson, '$.Operation.AutomationAdmission.ResponseId') AND json_extract(PayloadJson, '$.Operation.AutomationAdmission.RunId') IS NOT json_extract(PayloadJson, '$.Operation.AutomationAdmission.ResponseId') AND json_extract(PayloadJson, '$.Operation.Persona') IS NULL AND json_extract(PayloadJson, '$.Operation.Preferences') IS NULL AND json_extract(PayloadJson, '$.Operation.CredentialCheck') IS NULL AND json_extract(PayloadJson, '$.Operation.SandboxCommand') IS NULL AND json_extract(PayloadJson, '$.Operation.SandboxControl') IS NULL AND json_extract(PayloadJson, '$.Operation.SandboxRead') IS NULL AND json_extract(PayloadJson, '$.Operation.BrowserCommand') IS NULL AND json_extract(PayloadJson, '$.Operation.CredentialRead') IS NULL AND json_extract(PayloadJson, '$.Operation.ArtifactPublication') IS NULL AND json_extract(PayloadJson, '$.Operation.ArtifactVerification') IS NULL AND json_extract(PayloadJson, '$.Operation.WorkspaceRead') IS NULL AND json_extract(PayloadJson, '$.Operation.WorkspaceMutation') IS NULL AND json_extract(PayloadJson, '$.Operation.AttachmentRead') IS NULL AND json_extract(PayloadJson, '$.Operation.BackgroundStart') IS NULL AND json_extract(PayloadJson, '$.Operation.BackgroundReport') IS NULL AND json_extract(PayloadJson, '$.Operation.CompletionRead') IS NULL AND json_extract(PayloadJson, '$.Operation.ReportSourceRead') IS NULL AND json_extract(PayloadJson, '$.Operation.AccountRead') IS NULL AND json_extract(PayloadJson, '$.Operation.AccountAction') IS NULL),0)) AND json_extract(PayloadJson, '$.Operation.MemoryOperation') IS NULL) OR COALESCE((Kind IN (0,1) AND Status IN (0,1,2,3,4) AND Revision >= 1 AND ExpiresAt > CreatedAt
        AND length(Fingerprint) = 64 AND Fingerprint NOT GLOB '*[^0-9a-f]*'
        AND ResourceType = 'agent' AND ResourceId = InstanceId AND AutomationId IS NULL
        AND CredentialId IS NULL AND SandboxProfileId IS NULL AND SandboxProfileRevision IS NULL
        AND json_type(PayloadJson, '$.Operation.MemoryOperation') IS 'object'
        AND json_extract(PayloadJson, '$.Operation.MemoryOperation.SessionId') IS SessionId
        AND json_type(PayloadJson, '$.Operation.MemoryOperation.RootSessionId') IS 'text'
        AND length(json_extract(PayloadJson, '$.Operation.MemoryOperation.RootSessionId')) = 36
        AND (json_type(PayloadJson, '$.Operation.MemoryOperation.UserPrincipalId') IS 'null' OR (
            json_type(PayloadJson, '$.Operation.MemoryOperation.UserPrincipalId') IS 'text'
            AND length(json_extract(PayloadJson, '$.Operation.MemoryOperation.UserPrincipalId')) = 36))
        AND json_extract(PayloadJson, '$.Operation.MemoryOperation.ToolName') IN ('continuity.search','continuity.get','memory.forget','memory.consolidate')
        AND Permission IS CASE WHEN json_extract(PayloadJson, '$.Operation.MemoryOperation.ToolName') IN ('continuity.search','continuity.get') THEN 'memory.read' ELSE 'memory.manage' END
        AND (Permission = 'memory.manage' OR Kind = 0)
        AND json_type(PayloadJson, '$.Operation.MemoryOperation.ArgumentsJson') IS 'text'
        AND length(CAST(json_extract(PayloadJson, '$.Operation.MemoryOperation.ArgumentsJson') AS BLOB)) BETWEEN 2 AND 8192
        AND json_valid(json_extract(PayloadJson, '$.Operation.MemoryOperation.ArgumentsJson'))
        AND json_type(json_extract(PayloadJson, '$.Operation.MemoryOperation.ArgumentsJson')) IS 'object'
        AND json_type(PayloadJson, '$.Operation.MemoryOperation.SourcesJson') IS 'text'
        AND length(json_extract(PayloadJson, '$.Operation.MemoryOperation.SourcesJson')) BETWEEN 2 AND 8192
        AND json_valid(json_extract(PayloadJson, '$.Operation.MemoryOperation.SourcesJson'))
        AND json_type(json_extract(PayloadJson, '$.Operation.MemoryOperation.SourcesJson')) IS 'array'
        AND json_array_length(json_extract(PayloadJson, '$.Operation.MemoryOperation.SourcesJson')) BETWEEN 0 AND 11
        AND json_type(PayloadJson, '$.Operation.MemoryOperation.SourceLabelsJson') IS 'text'
        AND length(CAST(json_extract(PayloadJson, '$.Operation.MemoryOperation.SourceLabelsJson') AS BLOB)) BETWEEN 2 AND 16384
        AND json_valid(json_extract(PayloadJson, '$.Operation.MemoryOperation.SourceLabelsJson'))
        AND json_type(json_extract(PayloadJson, '$.Operation.MemoryOperation.SourceLabelsJson')) IS 'array'
        AND json_array_length(json_extract(PayloadJson, '$.Operation.MemoryOperation.SourceLabelsJson')) = json_array_length(json_extract(PayloadJson, '$.Operation.MemoryOperation.SourcesJson'))
        AND json_type(PayloadJson, '$.Operation.MemoryOperation.ObservationHash') IS 'text'
        AND length(json_extract(PayloadJson, '$.Operation.MemoryOperation.ObservationHash')) = 64
        AND json_extract(PayloadJson, '$.Operation.MemoryOperation.ObservationHash') NOT GLOB '*[^0-9a-f]*'
        AND CASE WHEN json_extract(PayloadJson, '$.Operation.MemoryOperation.ToolName') = 'memory.consolidate' THEN
            json_type(PayloadJson, '$.Operation.MemoryOperation.ResultMemoryId') IS 'text'
            AND length(json_extract(PayloadJson, '$.Operation.MemoryOperation.ResultMemoryId')) = 36
            AND json_array_length(json_extract(PayloadJson, '$.Operation.MemoryOperation.SourcesJson')) BETWEEN 2 AND 8
            ELSE json_type(PayloadJson, '$.Operation.MemoryOperation.ResultMemoryId') IS 'null' END
        AND (json_extract(PayloadJson, '$.Operation.MemoryOperation.ToolName') NOT IN ('continuity.get','memory.forget')
            OR json_array_length(json_extract(PayloadJson, '$.Operation.MemoryOperation.SourcesJson')) = 1)
        AND length(CAST(PayloadJson AS BLOB)) <= 131072 AND json_extract(PayloadJson, '$.Operation.Persona') IS NULL AND json_extract(PayloadJson, '$.Operation.Preferences') IS NULL AND json_extract(PayloadJson, '$.Operation.CredentialCheck') IS NULL AND json_extract(PayloadJson, '$.Operation.SandboxCommand') IS NULL AND json_extract(PayloadJson, '$.Operation.SandboxControl') IS NULL AND json_extract(PayloadJson, '$.Operation.SandboxRead') IS NULL AND json_extract(PayloadJson, '$.Operation.BrowserCommand') IS NULL AND json_extract(PayloadJson, '$.Operation.CredentialRead') IS NULL AND json_extract(PayloadJson, '$.Operation.ArtifactPublication') IS NULL AND json_extract(PayloadJson, '$.Operation.ArtifactVerification') IS NULL AND json_extract(PayloadJson, '$.Operation.WorkspaceRead') IS NULL AND json_extract(PayloadJson, '$.Operation.WorkspaceMutation') IS NULL AND json_extract(PayloadJson, '$.Operation.AttachmentRead') IS NULL AND json_extract(PayloadJson, '$.Operation.BackgroundStart') IS NULL AND json_extract(PayloadJson, '$.Operation.BackgroundReport') IS NULL AND json_extract(PayloadJson, '$.Operation.CompletionRead') IS NULL AND json_extract(PayloadJson, '$.Operation.ReportSourceRead') IS NULL AND json_extract(PayloadJson, '$.Operation.AccountRead') IS NULL AND json_extract(PayloadJson, '$.Operation.AccountAction') IS NULL AND json_extract(PayloadJson, '$.Operation.AutomationAdmission') IS NULL), 0)) AND json_extract(PayloadJson, '$.Operation.ContinuityRead') IS NULL) OR COALESCE((Kind = 0 AND Status IN (0,1,2,3,4) AND Revision >= 1 AND ExpiresAt > CreatedAt
        AND RunId IS NOT NULL AND SessionId IS NOT NULL AND AutomationId IS NULL
        AND Permission = 'sessions.read' AND ResourceType = 'session' AND ResourceId = SessionId
        AND CredentialId IS NULL AND SandboxProfileId IS NULL AND SandboxProfileRevision IS NULL
        AND length(Fingerprint) = 64 AND Fingerprint NOT GLOB '*[^0-9a-f]*'
        AND length(CAST(PayloadJson AS BLOB)) <= 262144
        AND json_type(PayloadJson, '$.Operation.ContinuityRead') IS 'object'
        AND json_extract(PayloadJson, '$.Operation.ContinuityRead.SessionId') IS SessionId
        AND json_type(PayloadJson, '$.Operation.ContinuityRead.RootSessionId') IS 'text'
        AND length(json_extract(PayloadJson, '$.Operation.ContinuityRead.RootSessionId')) = 36
        AND (json_type(PayloadJson, '$.Operation.ContinuityRead.UserPrincipalId') IS 'null' OR (json_type(PayloadJson, '$.Operation.ContinuityRead.UserPrincipalId') IS 'text' AND length(json_extract(PayloadJson, '$.Operation.ContinuityRead.UserPrincipalId')) = 36))
        AND json_extract(PayloadJson, '$.Operation.ContinuityRead.ToolName') IN ('continuity.search','continuity.get')
        AND json_type(PayloadJson, '$.Operation.ContinuityRead.ArgumentsJson') IS 'text'
        AND length(CAST(json_extract(PayloadJson, '$.Operation.ContinuityRead.ArgumentsJson') AS BLOB)) BETWEEN 2 AND 4096
        AND json_valid(json_extract(PayloadJson, '$.Operation.ContinuityRead.ArgumentsJson'))
        AND json_type(json_extract(PayloadJson, '$.Operation.ContinuityRead.ArgumentsJson')) IS 'object'
        AND json_type(PayloadJson, '$.Operation.ContinuityRead.SessionsJson') IS 'text'
        AND length(CAST(json_extract(PayloadJson, '$.Operation.ContinuityRead.SessionsJson') AS BLOB)) BETWEEN 2 AND 16384
        AND json_valid(json_extract(PayloadJson, '$.Operation.ContinuityRead.SessionsJson'))
        AND json_type(json_extract(PayloadJson, '$.Operation.ContinuityRead.SessionsJson')) IS 'array'
        AND json_array_length(json_extract(PayloadJson, '$.Operation.ContinuityRead.SessionsJson')) BETWEEN 0 AND 11
        AND json_type(PayloadJson, '$.Operation.ContinuityRead.SourceLabelsJson') IS 'text'
        AND length(CAST(json_extract(PayloadJson, '$.Operation.ContinuityRead.SourceLabelsJson') AS BLOB)) BETWEEN 2 AND 8192
        AND json_valid(json_extract(PayloadJson, '$.Operation.ContinuityRead.SourceLabelsJson'))
        AND json_type(json_extract(PayloadJson, '$.Operation.ContinuityRead.SourceLabelsJson')) IS 'array'
        AND json_array_length(json_extract(PayloadJson, '$.Operation.ContinuityRead.SourceLabelsJson')) = json_array_length(json_extract(PayloadJson, '$.Operation.ContinuityRead.SessionsJson'))
        AND json_type(PayloadJson, '$.Operation.ContinuityRead.ObservationHash') IS 'text'
        AND length(json_extract(PayloadJson, '$.Operation.ContinuityRead.ObservationHash')) = 64
        AND json_extract(PayloadJson, '$.Operation.ContinuityRead.ObservationHash') NOT GLOB '*[^0-9a-f]*'
        AND (json_extract(PayloadJson, '$.Operation.ContinuityRead.Memory') IS NULL OR (
            json_type(PayloadJson, '$.Operation.ContinuityRead.Memory') IS 'object'
            AND json_extract(PayloadJson, '$.Operation.ContinuityRead.Memory.SessionId') IS SessionId
            AND json_extract(PayloadJson, '$.Operation.ContinuityRead.Memory.RootSessionId') IS json_extract(PayloadJson, '$.Operation.ContinuityRead.RootSessionId')
            AND json_extract(PayloadJson, '$.Operation.ContinuityRead.Memory.UserPrincipalId') IS json_extract(PayloadJson, '$.Operation.ContinuityRead.UserPrincipalId')
            AND json_extract(PayloadJson, '$.Operation.ContinuityRead.Memory.ToolName') IS json_extract(PayloadJson, '$.Operation.ContinuityRead.ToolName')
            AND json_extract(PayloadJson, '$.Operation.ContinuityRead.Memory.ArgumentsJson') IS json_extract(PayloadJson, '$.Operation.ContinuityRead.ArgumentsJson')
            AND json_extract(PayloadJson, '$.Operation.ContinuityRead.Memory.ResultMemoryId') IS NULL
            AND json_type(PayloadJson, '$.Operation.ContinuityRead.Memory.SourcesJson') IS 'text'
            AND json_valid(json_extract(PayloadJson, '$.Operation.ContinuityRead.Memory.SourcesJson'))
            AND json_type(json_extract(PayloadJson, '$.Operation.ContinuityRead.Memory.SourcesJson')) IS 'array'
            AND json_array_length(json_extract(PayloadJson, '$.Operation.ContinuityRead.Memory.SourcesJson')) BETWEEN 0 AND 11
            AND length(json_extract(PayloadJson, '$.Operation.ContinuityRead.Memory.SourcesJson')) <= 8192
            AND json_type(PayloadJson, '$.Operation.ContinuityRead.Memory.SourceLabelsJson') IS 'text'
            AND json_valid(json_extract(PayloadJson, '$.Operation.ContinuityRead.Memory.SourceLabelsJson'))
            AND json_type(json_extract(PayloadJson, '$.Operation.ContinuityRead.Memory.SourceLabelsJson')) IS 'array'
            AND json_array_length(json_extract(PayloadJson, '$.Operation.ContinuityRead.Memory.SourceLabelsJson')) = json_array_length(json_extract(PayloadJson, '$.Operation.ContinuityRead.Memory.SourcesJson'))
            AND length(CAST(json_extract(PayloadJson, '$.Operation.ContinuityRead.Memory.SourceLabelsJson') AS BLOB)) <= 16384
            AND length(json_extract(PayloadJson, '$.Operation.ContinuityRead.Memory.ObservationHash')) = 64
            AND json_extract(PayloadJson, '$.Operation.ContinuityRead.Memory.ObservationHash') NOT GLOB '*[^0-9a-f]*'))
        AND json_extract(PayloadJson, '$.Operation.Persona') IS NULL AND json_extract(PayloadJson, '$.Operation.Preferences') IS NULL AND json_extract(PayloadJson, '$.Operation.CredentialCheck') IS NULL AND json_extract(PayloadJson, '$.Operation.SandboxCommand') IS NULL AND json_extract(PayloadJson, '$.Operation.SandboxControl') IS NULL AND json_extract(PayloadJson, '$.Operation.SandboxRead') IS NULL AND json_extract(PayloadJson, '$.Operation.BrowserCommand') IS NULL AND json_extract(PayloadJson, '$.Operation.CredentialRead') IS NULL AND json_extract(PayloadJson, '$.Operation.ArtifactPublication') IS NULL AND json_extract(PayloadJson, '$.Operation.ArtifactVerification') IS NULL AND json_extract(PayloadJson, '$.Operation.WorkspaceRead') IS NULL AND json_extract(PayloadJson, '$.Operation.WorkspaceMutation') IS NULL AND json_extract(PayloadJson, '$.Operation.AttachmentRead') IS NULL AND json_extract(PayloadJson, '$.Operation.BackgroundStart') IS NULL AND json_extract(PayloadJson, '$.Operation.BackgroundReport') IS NULL AND json_extract(PayloadJson, '$.Operation.CompletionRead') IS NULL AND json_extract(PayloadJson, '$.Operation.ReportSourceRead') IS NULL AND json_extract(PayloadJson, '$.Operation.AccountRead') IS NULL AND json_extract(PayloadJson, '$.Operation.AccountAction') IS NULL AND json_extract(PayloadJson, '$.Operation.AutomationAdmission') IS NULL AND json_extract(PayloadJson, '$.Operation.MemoryOperation') IS NULL),0)),
            CONSTRAINT "FK_TenantApprovalRequests_AgentRuns_RunId_RootActivationId" FOREIGN KEY ("RunId", "RootActivationId") REFERENCES "AgentRuns" ("AgentRunId", "RootActivationId") ON DELETE RESTRICT,
            CONSTRAINT "FK_TenantApprovalRequests_AgentRuns_RunId_SessionId" FOREIGN KEY ("RunId", "SessionId") REFERENCES "AgentRuns" ("AgentRunId", "SessionId") ON DELETE RESTRICT,
            CONSTRAINT "FK_TenantApprovalRequests_SandboxProfiles_SandboxProfileId_SandboxProfileRevision" FOREIGN KEY ("SandboxProfileId", "SandboxProfileRevision") REFERENCES "SandboxProfiles" ("Id", "Revision") ON DELETE RESTRICT,
            CONSTRAINT "FK_TenantApprovalRequests_TenantCredentials_TenantId_CredentialId" FOREIGN KEY ("TenantId", "CredentialId") REFERENCES "TenantCredentials" ("TenantId", "CredentialId") ON DELETE RESTRICT,
            CONSTRAINT "FK_TenantApprovalRequests_TenantSandboxProfiles_TenantId_SandboxProfileId" FOREIGN KEY ("TenantId", "SandboxProfileId") REFERENCES "TenantSandboxProfiles" ("TenantId", "ProfileId") ON DELETE RESTRICT,
            CONSTRAINT "FK_TenantApprovalRequests_TenantSessions_TenantId_SessionId_InstanceId_ExecutorPrincipalId" FOREIGN KEY ("TenantId", "SessionId", "InstanceId", "ExecutorPrincipalId") REFERENCES "TenantSessions" ("TenantId", "SessionId", "AgentInstanceId", "ExecutorPrincipalId") ON DELETE RESTRICT,
            CONSTRAINT "FK_TenantApprovalRequests_TenantUsers_TenantId_OriginalInitiatorPrincipalId" FOREIGN KEY ("TenantId", "OriginalInitiatorPrincipalId") REFERENCES "TenantUsers" ("TenantId", "PrincipalId") ON DELETE RESTRICT
        );
        INSERT INTO "TenantApprovalRequests_ContinuityRebuild" ("RequestId", "TenantId", "RunId", "SessionId", "AutomationId", "InstanceId", "RootActivationId", "ExecutorPrincipalId", "OriginalInitiatorPrincipalId", "OperationId", "Fingerprint", "Permission", "ResourceType", "ResourceId", "CredentialId", "SandboxProfileId", "SandboxProfileRevision", "Kind", "Status", "Revision", "CreatedAt", "ExpiresAt", "PayloadJson") SELECT "RequestId", "TenantId", "RunId", "SessionId", "AutomationId", "InstanceId", "RootActivationId", "ExecutorPrincipalId", "OriginalInitiatorPrincipalId", "OperationId", "Fingerprint", "Permission", "ResourceType", "ResourceId", "CredentialId", "SandboxProfileId", "SandboxProfileRevision", "Kind", "Status", "Revision", "CreatedAt", "ExpiresAt", "PayloadJson" FROM "TenantApprovalRequests";
        DROP TABLE "TenantApprovalRequests";
        ALTER TABLE "TenantApprovalRequests_ContinuityRebuild" RENAME TO "TenantApprovalRequests";
        CREATE UNIQUE INDEX "IX_TenantApprovalRequests_OperationId" ON "TenantApprovalRequests" ("OperationId");
        CREATE INDEX "IX_TenantApprovalRequests_RunId_RootActivationId" ON "TenantApprovalRequests" ("RunId", "RootActivationId");
        CREATE INDEX "IX_TenantApprovalRequests_RunId_SessionId" ON "TenantApprovalRequests" ("RunId", "SessionId");
        CREATE INDEX "IX_TenantApprovalRequests_SandboxProfileId_SandboxProfileRevision" ON "TenantApprovalRequests" ("SandboxProfileId", "SandboxProfileRevision");
        CREATE INDEX "IX_TenantApprovalRequests_Status_ExpiresAt_RequestId" ON "TenantApprovalRequests" ("Status", "ExpiresAt", "RequestId");
        CREATE INDEX "IX_TenantApprovalRequests_TenantId_AutomationId_Status" ON "TenantApprovalRequests" ("TenantId", "AutomationId", "Status");
        CREATE INDEX "IX_TenantApprovalRequests_TenantId_CredentialId" ON "TenantApprovalRequests" ("TenantId", "CredentialId");
        CREATE INDEX "IX_TenantApprovalRequests_TenantId_ExecutorPrincipalId_CreatedAt" ON "TenantApprovalRequests" ("TenantId", "ExecutorPrincipalId", "CreatedAt");
        CREATE INDEX "IX_TenantApprovalRequests_TenantId_OriginalInitiatorPrincipalId" ON "TenantApprovalRequests" ("TenantId", "OriginalInitiatorPrincipalId");
        CREATE UNIQUE INDEX "IX_TenantApprovalRequests_TenantId_RootActivationId_ExecutorPrincipalId_Fingerprint" ON "TenantApprovalRequests" ("TenantId", "RootActivationId", "ExecutorPrincipalId", "Fingerprint");
        CREATE INDEX "IX_TenantApprovalRequests_TenantId_SandboxProfileId" ON "TenantApprovalRequests" ("TenantId", "SandboxProfileId");
        CREATE INDEX "IX_TenantApprovalRequests_TenantId_SessionId_InstanceId_ExecutorPrincipalId" ON "TenantApprovalRequests" ("TenantId", "SessionId", "InstanceId", "ExecutorPrincipalId");
        CREATE INDEX "IX_TenantApprovalRequests_TenantId_Status_CreatedAt_RequestId" ON "TenantApprovalRequests" ("TenantId", "Status", "CreatedAt", "RequestId");
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
        AND o.ModelCatalogKey IS json_extract(NEW.PayloadJson, '$.Operation.AutomationAdmission.Model.CatalogKey')
        AND o.ModelProviderAlias IS json_extract(NEW.PayloadJson, '$.Operation.AutomationAdmission.Model.ProviderAlias')
        AND o.ModelId IS json_extract(NEW.PayloadJson, '$.Operation.AutomationAdmission.Model.ModelId')
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
        CREATE TRIGGER TR_TenantApprovals_AutomationAdmission_Immutable BEFORE UPDATE ON TenantApprovalRequests
        WHEN OLD.AutomationId IS NOT NULL OR NEW.AutomationId IS NOT NULL
        BEGIN SELECT RAISE(ABORT, 'Automation consent evidence is immutable') WHERE
        OLD.AutomationId IS NOT NEW.AutomationId OR OLD.RequestId IS NOT NEW.RequestId OR OLD.TenantId IS NOT NEW.TenantId
        OR OLD.RunId IS NOT NEW.RunId OR OLD.SessionId IS NOT NEW.SessionId OR OLD.InstanceId IS NOT NEW.InstanceId
        OR OLD.ExecutorPrincipalId IS NOT NEW.ExecutorPrincipalId OR OLD.OriginalInitiatorPrincipalId IS NOT NEW.OriginalInitiatorPrincipalId
        OR OLD.RootActivationId IS NOT NEW.RootActivationId OR OLD.Permission IS NOT NEW.Permission OR OLD.ResourceId IS NOT NEW.ResourceId
        OR OLD.ResourceType IS NOT NEW.ResourceType OR OLD.Fingerprint IS NOT NEW.Fingerprint OR OLD.OperationId IS NOT NEW.OperationId
        OR OLD.CreatedAt IS NOT NEW.CreatedAt OR OLD.ExpiresAt IS NOT NEW.ExpiresAt OR OLD.Kind IS NOT NEW.Kind
        OR json_extract(OLD.PayloadJson, '$.Operation') IS NOT json_extract(NEW.PayloadJson, '$.Operation'); END;
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
        AND NEW.CredentialId IS NULL AND NEW.SandboxProfileId IS NULL AND NEW.SandboxProfileRevision IS NULL
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
        CREATE TRIGGER TR_TenantApprovals_IndependentTask_Insert BEFORE INSERT ON TenantApprovalRequests
        WHEN NEW.OriginalInitiatorPrincipalId IS NULL AND json_extract(NEW.PayloadJson, '$.Operation.AutomationAdmission') IS NULL
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
        WHEN NEW.OriginalInitiatorPrincipalId IS NULL AND json_extract(NEW.PayloadJson, '$.Operation.AutomationAdmission') IS NULL
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
        CREATE TRIGGER TR_TenantApprovals_Memory_Insert BEFORE INSERT ON TenantApprovalRequests
        WHEN json_extract(NEW.PayloadJson, '$.Operation.MemoryOperation') IS NOT NULL AND (
            NOT EXISTS (SELECT 1 FROM AgentRuns r JOIN AgentRuns root ON root.ActivationId = r.RootActivationId
                WHERE r.AgentRunId = NEW.RunId AND r.TenantId = NEW.TenantId AND r.SessionId = NEW.SessionId
                    AND r.AgentInstanceId = NEW.InstanceId AND r.ExecutorPrincipalId = NEW.ExecutorPrincipalId
                    AND root.TenantId = NEW.TenantId AND root.RootActivationId = root.ActivationId
                    AND root.AgentInstanceId = r.AgentInstanceId AND root.ExecutorPrincipalId = r.ExecutorPrincipalId
                    AND root.SessionId = json_extract(NEW.PayloadJson, '$.Operation.MemoryOperation.RootSessionId') AND r.RootActivationId = NEW.RootActivationId
                    AND ((json_extract(root.PayloadJson, '$.admission.executionContext.origin') = 0
                        AND lower(root.OriginalInitiatorPrincipalId) = json_extract(NEW.PayloadJson, '$.Operation.MemoryOperation.UserPrincipalId')
                        AND EXISTS (SELECT 1 FROM TenantUsers u WHERE u.TenantId = NEW.TenantId AND u.PrincipalId = root.OriginalInitiatorPrincipalId))
                        OR (json_extract(root.PayloadJson, '$.admission.executionContext.origin') <> 0
                            AND json_extract(NEW.PayloadJson, '$.Operation.MemoryOperation.UserPrincipalId') IS NULL AND json_extract(NEW.PayloadJson, '$.Operation.MemoryOperation.ToolName') IN ('continuity.search','continuity.get'))))
            OR EXISTS (SELECT 1 FROM json_each(json_extract(NEW.PayloadJson, '$.Operation.MemoryOperation.SourcesJson')) source WHERE NOT EXISTS (
                SELECT 1 FROM TenantLearnedMemories m WHERE m.TenantId = NEW.TenantId AND lower(m.MemoryId) = json_extract(source.value, '$.MemoryId')
                    AND m.Revision = json_extract(source.value, '$.Revision') AND m.ContentHash = json_extract(source.value, '$.ContentHash')
                    AND ((json_extract(NEW.PayloadJson, '$.Operation.MemoryOperation.UserPrincipalId') IS NOT NULL AND lower(m.UserPrincipalId) = json_extract(NEW.PayloadJson, '$.Operation.MemoryOperation.UserPrincipalId'))
                        OR (json_extract(NEW.PayloadJson, '$.Operation.MemoryOperation.UserPrincipalId') IS NULL AND m.UserPrincipalId IS NULL AND m.Scope = 0 AND m.ExecutorPrincipalId = NEW.ExecutorPrincipalId))
                    AND ((m.Scope = 0 AND m.SessionId = NEW.SessionId) OR (m.Scope = 1 AND m.AgentInstanceId = NEW.InstanceId) OR m.Scope = 2)
                    AND (json_extract(NEW.PayloadJson, '$.Operation.MemoryOperation.ToolName') NOT IN ('memory.consolidate','memory.forget') OR
                        (json_extract(NEW.PayloadJson, '$.Operation.MemoryOperation.ToolName') = 'memory.consolidate' AND m.Status = 0) OR (json_extract(NEW.PayloadJson, '$.Operation.MemoryOperation.ToolName') = 'memory.forget' AND m.Status <> 2))
                    AND EXISTS (SELECT 1 FROM json_each(json_extract(NEW.PayloadJson, '$.Operation.MemoryOperation.SourceLabelsJson')) label
                        WHERE json_extract(label.value, '$.memoryId') = lower(m.MemoryId)
                            AND json_extract(label.value, '$.subject') = m.Subject
                            AND json_extract(label.value, '$.scope') = CASE m.Scope WHEN 0 THEN 'session' WHEN 1 THEN 'identityUser' ELSE 'user' END
                            AND json_extract(label.value, '$.kind') = CASE m.Kind WHEN 0 THEN 'Fact' WHEN 1 THEN 'Preference' WHEN 2 THEN 'Goal' WHEN 3 THEN 'Decision' ELSE 'OpenLoop' END)))
            OR (SELECT count(DISTINCT json_extract(value, '$.MemoryId')) FROM json_each(json_extract(NEW.PayloadJson, '$.Operation.MemoryOperation.SourcesJson'))) <> json_array_length(json_extract(NEW.PayloadJson, '$.Operation.MemoryOperation.SourcesJson'))
            OR (json_extract(NEW.PayloadJson, '$.Operation.MemoryOperation.ToolName') = 'memory.consolidate' AND (SELECT count(DISTINCT m.OwnerKey || ':' || m.Kind) FROM json_each(json_extract(NEW.PayloadJson, '$.Operation.MemoryOperation.SourcesJson')) source
                JOIN TenantLearnedMemories m ON m.TenantId = NEW.TenantId AND lower(m.MemoryId) = json_extract(source.value, '$.MemoryId')) <> 1)
        )
        BEGIN SELECT RAISE(ABORT, 'Memory approval requires exact actual-root owned source witnesses'); END;
        CREATE TRIGGER TR_TenantApprovals_Memory_Update BEFORE UPDATE ON TenantApprovalRequests
        WHEN json_extract(NEW.PayloadJson, '$.Operation.MemoryOperation') IS NOT json_extract(OLD.PayloadJson, '$.Operation.MemoryOperation')
        BEGIN SELECT RAISE(ABORT, 'Retain exact prepared Memory operation'); END;
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
        CREATE TRIGGER TR_TenantOccurrences_Source_Update BEFORE UPDATE ON TriggerOccurrences
        WHEN OLD.TenantId IS NOT NULL OR NEW.TenantId IS NOT NULL
        BEGIN
        SELECT RAISE(ABORT, 'tenant occurrence source is immutable') WHERE 1=1 AND (OLD.TenantId IS NOT NEW.TenantId OR OLD.ExecutorPrincipalId IS NOT NEW.ExecutorPrincipalId OR OLD.AgentInstanceId IS NOT NEW.AgentInstanceId
        OR OLD.ProfileId IS NOT NEW.ProfileId OR OLD.OccurrenceId IS NOT NEW.OccurrenceId OR OLD.AutomationId IS NOT NEW.AutomationId
        OR OLD.DedupeKey IS NOT NEW.DedupeKey OR OLD.EvidenceJson IS NOT NEW.EvidenceJson OR OLD.SourceKind IS NOT NEW.SourceKind
        OR OLD.TriggerRevision IS NOT NEW.TriggerRevision OR OLD.ScheduledAtUtc IS NOT NEW.ScheduledAtUtc OR OLD.ObservedAtUtc IS NOT NEW.ObservedAtUtc
        OR OLD.AdmittedAtUtc IS NOT NEW.AdmittedAtUtc
        OR OLD.SourceEventId IS NOT NEW.SourceEventId OR OLD.ModelCatalogKey IS NOT NEW.ModelCatalogKey OR OLD.ModelProviderAlias IS NOT NEW.ModelProviderAlias
        OR OLD.ModelId IS NOT NEW.ModelId OR OLD.ModelReasoningEffort IS NOT NEW.ModelReasoningEffort
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
        CREATE TRIGGER TR_TenantApprovals_Continuity_Insert BEFORE INSERT ON TenantApprovalRequests
        WHEN json_extract(NEW.PayloadJson, '$.Operation.ContinuityRead') IS NOT NULL AND (
            NOT EXISTS (SELECT 1 FROM AgentRuns r JOIN AgentRuns root ON root.ActivationId = r.RootActivationId
                WHERE r.AgentRunId = NEW.RunId AND r.TenantId = NEW.TenantId AND r.SessionId = NEW.SessionId
                    AND r.AgentInstanceId = NEW.InstanceId AND r.ExecutorPrincipalId = NEW.ExecutorPrincipalId
                    AND r.RootActivationId = NEW.RootActivationId AND root.TenantId = NEW.TenantId AND root.RootActivationId = root.ActivationId
                    AND root.AgentInstanceId = r.AgentInstanceId AND root.ExecutorPrincipalId = r.ExecutorPrincipalId
                    AND root.SessionId = json_extract(NEW.PayloadJson, '$.Operation.ContinuityRead.RootSessionId')
                    AND ((json_extract(root.PayloadJson, '$.admission.executionContext.origin') = 0
                        AND lower(root.OriginalInitiatorPrincipalId) = json_extract(NEW.PayloadJson, '$.Operation.ContinuityRead.UserPrincipalId')
                        AND EXISTS (SELECT 1 FROM TenantUsers u WHERE u.TenantId = NEW.TenantId AND u.PrincipalId = root.OriginalInitiatorPrincipalId))
                        OR (json_extract(root.PayloadJson, '$.admission.executionContext.origin') <> 0 AND json_extract(NEW.PayloadJson, '$.Operation.ContinuityRead.UserPrincipalId') IS NULL)))
            OR (SELECT count(DISTINCT json_extract(value, '$.SessionId')) FROM json_each(json_extract(NEW.PayloadJson, '$.Operation.ContinuityRead.SessionsJson'))) <> json_array_length(json_extract(NEW.PayloadJson, '$.Operation.ContinuityRead.SessionsJson'))
            OR EXISTS (SELECT 1 FROM json_each(json_extract(NEW.PayloadJson, '$.Operation.ContinuityRead.SessionsJson')) witness WHERE
                json_type(witness.value, '$.SessionId') IS NOT 'text'
                OR json_type(witness.value, '$.Revision') IS NOT 'integer' OR json_extract(witness.value, '$.Revision') < 1
                OR json_type(witness.value, '$.ThroughEntrySequence') IS NOT 'integer' OR json_extract(witness.value, '$.ThroughEntrySequence') < 0
                OR json_type(witness.value, '$.AfterEntrySequence') IS NOT 'integer' OR json_extract(witness.value, '$.AfterEntrySequence') < 0
                OR json_type(witness.value, '$.Limit') IS NOT 'integer' OR json_extract(witness.value, '$.Limit') NOT BETWEEN 1 AND 20
                OR json_type(witness.value, '$.ObservationHash') IS NOT 'text' OR length(json_extract(witness.value, '$.ObservationHash')) <> 64
                OR json_extract(witness.value, '$.ObservationHash') GLOB '*[^0-9a-f]*'
                OR NOT EXISTS (SELECT 1 FROM TenantSessions owner JOIN Sessions saved ON saved.SessionId = owner.SessionId JOIN SessionSnapshots snapshot ON snapshot.SessionId = owner.SessionId
                    WHERE owner.TenantId = NEW.TenantId AND owner.AgentInstanceId = NEW.InstanceId AND owner.ExecutorPrincipalId = NEW.ExecutorPrincipalId
                        AND owner.SessionId = json_extract(witness.value, '$.SessionId') AND owner.SessionId <> NEW.SessionId AND saved.DurablyDeletedAtUtc IS NULL
                        AND saved.Revision = json_extract(witness.value, '$.Revision') AND snapshot.LastEntrySequence = json_extract(witness.value, '$.ThroughEntrySequence'))
                OR NOT EXISTS (SELECT 1 FROM json_each(json_extract(NEW.PayloadJson, '$.Operation.ContinuityRead.SourceLabelsJson')) label
                    WHERE json_extract(label.value, '$.sessionId') = json_extract(witness.value, '$.SessionId')
                        AND json_type(label.value, '$.title') IS 'text' AND length(json_extract(label.value, '$.title')) <= 160
                        AND json_extract(label.value, '$.throughEntrySequence') = json_extract(witness.value, '$.ThroughEntrySequence')
                        AND json_extract(label.value, '$.afterEntrySequence') = json_extract(witness.value, '$.AfterEntrySequence')
                        AND json_extract(label.value, '$.limit') = json_extract(witness.value, '$.Limit')))
            OR (json_extract(NEW.PayloadJson, '$.Operation.ContinuityRead.Memory') IS NOT NULL AND (
                (SELECT count(DISTINCT json_extract(value, '$.MemoryId')) FROM json_each(json_extract(NEW.PayloadJson, '$.Operation.ContinuityRead.Memory.SourcesJson'))) <> json_array_length(json_extract(NEW.PayloadJson, '$.Operation.ContinuityRead.Memory.SourcesJson'))
                OR EXISTS (SELECT 1 FROM json_each(json_extract(NEW.PayloadJson, '$.Operation.ContinuityRead.Memory.SourcesJson')) witness WHERE
                    json_type(witness.value, '$.MemoryId') IS NOT 'text' OR json_type(witness.value, '$.Revision') IS NOT 'integer' OR json_extract(witness.value, '$.Revision') < 1
                    OR json_type(witness.value, '$.ContentHash') IS NOT 'text' OR length(json_extract(witness.value, '$.ContentHash')) <> 64
                    OR json_extract(witness.value, '$.ContentHash') GLOB '*[^0-9a-f]*'
                    OR NOT EXISTS (SELECT 1 FROM TenantLearnedMemories fact WHERE fact.TenantId = NEW.TenantId
                        AND lower(fact.MemoryId) = json_extract(witness.value, '$.MemoryId') AND fact.Revision = json_extract(witness.value, '$.Revision')
                        AND fact.ContentHash = json_extract(witness.value, '$.ContentHash')
                        AND ((json_extract(NEW.PayloadJson, '$.Operation.ContinuityRead.UserPrincipalId') IS NOT NULL AND lower(fact.UserPrincipalId) = json_extract(NEW.PayloadJson, '$.Operation.ContinuityRead.UserPrincipalId'))
                            OR (json_extract(NEW.PayloadJson, '$.Operation.ContinuityRead.UserPrincipalId') IS NULL AND fact.UserPrincipalId IS NULL AND fact.Scope = 0 AND fact.ExecutorPrincipalId = NEW.ExecutorPrincipalId))
                        AND ((fact.Scope = 0 AND fact.SessionId = NEW.SessionId) OR (fact.Scope = 1 AND fact.AgentInstanceId = NEW.InstanceId) OR fact.Scope = 2))))))
        BEGIN SELECT RAISE(ABORT, 'Continuity approval requires exact actual-root owned historical and learned source witnesses'); END;
        CREATE TRIGGER TR_TenantApprovals_Continuity_Update BEFORE UPDATE ON TenantApprovalRequests
        WHEN json_extract(NEW.PayloadJson, '$.Operation.ContinuityRead') IS NOT json_extract(OLD.PayloadJson, '$.Operation.ContinuityRead')
        BEGIN SELECT RAISE(ABORT, 'Retain the exact prepared historical continuity observation'); END;
        CREATE TEMP TABLE ContinuityForeignKeyGuard (Rows INTEGER CHECK(Rows=0));
        INSERT INTO ContinuityForeignKeyGuard SELECT count(*) FROM pragma_foreign_key_check;
        DROP TABLE ContinuityForeignKeyGuard;
        """;
    private const string Downgrade = """
        DROP TRIGGER IF EXISTS "TR_TenantApprovals_AccountAction_Insert";
        DROP TRIGGER IF EXISTS "TR_TenantApprovals_AccountAction_Update";
        DROP TRIGGER IF EXISTS "TR_TenantApprovals_AccountOperationRead_Insert";
        DROP TRIGGER IF EXISTS "TR_TenantApprovals_AccountOperationRead_Update";
        DROP TRIGGER IF EXISTS "TR_TenantApprovals_AccountRead_Insert";
        DROP TRIGGER IF EXISTS "TR_TenantApprovals_AccountRead_Update";
        DROP TRIGGER IF EXISTS "TR_TenantApprovals_AutomationAdmission_INSERT";
        DROP TRIGGER IF EXISTS "TR_TenantApprovals_AutomationAdmission_Immutable";
        DROP TRIGGER IF EXISTS "TR_TenantApprovals_AutomationAdmission_UPDATE";
        DROP TRIGGER IF EXISTS "TR_TenantApprovals_BackgroundCreation_Update";
        DROP TRIGGER IF EXISTS "TR_TenantApprovals_BackgroundReport_Insert";
        DROP TRIGGER IF EXISTS "TR_TenantApprovals_BackgroundReport_Update";
        DROP TRIGGER IF EXISTS "TR_TenantApprovals_BrowserArtifacts_Insert";
        DROP TRIGGER IF EXISTS "TR_TenantApprovals_BrowserArtifacts_Update";
        DROP TRIGGER IF EXISTS "TR_TenantApprovals_BrowserFiles_Insert";
        DROP TRIGGER IF EXISTS "TR_TenantApprovals_BrowserFiles_Update";
        DROP TRIGGER IF EXISTS "TR_TenantApprovals_CompletionRead_Insert";
        DROP TRIGGER IF EXISTS "TR_TenantApprovals_CompletionRead_Update";
        DROP TRIGGER IF EXISTS "TR_TenantApprovals_IndependentTask_Insert";
        DROP TRIGGER IF EXISTS "TR_TenantApprovals_IndependentTask_Update";
        DROP TRIGGER IF EXISTS "TR_TenantApprovals_Memory_Insert";
        DROP TRIGGER IF EXISTS "TR_TenantApprovals_Memory_Update";
        DROP TRIGGER IF EXISTS "TR_TenantApprovals_ReportSourceRead_Insert";
        DROP TRIGGER IF EXISTS "TR_TenantApprovals_ReportSourceRead_Update";
        DROP TRIGGER IF EXISTS "TR_TenantOccurrences_Source_Update";
        DROP TRIGGER IF EXISTS "TR_TenantReceipts_BrowserArtifacts_Insert";
        DROP TRIGGER IF EXISTS "TR_TenantApprovals_Continuity_Insert";
        DROP TRIGGER IF EXISTS "TR_TenantApprovals_Continuity_Update";
        CREATE TABLE "TenantApprovalRequests_ContinuityRebuild" (
            "RequestId" TEXT NOT NULL CONSTRAINT "PK_TenantApprovalRequests" PRIMARY KEY,
            "TenantId" TEXT NOT NULL,
            "RunId" TEXT NULL,
            "SessionId" TEXT NULL,
            "AutomationId" TEXT NULL,
            "InstanceId" TEXT NOT NULL,
            "RootActivationId" TEXT NOT NULL,
            "ExecutorPrincipalId" TEXT NOT NULL,
            "OriginalInitiatorPrincipalId" TEXT NULL,
            "OperationId" TEXT NOT NULL,
            "Fingerprint" TEXT NOT NULL,
            "Permission" TEXT NOT NULL,
            "ResourceType" TEXT NOT NULL,
            "ResourceId" TEXT NOT NULL,
            "CredentialId" TEXT NULL,
            "SandboxProfileId" TEXT NULL,
            "SandboxProfileRevision" INTEGER NULL,
            "Kind" INTEGER NOT NULL,
            "Status" INTEGER NOT NULL,
            "Revision" INTEGER NOT NULL,
            "CreatedAt" INTEGER NOT NULL,
            "ExpiresAt" INTEGER NOT NULL,
            "PayloadJson" TEXT NOT NULL,
            CONSTRAINT "AK_TenantApprovalRequests_RequestId_RootActivationId_InstanceId" UNIQUE ("RequestId", "RootActivationId", "InstanceId"),
            CONSTRAINT "AK_TenantApprovalRequests_TenantId_RequestId" UNIQUE ("TenantId", "RequestId"),
            CONSTRAINT "AK_TenantApprovalRequests_TenantId_RequestId_ExecutorPrincipalId_OperationId_Fingerprint" UNIQUE ("TenantId", "RequestId", "ExecutorPrincipalId", "OperationId", "Fingerprint"),
            CONSTRAINT "CK_TenantApprovalRequest_Values" CHECK ((((RunId IS NOT NULL AND SessionId IS NOT NULL AND AutomationId IS NULL AND json_extract(PayloadJson, '$.Operation.AutomationAdmission') IS NULL AND (((((((((((((((((((((((((((((((((Kind IN (0,1) AND Status IN (0,1,2,3,4) AND Revision >= 1 AND ExpiresAt > CreatedAt AND length(Fingerprint) = 64 AND ((Permission = 'agents.update' AND ResourceType = 'agent' AND ResourceId = InstanceId AND CredentialId IS NULL AND SandboxProfileId IS NULL AND SandboxProfileRevision IS NULL AND length(PayloadJson) <= 131072) OR (Permission = 'credentials.use' AND ResourceType = 'credential' AND CredentialId IS NOT NULL AND ResourceId = CredentialId AND SandboxProfileId IS NULL AND SandboxProfileRevision IS NULL AND length(PayloadJson) <= 131072) OR (Permission = 'sandbox.execute' AND Kind = 0 AND ResourceType = 'agent' AND ResourceId = InstanceId AND CredentialId IS NULL AND SandboxProfileId IS NOT NULL AND SandboxProfileRevision IS NOT NULL AND SandboxProfileRevision > 0 AND json_type(PayloadJson, '$.Operation.SandboxCommand.ProfileId') IS 'text' AND json_extract(PayloadJson, '$.Operation.SandboxCommand.ProfileId') IS SandboxProfileId AND json_extract(PayloadJson, '$.Operation.SandboxCommand.ProfileRevision') IS SandboxProfileRevision AND length(PayloadJson) <= 1048576) OR (Permission = 'sandbox.manage' AND Kind = 0 AND ResourceType = 'agent' AND ResourceId = InstanceId AND CredentialId IS NULL AND SandboxProfileId IS NOT NULL AND SandboxProfileRevision IS NOT NULL AND SandboxProfileRevision > 0 AND json_type(PayloadJson, '$.Operation.SandboxControl.ProfileId') IS 'text' AND json_extract(PayloadJson, '$.Operation.SandboxControl.ProfileId') IS SandboxProfileId AND json_extract(PayloadJson, '$.Operation.SandboxControl.ProfileRevision') IS SandboxProfileRevision AND length(PayloadJson) <= 131072) OR (Permission = 'sandbox.read' AND Kind = 0 AND ResourceType = 'agent' AND ResourceId = InstanceId AND CredentialId IS NULL AND SandboxProfileId IS NULL AND SandboxProfileRevision IS NULL AND json_type(PayloadJson, '$.Operation.SandboxRead') IS 'object' AND json_extract(PayloadJson, '$.Operation.SandboxRead.SessionId') IS SessionId AND json_extract(PayloadJson, '$.Operation.SandboxRead.Kind') IN (0,1,2) AND json_type(PayloadJson, '$.Operation.SandboxRead.ProfileReferencesJson') IS 'text' AND length(json_extract(PayloadJson, '$.Operation.SandboxRead.ProfileReferencesJson')) <= 4096 AND json_valid(json_extract(PayloadJson, '$.Operation.SandboxRead.ProfileReferencesJson')) AND json_type(json_extract(PayloadJson, '$.Operation.SandboxRead.ProfileReferencesJson')) IS 'array' AND json_array_length(json_extract(PayloadJson, '$.Operation.SandboxRead.ProfileReferencesJson')) <= 18 AND length(PayloadJson) <= 131072) OR (Permission IN ('browser.read','browser.use') AND Kind = 0 AND ResourceType = 'agent' AND ResourceId = InstanceId AND CredentialId IS NULL AND SandboxProfileId IS NULL AND SandboxProfileRevision IS NULL AND json_type(PayloadJson, '$.Operation.BrowserCommand') IS 'object' AND json_extract(PayloadJson, '$.Operation.Persona') IS NULL AND json_extract(PayloadJson, '$.Operation.Preferences') IS NULL AND json_extract(PayloadJson, '$.Operation.CredentialCheck') IS NULL AND json_extract(PayloadJson, '$.Operation.SandboxCommand') IS NULL AND json_extract(PayloadJson, '$.Operation.SandboxControl') IS NULL AND json_extract(PayloadJson, '$.Operation.SandboxRead') IS NULL AND json_extract(PayloadJson, '$.Operation.BrowserCommand.SessionId') IS SessionId AND json_type(PayloadJson, '$.Operation.BrowserCommand.ToolName') IS 'text' AND json_type(PayloadJson, '$.Operation.BrowserCommand.ReadOnly') IS CASE WHEN Permission = 'browser.read' THEN 'true' ELSE 'false' END AND json_type(PayloadJson, '$.Operation.BrowserCommand.ArgumentsJson') IS 'text' AND json_valid(json_extract(PayloadJson, '$.Operation.BrowserCommand.ArgumentsJson')) AND length(json_extract(PayloadJson, '$.Operation.BrowserCommand.ArgumentsJson')) <= 16384 AND json_type(PayloadJson, '$.Operation.BrowserCommand.TargetJson') IS 'text' AND json_valid(json_extract(PayloadJson, '$.Operation.BrowserCommand.TargetJson')) AND length(json_extract(PayloadJson, '$.Operation.BrowserCommand.TargetJson')) <= 4096 AND json_type(PayloadJson, '$.Operation.BrowserCommand.CommandHash') IS 'text' AND length(json_extract(PayloadJson, '$.Operation.BrowserCommand.CommandHash')) = 64 AND length(PayloadJson) <= 262144) OR (Kind = 0 AND SandboxProfileId IS NULL AND SandboxProfileRevision IS NULL AND json_type(PayloadJson, '$.Operation.CredentialRead') IS 'object' AND json_extract(PayloadJson, '$.Operation.CredentialRead.SessionId') IS SessionId AND json_type(PayloadJson, '$.Operation.CredentialRead.ArgumentsJson') IS 'text' AND length(json_extract(PayloadJson, '$.Operation.CredentialRead.ArgumentsJson')) <= 4096 AND json_valid(json_extract(PayloadJson, '$.Operation.CredentialRead.ArgumentsJson')) AND json_type(PayloadJson, '$.Operation.CredentialRead.ReferencesJson') IS 'text' AND length(json_extract(PayloadJson, '$.Operation.CredentialRead.ReferencesJson')) <= 65536 AND json_valid(json_extract(PayloadJson, '$.Operation.CredentialRead.ReferencesJson')) AND json_type(json_extract(PayloadJson, '$.Operation.CredentialRead.ReferencesJson')) IS 'array' AND json_array_length(json_extract(PayloadJson, '$.Operation.CredentialRead.ReferencesJson')) <= 101 AND ((Permission = 'credentials.read' AND ResourceType = 'credential' AND CredentialId IS NOT NULL AND ResourceId = CredentialId AND json_extract(PayloadJson, '$.Operation.CredentialRead.CredentialId') IS CredentialId AND json_extract(json_extract(PayloadJson, '$.Operation.CredentialRead.ReferencesJson'), '$[0].CredentialId') IS CredentialId) OR (Permission = 'sessions.read' AND ResourceType = 'session' AND ResourceId = SessionId AND CredentialId IS NULL AND json_extract(PayloadJson, '$.Operation.CredentialRead.CredentialId') IS NULL AND json_array_length(json_extract(PayloadJson, '$.Operation.CredentialRead.ReferencesJson')) = 0)) AND json_extract(PayloadJson, '$.Operation.Persona') IS NULL AND json_extract(PayloadJson, '$.Operation.Preferences') IS NULL AND json_extract(PayloadJson, '$.Operation.CredentialCheck') IS NULL AND json_extract(PayloadJson, '$.Operation.SandboxCommand') IS NULL AND json_extract(PayloadJson, '$.Operation.SandboxControl') IS NULL AND json_extract(PayloadJson, '$.Operation.SandboxRead') IS NULL AND json_extract(PayloadJson, '$.Operation.BrowserCommand') IS NULL AND length(PayloadJson) <= 262144))) AND json_extract(PayloadJson, '$.Operation.ArtifactPublication') IS NULL) OR (Kind IN (0,1) AND Status IN (0,1,2,3,4) AND Revision >= 1 AND ExpiresAt > CreatedAt AND length(Fingerprint) = 64 AND Permission = 'artifacts.create' AND ResourceType = 'session' AND ResourceId = SessionId AND CredentialId IS NULL AND SandboxProfileId IS NULL AND SandboxProfileRevision IS NULL AND json_type(PayloadJson, '$.Operation.ArtifactPublication') IS 'object' AND json_extract(PayloadJson, '$.Operation.ArtifactPublication.SessionId') IS SessionId AND json_type(PayloadJson, '$.Operation.ArtifactPublication.ArgumentsJson') IS 'text' AND length(CAST(json_extract(PayloadJson, '$.Operation.ArtifactPublication.ArgumentsJson') AS BLOB)) <= 65536 AND json_valid(json_extract(PayloadJson, '$.Operation.ArtifactPublication.ArgumentsJson')) AND json_type(json_extract(PayloadJson, '$.Operation.ArtifactPublication.ArgumentsJson')) IS 'object' AND json_type(json_extract(PayloadJson, '$.Operation.ArtifactPublication.ArgumentsJson'), '$.displayName') IS 'text' AND json_type(json_extract(PayloadJson, '$.Operation.ArtifactPublication.ArgumentsJson'), '$.contentType') IS 'text' AND json_type(json_extract(PayloadJson, '$.Operation.ArtifactPublication.ArgumentsJson'), '$.content') IS 'text' AND length(CAST(json_extract(json_extract(PayloadJson, '$.Operation.ArtifactPublication.ArgumentsJson'), '$.content') AS BLOB)) <= 32768 AND json_extract(PayloadJson, '$.Operation.Persona') IS NULL AND json_extract(PayloadJson, '$.Operation.Preferences') IS NULL AND json_extract(PayloadJson, '$.Operation.CredentialCheck') IS NULL AND json_extract(PayloadJson, '$.Operation.SandboxCommand') IS NULL AND json_extract(PayloadJson, '$.Operation.SandboxControl') IS NULL AND json_extract(PayloadJson, '$.Operation.SandboxRead') IS NULL AND json_extract(PayloadJson, '$.Operation.BrowserCommand') IS NULL AND json_extract(PayloadJson, '$.Operation.CredentialRead') IS NULL AND length(CAST(PayloadJson AS BLOB)) <= 524288)) AND json_extract(PayloadJson, '$.Operation.ArtifactVerification') IS NULL) OR (Kind = 0 AND Status IN (0,1,2,3,4) AND Revision >= 1 AND ExpiresAt > CreatedAt AND length(Fingerprint) = 64 AND Permission = 'artifacts.read' AND ResourceType = 'artifact' AND CredentialId IS NULL AND SandboxProfileId IS NULL AND SandboxProfileRevision IS NULL AND json_type(PayloadJson, '$.Operation.ArtifactVerification') IS 'object' AND json_extract(PayloadJson, '$.Operation.ArtifactVerification.SessionId') IS SessionId AND json_extract(PayloadJson, '$.Operation.ArtifactVerification.ArtifactId') IS ResourceId AND json_type(PayloadJson, '$.Operation.ArtifactVerification.DisplayName') IS 'text' AND length(json_extract(PayloadJson, '$.Operation.ArtifactVerification.DisplayName')) BETWEEN 1 AND 200 AND json_type(PayloadJson, '$.Operation.ArtifactVerification.ContentType') IS 'text' AND length(json_extract(PayloadJson, '$.Operation.ArtifactVerification.ContentType')) BETWEEN 1 AND 200 AND json_type(PayloadJson, '$.Operation.ArtifactVerification.ByteSize') IS 'integer' AND json_extract(PayloadJson, '$.Operation.ArtifactVerification.ByteSize') >= 0 AND json_type(PayloadJson, '$.Operation.ArtifactVerification.Sha256Hex') IS 'text' AND length(json_extract(PayloadJson, '$.Operation.ArtifactVerification.Sha256Hex')) = 64 AND json_extract(PayloadJson, '$.Operation.ArtifactVerification.Sha256Hex') NOT GLOB '*[^0-9a-f]*' AND json_extract(PayloadJson, '$.Operation.Persona') IS NULL AND json_extract(PayloadJson, '$.Operation.Preferences') IS NULL AND json_extract(PayloadJson, '$.Operation.CredentialCheck') IS NULL AND json_extract(PayloadJson, '$.Operation.SandboxCommand') IS NULL AND json_extract(PayloadJson, '$.Operation.SandboxControl') IS NULL AND json_extract(PayloadJson, '$.Operation.SandboxRead') IS NULL AND json_extract(PayloadJson, '$.Operation.BrowserCommand') IS NULL AND json_extract(PayloadJson, '$.Operation.CredentialRead') IS NULL AND json_extract(PayloadJson, '$.Operation.ArtifactPublication') IS NULL AND length(CAST(PayloadJson AS BLOB)) <= 131072)) AND json_extract(PayloadJson, '$.Operation.WorkspaceRead') IS NULL) OR (Kind = 0 AND Status IN (0,1,2,3,4) AND Revision >= 1 AND ExpiresAt > CreatedAt AND length(Fingerprint) = 64 AND ((Permission = 'workspaces.read' AND ResourceType = 'workspace' AND json_extract(PayloadJson, '$.Operation.WorkspaceRead.ResourceKey') IS NULL AND json_extract(PayloadJson, '$.Operation.WorkspaceRead.ResourceRevision') IS NULL) OR (Permission = 'resources.read' AND ResourceType = 'resource'
        AND json_type(PayloadJson, '$.Operation.WorkspaceRead.ResourceKey') IS 'text'
        AND ((length(json_extract(PayloadJson, '$.Operation.WorkspaceRead.ResourceKey')) = 45 AND substr(json_extract(PayloadJson, '$.Operation.WorkspaceRead.ResourceKey'),1,9) IS 'instance:')
            OR (length(json_extract(PayloadJson, '$.Operation.WorkspaceRead.ResourceKey')) = 47 AND substr(json_extract(PayloadJson, '$.Operation.WorkspaceRead.ResourceKey'),1,11) IS 'definition:'))
        AND substr(json_extract(PayloadJson, '$.Operation.WorkspaceRead.ResourceKey'),-36) NOT GLOB '*[^0-9a-f-]*'
        AND substr(json_extract(PayloadJson, '$.Operation.WorkspaceRead.ResourceKey'),-28,1) IS '-'
        AND substr(json_extract(PayloadJson, '$.Operation.WorkspaceRead.ResourceKey'),-23,1) IS '-'
        AND substr(json_extract(PayloadJson, '$.Operation.WorkspaceRead.ResourceKey'),-18,1) IS '-'
        AND substr(json_extract(PayloadJson, '$.Operation.WorkspaceRead.ResourceKey'),-13,1) IS '-'
        AND json_type(PayloadJson, '$.Operation.WorkspaceRead.ResourceRevision') IS 'integer'
        AND json_extract(PayloadJson, '$.Operation.WorkspaceRead.ResourceRevision') >= 0
        AND json_type(PayloadJson, '$.Operation.WorkspaceRead.Working') IS 'false'
        AND json_extract(PayloadJson, '$.Operation.WorkspaceRead.ToolName') IN ('workspace.read','office.document.inspect','office.spreadsheet.inspect')
        AND (substr(json_extract(json_extract(PayloadJson, '$.Operation.WorkspaceRead.ArgumentsJson'), '$.path'),1,17) IS '/agent/resources/'
            OR substr(json_extract(json_extract(PayloadJson, '$.Operation.WorkspaceRead.ArgumentsJson'), '$.path'),1,26) IS '/agent/instance/resources/'))) AND CredentialId IS NULL AND SandboxProfileId IS NULL AND SandboxProfileRevision IS NULL AND json_type(PayloadJson, '$.Operation.WorkspaceRead') IS 'object' AND json_extract(PayloadJson, '$.Operation.WorkspaceRead.SessionId') IS SessionId AND (json_type(PayloadJson, '$.Operation.WorkspaceRead.Working') IS 'true' OR json_type(PayloadJson, '$.Operation.WorkspaceRead.Working') IS 'false') AND ((Permission = 'workspaces.read' AND ResourceId IS CASE WHEN json_extract(PayloadJson, '$.Operation.WorkspaceRead.Working') = 1 THEN 'working:' || SessionId ELSE 'home:' || InstanceId END) OR (Permission = 'resources.read' AND ResourceId IS InstanceId || ':' || json_extract(PayloadJson, '$.Operation.WorkspaceRead.ResourceKey'))) AND json_type(PayloadJson, '$.Operation.WorkspaceRead.ToolName') IS 'text' AND json_extract(PayloadJson, '$.Operation.WorkspaceRead.ToolName') IN ('workspace.read','workspace.list','workspace.search','office.document.inspect','office.spreadsheet.inspect') AND json_type(PayloadJson, '$.Operation.WorkspaceRead.ArgumentsJson') IS 'text' AND length(CAST(json_extract(PayloadJson, '$.Operation.WorkspaceRead.ArgumentsJson') AS BLOB)) <= 16384 AND json_valid(json_extract(PayloadJson, '$.Operation.WorkspaceRead.ArgumentsJson')) AND json_type(json_extract(PayloadJson, '$.Operation.WorkspaceRead.ArgumentsJson')) IS 'object' AND json_type(json_extract(PayloadJson, '$.Operation.WorkspaceRead.ArgumentsJson'), '$.path') IS 'text' AND length(json_extract(json_extract(PayloadJson, '$.Operation.WorkspaceRead.ArgumentsJson'), '$.path')) BETWEEN 1 AND 4095 AND json_type(PayloadJson, '$.Operation.WorkspaceRead.ObservationHash') IS 'text' AND length(json_extract(PayloadJson, '$.Operation.WorkspaceRead.ObservationHash')) = 64 AND json_extract(PayloadJson, '$.Operation.WorkspaceRead.ObservationHash') NOT GLOB '*[^0-9a-f]*' AND length(CAST(PayloadJson AS BLOB)) <= 131072 AND json_extract(PayloadJson, '$.Operation.Persona') IS NULL AND json_extract(PayloadJson, '$.Operation.Preferences') IS NULL AND json_extract(PayloadJson, '$.Operation.CredentialCheck') IS NULL AND json_extract(PayloadJson, '$.Operation.SandboxCommand') IS NULL AND json_extract(PayloadJson, '$.Operation.SandboxControl') IS NULL AND json_extract(PayloadJson, '$.Operation.SandboxRead') IS NULL AND json_extract(PayloadJson, '$.Operation.BrowserCommand') IS NULL AND json_extract(PayloadJson, '$.Operation.CredentialRead') IS NULL AND json_extract(PayloadJson, '$.Operation.ArtifactPublication') IS NULL AND json_extract(PayloadJson, '$.Operation.ArtifactVerification') IS NULL)) AND json_extract(PayloadJson, '$.Operation.WorkspaceMutation') IS NULL) OR COALESCE((Kind IN (0,1) AND Status IN (0,1,2,3,4) AND Revision >= 1 AND ExpiresAt > CreatedAt AND length(Fingerprint) = 64
        AND Permission IN ('workspaces.write','workspaces.delete') AND ResourceType = 'workspace'
        AND CredentialId IS NULL AND SandboxProfileId IS NULL AND SandboxProfileRevision IS NULL
        AND json_type(PayloadJson, '$.Operation.WorkspaceMutation') IS 'object'
        AND json_extract(PayloadJson, '$.Operation.WorkspaceMutation.SessionId') IS SessionId
        AND json_type(PayloadJson, '$.Operation.WorkspaceMutation.Working') IN ('true','false')
        AND ResourceId IS CASE WHEN json_extract(PayloadJson, '$.Operation.WorkspaceMutation.Working') = 1 THEN 'working:' || SessionId ELSE 'home:' || InstanceId END
        AND json_extract(PayloadJson, '$.Operation.WorkspaceMutation.Permission') IS Permission
        AND json_extract(PayloadJson, '$.Operation.WorkspaceMutation.ToolName') IN ('workspace.write','workspace.patch','workspace.mkdir','workspace.copy','workspace.move','workspace.delete','workspace.batch','office.document.create','office.document.edit','office.spreadsheet.create','office.spreadsheet.edit')
        AND (json_extract(PayloadJson, '$.Operation.WorkspaceMutation.ToolName') = 'workspace.batch'
            OR Permission IS CASE WHEN json_extract(PayloadJson, '$.Operation.WorkspaceMutation.ToolName') IN ('workspace.move','workspace.delete') THEN 'workspaces.delete' ELSE 'workspaces.write' END)
        AND json_type(PayloadJson, '$.Operation.WorkspaceMutation.ArgumentsJson') IS 'text'
        AND length(CAST(json_extract(PayloadJson, '$.Operation.WorkspaceMutation.ArgumentsJson') AS BLOB)) BETWEEN 2 AND 65536
        AND json_valid(json_extract(PayloadJson, '$.Operation.WorkspaceMutation.ArgumentsJson'))
        AND json_type(json_extract(PayloadJson, '$.Operation.WorkspaceMutation.ArgumentsJson')) IS 'object'
        AND (json_extract(PayloadJson, '$.Operation.WorkspaceMutation.ToolName') = 'workspace.batch' OR (
            CASE json_extract(PayloadJson, '$.Operation.WorkspaceMutation.ToolName')
                WHEN 'workspace.copy' THEN json_extract(json_extract(PayloadJson, '$.Operation.WorkspaceMutation.ArgumentsJson'), '$.destination')
                WHEN 'workspace.move' THEN json_extract(json_extract(PayloadJson, '$.Operation.WorkspaceMutation.ArgumentsJson'), '$.source')
                ELSE json_extract(json_extract(PayloadJson, '$.Operation.WorkspaceMutation.ArgumentsJson'), '$.path') END
            LIKE CASE WHEN json_extract(PayloadJson, '$.Operation.WorkspaceMutation.Working') = 1 THEN '/working/%' ELSE '/home/%' END))
        AND json_type(PayloadJson, '$.Operation.WorkspaceMutation.ObservationHash') IS 'text'
        AND length(json_extract(PayloadJson, '$.Operation.WorkspaceMutation.ObservationHash')) = 64
        AND json_extract(PayloadJson, '$.Operation.WorkspaceMutation.ObservationHash') NOT GLOB '*[^0-9a-f]*'
        AND json_type(PayloadJson, '$.Operation.WorkspaceMutation.TargetsJson') IS 'text'
        AND length(json_extract(PayloadJson, '$.Operation.WorkspaceMutation.TargetsJson')) BETWEEN 2 AND 4096
        AND json_valid(json_extract(PayloadJson, '$.Operation.WorkspaceMutation.TargetsJson'))
        AND json_type(json_extract(PayloadJson, '$.Operation.WorkspaceMutation.TargetsJson')) IS 'array'
        AND json_array_length(json_extract(PayloadJson, '$.Operation.WorkspaceMutation.TargetsJson')) BETWEEN 1 AND 6
        AND json_extract(PayloadJson, '$.Operation.Persona') IS NULL
        AND json_extract(PayloadJson, '$.Operation.Preferences') IS NULL
        AND json_extract(PayloadJson, '$.Operation.CredentialCheck') IS NULL
        AND json_extract(PayloadJson, '$.Operation.SandboxCommand') IS NULL
        AND json_extract(PayloadJson, '$.Operation.SandboxControl') IS NULL
        AND json_extract(PayloadJson, '$.Operation.SandboxRead') IS NULL
        AND json_extract(PayloadJson, '$.Operation.BrowserCommand') IS NULL
        AND json_extract(PayloadJson, '$.Operation.CredentialRead') IS NULL
        AND json_extract(PayloadJson, '$.Operation.ArtifactPublication') IS NULL
        AND json_extract(PayloadJson, '$.Operation.ArtifactVerification') IS NULL
        AND json_extract(PayloadJson, '$.Operation.WorkspaceRead') IS NULL
        AND length(CAST(PayloadJson AS BLOB)) <= 524288), 0)) AND json_extract(PayloadJson, '$.Operation.ArtifactPublication.Source') IS NULL) OR COALESCE((Kind IN (0,1) AND Status IN (0,1,2,3,4) AND Revision >= 1 AND ExpiresAt > CreatedAt AND length(Fingerprint) = 64
        AND Permission = 'artifacts.create' AND ResourceType = 'session' AND ResourceId IS SessionId
        AND CredentialId IS NULL AND SandboxProfileId IS NULL AND SandboxProfileRevision IS NULL
        AND json_type(PayloadJson, '$.Operation.ArtifactPublication') IS 'object'
        AND json_extract(PayloadJson, '$.Operation.ArtifactPublication.SessionId') IS SessionId
        AND json_type(PayloadJson, '$.Operation.ArtifactPublication.Source') IS 'object'
        AND json_type(PayloadJson, '$.Operation.ArtifactPublication.Source.Working') IN ('true','false')
        AND json_type(PayloadJson, '$.Operation.ArtifactPublication.Source.Path') IS 'text'
        AND length(CAST(json_extract(PayloadJson, '$.Operation.ArtifactPublication.Source.Path') AS BLOB)) BETWEEN 7 AND 4095
        AND ((json_extract(PayloadJson, '$.Operation.ArtifactPublication.Source.ResourceKey') IS NULL
            AND json_extract(PayloadJson, '$.Operation.ArtifactPublication.Source.ResourceRevision') IS NULL
            AND CASE WHEN json_extract(PayloadJson, '$.Operation.ArtifactPublication.Source.Working') = 1
            THEN substr(json_extract(PayloadJson, '$.Operation.ArtifactPublication.Source.Path'),1,9) IS '/working/'
            ELSE substr(json_extract(PayloadJson, '$.Operation.ArtifactPublication.Source.Path'),1,6) IS '/home/' END)
            OR (json_type(PayloadJson, '$.Operation.ArtifactPublication.Source.Working') IS 'false'
                AND json_type(PayloadJson, '$.Operation.ArtifactPublication.Source.ResourceKey') IS 'text'
                AND ((length(json_extract(PayloadJson, '$.Operation.ArtifactPublication.Source.ResourceKey')) = 45 AND substr(json_extract(PayloadJson, '$.Operation.ArtifactPublication.Source.ResourceKey'),1,9) IS 'instance:')
                    OR (length(json_extract(PayloadJson, '$.Operation.ArtifactPublication.Source.ResourceKey')) = 47 AND substr(json_extract(PayloadJson, '$.Operation.ArtifactPublication.Source.ResourceKey'),1,11) IS 'definition:'))
                AND json_type(PayloadJson, '$.Operation.ArtifactPublication.Source.ResourceRevision') IS 'integer'
                AND json_extract(PayloadJson, '$.Operation.ArtifactPublication.Source.ResourceRevision') >= 0
                AND (substr(json_extract(PayloadJson, '$.Operation.ArtifactPublication.Source.Path'),1,17) IS '/agent/resources/'
                    OR substr(json_extract(PayloadJson, '$.Operation.ArtifactPublication.Source.Path'),1,26) IS '/agent/instance/resources/')))
        AND json_type(PayloadJson, '$.Operation.ArtifactPublication.Source.ContentType') IS 'text'
        AND length(json_extract(PayloadJson, '$.Operation.ArtifactPublication.Source.ContentType')) BETWEEN 1 AND 200
        AND json_type(PayloadJson, '$.Operation.ArtifactPublication.Source.ByteSize') IS 'integer'
        AND json_extract(PayloadJson, '$.Operation.ArtifactPublication.Source.ByteSize') BETWEEN 0 AND 52428800
        AND json_type(PayloadJson, '$.Operation.ArtifactPublication.Source.Sha256Hex') IS 'text'
        AND length(json_extract(PayloadJson, '$.Operation.ArtifactPublication.Source.Sha256Hex')) = 64
        AND json_extract(PayloadJson, '$.Operation.ArtifactPublication.Source.Sha256Hex') NOT GLOB '*[^0-9a-f]*'
        AND json_type(PayloadJson, '$.Operation.ArtifactPublication.Source.ObservationHash') IS 'text'
        AND length(json_extract(PayloadJson, '$.Operation.ArtifactPublication.Source.ObservationHash')) = 64
        AND json_extract(PayloadJson, '$.Operation.ArtifactPublication.Source.ObservationHash') NOT GLOB '*[^0-9a-f]*'
        AND json_type(PayloadJson, '$.Operation.ArtifactPublication.ArgumentsJson') IS 'text'
        AND length(CAST(json_extract(PayloadJson, '$.Operation.ArtifactPublication.ArgumentsJson') AS BLOB)) BETWEEN 2 AND 65536
        AND json_valid(json_extract(PayloadJson, '$.Operation.ArtifactPublication.ArgumentsJson'))
        AND json_type(json_extract(PayloadJson, '$.Operation.ArtifactPublication.ArgumentsJson')) IS 'object'
        AND json_type(json_extract(PayloadJson, '$.Operation.ArtifactPublication.ArgumentsJson'), '$.path') IS 'text'
        AND json_extract(json_extract(PayloadJson, '$.Operation.ArtifactPublication.ArgumentsJson'), '$.path') IS json_extract(PayloadJson, '$.Operation.ArtifactPublication.Source.Path')
        AND json_type(json_extract(PayloadJson, '$.Operation.ArtifactPublication.ArgumentsJson'), '$.displayName') IS 'text'
        AND length(json_extract(json_extract(PayloadJson, '$.Operation.ArtifactPublication.ArgumentsJson'), '$.displayName')) BETWEEN 1 AND 200
        AND (json_type(json_extract(PayloadJson, '$.Operation.ArtifactPublication.ArgumentsJson'), '$.contentType') IS NULL OR (
            json_type(json_extract(PayloadJson, '$.Operation.ArtifactPublication.ArgumentsJson'), '$.contentType') IS 'text'
            AND length(json_extract(json_extract(PayloadJson, '$.Operation.ArtifactPublication.ArgumentsJson'), '$.contentType')) BETWEEN 1 AND 200))
        AND json_type(json_extract(PayloadJson, '$.Operation.ArtifactPublication.ArgumentsJson'), '$.content') IS NULL
        AND json_extract(PayloadJson, '$.Operation.Persona') IS NULL
        AND json_extract(PayloadJson, '$.Operation.Preferences') IS NULL
        AND json_extract(PayloadJson, '$.Operation.CredentialCheck') IS NULL
        AND json_extract(PayloadJson, '$.Operation.SandboxCommand') IS NULL
        AND json_extract(PayloadJson, '$.Operation.SandboxControl') IS NULL
        AND json_extract(PayloadJson, '$.Operation.SandboxRead') IS NULL
        AND json_extract(PayloadJson, '$.Operation.BrowserCommand') IS NULL
        AND json_extract(PayloadJson, '$.Operation.CredentialRead') IS NULL
        AND json_extract(PayloadJson, '$.Operation.ArtifactVerification') IS NULL
        AND json_extract(PayloadJson, '$.Operation.WorkspaceRead') IS NULL
        AND json_extract(PayloadJson, '$.Operation.WorkspaceMutation') IS NULL
        AND length(CAST(PayloadJson AS BLOB)) <= 524288), 0)) AND json_extract(PayloadJson, '$.Operation.WorkspaceRead.ResourceMembersJson') IS NULL) OR COALESCE((Kind = 0 AND Status IN (0,1,2,3,4) AND Revision >= 1 AND ExpiresAt > CreatedAt AND length(Fingerprint) = 64
        AND Permission = 'sessions.read' AND ResourceType = 'session' AND ResourceId IS SessionId
        AND CredentialId IS NULL AND SandboxProfileId IS NULL AND SandboxProfileRevision IS NULL
        AND json_type(PayloadJson, '$.Operation.WorkspaceRead') IS 'object'
        AND json_extract(PayloadJson, '$.Operation.WorkspaceRead.SessionId') IS SessionId
        AND json_type(PayloadJson, '$.Operation.WorkspaceRead.Working') IS 'false'
        AND json_extract(PayloadJson, '$.Operation.WorkspaceRead.ResourceKey') IS NULL
        AND json_extract(PayloadJson, '$.Operation.WorkspaceRead.ResourceRevision') IS NULL
        AND json_extract(PayloadJson, '$.Operation.WorkspaceRead.ToolName') IN ('workspace.list','workspace.search')
        AND json_type(PayloadJson, '$.Operation.WorkspaceRead.ArgumentsJson') IS 'text'
        AND length(CAST(json_extract(PayloadJson, '$.Operation.WorkspaceRead.ArgumentsJson') AS BLOB)) <= 16384
        AND json_valid(json_extract(PayloadJson, '$.Operation.WorkspaceRead.ArgumentsJson'))
        AND json_type(json_extract(PayloadJson, '$.Operation.WorkspaceRead.ArgumentsJson'), '$.path') IS 'text'
        AND (json_extract(json_extract(PayloadJson, '$.Operation.WorkspaceRead.ArgumentsJson'), '$.path') IN ('/agent/resources','/agent/instance/resources')
            OR substr(json_extract(json_extract(PayloadJson, '$.Operation.WorkspaceRead.ArgumentsJson'), '$.path'),1,17) IS '/agent/resources/'
            OR substr(json_extract(json_extract(PayloadJson, '$.Operation.WorkspaceRead.ArgumentsJson'), '$.path'),1,26) IS '/agent/instance/resources/')
        AND json_type(PayloadJson, '$.Operation.WorkspaceRead.ResourceMembersJson') IS 'text'
        AND length(CAST(json_extract(PayloadJson, '$.Operation.WorkspaceRead.ResourceMembersJson') AS BLOB)) <= 65536
        AND json_valid(json_extract(PayloadJson, '$.Operation.WorkspaceRead.ResourceMembersJson'))
        AND json_type(json_extract(PayloadJson, '$.Operation.WorkspaceRead.ResourceMembersJson')) IS 'array'
        AND json_array_length(json_extract(PayloadJson, '$.Operation.WorkspaceRead.ResourceMembersJson')) <= 257
        AND json_type(PayloadJson, '$.Operation.WorkspaceRead.ObservationHash') IS 'text'
        AND length(json_extract(PayloadJson, '$.Operation.WorkspaceRead.ObservationHash')) = 64
        AND json_extract(PayloadJson, '$.Operation.WorkspaceRead.ObservationHash') NOT GLOB '*[^0-9a-f]*'
        AND json_extract(PayloadJson, '$.Operation.Persona') IS NULL
        AND json_extract(PayloadJson, '$.Operation.Preferences') IS NULL
        AND json_extract(PayloadJson, '$.Operation.CredentialCheck') IS NULL
        AND json_extract(PayloadJson, '$.Operation.SandboxCommand') IS NULL
        AND json_extract(PayloadJson, '$.Operation.SandboxControl') IS NULL
        AND json_extract(PayloadJson, '$.Operation.SandboxRead') IS NULL
        AND json_extract(PayloadJson, '$.Operation.BrowserCommand') IS NULL
        AND json_extract(PayloadJson, '$.Operation.CredentialRead') IS NULL
        AND json_extract(PayloadJson, '$.Operation.ArtifactPublication') IS NULL
        AND json_extract(PayloadJson, '$.Operation.ArtifactVerification') IS NULL
        AND json_extract(PayloadJson, '$.Operation.WorkspaceMutation') IS NULL
        AND length(CAST(PayloadJson AS BLOB)) <= 131072),0)) AND json_extract(PayloadJson, '$.Operation.WorkspaceRead.Knowledge') IS NULL) OR COALESCE((Kind = 0 AND Status IN (0,1,2,3,4) AND Revision >= 1 AND ExpiresAt > CreatedAt AND length(Fingerprint) = 64
        AND Permission = 'resources.read' AND ResourceType = 'resource'
        AND ResourceId IS InstanceId || ':' || json_extract(PayloadJson, '$.Operation.WorkspaceRead.ResourceKey')
        AND CredentialId IS NULL AND SandboxProfileId IS NULL AND SandboxProfileRevision IS NULL
        AND json_type(PayloadJson, '$.Operation.WorkspaceRead') IS 'object'
        AND json_extract(PayloadJson, '$.Operation.WorkspaceRead.SessionId') IS SessionId
        AND json_type(PayloadJson, '$.Operation.WorkspaceRead.Working') IS 'false'
        AND json_extract(PayloadJson, '$.Operation.WorkspaceRead.ToolName') IS 'knowledge.retrieve'
        AND json_type(PayloadJson, '$.Operation.WorkspaceRead.ResourceKey') IS 'text'
        AND ((length(json_extract(PayloadJson, '$.Operation.WorkspaceRead.ResourceKey')) = 45 AND substr(json_extract(PayloadJson, '$.Operation.WorkspaceRead.ResourceKey'),1,9) IS 'instance:')
            OR (length(json_extract(PayloadJson, '$.Operation.WorkspaceRead.ResourceKey')) = 47 AND substr(json_extract(PayloadJson, '$.Operation.WorkspaceRead.ResourceKey'),1,11) IS 'definition:'))
        AND json_type(PayloadJson, '$.Operation.WorkspaceRead.ResourceRevision') IS 'integer'
        AND json_extract(PayloadJson, '$.Operation.WorkspaceRead.ResourceRevision') >= 0
        AND json_extract(PayloadJson, '$.Operation.WorkspaceRead.ResourceMembersJson') IS NULL
        AND json_type(PayloadJson, '$.Operation.WorkspaceRead.ArgumentsJson') IS 'text'
        AND length(CAST(json_extract(PayloadJson, '$.Operation.WorkspaceRead.ArgumentsJson') AS BLOB)) <= 16384
        AND json_valid(json_extract(PayloadJson, '$.Operation.WorkspaceRead.ArgumentsJson'))
        AND json_type(json_extract(PayloadJson, '$.Operation.WorkspaceRead.ArgumentsJson'), '$.identity') IS 'text'
        AND json_type(PayloadJson, '$.Operation.WorkspaceRead.Knowledge') IS 'object'
        AND json_type(PayloadJson, '$.Operation.WorkspaceRead.Knowledge.Identity') IS 'text'
        AND length(json_extract(PayloadJson, '$.Operation.WorkspaceRead.Knowledge.Identity')) BETWEEN 1 AND 240
        AND json_extract(PayloadJson, '$.Operation.WorkspaceRead.Knowledge.Identity') IS json_extract(json_extract(PayloadJson, '$.Operation.WorkspaceRead.ArgumentsJson'), '$.identity')
        AND json_type(PayloadJson, '$.Operation.WorkspaceRead.Knowledge.Title') IS 'text'
        AND length(json_extract(PayloadJson, '$.Operation.WorkspaceRead.Knowledge.Title')) BETWEEN 1 AND 512
        AND json_type(PayloadJson, '$.Operation.WorkspaceRead.Knowledge.Citation') IS 'text'
        AND length(json_extract(PayloadJson, '$.Operation.WorkspaceRead.Knowledge.Citation')) BETWEEN 1 AND 1024
        AND json_type(PayloadJson, '$.Operation.WorkspaceRead.Knowledge.Path') IS 'text'
        AND length(CAST(json_extract(PayloadJson, '$.Operation.WorkspaceRead.Knowledge.Path') AS BLOB)) BETWEEN 1 AND 4095
        AND (substr(json_extract(PayloadJson, '$.Operation.WorkspaceRead.Knowledge.Path'),1,17) IS '/agent/resources/'
            OR substr(json_extract(PayloadJson, '$.Operation.WorkspaceRead.Knowledge.Path'),1,26) IS '/agent/instance/resources/')
        AND json_type(PayloadJson, '$.Operation.WorkspaceRead.ObservationHash') IS 'text'
        AND length(json_extract(PayloadJson, '$.Operation.WorkspaceRead.ObservationHash')) = 64
        AND json_extract(PayloadJson, '$.Operation.WorkspaceRead.ObservationHash') NOT GLOB '*[^0-9a-f]*'
        AND json_extract(PayloadJson, '$.Operation.Persona') IS NULL
        AND json_extract(PayloadJson, '$.Operation.Preferences') IS NULL
        AND json_extract(PayloadJson, '$.Operation.CredentialCheck') IS NULL
        AND json_extract(PayloadJson, '$.Operation.SandboxCommand') IS NULL
        AND json_extract(PayloadJson, '$.Operation.SandboxControl') IS NULL
        AND json_extract(PayloadJson, '$.Operation.SandboxRead') IS NULL
        AND json_extract(PayloadJson, '$.Operation.BrowserCommand') IS NULL
        AND json_extract(PayloadJson, '$.Operation.CredentialRead') IS NULL
        AND json_extract(PayloadJson, '$.Operation.ArtifactPublication') IS NULL
        AND json_extract(PayloadJson, '$.Operation.ArtifactVerification') IS NULL
        AND json_extract(PayloadJson, '$.Operation.WorkspaceMutation') IS NULL
        AND length(CAST(PayloadJson AS BLOB)) <= 131072),0)) AND json_extract(PayloadJson, '$.Operation.AttachmentRead') IS NULL) OR COALESCE((Kind = 0 AND Status IN (0,1,2,3,4) AND Revision >= 1 AND ExpiresAt > CreatedAt AND length(Fingerprint) = 64
        AND Permission = 'attachments.read' AND ResourceType = 'attachment'
        AND CredentialId IS NULL AND SandboxProfileId IS NULL AND SandboxProfileRevision IS NULL
        AND json_type(PayloadJson, '$.Operation.AttachmentRead') IS 'object'
        AND json_extract(PayloadJson, '$.Operation.AttachmentRead.SessionId') IS SessionId
        AND json_extract(PayloadJson, '$.Operation.AttachmentRead.AttachmentId') IS ResourceId
        AND length(ResourceId) = 36 AND ResourceId NOT GLOB '*[^0-9a-f-]*'
        AND substr(ResourceId,9,1) IS '-' AND substr(ResourceId,14,1) IS '-' AND substr(ResourceId,19,1) IS '-' AND substr(ResourceId,24,1) IS '-'
        AND json_type(PayloadJson, '$.Operation.AttachmentRead.EntryId') IS 'text'
        AND length(json_extract(PayloadJson, '$.Operation.AttachmentRead.EntryId')) = 36
        AND json_type(PayloadJson, '$.Operation.AttachmentRead.DisplayName') IS 'text'
        AND length(json_extract(PayloadJson, '$.Operation.AttachmentRead.DisplayName')) BETWEEN 1 AND 200
        AND json_type(PayloadJson, '$.Operation.AttachmentRead.ContentType') IS 'text'
        AND length(json_extract(PayloadJson, '$.Operation.AttachmentRead.ContentType')) BETWEEN 1 AND 200
        AND json_type(PayloadJson, '$.Operation.AttachmentRead.ByteSize') IS 'integer'
        AND json_extract(PayloadJson, '$.Operation.AttachmentRead.ByteSize') BETWEEN 0 AND 26214400
        AND json_type(PayloadJson, '$.Operation.AttachmentRead.Sha256Hex') IS 'text'
        AND length(json_extract(PayloadJson, '$.Operation.AttachmentRead.Sha256Hex')) = 64
        AND json_extract(PayloadJson, '$.Operation.AttachmentRead.Sha256Hex') NOT GLOB '*[^0-9a-f]*'
        AND (json_extract(PayloadJson, '$.Operation.AttachmentRead.ProcessorVersion') IS NULL OR
            (json_type(PayloadJson, '$.Operation.AttachmentRead.ProcessorVersion') IS 'text'
                AND length(json_extract(PayloadJson, '$.Operation.AttachmentRead.ProcessorVersion')) BETWEEN 1 AND 128))
        AND json_type(PayloadJson, '$.Operation.AttachmentRead.Vision') IN ('true','false')
        AND json_type(PayloadJson, '$.Operation.AttachmentRead.ArgumentsJson') IS 'text'
        AND length(CAST(json_extract(PayloadJson, '$.Operation.AttachmentRead.ArgumentsJson') AS BLOB)) <= 4096
        AND json_valid(json_extract(PayloadJson, '$.Operation.AttachmentRead.ArgumentsJson'))
        AND json_type(json_extract(PayloadJson, '$.Operation.AttachmentRead.ArgumentsJson')) IS 'object'
        AND json_extract(json_extract(PayloadJson, '$.Operation.AttachmentRead.ArgumentsJson'), '$.attachmentId') IS ResourceId
        AND json_extract(PayloadJson, '$.Operation.Persona') IS NULL
        AND json_extract(PayloadJson, '$.Operation.Preferences') IS NULL
        AND json_extract(PayloadJson, '$.Operation.CredentialCheck') IS NULL
        AND json_extract(PayloadJson, '$.Operation.SandboxCommand') IS NULL
        AND json_extract(PayloadJson, '$.Operation.SandboxControl') IS NULL
        AND json_extract(PayloadJson, '$.Operation.SandboxRead') IS NULL
        AND json_extract(PayloadJson, '$.Operation.BrowserCommand') IS NULL
        AND json_extract(PayloadJson, '$.Operation.CredentialRead') IS NULL
        AND json_extract(PayloadJson, '$.Operation.ArtifactPublication') IS NULL
        AND json_extract(PayloadJson, '$.Operation.ArtifactVerification') IS NULL
        AND json_extract(PayloadJson, '$.Operation.WorkspaceRead') IS NULL
        AND json_extract(PayloadJson, '$.Operation.WorkspaceMutation') IS NULL
        AND length(CAST(PayloadJson AS BLOB)) <= 131072),0)) AND json_extract(PayloadJson, '$.Operation.ArtifactPublication.Source.AttachmentId') IS NULL AND json_extract(PayloadJson, '$.Operation.ArtifactPublication.Source.EntryId') IS NULL AND json_extract(PayloadJson, '$.Operation.ArtifactPublication.Source.AttachmentName') IS NULL) OR COALESCE((Kind IN (0,1) AND Status IN (0,1,2,3,4) AND Revision >= 1 AND ExpiresAt > CreatedAt AND length(Fingerprint) = 64
        AND Permission = 'artifacts.create' AND ResourceType = 'session' AND ResourceId IS SessionId
        AND CredentialId IS NULL AND SandboxProfileId IS NULL AND SandboxProfileRevision IS NULL
        AND json_type(PayloadJson, '$.Operation.ArtifactPublication') IS 'object'
        AND json_extract(PayloadJson, '$.Operation.ArtifactPublication.SessionId') IS SessionId
        AND json_type(PayloadJson, '$.Operation.ArtifactPublication.Source') IS 'object'
        AND json_type(PayloadJson, '$.Operation.ArtifactPublication.Source.Working') IN ('true','false')
        AND json_type(PayloadJson, '$.Operation.ArtifactPublication.Source.Path') IS 'text'
        AND length(CAST(json_extract(PayloadJson, '$.Operation.ArtifactPublication.Source.Path') AS BLOB)) BETWEEN 7 AND 4095
        AND json_type(PayloadJson, '$.Operation.ArtifactPublication.Source.Working') IS 'true'
        AND json_extract(PayloadJson, '$.Operation.ArtifactPublication.Source.ResourceKey') IS NULL
        AND json_extract(PayloadJson, '$.Operation.ArtifactPublication.Source.ResourceRevision') IS NULL
        AND json_type(PayloadJson, '$.Operation.ArtifactPublication.Source.AttachmentId') IS 'text'
        AND length(json_extract(PayloadJson, '$.Operation.ArtifactPublication.Source.AttachmentId')) = 36
        AND json_extract(PayloadJson, '$.Operation.ArtifactPublication.Source.AttachmentId') NOT GLOB '*[^0-9a-f-]*'
        AND json_extract(PayloadJson, '$.Operation.ArtifactPublication.Source.AttachmentId') <> '00000000-0000-0000-0000-000000000000'
        AND substr(json_extract(PayloadJson, '$.Operation.ArtifactPublication.Source.AttachmentId'),9,1) IS '-'
        AND substr(json_extract(PayloadJson, '$.Operation.ArtifactPublication.Source.AttachmentId'),14,1) IS '-'
        AND substr(json_extract(PayloadJson, '$.Operation.ArtifactPublication.Source.AttachmentId'),19,1) IS '-'
        AND substr(json_extract(PayloadJson, '$.Operation.ArtifactPublication.Source.AttachmentId'),24,1) IS '-'
        AND json_type(PayloadJson, '$.Operation.ArtifactPublication.Source.AttachmentName') IS 'text'
        AND length(json_extract(PayloadJson, '$.Operation.ArtifactPublication.Source.AttachmentName')) BETWEEN 1 AND 200
        AND json_type(PayloadJson, '$.Operation.ArtifactPublication.Source.EntryId') IS 'text'
        AND length(json_extract(PayloadJson, '$.Operation.ArtifactPublication.Source.EntryId')) = 36
        AND json_extract(PayloadJson, '$.Operation.ArtifactPublication.Source.EntryId') <> '00000000-0000-0000-0000-000000000000'
        AND length(replace(json_extract(PayloadJson, '$.Operation.ArtifactPublication.Source.AttachmentId'),'-','')) = 32 AND replace(json_extract(PayloadJson, '$.Operation.ArtifactPublication.Source.AttachmentId'),'-','') NOT GLOB '*[^0-9a-f]*'
        AND length(replace(json_extract(PayloadJson, '$.Operation.ArtifactPublication.Source.EntryId'),'-','')) = 32 AND replace(json_extract(PayloadJson, '$.Operation.ArtifactPublication.Source.EntryId'),'-','') NOT GLOB '*[^0-9a-f]*'
        AND substr(json_extract(PayloadJson, '$.Operation.ArtifactPublication.Source.EntryId'),9,1) IS '-'
        AND substr(json_extract(PayloadJson, '$.Operation.ArtifactPublication.Source.EntryId'),14,1) IS '-'
        AND substr(json_extract(PayloadJson, '$.Operation.ArtifactPublication.Source.EntryId'),19,1) IS '-'
        AND substr(json_extract(PayloadJson, '$.Operation.ArtifactPublication.Source.EntryId'),24,1) IS '-'
        AND json_extract(PayloadJson, '$.Operation.ArtifactPublication.Source.Path') IS '/attachments/' || json_extract(PayloadJson, '$.Operation.ArtifactPublication.Source.AttachmentId')
        AND json_type(PayloadJson, '$.Operation.ArtifactPublication.Source.ContentType') IS 'text'
        AND length(json_extract(PayloadJson, '$.Operation.ArtifactPublication.Source.ContentType')) BETWEEN 1 AND 200
        AND json_type(PayloadJson, '$.Operation.ArtifactPublication.Source.ByteSize') IS 'integer'
        AND json_extract(PayloadJson, '$.Operation.ArtifactPublication.Source.ByteSize') BETWEEN 0 AND 26214400
        AND json_type(PayloadJson, '$.Operation.ArtifactPublication.Source.Sha256Hex') IS 'text'
        AND length(json_extract(PayloadJson, '$.Operation.ArtifactPublication.Source.Sha256Hex')) = 64
        AND json_extract(PayloadJson, '$.Operation.ArtifactPublication.Source.Sha256Hex') NOT GLOB '*[^0-9a-f]*'
        AND json_type(PayloadJson, '$.Operation.ArtifactPublication.Source.ObservationHash') IS 'text'
        AND length(json_extract(PayloadJson, '$.Operation.ArtifactPublication.Source.ObservationHash')) = 64
        AND json_extract(PayloadJson, '$.Operation.ArtifactPublication.Source.ObservationHash') NOT GLOB '*[^0-9a-f]*'
        AND json_type(PayloadJson, '$.Operation.ArtifactPublication.ArgumentsJson') IS 'text'
        AND length(CAST(json_extract(PayloadJson, '$.Operation.ArtifactPublication.ArgumentsJson') AS BLOB)) BETWEEN 2 AND 65536
        AND json_valid(json_extract(PayloadJson, '$.Operation.ArtifactPublication.ArgumentsJson'))
        AND json_type(json_extract(PayloadJson, '$.Operation.ArtifactPublication.ArgumentsJson')) IS 'object'
        AND json_type(json_extract(PayloadJson, '$.Operation.ArtifactPublication.ArgumentsJson'), '$.path') IS 'text'
        AND json_extract(json_extract(PayloadJson, '$.Operation.ArtifactPublication.ArgumentsJson'), '$.path') IS json_extract(PayloadJson, '$.Operation.ArtifactPublication.Source.Path')
        AND json_type(json_extract(PayloadJson, '$.Operation.ArtifactPublication.ArgumentsJson'), '$.displayName') IS 'text'
        AND length(json_extract(json_extract(PayloadJson, '$.Operation.ArtifactPublication.ArgumentsJson'), '$.displayName')) BETWEEN 1 AND 200
        AND (json_type(json_extract(PayloadJson, '$.Operation.ArtifactPublication.ArgumentsJson'), '$.contentType') IS NULL OR (
            json_type(json_extract(PayloadJson, '$.Operation.ArtifactPublication.ArgumentsJson'), '$.contentType') IS 'text'
            AND length(json_extract(json_extract(PayloadJson, '$.Operation.ArtifactPublication.ArgumentsJson'), '$.contentType')) BETWEEN 1 AND 200))
        AND json_type(json_extract(PayloadJson, '$.Operation.ArtifactPublication.ArgumentsJson'), '$.content') IS NULL
        AND json_extract(PayloadJson, '$.Operation.Persona') IS NULL
        AND json_extract(PayloadJson, '$.Operation.Preferences') IS NULL
        AND json_extract(PayloadJson, '$.Operation.CredentialCheck') IS NULL
        AND json_extract(PayloadJson, '$.Operation.SandboxCommand') IS NULL
        AND json_extract(PayloadJson, '$.Operation.SandboxControl') IS NULL
        AND json_extract(PayloadJson, '$.Operation.SandboxRead') IS NULL
        AND json_extract(PayloadJson, '$.Operation.BrowserCommand') IS NULL
        AND json_extract(PayloadJson, '$.Operation.CredentialRead') IS NULL
        AND json_extract(PayloadJson, '$.Operation.ArtifactVerification') IS NULL
        AND json_extract(PayloadJson, '$.Operation.WorkspaceRead') IS NULL
        AND json_extract(PayloadJson, '$.Operation.WorkspaceMutation') IS NULL
        AND length(CAST(PayloadJson AS BLOB)) <= 524288
        AND json_extract(PayloadJson, '$.Operation.AttachmentRead') IS NULL),0)) AND json_extract(PayloadJson, '$.Operation.WorkspaceRead.AttachmentMembersJson') IS NULL) OR COALESCE((Kind = 0 AND Status IN (0,1,2,3,4) AND Revision >= 1 AND ExpiresAt > CreatedAt AND length(Fingerprint) = 64
        AND Permission = 'sessions.read' AND ResourceType = 'session' AND ResourceId IS SessionId
        AND CredentialId IS NULL AND SandboxProfileId IS NULL AND SandboxProfileRevision IS NULL
        AND json_type(PayloadJson, '$.Operation.WorkspaceRead') IS 'object'
        AND json_extract(PayloadJson, '$.Operation.WorkspaceRead.SessionId') IS SessionId
        AND json_type(PayloadJson, '$.Operation.WorkspaceRead.Working') IS 'false'
        AND json_extract(PayloadJson, '$.Operation.WorkspaceRead.ResourceKey') IS NULL
        AND json_extract(PayloadJson, '$.Operation.WorkspaceRead.ResourceRevision') IS NULL
        AND json_extract(PayloadJson, '$.Operation.WorkspaceRead.ToolName') IN ('workspace.list','workspace.search')
        AND json_type(PayloadJson, '$.Operation.WorkspaceRead.ArgumentsJson') IS 'text'
        AND length(CAST(json_extract(PayloadJson, '$.Operation.WorkspaceRead.ArgumentsJson') AS BLOB)) <= 16384
        AND json_valid(json_extract(PayloadJson, '$.Operation.WorkspaceRead.ArgumentsJson'))
        AND json_type(json_extract(PayloadJson, '$.Operation.WorkspaceRead.ArgumentsJson'), '$.path') IS 'text'
        AND json_extract(json_extract(PayloadJson, '$.Operation.WorkspaceRead.ArgumentsJson'), '$.path') IS '/attachments'
        AND json_extract(PayloadJson, '$.Operation.WorkspaceRead.ResourceMembersJson') IS NULL
        AND json_extract(PayloadJson, '$.Operation.WorkspaceRead.Knowledge') IS NULL
        AND json_extract(PayloadJson, '$.Operation.AttachmentRead') IS NULL
        AND json_type(PayloadJson, '$.Operation.WorkspaceRead.AttachmentMembersJson') IS 'text'
        AND length(CAST(json_extract(PayloadJson, '$.Operation.WorkspaceRead.AttachmentMembersJson') AS BLOB)) <= 65536
        AND json_valid(json_extract(PayloadJson, '$.Operation.WorkspaceRead.AttachmentMembersJson'))
        AND json_type(json_extract(PayloadJson, '$.Operation.WorkspaceRead.AttachmentMembersJson')) IS 'array'
        AND json_array_length(json_extract(PayloadJson, '$.Operation.WorkspaceRead.AttachmentMembersJson')) <= 257
        AND json_type(PayloadJson, '$.Operation.WorkspaceRead.ObservationHash') IS 'text'
        AND length(json_extract(PayloadJson, '$.Operation.WorkspaceRead.ObservationHash')) = 64
        AND json_extract(PayloadJson, '$.Operation.WorkspaceRead.ObservationHash') NOT GLOB '*[^0-9a-f]*'
        AND json_extract(PayloadJson, '$.Operation.Persona') IS NULL
        AND json_extract(PayloadJson, '$.Operation.Preferences') IS NULL
        AND json_extract(PayloadJson, '$.Operation.CredentialCheck') IS NULL
        AND json_extract(PayloadJson, '$.Operation.SandboxCommand') IS NULL
        AND json_extract(PayloadJson, '$.Operation.SandboxControl') IS NULL
        AND json_extract(PayloadJson, '$.Operation.SandboxRead') IS NULL
        AND json_extract(PayloadJson, '$.Operation.BrowserCommand') IS NULL
        AND json_extract(PayloadJson, '$.Operation.CredentialRead') IS NULL
        AND json_extract(PayloadJson, '$.Operation.ArtifactPublication') IS NULL
        AND json_extract(PayloadJson, '$.Operation.ArtifactVerification') IS NULL
        AND json_extract(PayloadJson, '$.Operation.WorkspaceMutation') IS NULL
        AND length(CAST(PayloadJson AS BLOB)) <= 131072),0)) AND json_extract(PayloadJson, '$.Operation.BackgroundStart') IS NULL) OR COALESCE((Kind IN (0,1) AND Status IN (0,1,2,3,4) AND Revision >= 1 AND ExpiresAt > CreatedAt AND length(Fingerprint) = 64
        AND Permission IN ('sessions.create','agent-runs.create') AND ResourceType = 'tenant' AND ResourceId IS lower(TenantId)
        AND CredentialId IS NULL AND SandboxProfileId IS NULL AND SandboxProfileRevision IS NULL
        AND json_type(PayloadJson, '$.Operation.BackgroundStart') IS 'object'
        AND json_extract(PayloadJson, '$.Operation.BackgroundStart.TenantId') IS lower(TenantId)
        AND json_extract(PayloadJson, '$.Operation.BackgroundStart.SessionId') IS SessionId
        AND json_extract(PayloadJson, '$.Operation.BackgroundStart.Permission') IS Permission
        AND json_type(PayloadJson, '$.Operation.BackgroundStart.Objective') IS 'text'
        AND length(json_extract(PayloadJson, '$.Operation.BackgroundStart.Objective')) BETWEEN 1 AND 4000
        AND json_type(PayloadJson, '$.Operation.BackgroundStart.Title') IS 'text'
        AND length(json_extract(PayloadJson, '$.Operation.BackgroundStart.Title')) BETWEEN 1 AND 80
        AND json_type(PayloadJson, '$.Operation.BackgroundStart.ReportCompletion') IN ('true','false')
        AND json_type(PayloadJson, '$.Operation.BackgroundStart.ConfigurationHash') IS 'text'
        AND length(json_extract(PayloadJson, '$.Operation.BackgroundStart.ConfigurationHash')) = 64
        AND json_extract(PayloadJson, '$.Operation.BackgroundStart.ConfigurationHash') NOT GLOB '*[^0-9a-f]*'
        AND json_type(PayloadJson, '$.Operation.BackgroundStart.Model') IS 'object'
        AND json_type(PayloadJson, '$.Operation.BackgroundStart.Model.CatalogKey') IS 'text'
        AND length(json_extract(PayloadJson, '$.Operation.BackgroundStart.Model.CatalogKey')) BETWEEN 1 AND 128
        AND json_type(PayloadJson, '$.Operation.BackgroundStart.Model.ProviderAlias') IS 'text'
        AND length(json_extract(PayloadJson, '$.Operation.BackgroundStart.Model.ProviderAlias')) BETWEEN 1 AND 128
        AND json_type(PayloadJson, '$.Operation.BackgroundStart.Model.ModelId') IS 'text'
        AND length(json_extract(PayloadJson, '$.Operation.BackgroundStart.Model.ModelId')) BETWEEN 1 AND 128
        AND length(CAST(PayloadJson AS BLOB)) <= 131072 AND json_type(PayloadJson, '$.Operation.BackgroundStart.ChildSessionId') IS 'text' AND length(json_extract(PayloadJson, '$.Operation.BackgroundStart.ChildSessionId')) = 36 AND json_extract(PayloadJson, '$.Operation.BackgroundStart.ChildSessionId') NOT GLOB '*[^0-9a-f-]*' AND substr(json_extract(PayloadJson, '$.Operation.BackgroundStart.ChildSessionId'),9,1) IS '-' AND substr(json_extract(PayloadJson, '$.Operation.BackgroundStart.ChildSessionId'),14,1) IS '-' AND substr(json_extract(PayloadJson, '$.Operation.BackgroundStart.ChildSessionId'),19,1) IS '-' AND substr(json_extract(PayloadJson, '$.Operation.BackgroundStart.ChildSessionId'),24,1) IS '-' AND json_extract(PayloadJson, '$.Operation.BackgroundStart.ChildSessionId') IS NOT SessionId AND json_extract(PayloadJson, '$.Operation.BackgroundStart.ChildSessionId') IS NOT lower(TenantId) AND json_type(PayloadJson, '$.Operation.BackgroundStart.EntryId') IS 'text' AND length(json_extract(PayloadJson, '$.Operation.BackgroundStart.EntryId')) = 36 AND json_extract(PayloadJson, '$.Operation.BackgroundStart.EntryId') NOT GLOB '*[^0-9a-f-]*' AND substr(json_extract(PayloadJson, '$.Operation.BackgroundStart.EntryId'),9,1) IS '-' AND substr(json_extract(PayloadJson, '$.Operation.BackgroundStart.EntryId'),14,1) IS '-' AND substr(json_extract(PayloadJson, '$.Operation.BackgroundStart.EntryId'),19,1) IS '-' AND substr(json_extract(PayloadJson, '$.Operation.BackgroundStart.EntryId'),24,1) IS '-' AND json_extract(PayloadJson, '$.Operation.BackgroundStart.EntryId') IS NOT SessionId AND json_extract(PayloadJson, '$.Operation.BackgroundStart.EntryId') IS NOT lower(TenantId) AND json_type(PayloadJson, '$.Operation.BackgroundStart.ActivationId') IS 'text' AND length(json_extract(PayloadJson, '$.Operation.BackgroundStart.ActivationId')) = 36 AND json_extract(PayloadJson, '$.Operation.BackgroundStart.ActivationId') NOT GLOB '*[^0-9a-f-]*' AND substr(json_extract(PayloadJson, '$.Operation.BackgroundStart.ActivationId'),9,1) IS '-' AND substr(json_extract(PayloadJson, '$.Operation.BackgroundStart.ActivationId'),14,1) IS '-' AND substr(json_extract(PayloadJson, '$.Operation.BackgroundStart.ActivationId'),19,1) IS '-' AND substr(json_extract(PayloadJson, '$.Operation.BackgroundStart.ActivationId'),24,1) IS '-' AND json_extract(PayloadJson, '$.Operation.BackgroundStart.ActivationId') IS NOT SessionId AND json_extract(PayloadJson, '$.Operation.BackgroundStart.ActivationId') IS NOT lower(TenantId) AND json_type(PayloadJson, '$.Operation.BackgroundStart.RunId') IS 'text' AND length(json_extract(PayloadJson, '$.Operation.BackgroundStart.RunId')) = 36 AND json_extract(PayloadJson, '$.Operation.BackgroundStart.RunId') NOT GLOB '*[^0-9a-f-]*' AND substr(json_extract(PayloadJson, '$.Operation.BackgroundStart.RunId'),9,1) IS '-' AND substr(json_extract(PayloadJson, '$.Operation.BackgroundStart.RunId'),14,1) IS '-' AND substr(json_extract(PayloadJson, '$.Operation.BackgroundStart.RunId'),19,1) IS '-' AND substr(json_extract(PayloadJson, '$.Operation.BackgroundStart.RunId'),24,1) IS '-' AND json_extract(PayloadJson, '$.Operation.BackgroundStart.RunId') IS NOT SessionId AND json_extract(PayloadJson, '$.Operation.BackgroundStart.RunId') IS NOT lower(TenantId) AND json_type(PayloadJson, '$.Operation.BackgroundStart.ResponseId') IS 'text' AND length(json_extract(PayloadJson, '$.Operation.BackgroundStart.ResponseId')) = 36 AND json_extract(PayloadJson, '$.Operation.BackgroundStart.ResponseId') NOT GLOB '*[^0-9a-f-]*' AND substr(json_extract(PayloadJson, '$.Operation.BackgroundStart.ResponseId'),9,1) IS '-' AND substr(json_extract(PayloadJson, '$.Operation.BackgroundStart.ResponseId'),14,1) IS '-' AND substr(json_extract(PayloadJson, '$.Operation.BackgroundStart.ResponseId'),19,1) IS '-' AND substr(json_extract(PayloadJson, '$.Operation.BackgroundStart.ResponseId'),24,1) IS '-' AND json_extract(PayloadJson, '$.Operation.BackgroundStart.ResponseId') IS NOT SessionId AND json_extract(PayloadJson, '$.Operation.BackgroundStart.ResponseId') IS NOT lower(TenantId) AND json_extract(PayloadJson, '$.Operation.BackgroundStart.ChildSessionId') IS NOT json_extract(PayloadJson, '$.Operation.BackgroundStart.EntryId') AND json_extract(PayloadJson, '$.Operation.BackgroundStart.ChildSessionId') IS NOT json_extract(PayloadJson, '$.Operation.BackgroundStart.ActivationId') AND json_extract(PayloadJson, '$.Operation.BackgroundStart.ChildSessionId') IS NOT json_extract(PayloadJson, '$.Operation.BackgroundStart.RunId') AND json_extract(PayloadJson, '$.Operation.BackgroundStart.ChildSessionId') IS NOT json_extract(PayloadJson, '$.Operation.BackgroundStart.ResponseId') AND json_extract(PayloadJson, '$.Operation.BackgroundStart.EntryId') IS NOT json_extract(PayloadJson, '$.Operation.BackgroundStart.ActivationId') AND json_extract(PayloadJson, '$.Operation.BackgroundStart.EntryId') IS NOT json_extract(PayloadJson, '$.Operation.BackgroundStart.RunId') AND json_extract(PayloadJson, '$.Operation.BackgroundStart.EntryId') IS NOT json_extract(PayloadJson, '$.Operation.BackgroundStart.ResponseId') AND json_extract(PayloadJson, '$.Operation.BackgroundStart.ActivationId') IS NOT json_extract(PayloadJson, '$.Operation.BackgroundStart.RunId') AND json_extract(PayloadJson, '$.Operation.BackgroundStart.ActivationId') IS NOT json_extract(PayloadJson, '$.Operation.BackgroundStart.ResponseId') AND json_extract(PayloadJson, '$.Operation.BackgroundStart.RunId') IS NOT json_extract(PayloadJson, '$.Operation.BackgroundStart.ResponseId') AND json_extract(PayloadJson, '$.Operation.Persona') IS NULL AND json_extract(PayloadJson, '$.Operation.Preferences') IS NULL AND json_extract(PayloadJson, '$.Operation.CredentialCheck') IS NULL AND json_extract(PayloadJson, '$.Operation.SandboxCommand') IS NULL AND json_extract(PayloadJson, '$.Operation.SandboxControl') IS NULL AND json_extract(PayloadJson, '$.Operation.SandboxRead') IS NULL AND json_extract(PayloadJson, '$.Operation.BrowserCommand') IS NULL AND json_extract(PayloadJson, '$.Operation.CredentialRead') IS NULL AND json_extract(PayloadJson, '$.Operation.ArtifactPublication') IS NULL AND json_extract(PayloadJson, '$.Operation.ArtifactVerification') IS NULL AND json_extract(PayloadJson, '$.Operation.WorkspaceRead') IS NULL AND json_extract(PayloadJson, '$.Operation.WorkspaceMutation') IS NULL AND json_extract(PayloadJson, '$.Operation.AttachmentRead') IS NULL),0)) AND json_extract(PayloadJson, '$.Operation.BackgroundReport') IS NULL) OR COALESCE((Kind IN (0,1) AND Status IN (0,1,2,3,4) AND Revision >= 1 AND ExpiresAt > CreatedAt AND length(Fingerprint) = 64
        AND Permission = 'agent-runs.create' AND ResourceType = 'tenant' AND ResourceId IS lower(TenantId)
        AND CredentialId IS NULL AND SandboxProfileId IS NULL AND SandboxProfileRevision IS NULL
        AND json_type(PayloadJson, '$.Operation.BackgroundReport') IS 'object'
        AND json_extract(PayloadJson, '$.Operation.BackgroundReport.TenantId') IS lower(TenantId)
        AND json_extract(PayloadJson, '$.Operation.BackgroundReport.SessionId') IS SessionId
        AND json_extract(PayloadJson, '$.Operation.BackgroundReport.ChildRunId') IS RunId
        AND json_type(PayloadJson, '$.Operation.BackgroundReport.SourceRevision') IS 'integer'
        AND json_extract(PayloadJson, '$.Operation.BackgroundReport.SourceRevision') >= 1
        AND json_type(PayloadJson, '$.Operation.BackgroundReport.ParentRevision') IS 'integer'
        AND json_extract(PayloadJson, '$.Operation.BackgroundReport.ParentRevision') >= 1
        AND json_type(PayloadJson, '$.Operation.BackgroundReport.InputBoundarySequence') IS 'integer'
        AND json_extract(PayloadJson, '$.Operation.BackgroundReport.InputBoundarySequence') >= 0
        AND json_type(PayloadJson, '$.Operation.BackgroundReport.EvidenceJson') IS 'text'
        AND json_valid(json_extract(PayloadJson, '$.Operation.BackgroundReport.EvidenceJson'))
        AND json_type(PayloadJson, '$.Operation.BackgroundReport.ConfigurationHash') IS 'text'
        AND length(json_extract(PayloadJson, '$.Operation.BackgroundReport.ConfigurationHash')) = 64
        AND json_extract(PayloadJson, '$.Operation.BackgroundReport.ConfigurationHash') NOT GLOB '*[^0-9a-f]*'
        AND json_type(PayloadJson, '$.Operation.BackgroundReport.Model') IS 'object'
        AND length(CAST(PayloadJson AS BLOB)) <= 131072 AND json_type(PayloadJson, '$.Operation.BackgroundReport.ParentSessionId') IS 'text' AND length(json_extract(PayloadJson, '$.Operation.BackgroundReport.ParentSessionId')) = 36 AND json_extract(PayloadJson, '$.Operation.BackgroundReport.ParentSessionId') NOT GLOB '*[^0-9a-f-]*' AND substr(json_extract(PayloadJson, '$.Operation.BackgroundReport.ParentSessionId'),9,1) IS '-' AND substr(json_extract(PayloadJson, '$.Operation.BackgroundReport.ParentSessionId'),14,1) IS '-' AND substr(json_extract(PayloadJson, '$.Operation.BackgroundReport.ParentSessionId'),19,1) IS '-' AND substr(json_extract(PayloadJson, '$.Operation.BackgroundReport.ParentSessionId'),24,1) IS '-' AND json_extract(PayloadJson, '$.Operation.BackgroundReport.ParentSessionId') IS NOT SessionId AND json_extract(PayloadJson, '$.Operation.BackgroundReport.ParentSessionId') IS NOT RunId AND json_extract(PayloadJson, '$.Operation.BackgroundReport.ParentSessionId') IS NOT lower(TenantId) AND json_type(PayloadJson, '$.Operation.BackgroundReport.ActivationId') IS 'text' AND length(json_extract(PayloadJson, '$.Operation.BackgroundReport.ActivationId')) = 36 AND json_extract(PayloadJson, '$.Operation.BackgroundReport.ActivationId') NOT GLOB '*[^0-9a-f-]*' AND substr(json_extract(PayloadJson, '$.Operation.BackgroundReport.ActivationId'),9,1) IS '-' AND substr(json_extract(PayloadJson, '$.Operation.BackgroundReport.ActivationId'),14,1) IS '-' AND substr(json_extract(PayloadJson, '$.Operation.BackgroundReport.ActivationId'),19,1) IS '-' AND substr(json_extract(PayloadJson, '$.Operation.BackgroundReport.ActivationId'),24,1) IS '-' AND json_extract(PayloadJson, '$.Operation.BackgroundReport.ActivationId') IS NOT SessionId AND json_extract(PayloadJson, '$.Operation.BackgroundReport.ActivationId') IS NOT RunId AND json_extract(PayloadJson, '$.Operation.BackgroundReport.ActivationId') IS NOT lower(TenantId) AND json_type(PayloadJson, '$.Operation.BackgroundReport.RunId') IS 'text' AND length(json_extract(PayloadJson, '$.Operation.BackgroundReport.RunId')) = 36 AND json_extract(PayloadJson, '$.Operation.BackgroundReport.RunId') NOT GLOB '*[^0-9a-f-]*' AND substr(json_extract(PayloadJson, '$.Operation.BackgroundReport.RunId'),9,1) IS '-' AND substr(json_extract(PayloadJson, '$.Operation.BackgroundReport.RunId'),14,1) IS '-' AND substr(json_extract(PayloadJson, '$.Operation.BackgroundReport.RunId'),19,1) IS '-' AND substr(json_extract(PayloadJson, '$.Operation.BackgroundReport.RunId'),24,1) IS '-' AND json_extract(PayloadJson, '$.Operation.BackgroundReport.RunId') IS NOT SessionId AND json_extract(PayloadJson, '$.Operation.BackgroundReport.RunId') IS NOT RunId AND json_extract(PayloadJson, '$.Operation.BackgroundReport.RunId') IS NOT lower(TenantId) AND json_type(PayloadJson, '$.Operation.BackgroundReport.ResponseId') IS 'text' AND length(json_extract(PayloadJson, '$.Operation.BackgroundReport.ResponseId')) = 36 AND json_extract(PayloadJson, '$.Operation.BackgroundReport.ResponseId') NOT GLOB '*[^0-9a-f-]*' AND substr(json_extract(PayloadJson, '$.Operation.BackgroundReport.ResponseId'),9,1) IS '-' AND substr(json_extract(PayloadJson, '$.Operation.BackgroundReport.ResponseId'),14,1) IS '-' AND substr(json_extract(PayloadJson, '$.Operation.BackgroundReport.ResponseId'),19,1) IS '-' AND substr(json_extract(PayloadJson, '$.Operation.BackgroundReport.ResponseId'),24,1) IS '-' AND json_extract(PayloadJson, '$.Operation.BackgroundReport.ResponseId') IS NOT SessionId AND json_extract(PayloadJson, '$.Operation.BackgroundReport.ResponseId') IS NOT RunId AND json_extract(PayloadJson, '$.Operation.BackgroundReport.ResponseId') IS NOT lower(TenantId) AND json_extract(PayloadJson, '$.Operation.BackgroundReport.ParentSessionId') IS NOT json_extract(PayloadJson, '$.Operation.BackgroundReport.ActivationId') AND json_extract(PayloadJson, '$.Operation.BackgroundReport.ParentSessionId') IS NOT json_extract(PayloadJson, '$.Operation.BackgroundReport.RunId') AND json_extract(PayloadJson, '$.Operation.BackgroundReport.ParentSessionId') IS NOT json_extract(PayloadJson, '$.Operation.BackgroundReport.ResponseId') AND json_extract(PayloadJson, '$.Operation.BackgroundReport.ActivationId') IS NOT json_extract(PayloadJson, '$.Operation.BackgroundReport.RunId') AND json_extract(PayloadJson, '$.Operation.BackgroundReport.ActivationId') IS NOT json_extract(PayloadJson, '$.Operation.BackgroundReport.ResponseId') AND json_extract(PayloadJson, '$.Operation.BackgroundReport.RunId') IS NOT json_extract(PayloadJson, '$.Operation.BackgroundReport.ResponseId') AND json_type(PayloadJson, '$.Operation.BackgroundReport.Model.CatalogKey') IS 'text' AND length(json_extract(PayloadJson, '$.Operation.BackgroundReport.Model.CatalogKey')) BETWEEN 1 AND 128 AND json_type(PayloadJson, '$.Operation.BackgroundReport.Model.ProviderAlias') IS 'text' AND length(json_extract(PayloadJson, '$.Operation.BackgroundReport.Model.ProviderAlias')) BETWEEN 1 AND 128 AND json_type(PayloadJson, '$.Operation.BackgroundReport.Model.ModelId') IS 'text' AND length(json_extract(PayloadJson, '$.Operation.BackgroundReport.Model.ModelId')) BETWEEN 1 AND 128 AND json_extract(PayloadJson, '$.Operation.Persona') IS NULL AND json_extract(PayloadJson, '$.Operation.Preferences') IS NULL AND json_extract(PayloadJson, '$.Operation.CredentialCheck') IS NULL AND json_extract(PayloadJson, '$.Operation.SandboxCommand') IS NULL AND json_extract(PayloadJson, '$.Operation.SandboxControl') IS NULL AND json_extract(PayloadJson, '$.Operation.SandboxRead') IS NULL AND json_extract(PayloadJson, '$.Operation.BrowserCommand') IS NULL AND json_extract(PayloadJson, '$.Operation.CredentialRead') IS NULL AND json_extract(PayloadJson, '$.Operation.ArtifactPublication') IS NULL AND json_extract(PayloadJson, '$.Operation.ArtifactVerification') IS NULL AND json_extract(PayloadJson, '$.Operation.WorkspaceRead') IS NULL AND json_extract(PayloadJson, '$.Operation.WorkspaceMutation') IS NULL AND json_extract(PayloadJson, '$.Operation.AttachmentRead') IS NULL AND json_extract(PayloadJson, '$.Operation.BackgroundStart') IS NULL),0)) AND json_extract(PayloadJson, '$.Operation.CompletionRead') IS NULL) OR COALESCE((Kind = 0 AND Status IN (0,1,2,3,4) AND Revision >= 1 AND ExpiresAt > CreatedAt AND length(Fingerprint) = 64
        AND Permission = 'agent-runs.read' AND ResourceType = 'agent-run'
        AND ResourceId IS json_extract(PayloadJson, '$.Operation.CompletionRead.SourceRunId')
        AND CredentialId IS NULL AND SandboxProfileId IS NULL AND SandboxProfileRevision IS NULL
        AND json_type(PayloadJson, '$.Operation.CompletionRead') IS 'object'
        AND json_extract(PayloadJson, '$.Operation.CompletionRead.ParentSessionId') IS SessionId
        AND json_type(PayloadJson, '$.Operation.CompletionRead.SourceRevision') IS 'integer'
        AND json_extract(PayloadJson, '$.Operation.CompletionRead.SourceRevision') >= 1
        AND json_type(PayloadJson, '$.Operation.CompletionRead.Objective') IS 'text'
        AND length(CAST(json_extract(PayloadJson, '$.Operation.CompletionRead.Objective') AS BLOB)) BETWEEN 1 AND 4000
        AND json_type(PayloadJson, '$.Operation.CompletionRead.ObservationJson') IS 'text'
        AND length(CAST(json_extract(PayloadJson, '$.Operation.CompletionRead.ObservationJson') AS BLOB)) BETWEEN 2 AND 20000
        AND json_valid(json_extract(PayloadJson, '$.Operation.CompletionRead.ObservationJson'))
        AND json_type(json_extract(PayloadJson, '$.Operation.CompletionRead.ObservationJson')) IS 'object'
        AND json_extract(json_extract(PayloadJson, '$.Operation.CompletionRead.ObservationJson'), '$.untrusted') = 1
        AND json_extract(json_extract(PayloadJson, '$.Operation.CompletionRead.ObservationJson'), '$.tenantCompletionRead') = 1
        AND json_extract(json_extract(PayloadJson, '$.Operation.CompletionRead.ObservationJson'), '$.backgroundSessionId') IS json_extract(PayloadJson, '$.Operation.CompletionRead.ChildSessionId')
        AND json_extract(json_extract(PayloadJson, '$.Operation.CompletionRead.ObservationJson'), '$.parentSessionId') IS SessionId
        AND json_extract(json_extract(PayloadJson, '$.Operation.CompletionRead.ObservationJson'), '$.agentRunId') IS json_extract(PayloadJson, '$.Operation.CompletionRead.InitialRunId')
        AND json_extract(json_extract(PayloadJson, '$.Operation.CompletionRead.ObservationJson'), '$.sourceAgentRunId') IS ResourceId
        AND json_extract(json_extract(PayloadJson, '$.Operation.CompletionRead.ObservationJson'), '$.status') IN ('Completed','Failed','Cancelled')
        AND json_type(PayloadJson, '$.Operation.CompletionRead.ObservationHash') IS 'text'
        AND length(json_extract(PayloadJson, '$.Operation.CompletionRead.ObservationHash')) = 64
        AND json_extract(PayloadJson, '$.Operation.CompletionRead.ObservationHash') NOT GLOB '*[^0-9a-f]*'
        AND json_type(PayloadJson, '$.Operation.CompletionRead.ArtifactsJson') IS 'text'
        AND length(CAST(json_extract(PayloadJson, '$.Operation.CompletionRead.ArtifactsJson') AS BLOB)) <= 8192
        AND json_valid(json_extract(PayloadJson, '$.Operation.CompletionRead.ArtifactsJson'))
        AND json_type(json_extract(PayloadJson, '$.Operation.CompletionRead.ArtifactsJson')) IS 'array'
        AND json_array_length(json_extract(PayloadJson, '$.Operation.CompletionRead.ArtifactsJson')) <= 3
        AND length(CAST(PayloadJson AS BLOB)) <= 131072
        AND json_type(PayloadJson, '$.Operation.CompletionRead.ChildSessionId') IS 'text' AND length(json_extract(PayloadJson, '$.Operation.CompletionRead.ChildSessionId')) = 36 AND json_extract(PayloadJson, '$.Operation.CompletionRead.ChildSessionId') NOT GLOB '*[^0-9a-f-]*' AND substr(json_extract(PayloadJson, '$.Operation.CompletionRead.ChildSessionId'),9,1) IS '-' AND substr(json_extract(PayloadJson, '$.Operation.CompletionRead.ChildSessionId'),14,1) IS '-' AND substr(json_extract(PayloadJson, '$.Operation.CompletionRead.ChildSessionId'),19,1) IS '-' AND substr(json_extract(PayloadJson, '$.Operation.CompletionRead.ChildSessionId'),24,1) IS '-' AND json_extract(PayloadJson, '$.Operation.CompletionRead.ChildSessionId') IS NOT '00000000-0000-0000-0000-000000000000' AND json_extract(PayloadJson, '$.Operation.CompletionRead.ChildSessionId') IS NOT SessionId
        AND json_type(PayloadJson, '$.Operation.CompletionRead.InitialRunId') IS 'text' AND length(json_extract(PayloadJson, '$.Operation.CompletionRead.InitialRunId')) = 36 AND json_extract(PayloadJson, '$.Operation.CompletionRead.InitialRunId') NOT GLOB '*[^0-9a-f-]*' AND substr(json_extract(PayloadJson, '$.Operation.CompletionRead.InitialRunId'),9,1) IS '-' AND substr(json_extract(PayloadJson, '$.Operation.CompletionRead.InitialRunId'),14,1) IS '-' AND substr(json_extract(PayloadJson, '$.Operation.CompletionRead.InitialRunId'),19,1) IS '-' AND substr(json_extract(PayloadJson, '$.Operation.CompletionRead.InitialRunId'),24,1) IS '-' AND json_extract(PayloadJson, '$.Operation.CompletionRead.InitialRunId') IS NOT '00000000-0000-0000-0000-000000000000' AND json_extract(PayloadJson, '$.Operation.CompletionRead.InitialRunId') IS NOT SessionId
        AND json_type(PayloadJson, '$.Operation.CompletionRead.SourceRunId') IS 'text' AND length(json_extract(PayloadJson, '$.Operation.CompletionRead.SourceRunId')) = 36 AND json_extract(PayloadJson, '$.Operation.CompletionRead.SourceRunId') NOT GLOB '*[^0-9a-f-]*' AND substr(json_extract(PayloadJson, '$.Operation.CompletionRead.SourceRunId'),9,1) IS '-' AND substr(json_extract(PayloadJson, '$.Operation.CompletionRead.SourceRunId'),14,1) IS '-' AND substr(json_extract(PayloadJson, '$.Operation.CompletionRead.SourceRunId'),19,1) IS '-' AND substr(json_extract(PayloadJson, '$.Operation.CompletionRead.SourceRunId'),24,1) IS '-' AND json_extract(PayloadJson, '$.Operation.CompletionRead.SourceRunId') IS NOT '00000000-0000-0000-0000-000000000000' AND json_extract(PayloadJson, '$.Operation.CompletionRead.SourceRunId') IS NOT SessionId
        AND json_extract(PayloadJson, '$.Operation.Persona') IS NULL
        AND json_extract(PayloadJson, '$.Operation.Preferences') IS NULL
        AND json_extract(PayloadJson, '$.Operation.CredentialCheck') IS NULL
        AND json_extract(PayloadJson, '$.Operation.SandboxCommand') IS NULL
        AND json_extract(PayloadJson, '$.Operation.SandboxControl') IS NULL
        AND json_extract(PayloadJson, '$.Operation.SandboxRead') IS NULL
        AND json_extract(PayloadJson, '$.Operation.BrowserCommand') IS NULL
        AND json_extract(PayloadJson, '$.Operation.CredentialRead') IS NULL
        AND json_extract(PayloadJson, '$.Operation.ArtifactPublication') IS NULL
        AND json_extract(PayloadJson, '$.Operation.ArtifactVerification') IS NULL
        AND json_extract(PayloadJson, '$.Operation.WorkspaceRead') IS NULL
        AND json_extract(PayloadJson, '$.Operation.WorkspaceMutation') IS NULL
        AND json_extract(PayloadJson, '$.Operation.AttachmentRead') IS NULL
        AND json_extract(PayloadJson, '$.Operation.BackgroundStart') IS NULL
        AND json_extract(PayloadJson, '$.Operation.BackgroundReport') IS NULL),0)) AND json_extract(PayloadJson, '$.Operation.ReportSourceRead') IS NULL) OR COALESCE((Kind = 0 AND Status IN (0,1,2,3,4) AND Revision >= 1 AND ExpiresAt > CreatedAt AND length(Fingerprint) = 64
        AND Permission = 'agent-runs.read' AND ResourceType = 'agent-run' AND ResourceId IS RunId
        AND CredentialId IS NULL AND SandboxProfileId IS NULL AND SandboxProfileRevision IS NULL
        AND json_type(PayloadJson, '$.Operation.ReportSourceRead') IS 'object'
        AND json_extract(PayloadJson, '$.Operation.ReportSourceRead.ChildSessionId') IS SessionId
        AND json_extract(PayloadJson, '$.Operation.ReportSourceRead.SourceRunId') IS RunId
        AND json_extract(PayloadJson, '$.Operation.ReportSourceRead.ParentSessionId') IS NOT SessionId
        AND json_type(PayloadJson, '$.Operation.ReportSourceRead.SourceRevision') IS 'integer' AND json_extract(PayloadJson, '$.Operation.ReportSourceRead.SourceRevision') >= 1
        AND json_type(PayloadJson, '$.Operation.ReportSourceRead.Objective') IS 'text' AND length(CAST(json_extract(PayloadJson, '$.Operation.ReportSourceRead.Objective') AS BLOB)) BETWEEN 1 AND 4000
        AND json_type(PayloadJson, '$.Operation.ReportSourceRead.ObservationJson') IS 'text' AND length(CAST(json_extract(PayloadJson, '$.Operation.ReportSourceRead.ObservationJson') AS BLOB)) BETWEEN 2 AND 8192
        AND json_valid(json_extract(PayloadJson, '$.Operation.ReportSourceRead.ObservationJson'))
        AND json_extract(json_extract(PayloadJson, '$.Operation.ReportSourceRead.ObservationJson'), '$.TriggerKind') = 7
        AND json_extract(json_extract(PayloadJson, '$.Operation.ReportSourceRead.ObservationJson'), '$.InputBoundarySequence') IS NULL
        AND json_type(json_extract(PayloadJson, '$.Operation.ReportSourceRead.ObservationJson'), '$.Text') IS 'text'
        AND json_valid(json_extract(json_extract(PayloadJson, '$.Operation.ReportSourceRead.ObservationJson'), '$.Text'))
        AND json_extract(json_extract(json_extract(PayloadJson, '$.Operation.ReportSourceRead.ObservationJson'), '$.Text'), '$.childSessionId') IS SessionId
        AND json_extract(json_extract(json_extract(PayloadJson, '$.Operation.ReportSourceRead.ObservationJson'), '$.Text'), '$.initialAgentRunId') IS RunId
        AND json_extract(json_extract(json_extract(PayloadJson, '$.Operation.ReportSourceRead.ObservationJson'), '$.Text'), '$.status') IN ('Completed','Failed','Cancelled')
        AND json_array_length(json_extract(json_extract(json_extract(PayloadJson, '$.Operation.ReportSourceRead.ObservationJson'), '$.Text'), '$.artifacts')) = 0
        AND json_type(PayloadJson, '$.Operation.ReportSourceRead.ObservationHash') IS 'text' AND length(json_extract(PayloadJson, '$.Operation.ReportSourceRead.ObservationHash')) = 64
        AND json_extract(PayloadJson, '$.Operation.ReportSourceRead.ObservationHash') NOT GLOB '*[^0-9a-f]*'
        AND length(CAST(PayloadJson AS BLOB)) <= 131072
        AND json_type(PayloadJson, '$.Operation.ReportSourceRead.ParentSessionId') IS 'text' AND length(json_extract(PayloadJson, '$.Operation.ReportSourceRead.ParentSessionId')) = 36 AND json_extract(PayloadJson, '$.Operation.ReportSourceRead.ParentSessionId') NOT GLOB '*[^0-9a-f-]*' AND json_extract(PayloadJson, '$.Operation.ReportSourceRead.ParentSessionId') IS NOT '00000000-0000-0000-0000-000000000000'
        AND json_type(PayloadJson, '$.Operation.ReportSourceRead.ChildSessionId') IS 'text' AND length(json_extract(PayloadJson, '$.Operation.ReportSourceRead.ChildSessionId')) = 36 AND json_extract(PayloadJson, '$.Operation.ReportSourceRead.ChildSessionId') NOT GLOB '*[^0-9a-f-]*' AND json_extract(PayloadJson, '$.Operation.ReportSourceRead.ChildSessionId') IS NOT '00000000-0000-0000-0000-000000000000'
        AND json_type(PayloadJson, '$.Operation.ReportSourceRead.InitialRunId') IS 'text' AND length(json_extract(PayloadJson, '$.Operation.ReportSourceRead.InitialRunId')) = 36 AND json_extract(PayloadJson, '$.Operation.ReportSourceRead.InitialRunId') NOT GLOB '*[^0-9a-f-]*' AND json_extract(PayloadJson, '$.Operation.ReportSourceRead.InitialRunId') IS NOT '00000000-0000-0000-0000-000000000000'
        AND json_type(PayloadJson, '$.Operation.ReportSourceRead.SourceRunId') IS 'text' AND length(json_extract(PayloadJson, '$.Operation.ReportSourceRead.SourceRunId')) = 36 AND json_extract(PayloadJson, '$.Operation.ReportSourceRead.SourceRunId') NOT GLOB '*[^0-9a-f-]*' AND json_extract(PayloadJson, '$.Operation.ReportSourceRead.SourceRunId') IS NOT '00000000-0000-0000-0000-000000000000'
        AND json_extract(PayloadJson, '$.Operation.Persona') IS NULL
        AND json_extract(PayloadJson, '$.Operation.Preferences') IS NULL
        AND json_extract(PayloadJson, '$.Operation.CredentialCheck') IS NULL
        AND json_extract(PayloadJson, '$.Operation.SandboxCommand') IS NULL
        AND json_extract(PayloadJson, '$.Operation.SandboxControl') IS NULL
        AND json_extract(PayloadJson, '$.Operation.SandboxRead') IS NULL
        AND json_extract(PayloadJson, '$.Operation.BrowserCommand') IS NULL
        AND json_extract(PayloadJson, '$.Operation.CredentialRead') IS NULL
        AND json_extract(PayloadJson, '$.Operation.ArtifactPublication') IS NULL
        AND json_extract(PayloadJson, '$.Operation.ArtifactVerification') IS NULL
        AND json_extract(PayloadJson, '$.Operation.WorkspaceRead') IS NULL
        AND json_extract(PayloadJson, '$.Operation.WorkspaceMutation') IS NULL
        AND json_extract(PayloadJson, '$.Operation.AttachmentRead') IS NULL
        AND json_extract(PayloadJson, '$.Operation.BackgroundStart') IS NULL
        AND json_extract(PayloadJson, '$.Operation.BackgroundReport') IS NULL
        AND json_extract(PayloadJson, '$.Operation.CompletionRead') IS NULL),0)) AND json_extract(PayloadJson, '$.Operation.AccountRead') IS NULL) OR COALESCE((Kind IN (0,1) AND Status IN (0,1,2,3,4) AND Revision >= 1 AND ExpiresAt > CreatedAt AND length(Fingerprint) = 64
        AND Permission = 'credentials.use' AND ResourceType = 'credential' AND CredentialId IS NOT NULL AND ResourceId IS CredentialId
        AND SandboxProfileId IS NULL AND SandboxProfileRevision IS NULL
        AND json_type(PayloadJson, '$.Operation.AccountRead') IS 'object'
        AND json_extract(PayloadJson, '$.Operation.AccountRead.SessionId') IS SessionId
        AND json_extract(PayloadJson, '$.Operation.AccountRead.Credential.CredentialId') IS CredentialId
        AND json_type(PayloadJson, '$.Operation.AccountRead.Credential.BindingId') IS 'text'
        AND length(json_extract(PayloadJson, '$.Operation.AccountRead.Credential.BindingId')) = 36
        AND json_type(PayloadJson, '$.Operation.AccountRead.Credential.Reference') IS 'text'
        AND length(json_extract(PayloadJson, '$.Operation.AccountRead.Credential.Reference')) BETWEEN 1 AND 120
        AND json_type(PayloadJson, '$.Operation.AccountRead.Credential.BindingRevision') IS 'integer' AND json_extract(PayloadJson, '$.Operation.AccountRead.Credential.BindingRevision') >= 1
        AND json_type(PayloadJson, '$.Operation.AccountRead.Credential.CredentialRevision') IS 'integer' AND json_extract(PayloadJson, '$.Operation.AccountRead.Credential.CredentialRevision') >= 1
        AND json_type(PayloadJson, '$.Operation.AccountRead.Credential.SecretRevision') IS 'integer' AND json_extract(PayloadJson, '$.Operation.AccountRead.Credential.SecretRevision') >= CASE WHEN json_extract(PayloadJson, '$.Operation.AccountRead.Purpose') = 1 THEN 0 ELSE 1 END
        AND json_type(PayloadJson, '$.Operation.AccountRead.Credential.AuthEpoch') IS 'integer' AND json_extract(PayloadJson, '$.Operation.AccountRead.Credential.AuthEpoch') >= CASE WHEN json_extract(PayloadJson, '$.Operation.AccountRead.Purpose') = 1 THEN 0 ELSE 1 END
        AND json_type(PayloadJson, '$.Operation.AccountRead.ArgumentsJson') IS 'text' AND length(CAST(json_extract(PayloadJson, '$.Operation.AccountRead.ArgumentsJson') AS BLOB)) <= 8192
        AND json_valid(json_extract(PayloadJson, '$.Operation.AccountRead.ArgumentsJson'))
        AND json_type(json_extract(PayloadJson, '$.Operation.AccountRead.ArgumentsJson')) IS 'object'
        AND json_extract(json_extract(PayloadJson, '$.Operation.AccountRead.ArgumentsJson'), '$.accountRef') IS json_extract(PayloadJson, '$.Operation.AccountRead.Credential.Reference')
        AND ((json_extract(PayloadJson, '$.Operation.AccountRead.Purpose') = 1 AND json_extract(PayloadJson, '$.Operation.AccountRead.ToolName') IN ('email.search','email.read'))
          OR (json_extract(PayloadJson, '$.Operation.AccountRead.Purpose') = 2 AND json_extract(PayloadJson, '$.Operation.AccountRead.ToolName') IN ('drive.list','drive.search','drive.get')))
        AND length(CAST(PayloadJson AS BLOB)) <= 131072
        AND json_extract(PayloadJson, '$.Operation.Persona') IS NULL
        AND json_extract(PayloadJson, '$.Operation.Preferences') IS NULL
        AND json_extract(PayloadJson, '$.Operation.CredentialCheck') IS NULL
        AND json_extract(PayloadJson, '$.Operation.SandboxCommand') IS NULL
        AND json_extract(PayloadJson, '$.Operation.SandboxControl') IS NULL
        AND json_extract(PayloadJson, '$.Operation.SandboxRead') IS NULL
        AND json_extract(PayloadJson, '$.Operation.BrowserCommand') IS NULL
        AND json_extract(PayloadJson, '$.Operation.CredentialRead') IS NULL
        AND json_extract(PayloadJson, '$.Operation.ArtifactPublication') IS NULL
        AND json_extract(PayloadJson, '$.Operation.ArtifactVerification') IS NULL
        AND json_extract(PayloadJson, '$.Operation.WorkspaceRead') IS NULL
        AND json_extract(PayloadJson, '$.Operation.WorkspaceMutation') IS NULL
        AND json_extract(PayloadJson, '$.Operation.AttachmentRead') IS NULL
        AND json_extract(PayloadJson, '$.Operation.BackgroundStart') IS NULL
        AND json_extract(PayloadJson, '$.Operation.BackgroundReport') IS NULL
        AND json_extract(PayloadJson, '$.Operation.CompletionRead') IS NULL
        AND json_extract(PayloadJson, '$.Operation.ReportSourceRead') IS NULL),0)) AND json_extract(PayloadJson, '$.Operation.AccountAction') IS NULL) OR COALESCE((Kind IN (0,1) AND Status IN (0,1,2,3,4) AND Revision >= 1 AND ExpiresAt > CreatedAt AND length(Fingerprint) = 64
        AND CredentialId IS NOT NULL
        AND json_extract(PayloadJson, '$.Operation.AccountAction.Permission') IS Permission
        AND ((Permission IN ('email.draft','email.send','drive.write','drive.delete') AND ResourceType = 'credential' AND ResourceId IS CredentialId)
          OR (Permission = 'workspaces.write' AND ResourceType = 'workspace' AND ResourceId IS CASE
            WHEN substr(json_extract(json_extract(PayloadJson, '$.Operation.AccountAction.ArgumentsJson'),'$.path'),1,9) = '/working/' THEN 'working:' || SessionId ELSE 'home:' || InstanceId END))
        AND SandboxProfileId IS NULL AND SandboxProfileRevision IS NULL
        AND json_type(PayloadJson, '$.Operation.AccountAction') IS 'object'
        AND json_extract(PayloadJson, '$.Operation.AccountAction.SessionId') IS SessionId
        AND json_extract(PayloadJson, '$.Operation.AccountAction.Credential.CredentialId') IS CredentialId
        AND json_type(PayloadJson, '$.Operation.AccountAction.Credential.BindingId') IS 'text'
        AND length(json_extract(PayloadJson, '$.Operation.AccountAction.Credential.BindingId')) = 36
        AND json_type(PayloadJson, '$.Operation.AccountAction.Credential.Reference') IS 'text'
        AND length(json_extract(PayloadJson, '$.Operation.AccountAction.Credential.Reference')) BETWEEN 1 AND 120
        AND json_type(PayloadJson, '$.Operation.AccountAction.Credential.BindingRevision') IS 'integer' AND json_extract(PayloadJson, '$.Operation.AccountAction.Credential.BindingRevision') >= 1
        AND json_type(PayloadJson, '$.Operation.AccountAction.Credential.CredentialRevision') IS 'integer' AND json_extract(PayloadJson, '$.Operation.AccountAction.Credential.CredentialRevision') >= 1
        AND json_type(PayloadJson, '$.Operation.AccountAction.Credential.SecretRevision') IS 'integer' AND json_extract(PayloadJson, '$.Operation.AccountAction.Credential.SecretRevision') >= CASE WHEN json_extract(PayloadJson, '$.Operation.AccountAction.Purpose') = 1 THEN 0 ELSE 1 END
        AND json_type(PayloadJson, '$.Operation.AccountAction.Credential.AuthEpoch') IS 'integer' AND json_extract(PayloadJson, '$.Operation.AccountAction.Credential.AuthEpoch') >= CASE WHEN json_extract(PayloadJson, '$.Operation.AccountAction.Purpose') = 1 THEN 0 ELSE 1 END
        AND json_type(PayloadJson, '$.Operation.AccountAction.ArgumentsJson') IS 'text' AND length(CAST(json_extract(PayloadJson, '$.Operation.AccountAction.ArgumentsJson') AS BLOB)) <= 49152
        AND json_valid(json_extract(PayloadJson, '$.Operation.AccountAction.ArgumentsJson'))
        AND json_type(json_extract(PayloadJson, '$.Operation.AccountAction.ArgumentsJson')) IS 'object'
        AND json_extract(json_extract(PayloadJson, '$.Operation.AccountAction.ArgumentsJson'), '$.accountRef') IS json_extract(PayloadJson, '$.Operation.AccountAction.Credential.Reference')
        AND ((json_extract(PayloadJson, '$.Operation.AccountAction.Purpose') = 1
            AND ((Permission = 'email.draft' AND json_extract(PayloadJson, '$.Operation.AccountAction.ToolName') = 'email.create_draft')
              OR (Permission = 'email.send' AND json_extract(PayloadJson, '$.Operation.AccountAction.ToolName') = 'email.send'))
            AND json_type(PayloadJson, '$.Operation.AccountAction.Draft') IS 'object'
            AND json_type(PayloadJson, '$.Operation.AccountAction.Draft.DraftId') IS 'text' AND length(json_extract(PayloadJson, '$.Operation.AccountAction.Draft.DraftId')) BETWEEN 1 AND 200
            AND json_type(PayloadJson, '$.Operation.AccountAction.Draft.RecipientsJson') IS 'text' AND json_valid(json_extract(PayloadJson, '$.Operation.AccountAction.Draft.RecipientsJson'))
            AND json_type(PayloadJson, '$.Operation.AccountAction.Draft.Subject') IS 'text' AND length(json_extract(PayloadJson, '$.Operation.AccountAction.Draft.Subject')) <= 500
            AND json_type(PayloadJson, '$.Operation.AccountAction.Draft.Body') IS 'text' AND length(json_extract(PayloadJson, '$.Operation.AccountAction.Draft.Body')) <= 32000
            AND json_type(PayloadJson, '$.Operation.AccountAction.Draft.Sender') IS 'text' AND length(json_extract(PayloadJson, '$.Operation.AccountAction.Draft.Sender')) BETWEEN 3 AND 320
            AND json_extract(PayloadJson, '$.Operation.AccountAction.Draft.Revision') >= 1
            AND json_extract(PayloadJson, '$.Operation.AccountAction.RemoteSourcesJson') = '[]'
            AND json_extract(PayloadJson, '$.Operation.AccountAction.UploadSource') IS NULL AND json_extract(PayloadJson, '$.Operation.AccountAction.DestinationObservationHash') IS NULL)
          OR (json_extract(PayloadJson, '$.Operation.AccountAction.Purpose') = 2
            AND json_extract(PayloadJson, '$.Operation.AccountAction.Draft') IS NULL
            AND ((Permission = 'drive.write' AND json_extract(PayloadJson, '$.Operation.AccountAction.ToolName') IN ('drive.create_folder','drive.upload','drive.rename','drive.move'))
              OR (Permission = 'drive.delete' AND json_extract(PayloadJson, '$.Operation.AccountAction.ToolName') IN ('drive.trash','drive.trash_batch'))
              OR (Permission = 'workspaces.write' AND json_extract(PayloadJson, '$.Operation.AccountAction.ToolName') = 'drive.download'))))
        AND json_type(PayloadJson, '$.Operation.AccountAction.RemoteSourcesJson') IS 'text'
        AND length(CAST(json_extract(PayloadJson, '$.Operation.AccountAction.RemoteSourcesJson') AS BLOB)) <= 32768
        AND json_valid(json_extract(PayloadJson, '$.Operation.AccountAction.RemoteSourcesJson'))
        AND json_type(json_extract(PayloadJson, '$.Operation.AccountAction.RemoteSourcesJson')) IS 'array'
        AND json_array_length(json_extract(PayloadJson, '$.Operation.AccountAction.RemoteSourcesJson')) <= 21
        AND (json_extract(PayloadJson, '$.Operation.AccountAction.ToolName') <> 'drive.upload' OR json_type(PayloadJson, '$.Operation.AccountAction.UploadSource') IS 'object')
        AND (json_extract(PayloadJson, '$.Operation.AccountAction.ToolName') <> 'drive.download' OR (json_type(PayloadJson, '$.Operation.AccountAction.DestinationObservationHash') IS 'text'
            AND length(json_extract(PayloadJson, '$.Operation.AccountAction.DestinationObservationHash')) = 64 AND json_extract(PayloadJson, '$.Operation.AccountAction.DestinationObservationHash') NOT GLOB '*[^0-9a-f]*'))
        AND length(CAST(PayloadJson AS BLOB)) <= 131072
        AND json_extract(PayloadJson, '$.Operation.Persona') IS NULL
        AND json_extract(PayloadJson, '$.Operation.Preferences') IS NULL
        AND json_extract(PayloadJson, '$.Operation.CredentialCheck') IS NULL
        AND json_extract(PayloadJson, '$.Operation.SandboxCommand') IS NULL
        AND json_extract(PayloadJson, '$.Operation.SandboxControl') IS NULL
        AND json_extract(PayloadJson, '$.Operation.SandboxRead') IS NULL
        AND json_extract(PayloadJson, '$.Operation.BrowserCommand') IS NULL
        AND json_extract(PayloadJson, '$.Operation.CredentialRead') IS NULL
        AND json_extract(PayloadJson, '$.Operation.ArtifactPublication') IS NULL
        AND json_extract(PayloadJson, '$.Operation.ArtifactVerification') IS NULL
        AND json_extract(PayloadJson, '$.Operation.WorkspaceRead') IS NULL
        AND json_extract(PayloadJson, '$.Operation.WorkspaceMutation') IS NULL
        AND json_extract(PayloadJson, '$.Operation.AttachmentRead') IS NULL
        AND json_extract(PayloadJson, '$.Operation.BackgroundStart') IS NULL
        AND json_extract(PayloadJson, '$.Operation.BackgroundReport') IS NULL
        AND json_extract(PayloadJson, '$.Operation.CompletionRead') IS NULL
        AND json_extract(PayloadJson, '$.Operation.ReportSourceRead') IS NULL
        AND json_extract(PayloadJson, '$.Operation.AccountRead') IS NULL),0))) OR COALESCE((Kind IN (0,1) AND Status IN (0,1,2,3,4) AND Revision >= 1 AND ExpiresAt > CreatedAt AND ExpiresAt - CreatedAt <= 86400000 AND length(Fingerprint) = 64
        AND RunId IS NULL AND SessionId IS NULL AND AutomationId IS NOT NULL
        AND CredentialId IS NULL AND SandboxProfileId IS NULL AND SandboxProfileRevision IS NULL
        AND Permission IN ('automations.run','sessions.create','agent-runs.create','sessions.read','agents.chat','webhooks.read','core-events.read','credentials.read','credentials.use','email.read')
        AND (Permission NOT IN ('sessions.read','agents.chat','webhooks.read','core-events.read','credentials.read','credentials.use','email.read') OR Kind = 0)
        AND ResourceType IS CASE Permission WHEN 'automations.run' THEN 'automation' WHEN 'sessions.read' THEN 'session' WHEN 'agents.chat' THEN 'agent' WHEN 'webhooks.read' THEN 'webhook' WHEN 'core-events.read' THEN 'event' WHEN 'credentials.read' THEN 'credential' WHEN 'credentials.use' THEN 'credential' WHEN 'email.read' THEN 'credential' ELSE 'tenant' END
        AND ResourceId IS CASE Permission WHEN 'automations.run' THEN AutomationId WHEN 'sessions.read' THEN json_extract(PayloadJson, '$.Operation.AutomationAdmission.SessionId') WHEN 'agents.chat' THEN InstanceId WHEN 'webhooks.read' THEN json_extract(PayloadJson, '$.Operation.AutomationAdmission.EventSource.ResourceId') WHEN 'core-events.read' THEN json_extract(PayloadJson, '$.Operation.AutomationAdmission.EventSource.EventIds[0]') WHEN 'credentials.read' THEN min(coalesce(json_extract(json_extract(PayloadJson, '$.Operation.AutomationAdmission.Occurrence.EvidenceJson'), '$.triggerContext.source.accountId'),'ffffffff-ffff-ffff-ffff-ffffffffffff'),coalesce(json_extract(json_extract(PayloadJson, '$.Operation.AutomationAdmission.Occurrence.EvidenceJson'), '$.triggerContext.sources[0].data.accountId'),'ffffffff-ffff-ffff-ffff-ffffffffffff'),coalesce(json_extract(json_extract(PayloadJson, '$.Operation.AutomationAdmission.Occurrence.EvidenceJson'), '$.triggerContext.sources[1].data.accountId'),'ffffffff-ffff-ffff-ffff-ffffffffffff'),coalesce(json_extract(json_extract(PayloadJson, '$.Operation.AutomationAdmission.Occurrence.EvidenceJson'), '$.triggerContext.sources[2].data.accountId'),'ffffffff-ffff-ffff-ffff-ffffffffffff'),coalesce(json_extract(json_extract(PayloadJson, '$.Operation.AutomationAdmission.Occurrence.EvidenceJson'), '$.triggerContext.sources[3].data.accountId'),'ffffffff-ffff-ffff-ffff-ffffffffffff'),coalesce(json_extract(json_extract(PayloadJson, '$.Operation.AutomationAdmission.Occurrence.EvidenceJson'), '$.triggerContext.sources[4].data.accountId'),'ffffffff-ffff-ffff-ffff-ffffffffffff'),coalesce(json_extract(json_extract(PayloadJson, '$.Operation.AutomationAdmission.Occurrence.EvidenceJson'), '$.triggerContext.sources[5].data.accountId'),'ffffffff-ffff-ffff-ffff-ffffffffffff'),coalesce(json_extract(json_extract(PayloadJson, '$.Operation.AutomationAdmission.Occurrence.EvidenceJson'), '$.triggerContext.sources[6].data.accountId'),'ffffffff-ffff-ffff-ffff-ffffffffffff'),coalesce(json_extract(json_extract(PayloadJson, '$.Operation.AutomationAdmission.Occurrence.EvidenceJson'), '$.triggerContext.sources[7].data.accountId'),'ffffffff-ffff-ffff-ffff-ffffffffffff'),coalesce(json_extract(json_extract(PayloadJson, '$.Operation.AutomationAdmission.Occurrence.EvidenceJson'), '$.triggerContext.sources[8].data.accountId'),'ffffffff-ffff-ffff-ffff-ffffffffffff'),coalesce(json_extract(json_extract(PayloadJson, '$.Operation.AutomationAdmission.Occurrence.EvidenceJson'), '$.triggerContext.sources[9].data.accountId'),'ffffffff-ffff-ffff-ffff-ffffffffffff'),coalesce(json_extract(json_extract(PayloadJson, '$.Operation.AutomationAdmission.Occurrence.EvidenceJson'), '$.triggerContext.sources[10].data.accountId'),'ffffffff-ffff-ffff-ffff-ffffffffffff'),coalesce(json_extract(json_extract(PayloadJson, '$.Operation.AutomationAdmission.Occurrence.EvidenceJson'), '$.triggerContext.sources[11].data.accountId'),'ffffffff-ffff-ffff-ffff-ffffffffffff'),coalesce(json_extract(json_extract(PayloadJson, '$.Operation.AutomationAdmission.Occurrence.EvidenceJson'), '$.triggerContext.sources[12].data.accountId'),'ffffffff-ffff-ffff-ffff-ffffffffffff'),coalesce(json_extract(json_extract(PayloadJson, '$.Operation.AutomationAdmission.Occurrence.EvidenceJson'), '$.triggerContext.sources[13].data.accountId'),'ffffffff-ffff-ffff-ffff-ffffffffffff'),coalesce(json_extract(json_extract(PayloadJson, '$.Operation.AutomationAdmission.Occurrence.EvidenceJson'), '$.triggerContext.sources[14].data.accountId'),'ffffffff-ffff-ffff-ffff-ffffffffffff'),coalesce(json_extract(json_extract(PayloadJson, '$.Operation.AutomationAdmission.Occurrence.EvidenceJson'), '$.triggerContext.sources[15].data.accountId'),'ffffffff-ffff-ffff-ffff-ffffffffffff'),coalesce(json_extract(json_extract(PayloadJson, '$.Operation.AutomationAdmission.Occurrence.EvidenceJson'), '$.triggerContext.sources[16].data.accountId'),'ffffffff-ffff-ffff-ffff-ffffffffffff'),coalesce(json_extract(json_extract(PayloadJson, '$.Operation.AutomationAdmission.Occurrence.EvidenceJson'), '$.triggerContext.sources[17].data.accountId'),'ffffffff-ffff-ffff-ffff-ffffffffffff'),coalesce(json_extract(json_extract(PayloadJson, '$.Operation.AutomationAdmission.Occurrence.EvidenceJson'), '$.triggerContext.sources[18].data.accountId'),'ffffffff-ffff-ffff-ffff-ffffffffffff'),coalesce(json_extract(json_extract(PayloadJson, '$.Operation.AutomationAdmission.Occurrence.EvidenceJson'), '$.triggerContext.sources[19].data.accountId'),'ffffffff-ffff-ffff-ffff-ffffffffffff'),coalesce(json_extract(json_extract(PayloadJson, '$.Operation.AutomationAdmission.Occurrence.EvidenceJson'), '$.triggerContext.sources[20].data.accountId'),'ffffffff-ffff-ffff-ffff-ffffffffffff'),coalesce(json_extract(json_extract(PayloadJson, '$.Operation.AutomationAdmission.Occurrence.EvidenceJson'), '$.triggerContext.sources[21].data.accountId'),'ffffffff-ffff-ffff-ffff-ffffffffffff'),coalesce(json_extract(json_extract(PayloadJson, '$.Operation.AutomationAdmission.Occurrence.EvidenceJson'), '$.triggerContext.sources[22].data.accountId'),'ffffffff-ffff-ffff-ffff-ffffffffffff'),coalesce(json_extract(json_extract(PayloadJson, '$.Operation.AutomationAdmission.Occurrence.EvidenceJson'), '$.triggerContext.sources[23].data.accountId'),'ffffffff-ffff-ffff-ffff-ffffffffffff')) WHEN 'credentials.use' THEN min(coalesce(json_extract(json_extract(PayloadJson, '$.Operation.AutomationAdmission.Occurrence.EvidenceJson'), '$.triggerContext.source.accountId'),'ffffffff-ffff-ffff-ffff-ffffffffffff'),coalesce(json_extract(json_extract(PayloadJson, '$.Operation.AutomationAdmission.Occurrence.EvidenceJson'), '$.triggerContext.sources[0].data.accountId'),'ffffffff-ffff-ffff-ffff-ffffffffffff'),coalesce(json_extract(json_extract(PayloadJson, '$.Operation.AutomationAdmission.Occurrence.EvidenceJson'), '$.triggerContext.sources[1].data.accountId'),'ffffffff-ffff-ffff-ffff-ffffffffffff'),coalesce(json_extract(json_extract(PayloadJson, '$.Operation.AutomationAdmission.Occurrence.EvidenceJson'), '$.triggerContext.sources[2].data.accountId'),'ffffffff-ffff-ffff-ffff-ffffffffffff'),coalesce(json_extract(json_extract(PayloadJson, '$.Operation.AutomationAdmission.Occurrence.EvidenceJson'), '$.triggerContext.sources[3].data.accountId'),'ffffffff-ffff-ffff-ffff-ffffffffffff'),coalesce(json_extract(json_extract(PayloadJson, '$.Operation.AutomationAdmission.Occurrence.EvidenceJson'), '$.triggerContext.sources[4].data.accountId'),'ffffffff-ffff-ffff-ffff-ffffffffffff'),coalesce(json_extract(json_extract(PayloadJson, '$.Operation.AutomationAdmission.Occurrence.EvidenceJson'), '$.triggerContext.sources[5].data.accountId'),'ffffffff-ffff-ffff-ffff-ffffffffffff'),coalesce(json_extract(json_extract(PayloadJson, '$.Operation.AutomationAdmission.Occurrence.EvidenceJson'), '$.triggerContext.sources[6].data.accountId'),'ffffffff-ffff-ffff-ffff-ffffffffffff'),coalesce(json_extract(json_extract(PayloadJson, '$.Operation.AutomationAdmission.Occurrence.EvidenceJson'), '$.triggerContext.sources[7].data.accountId'),'ffffffff-ffff-ffff-ffff-ffffffffffff'),coalesce(json_extract(json_extract(PayloadJson, '$.Operation.AutomationAdmission.Occurrence.EvidenceJson'), '$.triggerContext.sources[8].data.accountId'),'ffffffff-ffff-ffff-ffff-ffffffffffff'),coalesce(json_extract(json_extract(PayloadJson, '$.Operation.AutomationAdmission.Occurrence.EvidenceJson'), '$.triggerContext.sources[9].data.accountId'),'ffffffff-ffff-ffff-ffff-ffffffffffff'),coalesce(json_extract(json_extract(PayloadJson, '$.Operation.AutomationAdmission.Occurrence.EvidenceJson'), '$.triggerContext.sources[10].data.accountId'),'ffffffff-ffff-ffff-ffff-ffffffffffff'),coalesce(json_extract(json_extract(PayloadJson, '$.Operation.AutomationAdmission.Occurrence.EvidenceJson'), '$.triggerContext.sources[11].data.accountId'),'ffffffff-ffff-ffff-ffff-ffffffffffff'),coalesce(json_extract(json_extract(PayloadJson, '$.Operation.AutomationAdmission.Occurrence.EvidenceJson'), '$.triggerContext.sources[12].data.accountId'),'ffffffff-ffff-ffff-ffff-ffffffffffff'),coalesce(json_extract(json_extract(PayloadJson, '$.Operation.AutomationAdmission.Occurrence.EvidenceJson'), '$.triggerContext.sources[13].data.accountId'),'ffffffff-ffff-ffff-ffff-ffffffffffff'),coalesce(json_extract(json_extract(PayloadJson, '$.Operation.AutomationAdmission.Occurrence.EvidenceJson'), '$.triggerContext.sources[14].data.accountId'),'ffffffff-ffff-ffff-ffff-ffffffffffff'),coalesce(json_extract(json_extract(PayloadJson, '$.Operation.AutomationAdmission.Occurrence.EvidenceJson'), '$.triggerContext.sources[15].data.accountId'),'ffffffff-ffff-ffff-ffff-ffffffffffff'),coalesce(json_extract(json_extract(PayloadJson, '$.Operation.AutomationAdmission.Occurrence.EvidenceJson'), '$.triggerContext.sources[16].data.accountId'),'ffffffff-ffff-ffff-ffff-ffffffffffff'),coalesce(json_extract(json_extract(PayloadJson, '$.Operation.AutomationAdmission.Occurrence.EvidenceJson'), '$.triggerContext.sources[17].data.accountId'),'ffffffff-ffff-ffff-ffff-ffffffffffff'),coalesce(json_extract(json_extract(PayloadJson, '$.Operation.AutomationAdmission.Occurrence.EvidenceJson'), '$.triggerContext.sources[18].data.accountId'),'ffffffff-ffff-ffff-ffff-ffffffffffff'),coalesce(json_extract(json_extract(PayloadJson, '$.Operation.AutomationAdmission.Occurrence.EvidenceJson'), '$.triggerContext.sources[19].data.accountId'),'ffffffff-ffff-ffff-ffff-ffffffffffff'),coalesce(json_extract(json_extract(PayloadJson, '$.Operation.AutomationAdmission.Occurrence.EvidenceJson'), '$.triggerContext.sources[20].data.accountId'),'ffffffff-ffff-ffff-ffff-ffffffffffff'),coalesce(json_extract(json_extract(PayloadJson, '$.Operation.AutomationAdmission.Occurrence.EvidenceJson'), '$.triggerContext.sources[21].data.accountId'),'ffffffff-ffff-ffff-ffff-ffffffffffff'),coalesce(json_extract(json_extract(PayloadJson, '$.Operation.AutomationAdmission.Occurrence.EvidenceJson'), '$.triggerContext.sources[22].data.accountId'),'ffffffff-ffff-ffff-ffff-ffffffffffff'),coalesce(json_extract(json_extract(PayloadJson, '$.Operation.AutomationAdmission.Occurrence.EvidenceJson'), '$.triggerContext.sources[23].data.accountId'),'ffffffff-ffff-ffff-ffff-ffffffffffff')) WHEN 'email.read' THEN min(coalesce(json_extract(json_extract(PayloadJson, '$.Operation.AutomationAdmission.Occurrence.EvidenceJson'), '$.triggerContext.source.accountId'),'ffffffff-ffff-ffff-ffff-ffffffffffff'),coalesce(json_extract(json_extract(PayloadJson, '$.Operation.AutomationAdmission.Occurrence.EvidenceJson'), '$.triggerContext.sources[0].data.accountId'),'ffffffff-ffff-ffff-ffff-ffffffffffff'),coalesce(json_extract(json_extract(PayloadJson, '$.Operation.AutomationAdmission.Occurrence.EvidenceJson'), '$.triggerContext.sources[1].data.accountId'),'ffffffff-ffff-ffff-ffff-ffffffffffff'),coalesce(json_extract(json_extract(PayloadJson, '$.Operation.AutomationAdmission.Occurrence.EvidenceJson'), '$.triggerContext.sources[2].data.accountId'),'ffffffff-ffff-ffff-ffff-ffffffffffff'),coalesce(json_extract(json_extract(PayloadJson, '$.Operation.AutomationAdmission.Occurrence.EvidenceJson'), '$.triggerContext.sources[3].data.accountId'),'ffffffff-ffff-ffff-ffff-ffffffffffff'),coalesce(json_extract(json_extract(PayloadJson, '$.Operation.AutomationAdmission.Occurrence.EvidenceJson'), '$.triggerContext.sources[4].data.accountId'),'ffffffff-ffff-ffff-ffff-ffffffffffff'),coalesce(json_extract(json_extract(PayloadJson, '$.Operation.AutomationAdmission.Occurrence.EvidenceJson'), '$.triggerContext.sources[5].data.accountId'),'ffffffff-ffff-ffff-ffff-ffffffffffff'),coalesce(json_extract(json_extract(PayloadJson, '$.Operation.AutomationAdmission.Occurrence.EvidenceJson'), '$.triggerContext.sources[6].data.accountId'),'ffffffff-ffff-ffff-ffff-ffffffffffff'),coalesce(json_extract(json_extract(PayloadJson, '$.Operation.AutomationAdmission.Occurrence.EvidenceJson'), '$.triggerContext.sources[7].data.accountId'),'ffffffff-ffff-ffff-ffff-ffffffffffff'),coalesce(json_extract(json_extract(PayloadJson, '$.Operation.AutomationAdmission.Occurrence.EvidenceJson'), '$.triggerContext.sources[8].data.accountId'),'ffffffff-ffff-ffff-ffff-ffffffffffff'),coalesce(json_extract(json_extract(PayloadJson, '$.Operation.AutomationAdmission.Occurrence.EvidenceJson'), '$.triggerContext.sources[9].data.accountId'),'ffffffff-ffff-ffff-ffff-ffffffffffff'),coalesce(json_extract(json_extract(PayloadJson, '$.Operation.AutomationAdmission.Occurrence.EvidenceJson'), '$.triggerContext.sources[10].data.accountId'),'ffffffff-ffff-ffff-ffff-ffffffffffff'),coalesce(json_extract(json_extract(PayloadJson, '$.Operation.AutomationAdmission.Occurrence.EvidenceJson'), '$.triggerContext.sources[11].data.accountId'),'ffffffff-ffff-ffff-ffff-ffffffffffff'),coalesce(json_extract(json_extract(PayloadJson, '$.Operation.AutomationAdmission.Occurrence.EvidenceJson'), '$.triggerContext.sources[12].data.accountId'),'ffffffff-ffff-ffff-ffff-ffffffffffff'),coalesce(json_extract(json_extract(PayloadJson, '$.Operation.AutomationAdmission.Occurrence.EvidenceJson'), '$.triggerContext.sources[13].data.accountId'),'ffffffff-ffff-ffff-ffff-ffffffffffff'),coalesce(json_extract(json_extract(PayloadJson, '$.Operation.AutomationAdmission.Occurrence.EvidenceJson'), '$.triggerContext.sources[14].data.accountId'),'ffffffff-ffff-ffff-ffff-ffffffffffff'),coalesce(json_extract(json_extract(PayloadJson, '$.Operation.AutomationAdmission.Occurrence.EvidenceJson'), '$.triggerContext.sources[15].data.accountId'),'ffffffff-ffff-ffff-ffff-ffffffffffff'),coalesce(json_extract(json_extract(PayloadJson, '$.Operation.AutomationAdmission.Occurrence.EvidenceJson'), '$.triggerContext.sources[16].data.accountId'),'ffffffff-ffff-ffff-ffff-ffffffffffff'),coalesce(json_extract(json_extract(PayloadJson, '$.Operation.AutomationAdmission.Occurrence.EvidenceJson'), '$.triggerContext.sources[17].data.accountId'),'ffffffff-ffff-ffff-ffff-ffffffffffff'),coalesce(json_extract(json_extract(PayloadJson, '$.Operation.AutomationAdmission.Occurrence.EvidenceJson'), '$.triggerContext.sources[18].data.accountId'),'ffffffff-ffff-ffff-ffff-ffffffffffff'),coalesce(json_extract(json_extract(PayloadJson, '$.Operation.AutomationAdmission.Occurrence.EvidenceJson'), '$.triggerContext.sources[19].data.accountId'),'ffffffff-ffff-ffff-ffff-ffffffffffff'),coalesce(json_extract(json_extract(PayloadJson, '$.Operation.AutomationAdmission.Occurrence.EvidenceJson'), '$.triggerContext.sources[20].data.accountId'),'ffffffff-ffff-ffff-ffff-ffffffffffff'),coalesce(json_extract(json_extract(PayloadJson, '$.Operation.AutomationAdmission.Occurrence.EvidenceJson'), '$.triggerContext.sources[21].data.accountId'),'ffffffff-ffff-ffff-ffff-ffffffffffff'),coalesce(json_extract(json_extract(PayloadJson, '$.Operation.AutomationAdmission.Occurrence.EvidenceJson'), '$.triggerContext.sources[22].data.accountId'),'ffffffff-ffff-ffff-ffff-ffffffffffff'),coalesce(json_extract(json_extract(PayloadJson, '$.Operation.AutomationAdmission.Occurrence.EvidenceJson'), '$.triggerContext.sources[23].data.accountId'),'ffffffff-ffff-ffff-ffff-ffffffffffff')) ELSE lower(TenantId) END
        AND json_type(PayloadJson, '$.Operation.AutomationAdmission') IS 'object'
        AND ((json_extract(PayloadJson, '$.Operation.AutomationAdmission.Occurrence.ExecutionTarget.Kind') = 0
                  AND json_extract(PayloadJson, '$.Operation.AutomationAdmission.Occurrence.ExecutionTarget.SessionId') IS NULL
                  AND json_type(PayloadJson, '$.Operation.AutomationAdmission.Current.TargetSessionId') IS NULL
                  AND json_type(PayloadJson, '$.Operation.AutomationAdmission.Next.TargetSessionId') IS NULL
                  AND json_extract(PayloadJson, '$.Operation.AutomationAdmission.Conversation') IS NULL
                  AND json_extract(PayloadJson, '$.Operation.AutomationAdmission.Current.ReportToSessionId') IS json_extract(PayloadJson, '$.Operation.AutomationAdmission.Next.ReportToSessionId')
                  AND json_extract(PayloadJson, '$.Operation.AutomationAdmission.Current.ReportToSessionId') IS json_extract(PayloadJson, '$.Operation.AutomationAdmission.Occurrence.CompletionDelivery.SessionId')
                  AND ((json_extract(PayloadJson, '$.Operation.AutomationAdmission.Current.ReportToSessionId') IS NULL
                    AND json_type(PayloadJson, '$.Operation.AutomationAdmission.Current.ReportToSessionId') IS NULL
                    AND json_type(PayloadJson, '$.Operation.AutomationAdmission.Next.ReportToSessionId') IS NULL
                    AND json_extract(PayloadJson, '$.Operation.AutomationAdmission.Current.SchemaVersion') IS NOT 3
                    AND json_extract(PayloadJson, '$.Operation.AutomationAdmission.Next.SchemaVersion') IS NOT 3)
                  OR (json_type(PayloadJson, '$.Operation.AutomationAdmission.Current.ReportToSessionId') IS 'text'
                    AND length(json_extract(PayloadJson, '$.Operation.AutomationAdmission.Current.ReportToSessionId')) = 36
                    AND json_extract(PayloadJson, '$.Operation.AutomationAdmission.Current.ReportToSessionId') NOT GLOB '*[^0-9a-f-]*'
                    AND json_extract(PayloadJson, '$.Operation.AutomationAdmission.Current.ReportToSessionId') <> '00000000-0000-0000-0000-000000000000'
                    AND substr(json_extract(PayloadJson, '$.Operation.AutomationAdmission.Current.ReportToSessionId'),9,1) = '-'
                    AND substr(json_extract(PayloadJson, '$.Operation.AutomationAdmission.Current.ReportToSessionId'),14,1) = '-'
                    AND substr(json_extract(PayloadJson, '$.Operation.AutomationAdmission.Current.ReportToSessionId'),19,1) = '-'
                    AND substr(json_extract(PayloadJson, '$.Operation.AutomationAdmission.Current.ReportToSessionId'),24,1) = '-'
                    AND json_extract(PayloadJson, '$.Operation.AutomationAdmission.Current.SchemaVersion') = 3
                    AND json_extract(PayloadJson, '$.Operation.AutomationAdmission.Next.SchemaVersion') = 3)))
                OR (json_extract(PayloadJson, '$.Operation.AutomationAdmission.Occurrence.ExecutionTarget.Kind') = 1
                  AND json_extract(PayloadJson, '$.Operation.AutomationAdmission.Occurrence.ExecutionTarget.SessionId') IS json_extract(PayloadJson, '$.Operation.AutomationAdmission.SessionId')
                  AND json_extract(PayloadJson, '$.Operation.AutomationAdmission.Current.TargetSessionId') IS json_extract(PayloadJson, '$.Operation.AutomationAdmission.SessionId')
                  AND json_extract(PayloadJson, '$.Operation.AutomationAdmission.Next.TargetSessionId') IS json_extract(PayloadJson, '$.Operation.AutomationAdmission.SessionId')
                  AND json_extract(PayloadJson, '$.Operation.AutomationAdmission.Current.SchemaVersion') = 4
                  AND json_extract(PayloadJson, '$.Operation.AutomationAdmission.Next.SchemaVersion') = 4
                  AND json_type(PayloadJson, '$.Operation.AutomationAdmission.Current.ReportToSessionId') IS NULL
                  AND json_type(PayloadJson, '$.Operation.AutomationAdmission.Next.ReportToSessionId') IS NULL
                  AND json_extract(PayloadJson, '$.Operation.AutomationAdmission.Occurrence.CompletionDelivery.SessionId') IS NULL
                  AND json_type(PayloadJson, '$.Operation.AutomationAdmission.Conversation') IS 'object'
                  AND json_extract(PayloadJson, '$.Operation.AutomationAdmission.Conversation.SessionRevision') >= 1
                  AND json_extract(PayloadJson, '$.Operation.AutomationAdmission.Conversation.InputBoundarySequence') >= 0))
        AND json_extract(PayloadJson, '$.Operation.AutomationAdmission.TenantId') IS lower(TenantId)
        AND json_extract(PayloadJson, '$.Operation.AutomationAdmission.Permission') IS Permission
        AND json_extract(PayloadJson, '$.Operation.AutomationAdmission.RequestId') IS lower(RequestId)
        AND json_extract(PayloadJson, '$.Operation.AutomationAdmission.Current.AutomationId') IS AutomationId
        AND json_extract(PayloadJson, '$.Operation.AutomationAdmission.Next.AutomationId') IS AutomationId
        AND json_extract(PayloadJson, '$.Operation.AutomationAdmission.Current.Status') = 0
        AND json_extract(PayloadJson, '$.Operation.AutomationAdmission.Next.Status') IN (0,1)
        AND json_extract(PayloadJson, '$.Operation.AutomationAdmission.Next.Revision') = json_extract(PayloadJson, '$.Operation.AutomationAdmission.Current.Revision') + 1
        AND json_extract(PayloadJson, '$.Operation.AutomationAdmission.Current.Owner.TenantId') IS lower(TenantId)
        AND json_extract(PayloadJson, '$.Operation.AutomationAdmission.Current.Owner.AgentInstanceId') IS InstanceId
        AND json_extract(PayloadJson, '$.Operation.AutomationAdmission.Current.Owner.ExecutorPrincipalId') IS lower(ExecutorPrincipalId)
        AND json_extract(PayloadJson, '$.Operation.AutomationAdmission.Current.Owner.profileId') IS NULL
        AND json_extract(PayloadJson, '$.Operation.AutomationAdmission.Next.Owner') IS json_extract(PayloadJson, '$.Operation.AutomationAdmission.Current.Owner')
        AND json_extract(PayloadJson, '$.Operation.AutomationAdmission.Occurrence.Owner') IS json_extract(PayloadJson, '$.Operation.AutomationAdmission.Current.Owner')
        AND json_extract(PayloadJson, '$.Operation.AutomationAdmission.Occurrence.AutomationId') IS AutomationId
        AND json_extract(PayloadJson, '$.Operation.AutomationAdmission.Occurrence.SourceKind') IN (0,1,2,3)
        AND json_extract(PayloadJson, '$.Operation.AutomationAdmission.Occurrence.Disposition') = 0
        AND json_extract(PayloadJson, '$.Operation.AutomationAdmission.Occurrence.RoutingRevision') = 0
        AND json_extract(PayloadJson, '$.Operation.AutomationAdmission.Occurrence.ExecutionSessionId') IS NULL
        AND json_extract(PayloadJson, '$.Operation.AutomationAdmission.Occurrence.AcceptedAgentRunId') IS NULL
        AND ((json_extract(PayloadJson, '$.Operation.AutomationAdmission.Occurrence.SourceKind') IN (0,1,3) AND OriginalInitiatorPrincipalId IS NULL)
          OR (json_extract(PayloadJson, '$.Operation.AutomationAdmission.Occurrence.SourceKind') = 2 AND OriginalInitiatorPrincipalId IS NOT NULL))
        AND json_extract(PayloadJson, '$.Operation.AutomationAdmission.ManualInitiatorPrincipalId') IS lower(OriginalInitiatorPrincipalId)
        AND json_extract(PayloadJson, '$.Operation.AutomationAdmission.RunId') IS json_extract(PayloadJson, '$.RunId')
        AND json_extract(PayloadJson, '$.Operation.AutomationAdmission.SessionId') IS json_extract(PayloadJson, '$.SessionId')
        AND json_extract(PayloadJson, '$.Operation.AutomationAdmission.ActivationId') IS RootActivationId
        AND json_type(PayloadJson, '$.Operation.AutomationAdmission.ConfigurationHash') IS 'text'
        AND length(json_extract(PayloadJson, '$.Operation.AutomationAdmission.ConfigurationHash')) = 64
        AND json_extract(PayloadJson, '$.Operation.AutomationAdmission.ConfigurationHash') NOT GLOB '*[^0-9a-f]*'
        AND json_type(PayloadJson, '$.Operation.AutomationAdmission.Model') IS 'object'
        AND length(CAST(PayloadJson AS BLOB)) <= CASE WHEN json_type(PayloadJson, '$.Operation.AutomationAdmission.EventSource') IS 'object' THEN 1048576 ELSE 262144 END AND ((json_extract(PayloadJson, '$.Operation.AutomationAdmission.Occurrence.SourceKind') IN (0,2)
          AND json_extract(PayloadJson, '$.Operation.AutomationAdmission.EventSource') IS NULL
          AND Permission NOT IN ('webhooks.read','core-events.read','credentials.read','credentials.use','email.read'))
        OR (json_extract(PayloadJson, '$.Operation.AutomationAdmission.Occurrence.SourceKind') IN (1,3)
          AND json_type(PayloadJson, '$.Operation.AutomationAdmission.EventSource') IS 'object'
          AND json_extract(PayloadJson, '$.Operation.AutomationAdmission.EventSource.TenantId') IS lower(TenantId)
          AND json_extract(PayloadJson, '$.Operation.AutomationAdmission.EventSource.SourceKind') = json_extract(PayloadJson, '$.Operation.AutomationAdmission.Occurrence.SourceKind')
          AND json_extract(PayloadJson, '$.Operation.AutomationAdmission.EventSource.TriggerId') IS json_extract(PayloadJson, '$.Operation.AutomationAdmission.Occurrence.TriggerId')
          AND json_extract(PayloadJson, '$.Operation.AutomationAdmission.EventSource.SourceId') IS json_extract(PayloadJson, '$.Operation.AutomationAdmission.Occurrence.SourceEventId')
          AND json_extract(PayloadJson, '$.Operation.AutomationAdmission.EventSource.DedupeKey') IS json_extract(PayloadJson, '$.Operation.AutomationAdmission.Occurrence.DedupeKey')
          AND json_type(PayloadJson, '$.Operation.AutomationAdmission.EventSource.IsBucket') IN ('true','false')
          AND json_type(PayloadJson, '$.Operation.AutomationAdmission.EventSource.EventIds') IS 'array'
          AND json_array_length(PayloadJson, '$.Operation.AutomationAdmission.EventSource.EventIds') BETWEEN 1 AND 24
          AND (json_extract(PayloadJson, '$.Operation.AutomationAdmission.EventSource.IsBucket') = 1
            OR (json_array_length(PayloadJson, '$.Operation.AutomationAdmission.EventSource.EventIds') = 1
                AND json_extract(PayloadJson, '$.Operation.AutomationAdmission.EventSource.EventIds[0]') IS json_extract(PayloadJson, '$.Operation.AutomationAdmission.EventSource.SourceId')))
          AND length(json_extract(PayloadJson, '$.Operation.AutomationAdmission.EventSource.SourceHash')) = 64
          AND json_extract(PayloadJson, '$.Operation.AutomationAdmission.EventSource.SourceHash') NOT GLOB '*[^0-9a-f]*'
          AND json_extract(PayloadJson, '$.Operation.AutomationAdmission.Current.SchemaVersion') IN (2,3,4)
          AND json_extract(PayloadJson, '$.Operation.AutomationAdmission.Next.SchemaVersion') IN (2,3,4)
          AND ((json_extract(PayloadJson, '$.Operation.AutomationAdmission.EventSource.SourceKind') = 1
            AND json_extract(PayloadJson, '$.Operation.AutomationAdmission.EventSource.ResourceRevision') >= 1
            AND length(json_extract(PayloadJson, '$.Operation.AutomationAdmission.EventSource.ResourceId')) = 36
            AND Permission NOT IN ('core-events.read','credentials.read','credentials.use','email.read'))
           OR (json_extract(PayloadJson, '$.Operation.AutomationAdmission.EventSource.SourceKind') = 3
            AND json_extract(PayloadJson, '$.Operation.AutomationAdmission.EventSource.ResourceRevision') IS NULL
            AND json_extract(PayloadJson, '$.Operation.AutomationAdmission.EventSource.ResourceId') IS NULL
            AND Permission <> 'webhooks.read')))) AND json_type(PayloadJson, '$.Operation.AutomationAdmission.RequestId') IS 'text' AND length(json_extract(PayloadJson, '$.Operation.AutomationAdmission.RequestId')) = 36 AND json_extract(PayloadJson, '$.Operation.AutomationAdmission.RequestId') NOT GLOB '*[^0-9a-f-]*' AND json_extract(PayloadJson, '$.Operation.AutomationAdmission.RequestId') IS NOT '00000000-0000-0000-0000-000000000000' AND substr(json_extract(PayloadJson, '$.Operation.AutomationAdmission.RequestId'),9,1) IS '-' AND substr(json_extract(PayloadJson, '$.Operation.AutomationAdmission.RequestId'),14,1) IS '-' AND substr(json_extract(PayloadJson, '$.Operation.AutomationAdmission.RequestId'),19,1) IS '-' AND substr(json_extract(PayloadJson, '$.Operation.AutomationAdmission.RequestId'),24,1) IS '-' AND json_type(PayloadJson, '$.Operation.AutomationAdmission.SessionId') IS 'text' AND length(json_extract(PayloadJson, '$.Operation.AutomationAdmission.SessionId')) = 36 AND json_extract(PayloadJson, '$.Operation.AutomationAdmission.SessionId') NOT GLOB '*[^0-9a-f-]*' AND json_extract(PayloadJson, '$.Operation.AutomationAdmission.SessionId') IS NOT '00000000-0000-0000-0000-000000000000' AND substr(json_extract(PayloadJson, '$.Operation.AutomationAdmission.SessionId'),9,1) IS '-' AND substr(json_extract(PayloadJson, '$.Operation.AutomationAdmission.SessionId'),14,1) IS '-' AND substr(json_extract(PayloadJson, '$.Operation.AutomationAdmission.SessionId'),19,1) IS '-' AND substr(json_extract(PayloadJson, '$.Operation.AutomationAdmission.SessionId'),24,1) IS '-' AND json_type(PayloadJson, '$.Operation.AutomationAdmission.EntryId') IS 'text' AND length(json_extract(PayloadJson, '$.Operation.AutomationAdmission.EntryId')) = 36 AND json_extract(PayloadJson, '$.Operation.AutomationAdmission.EntryId') NOT GLOB '*[^0-9a-f-]*' AND json_extract(PayloadJson, '$.Operation.AutomationAdmission.EntryId') IS NOT '00000000-0000-0000-0000-000000000000' AND substr(json_extract(PayloadJson, '$.Operation.AutomationAdmission.EntryId'),9,1) IS '-' AND substr(json_extract(PayloadJson, '$.Operation.AutomationAdmission.EntryId'),14,1) IS '-' AND substr(json_extract(PayloadJson, '$.Operation.AutomationAdmission.EntryId'),19,1) IS '-' AND substr(json_extract(PayloadJson, '$.Operation.AutomationAdmission.EntryId'),24,1) IS '-' AND json_type(PayloadJson, '$.Operation.AutomationAdmission.ActivationId') IS 'text' AND length(json_extract(PayloadJson, '$.Operation.AutomationAdmission.ActivationId')) = 36 AND json_extract(PayloadJson, '$.Operation.AutomationAdmission.ActivationId') NOT GLOB '*[^0-9a-f-]*' AND json_extract(PayloadJson, '$.Operation.AutomationAdmission.ActivationId') IS NOT '00000000-0000-0000-0000-000000000000' AND substr(json_extract(PayloadJson, '$.Operation.AutomationAdmission.ActivationId'),9,1) IS '-' AND substr(json_extract(PayloadJson, '$.Operation.AutomationAdmission.ActivationId'),14,1) IS '-' AND substr(json_extract(PayloadJson, '$.Operation.AutomationAdmission.ActivationId'),19,1) IS '-' AND substr(json_extract(PayloadJson, '$.Operation.AutomationAdmission.ActivationId'),24,1) IS '-' AND json_type(PayloadJson, '$.Operation.AutomationAdmission.RunId') IS 'text' AND length(json_extract(PayloadJson, '$.Operation.AutomationAdmission.RunId')) = 36 AND json_extract(PayloadJson, '$.Operation.AutomationAdmission.RunId') NOT GLOB '*[^0-9a-f-]*' AND json_extract(PayloadJson, '$.Operation.AutomationAdmission.RunId') IS NOT '00000000-0000-0000-0000-000000000000' AND substr(json_extract(PayloadJson, '$.Operation.AutomationAdmission.RunId'),9,1) IS '-' AND substr(json_extract(PayloadJson, '$.Operation.AutomationAdmission.RunId'),14,1) IS '-' AND substr(json_extract(PayloadJson, '$.Operation.AutomationAdmission.RunId'),19,1) IS '-' AND substr(json_extract(PayloadJson, '$.Operation.AutomationAdmission.RunId'),24,1) IS '-' AND json_type(PayloadJson, '$.Operation.AutomationAdmission.ResponseId') IS 'text' AND length(json_extract(PayloadJson, '$.Operation.AutomationAdmission.ResponseId')) = 36 AND json_extract(PayloadJson, '$.Operation.AutomationAdmission.ResponseId') NOT GLOB '*[^0-9a-f-]*' AND json_extract(PayloadJson, '$.Operation.AutomationAdmission.ResponseId') IS NOT '00000000-0000-0000-0000-000000000000' AND substr(json_extract(PayloadJson, '$.Operation.AutomationAdmission.ResponseId'),9,1) IS '-' AND substr(json_extract(PayloadJson, '$.Operation.AutomationAdmission.ResponseId'),14,1) IS '-' AND substr(json_extract(PayloadJson, '$.Operation.AutomationAdmission.ResponseId'),19,1) IS '-' AND substr(json_extract(PayloadJson, '$.Operation.AutomationAdmission.ResponseId'),24,1) IS '-' AND json_extract(PayloadJson, '$.Operation.AutomationAdmission.RequestId') IS NOT json_extract(PayloadJson, '$.Operation.AutomationAdmission.SessionId') AND json_extract(PayloadJson, '$.Operation.AutomationAdmission.RequestId') IS NOT json_extract(PayloadJson, '$.Operation.AutomationAdmission.EntryId') AND json_extract(PayloadJson, '$.Operation.AutomationAdmission.RequestId') IS NOT json_extract(PayloadJson, '$.Operation.AutomationAdmission.ActivationId') AND json_extract(PayloadJson, '$.Operation.AutomationAdmission.RequestId') IS NOT json_extract(PayloadJson, '$.Operation.AutomationAdmission.RunId') AND json_extract(PayloadJson, '$.Operation.AutomationAdmission.RequestId') IS NOT json_extract(PayloadJson, '$.Operation.AutomationAdmission.ResponseId') AND json_extract(PayloadJson, '$.Operation.AutomationAdmission.SessionId') IS NOT json_extract(PayloadJson, '$.Operation.AutomationAdmission.EntryId') AND json_extract(PayloadJson, '$.Operation.AutomationAdmission.SessionId') IS NOT json_extract(PayloadJson, '$.Operation.AutomationAdmission.ActivationId') AND json_extract(PayloadJson, '$.Operation.AutomationAdmission.SessionId') IS NOT json_extract(PayloadJson, '$.Operation.AutomationAdmission.RunId') AND json_extract(PayloadJson, '$.Operation.AutomationAdmission.SessionId') IS NOT json_extract(PayloadJson, '$.Operation.AutomationAdmission.ResponseId') AND json_extract(PayloadJson, '$.Operation.AutomationAdmission.EntryId') IS NOT json_extract(PayloadJson, '$.Operation.AutomationAdmission.ActivationId') AND json_extract(PayloadJson, '$.Operation.AutomationAdmission.EntryId') IS NOT json_extract(PayloadJson, '$.Operation.AutomationAdmission.RunId') AND json_extract(PayloadJson, '$.Operation.AutomationAdmission.EntryId') IS NOT json_extract(PayloadJson, '$.Operation.AutomationAdmission.ResponseId') AND json_extract(PayloadJson, '$.Operation.AutomationAdmission.ActivationId') IS NOT json_extract(PayloadJson, '$.Operation.AutomationAdmission.RunId') AND json_extract(PayloadJson, '$.Operation.AutomationAdmission.ActivationId') IS NOT json_extract(PayloadJson, '$.Operation.AutomationAdmission.ResponseId') AND json_extract(PayloadJson, '$.Operation.AutomationAdmission.RunId') IS NOT json_extract(PayloadJson, '$.Operation.AutomationAdmission.ResponseId') AND json_extract(PayloadJson, '$.Operation.Persona') IS NULL AND json_extract(PayloadJson, '$.Operation.Preferences') IS NULL AND json_extract(PayloadJson, '$.Operation.CredentialCheck') IS NULL AND json_extract(PayloadJson, '$.Operation.SandboxCommand') IS NULL AND json_extract(PayloadJson, '$.Operation.SandboxControl') IS NULL AND json_extract(PayloadJson, '$.Operation.SandboxRead') IS NULL AND json_extract(PayloadJson, '$.Operation.BrowserCommand') IS NULL AND json_extract(PayloadJson, '$.Operation.CredentialRead') IS NULL AND json_extract(PayloadJson, '$.Operation.ArtifactPublication') IS NULL AND json_extract(PayloadJson, '$.Operation.ArtifactVerification') IS NULL AND json_extract(PayloadJson, '$.Operation.WorkspaceRead') IS NULL AND json_extract(PayloadJson, '$.Operation.WorkspaceMutation') IS NULL AND json_extract(PayloadJson, '$.Operation.AttachmentRead') IS NULL AND json_extract(PayloadJson, '$.Operation.BackgroundStart') IS NULL AND json_extract(PayloadJson, '$.Operation.BackgroundReport') IS NULL AND json_extract(PayloadJson, '$.Operation.CompletionRead') IS NULL AND json_extract(PayloadJson, '$.Operation.ReportSourceRead') IS NULL AND json_extract(PayloadJson, '$.Operation.AccountRead') IS NULL AND json_extract(PayloadJson, '$.Operation.AccountAction') IS NULL),0)) AND json_extract(PayloadJson, '$.Operation.MemoryOperation') IS NULL) OR COALESCE((Kind IN (0,1) AND Status IN (0,1,2,3,4) AND Revision >= 1 AND ExpiresAt > CreatedAt
        AND length(Fingerprint) = 64 AND Fingerprint NOT GLOB '*[^0-9a-f]*'
        AND ResourceType = 'agent' AND ResourceId = InstanceId AND AutomationId IS NULL
        AND CredentialId IS NULL AND SandboxProfileId IS NULL AND SandboxProfileRevision IS NULL
        AND json_type(PayloadJson, '$.Operation.MemoryOperation') IS 'object'
        AND json_extract(PayloadJson, '$.Operation.MemoryOperation.SessionId') IS SessionId
        AND json_type(PayloadJson, '$.Operation.MemoryOperation.RootSessionId') IS 'text'
        AND length(json_extract(PayloadJson, '$.Operation.MemoryOperation.RootSessionId')) = 36
        AND (json_type(PayloadJson, '$.Operation.MemoryOperation.UserPrincipalId') IS 'null' OR (
            json_type(PayloadJson, '$.Operation.MemoryOperation.UserPrincipalId') IS 'text'
            AND length(json_extract(PayloadJson, '$.Operation.MemoryOperation.UserPrincipalId')) = 36))
        AND json_extract(PayloadJson, '$.Operation.MemoryOperation.ToolName') IN ('continuity.search','continuity.get','memory.forget','memory.consolidate')
        AND Permission IS CASE WHEN json_extract(PayloadJson, '$.Operation.MemoryOperation.ToolName') IN ('continuity.search','continuity.get') THEN 'memory.read' ELSE 'memory.manage' END
        AND (Permission = 'memory.manage' OR Kind = 0)
        AND json_type(PayloadJson, '$.Operation.MemoryOperation.ArgumentsJson') IS 'text'
        AND length(CAST(json_extract(PayloadJson, '$.Operation.MemoryOperation.ArgumentsJson') AS BLOB)) BETWEEN 2 AND 8192
        AND json_valid(json_extract(PayloadJson, '$.Operation.MemoryOperation.ArgumentsJson'))
        AND json_type(json_extract(PayloadJson, '$.Operation.MemoryOperation.ArgumentsJson')) IS 'object'
        AND json_type(PayloadJson, '$.Operation.MemoryOperation.SourcesJson') IS 'text'
        AND length(json_extract(PayloadJson, '$.Operation.MemoryOperation.SourcesJson')) BETWEEN 2 AND 8192
        AND json_valid(json_extract(PayloadJson, '$.Operation.MemoryOperation.SourcesJson'))
        AND json_type(json_extract(PayloadJson, '$.Operation.MemoryOperation.SourcesJson')) IS 'array'
        AND json_array_length(json_extract(PayloadJson, '$.Operation.MemoryOperation.SourcesJson')) BETWEEN 0 AND 11
        AND json_type(PayloadJson, '$.Operation.MemoryOperation.SourceLabelsJson') IS 'text'
        AND length(CAST(json_extract(PayloadJson, '$.Operation.MemoryOperation.SourceLabelsJson') AS BLOB)) BETWEEN 2 AND 16384
        AND json_valid(json_extract(PayloadJson, '$.Operation.MemoryOperation.SourceLabelsJson'))
        AND json_type(json_extract(PayloadJson, '$.Operation.MemoryOperation.SourceLabelsJson')) IS 'array'
        AND json_array_length(json_extract(PayloadJson, '$.Operation.MemoryOperation.SourceLabelsJson')) = json_array_length(json_extract(PayloadJson, '$.Operation.MemoryOperation.SourcesJson'))
        AND json_type(PayloadJson, '$.Operation.MemoryOperation.ObservationHash') IS 'text'
        AND length(json_extract(PayloadJson, '$.Operation.MemoryOperation.ObservationHash')) = 64
        AND json_extract(PayloadJson, '$.Operation.MemoryOperation.ObservationHash') NOT GLOB '*[^0-9a-f]*'
        AND CASE WHEN json_extract(PayloadJson, '$.Operation.MemoryOperation.ToolName') = 'memory.consolidate' THEN
            json_type(PayloadJson, '$.Operation.MemoryOperation.ResultMemoryId') IS 'text'
            AND length(json_extract(PayloadJson, '$.Operation.MemoryOperation.ResultMemoryId')) = 36
            AND json_array_length(json_extract(PayloadJson, '$.Operation.MemoryOperation.SourcesJson')) BETWEEN 2 AND 8
            ELSE json_type(PayloadJson, '$.Operation.MemoryOperation.ResultMemoryId') IS 'null' END
        AND (json_extract(PayloadJson, '$.Operation.MemoryOperation.ToolName') NOT IN ('continuity.get','memory.forget')
            OR json_array_length(json_extract(PayloadJson, '$.Operation.MemoryOperation.SourcesJson')) = 1)
        AND length(CAST(PayloadJson AS BLOB)) <= 131072 AND json_extract(PayloadJson, '$.Operation.Persona') IS NULL AND json_extract(PayloadJson, '$.Operation.Preferences') IS NULL AND json_extract(PayloadJson, '$.Operation.CredentialCheck') IS NULL AND json_extract(PayloadJson, '$.Operation.SandboxCommand') IS NULL AND json_extract(PayloadJson, '$.Operation.SandboxControl') IS NULL AND json_extract(PayloadJson, '$.Operation.SandboxRead') IS NULL AND json_extract(PayloadJson, '$.Operation.BrowserCommand') IS NULL AND json_extract(PayloadJson, '$.Operation.CredentialRead') IS NULL AND json_extract(PayloadJson, '$.Operation.ArtifactPublication') IS NULL AND json_extract(PayloadJson, '$.Operation.ArtifactVerification') IS NULL AND json_extract(PayloadJson, '$.Operation.WorkspaceRead') IS NULL AND json_extract(PayloadJson, '$.Operation.WorkspaceMutation') IS NULL AND json_extract(PayloadJson, '$.Operation.AttachmentRead') IS NULL AND json_extract(PayloadJson, '$.Operation.BackgroundStart') IS NULL AND json_extract(PayloadJson, '$.Operation.BackgroundReport') IS NULL AND json_extract(PayloadJson, '$.Operation.CompletionRead') IS NULL AND json_extract(PayloadJson, '$.Operation.ReportSourceRead') IS NULL AND json_extract(PayloadJson, '$.Operation.AccountRead') IS NULL AND json_extract(PayloadJson, '$.Operation.AccountAction') IS NULL AND json_extract(PayloadJson, '$.Operation.AutomationAdmission') IS NULL), 0)),
            CONSTRAINT "FK_TenantApprovalRequests_AgentRuns_RunId_RootActivationId" FOREIGN KEY ("RunId", "RootActivationId") REFERENCES "AgentRuns" ("AgentRunId", "RootActivationId") ON DELETE RESTRICT,
            CONSTRAINT "FK_TenantApprovalRequests_AgentRuns_RunId_SessionId" FOREIGN KEY ("RunId", "SessionId") REFERENCES "AgentRuns" ("AgentRunId", "SessionId") ON DELETE RESTRICT,
            CONSTRAINT "FK_TenantApprovalRequests_SandboxProfiles_SandboxProfileId_SandboxProfileRevision" FOREIGN KEY ("SandboxProfileId", "SandboxProfileRevision") REFERENCES "SandboxProfiles" ("Id", "Revision") ON DELETE RESTRICT,
            CONSTRAINT "FK_TenantApprovalRequests_TenantCredentials_TenantId_CredentialId" FOREIGN KEY ("TenantId", "CredentialId") REFERENCES "TenantCredentials" ("TenantId", "CredentialId") ON DELETE RESTRICT,
            CONSTRAINT "FK_TenantApprovalRequests_TenantSandboxProfiles_TenantId_SandboxProfileId" FOREIGN KEY ("TenantId", "SandboxProfileId") REFERENCES "TenantSandboxProfiles" ("TenantId", "ProfileId") ON DELETE RESTRICT,
            CONSTRAINT "FK_TenantApprovalRequests_TenantSessions_TenantId_SessionId_InstanceId_ExecutorPrincipalId" FOREIGN KEY ("TenantId", "SessionId", "InstanceId", "ExecutorPrincipalId") REFERENCES "TenantSessions" ("TenantId", "SessionId", "AgentInstanceId", "ExecutorPrincipalId") ON DELETE RESTRICT,
            CONSTRAINT "FK_TenantApprovalRequests_TenantUsers_TenantId_OriginalInitiatorPrincipalId" FOREIGN KEY ("TenantId", "OriginalInitiatorPrincipalId") REFERENCES "TenantUsers" ("TenantId", "PrincipalId") ON DELETE RESTRICT
        );
        INSERT INTO "TenantApprovalRequests_ContinuityRebuild" ("RequestId", "TenantId", "RunId", "SessionId", "AutomationId", "InstanceId", "RootActivationId", "ExecutorPrincipalId", "OriginalInitiatorPrincipalId", "OperationId", "Fingerprint", "Permission", "ResourceType", "ResourceId", "CredentialId", "SandboxProfileId", "SandboxProfileRevision", "Kind", "Status", "Revision", "CreatedAt", "ExpiresAt", "PayloadJson") SELECT "RequestId", "TenantId", "RunId", "SessionId", "AutomationId", "InstanceId", "RootActivationId", "ExecutorPrincipalId", "OriginalInitiatorPrincipalId", "OperationId", "Fingerprint", "Permission", "ResourceType", "ResourceId", "CredentialId", "SandboxProfileId", "SandboxProfileRevision", "Kind", "Status", "Revision", "CreatedAt", "ExpiresAt", "PayloadJson" FROM "TenantApprovalRequests";
        DROP TABLE "TenantApprovalRequests";
        ALTER TABLE "TenantApprovalRequests_ContinuityRebuild" RENAME TO "TenantApprovalRequests";
        CREATE UNIQUE INDEX "IX_TenantApprovalRequests_OperationId" ON "TenantApprovalRequests" ("OperationId");
        CREATE INDEX "IX_TenantApprovalRequests_RunId_RootActivationId" ON "TenantApprovalRequests" ("RunId", "RootActivationId");
        CREATE INDEX "IX_TenantApprovalRequests_RunId_SessionId" ON "TenantApprovalRequests" ("RunId", "SessionId");
        CREATE INDEX "IX_TenantApprovalRequests_SandboxProfileId_SandboxProfileRevision" ON "TenantApprovalRequests" ("SandboxProfileId", "SandboxProfileRevision");
        CREATE INDEX "IX_TenantApprovalRequests_Status_ExpiresAt_RequestId" ON "TenantApprovalRequests" ("Status", "ExpiresAt", "RequestId");
        CREATE INDEX "IX_TenantApprovalRequests_TenantId_AutomationId_Status" ON "TenantApprovalRequests" ("TenantId", "AutomationId", "Status");
        CREATE INDEX "IX_TenantApprovalRequests_TenantId_CredentialId" ON "TenantApprovalRequests" ("TenantId", "CredentialId");
        CREATE INDEX "IX_TenantApprovalRequests_TenantId_ExecutorPrincipalId_CreatedAt" ON "TenantApprovalRequests" ("TenantId", "ExecutorPrincipalId", "CreatedAt");
        CREATE INDEX "IX_TenantApprovalRequests_TenantId_OriginalInitiatorPrincipalId" ON "TenantApprovalRequests" ("TenantId", "OriginalInitiatorPrincipalId");
        CREATE UNIQUE INDEX "IX_TenantApprovalRequests_TenantId_RootActivationId_ExecutorPrincipalId_Fingerprint" ON "TenantApprovalRequests" ("TenantId", "RootActivationId", "ExecutorPrincipalId", "Fingerprint");
        CREATE INDEX "IX_TenantApprovalRequests_TenantId_SandboxProfileId" ON "TenantApprovalRequests" ("TenantId", "SandboxProfileId");
        CREATE INDEX "IX_TenantApprovalRequests_TenantId_SessionId_InstanceId_ExecutorPrincipalId" ON "TenantApprovalRequests" ("TenantId", "SessionId", "InstanceId", "ExecutorPrincipalId");
        CREATE INDEX "IX_TenantApprovalRequests_TenantId_Status_CreatedAt_RequestId" ON "TenantApprovalRequests" ("TenantId", "Status", "CreatedAt", "RequestId");
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
        AND o.ModelCatalogKey IS json_extract(NEW.PayloadJson, '$.Operation.AutomationAdmission.Model.CatalogKey')
        AND o.ModelProviderAlias IS json_extract(NEW.PayloadJson, '$.Operation.AutomationAdmission.Model.ProviderAlias')
        AND o.ModelId IS json_extract(NEW.PayloadJson, '$.Operation.AutomationAdmission.Model.ModelId')
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
        CREATE TRIGGER TR_TenantApprovals_AutomationAdmission_Immutable BEFORE UPDATE ON TenantApprovalRequests
        WHEN OLD.AutomationId IS NOT NULL OR NEW.AutomationId IS NOT NULL
        BEGIN SELECT RAISE(ABORT, 'Automation consent evidence is immutable') WHERE
        OLD.AutomationId IS NOT NEW.AutomationId OR OLD.RequestId IS NOT NEW.RequestId OR OLD.TenantId IS NOT NEW.TenantId
        OR OLD.RunId IS NOT NEW.RunId OR OLD.SessionId IS NOT NEW.SessionId OR OLD.InstanceId IS NOT NEW.InstanceId
        OR OLD.ExecutorPrincipalId IS NOT NEW.ExecutorPrincipalId OR OLD.OriginalInitiatorPrincipalId IS NOT NEW.OriginalInitiatorPrincipalId
        OR OLD.RootActivationId IS NOT NEW.RootActivationId OR OLD.Permission IS NOT NEW.Permission OR OLD.ResourceId IS NOT NEW.ResourceId
        OR OLD.ResourceType IS NOT NEW.ResourceType OR OLD.Fingerprint IS NOT NEW.Fingerprint OR OLD.OperationId IS NOT NEW.OperationId
        OR OLD.CreatedAt IS NOT NEW.CreatedAt OR OLD.ExpiresAt IS NOT NEW.ExpiresAt OR OLD.Kind IS NOT NEW.Kind
        OR json_extract(OLD.PayloadJson, '$.Operation') IS NOT json_extract(NEW.PayloadJson, '$.Operation'); END;
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
        AND NEW.CredentialId IS NULL AND NEW.SandboxProfileId IS NULL AND NEW.SandboxProfileRevision IS NULL
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
        CREATE TRIGGER TR_TenantApprovals_IndependentTask_Insert BEFORE INSERT ON TenantApprovalRequests
        WHEN NEW.OriginalInitiatorPrincipalId IS NULL AND json_extract(NEW.PayloadJson, '$.Operation.AutomationAdmission') IS NULL
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
        WHEN NEW.OriginalInitiatorPrincipalId IS NULL AND json_extract(NEW.PayloadJson, '$.Operation.AutomationAdmission') IS NULL
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
        CREATE TRIGGER TR_TenantApprovals_Memory_Insert BEFORE INSERT ON TenantApprovalRequests
        WHEN json_extract(NEW.PayloadJson, '$.Operation.MemoryOperation') IS NOT NULL AND (
            NOT EXISTS (SELECT 1 FROM AgentRuns r JOIN AgentRuns root ON root.ActivationId = r.RootActivationId
                WHERE r.AgentRunId = NEW.RunId AND r.TenantId = NEW.TenantId AND r.SessionId = NEW.SessionId
                    AND r.AgentInstanceId = NEW.InstanceId AND r.ExecutorPrincipalId = NEW.ExecutorPrincipalId
                    AND root.TenantId = NEW.TenantId AND root.RootActivationId = root.ActivationId
                    AND root.AgentInstanceId = r.AgentInstanceId AND root.ExecutorPrincipalId = r.ExecutorPrincipalId
                    AND root.SessionId = json_extract(NEW.PayloadJson, '$.Operation.MemoryOperation.RootSessionId') AND r.RootActivationId = NEW.RootActivationId
                    AND ((json_extract(root.PayloadJson, '$.admission.executionContext.origin') = 0
                        AND lower(root.OriginalInitiatorPrincipalId) = json_extract(NEW.PayloadJson, '$.Operation.MemoryOperation.UserPrincipalId')
                        AND EXISTS (SELECT 1 FROM TenantUsers u WHERE u.TenantId = NEW.TenantId AND u.PrincipalId = root.OriginalInitiatorPrincipalId))
                        OR (json_extract(root.PayloadJson, '$.admission.executionContext.origin') <> 0
                            AND json_extract(NEW.PayloadJson, '$.Operation.MemoryOperation.UserPrincipalId') IS NULL AND json_extract(NEW.PayloadJson, '$.Operation.MemoryOperation.ToolName') IN ('continuity.search','continuity.get'))))
            OR EXISTS (SELECT 1 FROM json_each(json_extract(NEW.PayloadJson, '$.Operation.MemoryOperation.SourcesJson')) source WHERE NOT EXISTS (
                SELECT 1 FROM TenantLearnedMemories m WHERE m.TenantId = NEW.TenantId AND lower(m.MemoryId) = json_extract(source.value, '$.MemoryId')
                    AND m.Revision = json_extract(source.value, '$.Revision') AND m.ContentHash = json_extract(source.value, '$.ContentHash')
                    AND ((json_extract(NEW.PayloadJson, '$.Operation.MemoryOperation.UserPrincipalId') IS NOT NULL AND lower(m.UserPrincipalId) = json_extract(NEW.PayloadJson, '$.Operation.MemoryOperation.UserPrincipalId'))
                        OR (json_extract(NEW.PayloadJson, '$.Operation.MemoryOperation.UserPrincipalId') IS NULL AND m.UserPrincipalId IS NULL AND m.Scope = 0 AND m.ExecutorPrincipalId = NEW.ExecutorPrincipalId))
                    AND ((m.Scope = 0 AND m.SessionId = NEW.SessionId) OR (m.Scope = 1 AND m.AgentInstanceId = NEW.InstanceId) OR m.Scope = 2)
                    AND (json_extract(NEW.PayloadJson, '$.Operation.MemoryOperation.ToolName') NOT IN ('memory.consolidate','memory.forget') OR
                        (json_extract(NEW.PayloadJson, '$.Operation.MemoryOperation.ToolName') = 'memory.consolidate' AND m.Status = 0) OR (json_extract(NEW.PayloadJson, '$.Operation.MemoryOperation.ToolName') = 'memory.forget' AND m.Status <> 2))
                    AND EXISTS (SELECT 1 FROM json_each(json_extract(NEW.PayloadJson, '$.Operation.MemoryOperation.SourceLabelsJson')) label
                        WHERE json_extract(label.value, '$.memoryId') = lower(m.MemoryId)
                            AND json_extract(label.value, '$.subject') = m.Subject
                            AND json_extract(label.value, '$.scope') = CASE m.Scope WHEN 0 THEN 'session' WHEN 1 THEN 'identityUser' ELSE 'user' END
                            AND json_extract(label.value, '$.kind') = CASE m.Kind WHEN 0 THEN 'Fact' WHEN 1 THEN 'Preference' WHEN 2 THEN 'Goal' WHEN 3 THEN 'Decision' ELSE 'OpenLoop' END)))
            OR (SELECT count(DISTINCT json_extract(value, '$.MemoryId')) FROM json_each(json_extract(NEW.PayloadJson, '$.Operation.MemoryOperation.SourcesJson'))) <> json_array_length(json_extract(NEW.PayloadJson, '$.Operation.MemoryOperation.SourcesJson'))
            OR (json_extract(NEW.PayloadJson, '$.Operation.MemoryOperation.ToolName') = 'memory.consolidate' AND (SELECT count(DISTINCT m.OwnerKey || ':' || m.Kind) FROM json_each(json_extract(NEW.PayloadJson, '$.Operation.MemoryOperation.SourcesJson')) source
                JOIN TenantLearnedMemories m ON m.TenantId = NEW.TenantId AND lower(m.MemoryId) = json_extract(source.value, '$.MemoryId')) <> 1)
        )
        BEGIN SELECT RAISE(ABORT, 'Memory approval requires exact actual-root owned source witnesses'); END;
        CREATE TRIGGER TR_TenantApprovals_Memory_Update BEFORE UPDATE ON TenantApprovalRequests
        WHEN json_extract(NEW.PayloadJson, '$.Operation.MemoryOperation') IS NOT json_extract(OLD.PayloadJson, '$.Operation.MemoryOperation')
        BEGIN SELECT RAISE(ABORT, 'Retain exact prepared Memory operation'); END;
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
        CREATE TRIGGER TR_TenantOccurrences_Source_Update BEFORE UPDATE ON TriggerOccurrences
        WHEN OLD.TenantId IS NOT NULL OR NEW.TenantId IS NOT NULL
        BEGIN
        SELECT RAISE(ABORT, 'tenant occurrence source is immutable') WHERE 1=1 AND (OLD.TenantId IS NOT NEW.TenantId OR OLD.ExecutorPrincipalId IS NOT NEW.ExecutorPrincipalId OR OLD.AgentInstanceId IS NOT NEW.AgentInstanceId
        OR OLD.ProfileId IS NOT NEW.ProfileId OR OLD.OccurrenceId IS NOT NEW.OccurrenceId OR OLD.AutomationId IS NOT NEW.AutomationId
        OR OLD.DedupeKey IS NOT NEW.DedupeKey OR OLD.EvidenceJson IS NOT NEW.EvidenceJson OR OLD.SourceKind IS NOT NEW.SourceKind
        OR OLD.TriggerRevision IS NOT NEW.TriggerRevision OR OLD.ScheduledAtUtc IS NOT NEW.ScheduledAtUtc OR OLD.ObservedAtUtc IS NOT NEW.ObservedAtUtc
        OR OLD.AdmittedAtUtc IS NOT NEW.AdmittedAtUtc
        OR OLD.SourceEventId IS NOT NEW.SourceEventId OR OLD.ModelCatalogKey IS NOT NEW.ModelCatalogKey OR OLD.ModelProviderAlias IS NOT NEW.ModelProviderAlias
        OR OLD.ModelId IS NOT NEW.ModelId OR OLD.ModelReasoningEffort IS NOT NEW.ModelReasoningEffort
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
        CREATE TEMP TABLE ContinuityForeignKeyGuard (Rows INTEGER CHECK(Rows=0));
        INSERT INTO ContinuityForeignKeyGuard SELECT count(*) FROM pragma_foreign_key_check;
        DROP TABLE ContinuityForeignKeyGuard;
        """;
}
