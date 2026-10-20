using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgentCore.Infrastructure.Persistence.Migrations;

public partial class TenantBackgroundCreationReview : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => Rebuild(migrationBuilder, Upgrade);
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            CREATE TEMP TABLE BackgroundCreationDowngradeGuard (Rows INTEGER CHECK(Rows=0));
            INSERT INTO BackgroundCreationDowngradeGuard SELECT count(*) FROM TenantApprovalRequests WHERE json_type(PayloadJson, '$.Operation.BackgroundStart') IS 'object';
            DROP TABLE BackgroundCreationDowngradeGuard;
            """);
        Rebuild(migrationBuilder, Downgrade);
    }
    // Explicit ordering preserves current source/publication triggers after SQLite's
    // table replacement. No receipts, grants, requests or source rows are discarded.
    private static void Rebuild(MigrationBuilder migrationBuilder, string sql)
    {
        migrationBuilder.Sql("PRAGMA foreign_keys = 0;", suppressTransaction: true);
        migrationBuilder.Sql(sql);
        migrationBuilder.Sql("PRAGMA foreign_keys = 1;", suppressTransaction: true);
    }
    private const string Upgrade = """
            DROP TRIGGER IF EXISTS TR_TenantApprovals_BackgroundCreation_Update;
            DROP TRIGGER IF EXISTS TR_TenantApprovals_BrowserArtifacts_Insert;
            DROP TRIGGER IF EXISTS TR_TenantApprovals_BrowserArtifacts_Update;
            DROP TRIGGER IF EXISTS TR_TenantReceipts_BrowserArtifacts_Insert;
            DROP TRIGGER IF EXISTS TR_TenantReceipts_BrowserArtifacts_Update;
            DROP TRIGGER IF EXISTS TR_TenantApprovals_BrowserFiles_Insert;
            DROP TRIGGER IF EXISTS TR_TenantApprovals_BrowserFiles_Update;
            CREATE TABLE "ef_temp_TenantApprovalRequests" (
                "RequestId" TEXT NOT NULL CONSTRAINT "PK_TenantApprovalRequests" PRIMARY KEY,
                "CreatedAt" INTEGER NOT NULL,
                "CredentialId" TEXT NULL,
                "ExecutorPrincipalId" TEXT NOT NULL,
                "ExpiresAt" INTEGER NOT NULL,
                "Fingerprint" TEXT NOT NULL,
                "InstanceId" TEXT NOT NULL,
                "Kind" INTEGER NOT NULL,
                "OperationId" TEXT NOT NULL,
                "OriginalInitiatorPrincipalId" TEXT NULL,
                "PayloadJson" TEXT NOT NULL,
                "Permission" TEXT NOT NULL,
                "ResourceId" TEXT NOT NULL,
                "ResourceType" TEXT NOT NULL,
                "Revision" INTEGER NOT NULL,
                "RootActivationId" TEXT NOT NULL,
                "RunId" TEXT NOT NULL,
                "SandboxProfileId" TEXT NULL,
                "SandboxProfileRevision" INTEGER NULL,
                "SessionId" TEXT NOT NULL,
                "Status" INTEGER NOT NULL,
                "TenantId" TEXT NOT NULL,
                CONSTRAINT "AK_TenantApprovalRequests_RequestId_RootActivationId_InstanceId" UNIQUE ("RequestId", "RootActivationId", "InstanceId"),
                CONSTRAINT "AK_TenantApprovalRequests_TenantId_RequestId" UNIQUE ("TenantId", "RequestId"),
                CONSTRAINT "AK_TenantApprovalRequests_TenantId_RequestId_ExecutorPrincipalId_OperationId_Fingerprint" UNIQUE ("TenantId", "RequestId", "ExecutorPrincipalId", "OperationId", "Fingerprint"),
                CONSTRAINT "CK_TenantApprovalRequest_Values" CHECK (((((((((((((((((((((((Kind IN (0,1) AND Status IN (0,1,2,3,4) AND Revision >= 1 AND ExpiresAt > CreatedAt AND length(Fingerprint) = 64 AND ((Permission = 'agents.update' AND ResourceType = 'agent' AND ResourceId = InstanceId AND CredentialId IS NULL AND SandboxProfileId IS NULL AND SandboxProfileRevision IS NULL AND length(PayloadJson) <= 131072) OR (Permission = 'credentials.use' AND ResourceType = 'credential' AND CredentialId IS NOT NULL AND ResourceId = CredentialId AND SandboxProfileId IS NULL AND SandboxProfileRevision IS NULL AND length(PayloadJson) <= 131072) OR (Permission = 'sandbox.execute' AND Kind = 0 AND ResourceType = 'agent' AND ResourceId = InstanceId AND CredentialId IS NULL AND SandboxProfileId IS NOT NULL AND SandboxProfileRevision IS NOT NULL AND SandboxProfileRevision > 0 AND json_type(PayloadJson, '$.Operation.SandboxCommand.ProfileId') IS 'text' AND json_extract(PayloadJson, '$.Operation.SandboxCommand.ProfileId') IS SandboxProfileId AND json_extract(PayloadJson, '$.Operation.SandboxCommand.ProfileRevision') IS SandboxProfileRevision AND length(PayloadJson) <= 1048576) OR (Permission = 'sandbox.manage' AND Kind = 0 AND ResourceType = 'agent' AND ResourceId = InstanceId AND CredentialId IS NULL AND SandboxProfileId IS NOT NULL AND SandboxProfileRevision IS NOT NULL AND SandboxProfileRevision > 0 AND json_type(PayloadJson, '$.Operation.SandboxControl.ProfileId') IS 'text' AND json_extract(PayloadJson, '$.Operation.SandboxControl.ProfileId') IS SandboxProfileId AND json_extract(PayloadJson, '$.Operation.SandboxControl.ProfileRevision') IS SandboxProfileRevision AND length(PayloadJson) <= 131072) OR (Permission = 'sandbox.read' AND Kind = 0 AND ResourceType = 'agent' AND ResourceId = InstanceId AND CredentialId IS NULL AND SandboxProfileId IS NULL AND SandboxProfileRevision IS NULL AND json_type(PayloadJson, '$.Operation.SandboxRead') IS 'object' AND json_extract(PayloadJson, '$.Operation.SandboxRead.SessionId') IS SessionId AND json_extract(PayloadJson, '$.Operation.SandboxRead.Kind') IN (0,1,2) AND json_type(PayloadJson, '$.Operation.SandboxRead.ProfileReferencesJson') IS 'text' AND length(json_extract(PayloadJson, '$.Operation.SandboxRead.ProfileReferencesJson')) <= 4096 AND json_valid(json_extract(PayloadJson, '$.Operation.SandboxRead.ProfileReferencesJson')) AND json_type(json_extract(PayloadJson, '$.Operation.SandboxRead.ProfileReferencesJson')) IS 'array' AND json_array_length(json_extract(PayloadJson, '$.Operation.SandboxRead.ProfileReferencesJson')) <= 18 AND length(PayloadJson) <= 131072) OR (Permission IN ('browser.read','browser.use') AND Kind = 0 AND ResourceType = 'agent' AND ResourceId = InstanceId AND CredentialId IS NULL AND SandboxProfileId IS NULL AND SandboxProfileRevision IS NULL AND json_type(PayloadJson, '$.Operation.BrowserCommand') IS 'object' AND json_extract(PayloadJson, '$.Operation.Persona') IS NULL AND json_extract(PayloadJson, '$.Operation.Preferences') IS NULL AND json_extract(PayloadJson, '$.Operation.CredentialCheck') IS NULL AND json_extract(PayloadJson, '$.Operation.SandboxCommand') IS NULL AND json_extract(PayloadJson, '$.Operation.SandboxControl') IS NULL AND json_extract(PayloadJson, '$.Operation.SandboxRead') IS NULL AND json_extract(PayloadJson, '$.Operation.BrowserCommand.SessionId') IS SessionId AND json_type(PayloadJson, '$.Operation.BrowserCommand.ToolName') IS 'text' AND json_type(PayloadJson, '$.Operation.BrowserCommand.ReadOnly') IS CASE WHEN Permission = 'browser.read' THEN 'true' ELSE 'false' END AND json_type(PayloadJson, '$.Operation.BrowserCommand.ArgumentsJson') IS 'text' AND json_valid(json_extract(PayloadJson, '$.Operation.BrowserCommand.ArgumentsJson')) AND length(json_extract(PayloadJson, '$.Operation.BrowserCommand.ArgumentsJson')) <= 16384 AND json_type(PayloadJson, '$.Operation.BrowserCommand.TargetJson') IS 'text' AND json_valid(json_extract(PayloadJson, '$.Operation.BrowserCommand.TargetJson')) AND length(json_extract(PayloadJson, '$.Operation.BrowserCommand.TargetJson')) <= 4096 AND json_type(PayloadJson, '$.Operation.BrowserCommand.CommandHash') IS 'text' AND length(json_extract(PayloadJson, '$.Operation.BrowserCommand.CommandHash')) = 64 AND length(PayloadJson) <= 262144) OR (Kind = 0 AND SandboxProfileId IS NULL AND SandboxProfileRevision IS NULL AND json_type(PayloadJson, '$.Operation.CredentialRead') IS 'object' AND json_extract(PayloadJson, '$.Operation.CredentialRead.SessionId') IS SessionId AND json_type(PayloadJson, '$.Operation.CredentialRead.ArgumentsJson') IS 'text' AND length(json_extract(PayloadJson, '$.Operation.CredentialRead.ArgumentsJson')) <= 4096 AND json_valid(json_extract(PayloadJson, '$.Operation.CredentialRead.ArgumentsJson')) AND json_type(PayloadJson, '$.Operation.CredentialRead.ReferencesJson') IS 'text' AND length(json_extract(PayloadJson, '$.Operation.CredentialRead.ReferencesJson')) <= 65536 AND json_valid(json_extract(PayloadJson, '$.Operation.CredentialRead.ReferencesJson')) AND json_type(json_extract(PayloadJson, '$.Operation.CredentialRead.ReferencesJson')) IS 'array' AND json_array_length(json_extract(PayloadJson, '$.Operation.CredentialRead.ReferencesJson')) <= 101 AND ((Permission = 'credentials.read' AND ResourceType = 'credential' AND CredentialId IS NOT NULL AND ResourceId = CredentialId AND json_extract(PayloadJson, '$.Operation.CredentialRead.CredentialId') IS CredentialId AND json_extract(json_extract(PayloadJson, '$.Operation.CredentialRead.ReferencesJson'), '$[0].CredentialId') IS CredentialId) OR (Permission = 'sessions.read' AND ResourceType = 'session' AND ResourceId = SessionId AND CredentialId IS NULL AND json_extract(PayloadJson, '$.Operation.CredentialRead.CredentialId') IS NULL AND json_array_length(json_extract(PayloadJson, '$.Operation.CredentialRead.ReferencesJson')) = 0)) AND json_extract(PayloadJson, '$.Operation.Persona') IS NULL AND json_extract(PayloadJson, '$.Operation.Preferences') IS NULL AND json_extract(PayloadJson, '$.Operation.CredentialCheck') IS NULL AND json_extract(PayloadJson, '$.Operation.SandboxCommand') IS NULL AND json_extract(PayloadJson, '$.Operation.SandboxControl') IS NULL AND json_extract(PayloadJson, '$.Operation.SandboxRead') IS NULL AND json_extract(PayloadJson, '$.Operation.BrowserCommand') IS NULL AND length(PayloadJson) <= 262144))) AND json_extract(PayloadJson, '$.Operation.ArtifactPublication') IS NULL) OR (Kind IN (0,1) AND Status IN (0,1,2,3,4) AND Revision >= 1 AND ExpiresAt > CreatedAt AND length(Fingerprint) = 64 AND Permission = 'artifacts.create' AND ResourceType = 'session' AND ResourceId = SessionId AND CredentialId IS NULL AND SandboxProfileId IS NULL AND SandboxProfileRevision IS NULL AND json_type(PayloadJson, '$.Operation.ArtifactPublication') IS 'object' AND json_extract(PayloadJson, '$.Operation.ArtifactPublication.SessionId') IS SessionId AND json_type(PayloadJson, '$.Operation.ArtifactPublication.ArgumentsJson') IS 'text' AND length(CAST(json_extract(PayloadJson, '$.Operation.ArtifactPublication.ArgumentsJson') AS BLOB)) <= 65536 AND json_valid(json_extract(PayloadJson, '$.Operation.ArtifactPublication.ArgumentsJson')) AND json_type(json_extract(PayloadJson, '$.Operation.ArtifactPublication.ArgumentsJson')) IS 'object' AND json_type(json_extract(PayloadJson, '$.Operation.ArtifactPublication.ArgumentsJson'), '$.displayName') IS 'text' AND json_type(json_extract(PayloadJson, '$.Operation.ArtifactPublication.ArgumentsJson'), '$.contentType') IS 'text' AND json_type(json_extract(PayloadJson, '$.Operation.ArtifactPublication.ArgumentsJson'), '$.content') IS 'text' AND length(CAST(json_extract(json_extract(PayloadJson, '$.Operation.ArtifactPublication.ArgumentsJson'), '$.content') AS BLOB)) <= 32768 AND json_extract(PayloadJson, '$.Operation.Persona') IS NULL AND json_extract(PayloadJson, '$.Operation.Preferences') IS NULL AND json_extract(PayloadJson, '$.Operation.CredentialCheck') IS NULL AND json_extract(PayloadJson, '$.Operation.SandboxCommand') IS NULL AND json_extract(PayloadJson, '$.Operation.SandboxControl') IS NULL AND json_extract(PayloadJson, '$.Operation.SandboxRead') IS NULL AND json_extract(PayloadJson, '$.Operation.BrowserCommand') IS NULL AND json_extract(PayloadJson, '$.Operation.CredentialRead') IS NULL AND length(CAST(PayloadJson AS BLOB)) <= 524288)) AND json_extract(PayloadJson, '$.Operation.ArtifactVerification') IS NULL) OR (Kind = 0 AND Status IN (0,1,2,3,4) AND Revision >= 1 AND ExpiresAt > CreatedAt AND length(Fingerprint) = 64 AND Permission = 'artifacts.read' AND ResourceType = 'artifact' AND CredentialId IS NULL AND SandboxProfileId IS NULL AND SandboxProfileRevision IS NULL AND json_type(PayloadJson, '$.Operation.ArtifactVerification') IS 'object' AND json_extract(PayloadJson, '$.Operation.ArtifactVerification.SessionId') IS SessionId AND json_extract(PayloadJson, '$.Operation.ArtifactVerification.ArtifactId') IS ResourceId AND json_type(PayloadJson, '$.Operation.ArtifactVerification.DisplayName') IS 'text' AND length(json_extract(PayloadJson, '$.Operation.ArtifactVerification.DisplayName')) BETWEEN 1 AND 200 AND json_type(PayloadJson, '$.Operation.ArtifactVerification.ContentType') IS 'text' AND length(json_extract(PayloadJson, '$.Operation.ArtifactVerification.ContentType')) BETWEEN 1 AND 200 AND json_type(PayloadJson, '$.Operation.ArtifactVerification.ByteSize') IS 'integer' AND json_extract(PayloadJson, '$.Operation.ArtifactVerification.ByteSize') >= 0 AND json_type(PayloadJson, '$.Operation.ArtifactVerification.Sha256Hex') IS 'text' AND length(json_extract(PayloadJson, '$.Operation.ArtifactVerification.Sha256Hex')) = 64 AND json_extract(PayloadJson, '$.Operation.ArtifactVerification.Sha256Hex') NOT GLOB '*[^0-9a-f]*' AND json_extract(PayloadJson, '$.Operation.Persona') IS NULL AND json_extract(PayloadJson, '$.Operation.Preferences') IS NULL AND json_extract(PayloadJson, '$.Operation.CredentialCheck') IS NULL AND json_extract(PayloadJson, '$.Operation.SandboxCommand') IS NULL AND json_extract(PayloadJson, '$.Operation.SandboxControl') IS NULL AND json_extract(PayloadJson, '$.Operation.SandboxRead') IS NULL AND json_extract(PayloadJson, '$.Operation.BrowserCommand') IS NULL AND json_extract(PayloadJson, '$.Operation.CredentialRead') IS NULL AND json_extract(PayloadJson, '$.Operation.ArtifactPublication') IS NULL AND length(CAST(PayloadJson AS BLOB)) <= 131072)) AND json_extract(PayloadJson, '$.Operation.WorkspaceRead') IS NULL) OR (Kind = 0 AND Status IN (0,1,2,3,4) AND Revision >= 1 AND ExpiresAt > CreatedAt AND length(Fingerprint) = 64 AND ((Permission = 'workspaces.read' AND ResourceType = 'workspace' AND json_extract(PayloadJson, '$.Operation.WorkspaceRead.ResourceKey') IS NULL AND json_extract(PayloadJson, '$.Operation.WorkspaceRead.ResourceRevision') IS NULL) OR (Permission = 'resources.read' AND ResourceType = 'resource'
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
            AND OriginalInitiatorPrincipalId IS NOT NULL
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
            AND length(CAST(PayloadJson AS BLOB)) <= 131072 AND json_type(PayloadJson, '$.Operation.BackgroundStart.ChildSessionId') IS 'text' AND length(json_extract(PayloadJson, '$.Operation.BackgroundStart.ChildSessionId')) = 36 AND json_extract(PayloadJson, '$.Operation.BackgroundStart.ChildSessionId') NOT GLOB '*[^0-9a-f-]*' AND substr(json_extract(PayloadJson, '$.Operation.BackgroundStart.ChildSessionId'),9,1) IS '-' AND substr(json_extract(PayloadJson, '$.Operation.BackgroundStart.ChildSessionId'),14,1) IS '-' AND substr(json_extract(PayloadJson, '$.Operation.BackgroundStart.ChildSessionId'),19,1) IS '-' AND substr(json_extract(PayloadJson, '$.Operation.BackgroundStart.ChildSessionId'),24,1) IS '-' AND json_extract(PayloadJson, '$.Operation.BackgroundStart.ChildSessionId') IS NOT SessionId AND json_extract(PayloadJson, '$.Operation.BackgroundStart.ChildSessionId') IS NOT lower(TenantId) AND json_type(PayloadJson, '$.Operation.BackgroundStart.EntryId') IS 'text' AND length(json_extract(PayloadJson, '$.Operation.BackgroundStart.EntryId')) = 36 AND json_extract(PayloadJson, '$.Operation.BackgroundStart.EntryId') NOT GLOB '*[^0-9a-f-]*' AND substr(json_extract(PayloadJson, '$.Operation.BackgroundStart.EntryId'),9,1) IS '-' AND substr(json_extract(PayloadJson, '$.Operation.BackgroundStart.EntryId'),14,1) IS '-' AND substr(json_extract(PayloadJson, '$.Operation.BackgroundStart.EntryId'),19,1) IS '-' AND substr(json_extract(PayloadJson, '$.Operation.BackgroundStart.EntryId'),24,1) IS '-' AND json_extract(PayloadJson, '$.Operation.BackgroundStart.EntryId') IS NOT SessionId AND json_extract(PayloadJson, '$.Operation.BackgroundStart.EntryId') IS NOT lower(TenantId) AND json_type(PayloadJson, '$.Operation.BackgroundStart.ActivationId') IS 'text' AND length(json_extract(PayloadJson, '$.Operation.BackgroundStart.ActivationId')) = 36 AND json_extract(PayloadJson, '$.Operation.BackgroundStart.ActivationId') NOT GLOB '*[^0-9a-f-]*' AND substr(json_extract(PayloadJson, '$.Operation.BackgroundStart.ActivationId'),9,1) IS '-' AND substr(json_extract(PayloadJson, '$.Operation.BackgroundStart.ActivationId'),14,1) IS '-' AND substr(json_extract(PayloadJson, '$.Operation.BackgroundStart.ActivationId'),19,1) IS '-' AND substr(json_extract(PayloadJson, '$.Operation.BackgroundStart.ActivationId'),24,1) IS '-' AND json_extract(PayloadJson, '$.Operation.BackgroundStart.ActivationId') IS NOT SessionId AND json_extract(PayloadJson, '$.Operation.BackgroundStart.ActivationId') IS NOT lower(TenantId) AND json_type(PayloadJson, '$.Operation.BackgroundStart.RunId') IS 'text' AND length(json_extract(PayloadJson, '$.Operation.BackgroundStart.RunId')) = 36 AND json_extract(PayloadJson, '$.Operation.BackgroundStart.RunId') NOT GLOB '*[^0-9a-f-]*' AND substr(json_extract(PayloadJson, '$.Operation.BackgroundStart.RunId'),9,1) IS '-' AND substr(json_extract(PayloadJson, '$.Operation.BackgroundStart.RunId'),14,1) IS '-' AND substr(json_extract(PayloadJson, '$.Operation.BackgroundStart.RunId'),19,1) IS '-' AND substr(json_extract(PayloadJson, '$.Operation.BackgroundStart.RunId'),24,1) IS '-' AND json_extract(PayloadJson, '$.Operation.BackgroundStart.RunId') IS NOT SessionId AND json_extract(PayloadJson, '$.Operation.BackgroundStart.RunId') IS NOT lower(TenantId) AND json_type(PayloadJson, '$.Operation.BackgroundStart.ResponseId') IS 'text' AND length(json_extract(PayloadJson, '$.Operation.BackgroundStart.ResponseId')) = 36 AND json_extract(PayloadJson, '$.Operation.BackgroundStart.ResponseId') NOT GLOB '*[^0-9a-f-]*' AND substr(json_extract(PayloadJson, '$.Operation.BackgroundStart.ResponseId'),9,1) IS '-' AND substr(json_extract(PayloadJson, '$.Operation.BackgroundStart.ResponseId'),14,1) IS '-' AND substr(json_extract(PayloadJson, '$.Operation.BackgroundStart.ResponseId'),19,1) IS '-' AND substr(json_extract(PayloadJson, '$.Operation.BackgroundStart.ResponseId'),24,1) IS '-' AND json_extract(PayloadJson, '$.Operation.BackgroundStart.ResponseId') IS NOT SessionId AND json_extract(PayloadJson, '$.Operation.BackgroundStart.ResponseId') IS NOT lower(TenantId) AND json_extract(PayloadJson, '$.Operation.BackgroundStart.ChildSessionId') IS NOT json_extract(PayloadJson, '$.Operation.BackgroundStart.EntryId') AND json_extract(PayloadJson, '$.Operation.BackgroundStart.ChildSessionId') IS NOT json_extract(PayloadJson, '$.Operation.BackgroundStart.ActivationId') AND json_extract(PayloadJson, '$.Operation.BackgroundStart.ChildSessionId') IS NOT json_extract(PayloadJson, '$.Operation.BackgroundStart.RunId') AND json_extract(PayloadJson, '$.Operation.BackgroundStart.ChildSessionId') IS NOT json_extract(PayloadJson, '$.Operation.BackgroundStart.ResponseId') AND json_extract(PayloadJson, '$.Operation.BackgroundStart.EntryId') IS NOT json_extract(PayloadJson, '$.Operation.BackgroundStart.ActivationId') AND json_extract(PayloadJson, '$.Operation.BackgroundStart.EntryId') IS NOT json_extract(PayloadJson, '$.Operation.BackgroundStart.RunId') AND json_extract(PayloadJson, '$.Operation.BackgroundStart.EntryId') IS NOT json_extract(PayloadJson, '$.Operation.BackgroundStart.ResponseId') AND json_extract(PayloadJson, '$.Operation.BackgroundStart.ActivationId') IS NOT json_extract(PayloadJson, '$.Operation.BackgroundStart.RunId') AND json_extract(PayloadJson, '$.Operation.BackgroundStart.ActivationId') IS NOT json_extract(PayloadJson, '$.Operation.BackgroundStart.ResponseId') AND json_extract(PayloadJson, '$.Operation.BackgroundStart.RunId') IS NOT json_extract(PayloadJson, '$.Operation.BackgroundStart.ResponseId') AND json_extract(PayloadJson, '$.Operation.Persona') IS NULL AND json_extract(PayloadJson, '$.Operation.Preferences') IS NULL AND json_extract(PayloadJson, '$.Operation.CredentialCheck') IS NULL AND json_extract(PayloadJson, '$.Operation.SandboxCommand') IS NULL AND json_extract(PayloadJson, '$.Operation.SandboxControl') IS NULL AND json_extract(PayloadJson, '$.Operation.SandboxRead') IS NULL AND json_extract(PayloadJson, '$.Operation.BrowserCommand') IS NULL AND json_extract(PayloadJson, '$.Operation.CredentialRead') IS NULL AND json_extract(PayloadJson, '$.Operation.ArtifactPublication') IS NULL AND json_extract(PayloadJson, '$.Operation.ArtifactVerification') IS NULL AND json_extract(PayloadJson, '$.Operation.WorkspaceRead') IS NULL AND json_extract(PayloadJson, '$.Operation.WorkspaceMutation') IS NULL AND json_extract(PayloadJson, '$.Operation.AttachmentRead') IS NULL),0)),
                CONSTRAINT "FK_TenantApprovalRequests_AgentRuns_RunId_RootActivationId" FOREIGN KEY ("RunId", "RootActivationId") REFERENCES "AgentRuns" ("AgentRunId", "RootActivationId") ON DELETE RESTRICT,
                CONSTRAINT "FK_TenantApprovalRequests_AgentRuns_RunId_SessionId" FOREIGN KEY ("RunId", "SessionId") REFERENCES "AgentRuns" ("AgentRunId", "SessionId") ON DELETE RESTRICT,
                CONSTRAINT "FK_TenantApprovalRequests_SandboxProfiles_SandboxProfileId_SandboxProfileRevision" FOREIGN KEY ("SandboxProfileId", "SandboxProfileRevision") REFERENCES "SandboxProfiles" ("Id", "Revision") ON DELETE RESTRICT,
                CONSTRAINT "FK_TenantApprovalRequests_TenantCredentials_TenantId_CredentialId" FOREIGN KEY ("TenantId", "CredentialId") REFERENCES "TenantCredentials" ("TenantId", "CredentialId") ON DELETE RESTRICT,
                CONSTRAINT "FK_TenantApprovalRequests_TenantSandboxProfiles_TenantId_SandboxProfileId" FOREIGN KEY ("TenantId", "SandboxProfileId") REFERENCES "TenantSandboxProfiles" ("TenantId", "ProfileId") ON DELETE RESTRICT,
                CONSTRAINT "FK_TenantApprovalRequests_TenantSessions_TenantId_SessionId_InstanceId_ExecutorPrincipalId" FOREIGN KEY ("TenantId", "SessionId", "InstanceId", "ExecutorPrincipalId") REFERENCES "TenantSessions" ("TenantId", "SessionId", "AgentInstanceId", "ExecutorPrincipalId") ON DELETE RESTRICT,
                CONSTRAINT "FK_TenantApprovalRequests_TenantUsers_TenantId_OriginalInitiatorPrincipalId" FOREIGN KEY ("TenantId", "OriginalInitiatorPrincipalId") REFERENCES "TenantUsers" ("TenantId", "PrincipalId") ON DELETE RESTRICT
            );

            INSERT INTO "ef_temp_TenantApprovalRequests" ("RequestId", "CreatedAt", "CredentialId", "ExecutorPrincipalId", "ExpiresAt", "Fingerprint", "InstanceId", "Kind", "OperationId", "OriginalInitiatorPrincipalId", "PayloadJson", "Permission", "ResourceId", "ResourceType", "Revision", "RootActivationId", "RunId", "SandboxProfileId", "SandboxProfileRevision", "SessionId", "Status", "TenantId")
            SELECT "RequestId", "CreatedAt", "CredentialId", "ExecutorPrincipalId", "ExpiresAt", "Fingerprint", "InstanceId", "Kind", "OperationId", "OriginalInitiatorPrincipalId", "PayloadJson", "Permission", "ResourceId", "ResourceType", "Revision", "RootActivationId", "RunId", "SandboxProfileId", "SandboxProfileRevision", "SessionId", "Status", "TenantId"
            FROM "TenantApprovalRequests";



            DROP TABLE "TenantApprovalRequests";

            ALTER TABLE "ef_temp_TenantApprovalRequests" RENAME TO "TenantApprovalRequests";



            CREATE UNIQUE INDEX "IX_TenantApprovalRequests_OperationId" ON "TenantApprovalRequests" ("OperationId");

            CREATE INDEX "IX_TenantApprovalRequests_RunId_RootActivationId" ON "TenantApprovalRequests" ("RunId", "RootActivationId");

            CREATE INDEX "IX_TenantApprovalRequests_RunId_SessionId" ON "TenantApprovalRequests" ("RunId", "SessionId");

            CREATE INDEX "IX_TenantApprovalRequests_SandboxProfileId_SandboxProfileRevision" ON "TenantApprovalRequests" ("SandboxProfileId", "SandboxProfileRevision");

            CREATE INDEX "IX_TenantApprovalRequests_Status_ExpiresAt_RequestId" ON "TenantApprovalRequests" ("Status", "ExpiresAt", "RequestId");

            CREATE INDEX "IX_TenantApprovalRequests_TenantId_CredentialId" ON "TenantApprovalRequests" ("TenantId", "CredentialId");

            CREATE INDEX "IX_TenantApprovalRequests_TenantId_ExecutorPrincipalId_CreatedAt" ON "TenantApprovalRequests" ("TenantId", "ExecutorPrincipalId", "CreatedAt");

            CREATE INDEX "IX_TenantApprovalRequests_TenantId_OriginalInitiatorPrincipalId" ON "TenantApprovalRequests" ("TenantId", "OriginalInitiatorPrincipalId");

            CREATE UNIQUE INDEX "IX_TenantApprovalRequests_TenantId_RootActivationId_ExecutorPrincipalId_Fingerprint" ON "TenantApprovalRequests" ("TenantId", "RootActivationId", "ExecutorPrincipalId", "Fingerprint");

            CREATE INDEX "IX_TenantApprovalRequests_TenantId_SandboxProfileId" ON "TenantApprovalRequests" ("TenantId", "SandboxProfileId");

            CREATE INDEX "IX_TenantApprovalRequests_TenantId_SessionId_InstanceId_ExecutorPrincipalId" ON "TenantApprovalRequests" ("TenantId", "SessionId", "InstanceId", "ExecutorPrincipalId");

            CREATE INDEX "IX_TenantApprovalRequests_TenantId_Status_CreatedAt_RequestId" ON "TenantApprovalRequests" ("TenantId", "Status", "CreatedAt", "RequestId");
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
            """;
    private const string Downgrade = """
            DROP TRIGGER IF EXISTS TR_TenantApprovals_BackgroundCreation_Update;
            DROP TRIGGER IF EXISTS TR_TenantApprovals_BrowserArtifacts_Insert;
            DROP TRIGGER IF EXISTS TR_TenantApprovals_BrowserArtifacts_Update;
            DROP TRIGGER IF EXISTS TR_TenantReceipts_BrowserArtifacts_Insert;
            DROP TRIGGER IF EXISTS TR_TenantReceipts_BrowserArtifacts_Update;
            DROP TRIGGER IF EXISTS TR_TenantApprovals_BrowserFiles_Insert;
            DROP TRIGGER IF EXISTS TR_TenantApprovals_BrowserFiles_Update;
            CREATE TABLE "ef_temp_TenantApprovalRequests" (
                "RequestId" TEXT NOT NULL CONSTRAINT "PK_TenantApprovalRequests" PRIMARY KEY,
                "CreatedAt" INTEGER NOT NULL,
                "CredentialId" TEXT NULL,
                "ExecutorPrincipalId" TEXT NOT NULL,
                "ExpiresAt" INTEGER NOT NULL,
                "Fingerprint" TEXT NOT NULL,
                "InstanceId" TEXT NOT NULL,
                "Kind" INTEGER NOT NULL,
                "OperationId" TEXT NOT NULL,
                "OriginalInitiatorPrincipalId" TEXT NULL,
                "PayloadJson" TEXT NOT NULL,
                "Permission" TEXT NOT NULL,
                "ResourceId" TEXT NOT NULL,
                "ResourceType" TEXT NOT NULL,
                "Revision" INTEGER NOT NULL,
                "RootActivationId" TEXT NOT NULL,
                "RunId" TEXT NOT NULL,
                "SandboxProfileId" TEXT NULL,
                "SandboxProfileRevision" INTEGER NULL,
                "SessionId" TEXT NOT NULL,
                "Status" INTEGER NOT NULL,
                "TenantId" TEXT NOT NULL,
                CONSTRAINT "AK_TenantApprovalRequests_RequestId_RootActivationId_InstanceId" UNIQUE ("RequestId", "RootActivationId", "InstanceId"),
                CONSTRAINT "AK_TenantApprovalRequests_TenantId_RequestId" UNIQUE ("TenantId", "RequestId"),
                CONSTRAINT "AK_TenantApprovalRequests_TenantId_RequestId_ExecutorPrincipalId_OperationId_Fingerprint" UNIQUE ("TenantId", "RequestId", "ExecutorPrincipalId", "OperationId", "Fingerprint"),
                CONSTRAINT "CK_TenantApprovalRequest_Values" CHECK (((((((((((((((((((((Kind IN (0,1) AND Status IN (0,1,2,3,4) AND Revision >= 1 AND ExpiresAt > CreatedAt AND length(Fingerprint) = 64 AND ((Permission = 'agents.update' AND ResourceType = 'agent' AND ResourceId = InstanceId AND CredentialId IS NULL AND SandboxProfileId IS NULL AND SandboxProfileRevision IS NULL AND length(PayloadJson) <= 131072) OR (Permission = 'credentials.use' AND ResourceType = 'credential' AND CredentialId IS NOT NULL AND ResourceId = CredentialId AND SandboxProfileId IS NULL AND SandboxProfileRevision IS NULL AND length(PayloadJson) <= 131072) OR (Permission = 'sandbox.execute' AND Kind = 0 AND ResourceType = 'agent' AND ResourceId = InstanceId AND CredentialId IS NULL AND SandboxProfileId IS NOT NULL AND SandboxProfileRevision IS NOT NULL AND SandboxProfileRevision > 0 AND json_type(PayloadJson, '$.Operation.SandboxCommand.ProfileId') IS 'text' AND json_extract(PayloadJson, '$.Operation.SandboxCommand.ProfileId') IS SandboxProfileId AND json_extract(PayloadJson, '$.Operation.SandboxCommand.ProfileRevision') IS SandboxProfileRevision AND length(PayloadJson) <= 1048576) OR (Permission = 'sandbox.manage' AND Kind = 0 AND ResourceType = 'agent' AND ResourceId = InstanceId AND CredentialId IS NULL AND SandboxProfileId IS NOT NULL AND SandboxProfileRevision IS NOT NULL AND SandboxProfileRevision > 0 AND json_type(PayloadJson, '$.Operation.SandboxControl.ProfileId') IS 'text' AND json_extract(PayloadJson, '$.Operation.SandboxControl.ProfileId') IS SandboxProfileId AND json_extract(PayloadJson, '$.Operation.SandboxControl.ProfileRevision') IS SandboxProfileRevision AND length(PayloadJson) <= 131072) OR (Permission = 'sandbox.read' AND Kind = 0 AND ResourceType = 'agent' AND ResourceId = InstanceId AND CredentialId IS NULL AND SandboxProfileId IS NULL AND SandboxProfileRevision IS NULL AND json_type(PayloadJson, '$.Operation.SandboxRead') IS 'object' AND json_extract(PayloadJson, '$.Operation.SandboxRead.SessionId') IS SessionId AND json_extract(PayloadJson, '$.Operation.SandboxRead.Kind') IN (0,1,2) AND json_type(PayloadJson, '$.Operation.SandboxRead.ProfileReferencesJson') IS 'text' AND length(json_extract(PayloadJson, '$.Operation.SandboxRead.ProfileReferencesJson')) <= 4096 AND json_valid(json_extract(PayloadJson, '$.Operation.SandboxRead.ProfileReferencesJson')) AND json_type(json_extract(PayloadJson, '$.Operation.SandboxRead.ProfileReferencesJson')) IS 'array' AND json_array_length(json_extract(PayloadJson, '$.Operation.SandboxRead.ProfileReferencesJson')) <= 18 AND length(PayloadJson) <= 131072) OR (Permission IN ('browser.read','browser.use') AND Kind = 0 AND ResourceType = 'agent' AND ResourceId = InstanceId AND CredentialId IS NULL AND SandboxProfileId IS NULL AND SandboxProfileRevision IS NULL AND json_type(PayloadJson, '$.Operation.BrowserCommand') IS 'object' AND json_extract(PayloadJson, '$.Operation.Persona') IS NULL AND json_extract(PayloadJson, '$.Operation.Preferences') IS NULL AND json_extract(PayloadJson, '$.Operation.CredentialCheck') IS NULL AND json_extract(PayloadJson, '$.Operation.SandboxCommand') IS NULL AND json_extract(PayloadJson, '$.Operation.SandboxControl') IS NULL AND json_extract(PayloadJson, '$.Operation.SandboxRead') IS NULL AND json_extract(PayloadJson, '$.Operation.BrowserCommand.SessionId') IS SessionId AND json_type(PayloadJson, '$.Operation.BrowserCommand.ToolName') IS 'text' AND json_type(PayloadJson, '$.Operation.BrowserCommand.ReadOnly') IS CASE WHEN Permission = 'browser.read' THEN 'true' ELSE 'false' END AND json_type(PayloadJson, '$.Operation.BrowserCommand.ArgumentsJson') IS 'text' AND json_valid(json_extract(PayloadJson, '$.Operation.BrowserCommand.ArgumentsJson')) AND length(json_extract(PayloadJson, '$.Operation.BrowserCommand.ArgumentsJson')) <= 16384 AND json_type(PayloadJson, '$.Operation.BrowserCommand.TargetJson') IS 'text' AND json_valid(json_extract(PayloadJson, '$.Operation.BrowserCommand.TargetJson')) AND length(json_extract(PayloadJson, '$.Operation.BrowserCommand.TargetJson')) <= 4096 AND json_type(PayloadJson, '$.Operation.BrowserCommand.CommandHash') IS 'text' AND length(json_extract(PayloadJson, '$.Operation.BrowserCommand.CommandHash')) = 64 AND length(PayloadJson) <= 262144) OR (Kind = 0 AND SandboxProfileId IS NULL AND SandboxProfileRevision IS NULL AND json_type(PayloadJson, '$.Operation.CredentialRead') IS 'object' AND json_extract(PayloadJson, '$.Operation.CredentialRead.SessionId') IS SessionId AND json_type(PayloadJson, '$.Operation.CredentialRead.ArgumentsJson') IS 'text' AND length(json_extract(PayloadJson, '$.Operation.CredentialRead.ArgumentsJson')) <= 4096 AND json_valid(json_extract(PayloadJson, '$.Operation.CredentialRead.ArgumentsJson')) AND json_type(PayloadJson, '$.Operation.CredentialRead.ReferencesJson') IS 'text' AND length(json_extract(PayloadJson, '$.Operation.CredentialRead.ReferencesJson')) <= 65536 AND json_valid(json_extract(PayloadJson, '$.Operation.CredentialRead.ReferencesJson')) AND json_type(json_extract(PayloadJson, '$.Operation.CredentialRead.ReferencesJson')) IS 'array' AND json_array_length(json_extract(PayloadJson, '$.Operation.CredentialRead.ReferencesJson')) <= 101 AND ((Permission = 'credentials.read' AND ResourceType = 'credential' AND CredentialId IS NOT NULL AND ResourceId = CredentialId AND json_extract(PayloadJson, '$.Operation.CredentialRead.CredentialId') IS CredentialId AND json_extract(json_extract(PayloadJson, '$.Operation.CredentialRead.ReferencesJson'), '$[0].CredentialId') IS CredentialId) OR (Permission = 'sessions.read' AND ResourceType = 'session' AND ResourceId = SessionId AND CredentialId IS NULL AND json_extract(PayloadJson, '$.Operation.CredentialRead.CredentialId') IS NULL AND json_array_length(json_extract(PayloadJson, '$.Operation.CredentialRead.ReferencesJson')) = 0)) AND json_extract(PayloadJson, '$.Operation.Persona') IS NULL AND json_extract(PayloadJson, '$.Operation.Preferences') IS NULL AND json_extract(PayloadJson, '$.Operation.CredentialCheck') IS NULL AND json_extract(PayloadJson, '$.Operation.SandboxCommand') IS NULL AND json_extract(PayloadJson, '$.Operation.SandboxControl') IS NULL AND json_extract(PayloadJson, '$.Operation.SandboxRead') IS NULL AND json_extract(PayloadJson, '$.Operation.BrowserCommand') IS NULL AND length(PayloadJson) <= 262144))) AND json_extract(PayloadJson, '$.Operation.ArtifactPublication') IS NULL) OR (Kind IN (0,1) AND Status IN (0,1,2,3,4) AND Revision >= 1 AND ExpiresAt > CreatedAt AND length(Fingerprint) = 64 AND Permission = 'artifacts.create' AND ResourceType = 'session' AND ResourceId = SessionId AND CredentialId IS NULL AND SandboxProfileId IS NULL AND SandboxProfileRevision IS NULL AND json_type(PayloadJson, '$.Operation.ArtifactPublication') IS 'object' AND json_extract(PayloadJson, '$.Operation.ArtifactPublication.SessionId') IS SessionId AND json_type(PayloadJson, '$.Operation.ArtifactPublication.ArgumentsJson') IS 'text' AND length(CAST(json_extract(PayloadJson, '$.Operation.ArtifactPublication.ArgumentsJson') AS BLOB)) <= 65536 AND json_valid(json_extract(PayloadJson, '$.Operation.ArtifactPublication.ArgumentsJson')) AND json_type(json_extract(PayloadJson, '$.Operation.ArtifactPublication.ArgumentsJson')) IS 'object' AND json_type(json_extract(PayloadJson, '$.Operation.ArtifactPublication.ArgumentsJson'), '$.displayName') IS 'text' AND json_type(json_extract(PayloadJson, '$.Operation.ArtifactPublication.ArgumentsJson'), '$.contentType') IS 'text' AND json_type(json_extract(PayloadJson, '$.Operation.ArtifactPublication.ArgumentsJson'), '$.content') IS 'text' AND length(CAST(json_extract(json_extract(PayloadJson, '$.Operation.ArtifactPublication.ArgumentsJson'), '$.content') AS BLOB)) <= 32768 AND json_extract(PayloadJson, '$.Operation.Persona') IS NULL AND json_extract(PayloadJson, '$.Operation.Preferences') IS NULL AND json_extract(PayloadJson, '$.Operation.CredentialCheck') IS NULL AND json_extract(PayloadJson, '$.Operation.SandboxCommand') IS NULL AND json_extract(PayloadJson, '$.Operation.SandboxControl') IS NULL AND json_extract(PayloadJson, '$.Operation.SandboxRead') IS NULL AND json_extract(PayloadJson, '$.Operation.BrowserCommand') IS NULL AND json_extract(PayloadJson, '$.Operation.CredentialRead') IS NULL AND length(CAST(PayloadJson AS BLOB)) <= 524288)) AND json_extract(PayloadJson, '$.Operation.ArtifactVerification') IS NULL) OR (Kind = 0 AND Status IN (0,1,2,3,4) AND Revision >= 1 AND ExpiresAt > CreatedAt AND length(Fingerprint) = 64 AND Permission = 'artifacts.read' AND ResourceType = 'artifact' AND CredentialId IS NULL AND SandboxProfileId IS NULL AND SandboxProfileRevision IS NULL AND json_type(PayloadJson, '$.Operation.ArtifactVerification') IS 'object' AND json_extract(PayloadJson, '$.Operation.ArtifactVerification.SessionId') IS SessionId AND json_extract(PayloadJson, '$.Operation.ArtifactVerification.ArtifactId') IS ResourceId AND json_type(PayloadJson, '$.Operation.ArtifactVerification.DisplayName') IS 'text' AND length(json_extract(PayloadJson, '$.Operation.ArtifactVerification.DisplayName')) BETWEEN 1 AND 200 AND json_type(PayloadJson, '$.Operation.ArtifactVerification.ContentType') IS 'text' AND length(json_extract(PayloadJson, '$.Operation.ArtifactVerification.ContentType')) BETWEEN 1 AND 200 AND json_type(PayloadJson, '$.Operation.ArtifactVerification.ByteSize') IS 'integer' AND json_extract(PayloadJson, '$.Operation.ArtifactVerification.ByteSize') >= 0 AND json_type(PayloadJson, '$.Operation.ArtifactVerification.Sha256Hex') IS 'text' AND length(json_extract(PayloadJson, '$.Operation.ArtifactVerification.Sha256Hex')) = 64 AND json_extract(PayloadJson, '$.Operation.ArtifactVerification.Sha256Hex') NOT GLOB '*[^0-9a-f]*' AND json_extract(PayloadJson, '$.Operation.Persona') IS NULL AND json_extract(PayloadJson, '$.Operation.Preferences') IS NULL AND json_extract(PayloadJson, '$.Operation.CredentialCheck') IS NULL AND json_extract(PayloadJson, '$.Operation.SandboxCommand') IS NULL AND json_extract(PayloadJson, '$.Operation.SandboxControl') IS NULL AND json_extract(PayloadJson, '$.Operation.SandboxRead') IS NULL AND json_extract(PayloadJson, '$.Operation.BrowserCommand') IS NULL AND json_extract(PayloadJson, '$.Operation.CredentialRead') IS NULL AND json_extract(PayloadJson, '$.Operation.ArtifactPublication') IS NULL AND length(CAST(PayloadJson AS BLOB)) <= 131072)) AND json_extract(PayloadJson, '$.Operation.WorkspaceRead') IS NULL) OR (Kind = 0 AND Status IN (0,1,2,3,4) AND Revision >= 1 AND ExpiresAt > CreatedAt AND length(Fingerprint) = 64 AND ((Permission = 'workspaces.read' AND ResourceType = 'workspace' AND json_extract(PayloadJson, '$.Operation.WorkspaceRead.ResourceKey') IS NULL AND json_extract(PayloadJson, '$.Operation.WorkspaceRead.ResourceRevision') IS NULL) OR (Permission = 'resources.read' AND ResourceType = 'resource'
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
            AND length(CAST(PayloadJson AS BLOB)) <= 131072),0)),
                CONSTRAINT "FK_TenantApprovalRequests_AgentRuns_RunId_RootActivationId" FOREIGN KEY ("RunId", "RootActivationId") REFERENCES "AgentRuns" ("AgentRunId", "RootActivationId") ON DELETE RESTRICT,
                CONSTRAINT "FK_TenantApprovalRequests_AgentRuns_RunId_SessionId" FOREIGN KEY ("RunId", "SessionId") REFERENCES "AgentRuns" ("AgentRunId", "SessionId") ON DELETE RESTRICT,
                CONSTRAINT "FK_TenantApprovalRequests_SandboxProfiles_SandboxProfileId_SandboxProfileRevision" FOREIGN KEY ("SandboxProfileId", "SandboxProfileRevision") REFERENCES "SandboxProfiles" ("Id", "Revision") ON DELETE RESTRICT,
                CONSTRAINT "FK_TenantApprovalRequests_TenantCredentials_TenantId_CredentialId" FOREIGN KEY ("TenantId", "CredentialId") REFERENCES "TenantCredentials" ("TenantId", "CredentialId") ON DELETE RESTRICT,
                CONSTRAINT "FK_TenantApprovalRequests_TenantSandboxProfiles_TenantId_SandboxProfileId" FOREIGN KEY ("TenantId", "SandboxProfileId") REFERENCES "TenantSandboxProfiles" ("TenantId", "ProfileId") ON DELETE RESTRICT,
                CONSTRAINT "FK_TenantApprovalRequests_TenantSessions_TenantId_SessionId_InstanceId_ExecutorPrincipalId" FOREIGN KEY ("TenantId", "SessionId", "InstanceId", "ExecutorPrincipalId") REFERENCES "TenantSessions" ("TenantId", "SessionId", "AgentInstanceId", "ExecutorPrincipalId") ON DELETE RESTRICT,
                CONSTRAINT "FK_TenantApprovalRequests_TenantUsers_TenantId_OriginalInitiatorPrincipalId" FOREIGN KEY ("TenantId", "OriginalInitiatorPrincipalId") REFERENCES "TenantUsers" ("TenantId", "PrincipalId") ON DELETE RESTRICT
            );

            INSERT INTO "ef_temp_TenantApprovalRequests" ("RequestId", "CreatedAt", "CredentialId", "ExecutorPrincipalId", "ExpiresAt", "Fingerprint", "InstanceId", "Kind", "OperationId", "OriginalInitiatorPrincipalId", "PayloadJson", "Permission", "ResourceId", "ResourceType", "Revision", "RootActivationId", "RunId", "SandboxProfileId", "SandboxProfileRevision", "SessionId", "Status", "TenantId")
            SELECT "RequestId", "CreatedAt", "CredentialId", "ExecutorPrincipalId", "ExpiresAt", "Fingerprint", "InstanceId", "Kind", "OperationId", "OriginalInitiatorPrincipalId", "PayloadJson", "Permission", "ResourceId", "ResourceType", "Revision", "RootActivationId", "RunId", "SandboxProfileId", "SandboxProfileRevision", "SessionId", "Status", "TenantId"
            FROM "TenantApprovalRequests";



            DROP TABLE "TenantApprovalRequests";

            ALTER TABLE "ef_temp_TenantApprovalRequests" RENAME TO "TenantApprovalRequests";



            CREATE UNIQUE INDEX "IX_TenantApprovalRequests_OperationId" ON "TenantApprovalRequests" ("OperationId");

            CREATE INDEX "IX_TenantApprovalRequests_RunId_RootActivationId" ON "TenantApprovalRequests" ("RunId", "RootActivationId");

            CREATE INDEX "IX_TenantApprovalRequests_RunId_SessionId" ON "TenantApprovalRequests" ("RunId", "SessionId");

            CREATE INDEX "IX_TenantApprovalRequests_SandboxProfileId_SandboxProfileRevision" ON "TenantApprovalRequests" ("SandboxProfileId", "SandboxProfileRevision");

            CREATE INDEX "IX_TenantApprovalRequests_Status_ExpiresAt_RequestId" ON "TenantApprovalRequests" ("Status", "ExpiresAt", "RequestId");

            CREATE INDEX "IX_TenantApprovalRequests_TenantId_CredentialId" ON "TenantApprovalRequests" ("TenantId", "CredentialId");

            CREATE INDEX "IX_TenantApprovalRequests_TenantId_ExecutorPrincipalId_CreatedAt" ON "TenantApprovalRequests" ("TenantId", "ExecutorPrincipalId", "CreatedAt");

            CREATE INDEX "IX_TenantApprovalRequests_TenantId_OriginalInitiatorPrincipalId" ON "TenantApprovalRequests" ("TenantId", "OriginalInitiatorPrincipalId");

            CREATE UNIQUE INDEX "IX_TenantApprovalRequests_TenantId_RootActivationId_ExecutorPrincipalId_Fingerprint" ON "TenantApprovalRequests" ("TenantId", "RootActivationId", "ExecutorPrincipalId", "Fingerprint");

            CREATE INDEX "IX_TenantApprovalRequests_TenantId_SandboxProfileId" ON "TenantApprovalRequests" ("TenantId", "SandboxProfileId");

            CREATE INDEX "IX_TenantApprovalRequests_TenantId_SessionId_InstanceId_ExecutorPrincipalId" ON "TenantApprovalRequests" ("TenantId", "SessionId", "InstanceId", "ExecutorPrincipalId");

            CREATE INDEX "IX_TenantApprovalRequests_TenantId_Status_CreatedAt_RequestId" ON "TenantApprovalRequests" ("TenantId", "Status", "CreatedAt", "RequestId");
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
            """;
}
