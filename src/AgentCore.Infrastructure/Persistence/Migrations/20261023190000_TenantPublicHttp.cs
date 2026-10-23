using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable
namespace AgentCore.Infrastructure.Persistence.Migrations;

[DbContext(typeof(AgentCoreDbContext))]
[Migration("20261023190000_TenantPublicHttp")]
public sealed class TenantPublicHttp : TenantPublicDownloads
{
    internal new const string ReceiptConstraint = "HttpJson IS NULL OR COALESCE(Outcome=0 AND ReasonCode='http_request_completed' AND RequestId IS NOT NULL AND length(CAST(HttpJson AS BLOB)) BETWEEN 2 AND 16384 AND json_valid(HttpJson) AND json_type(HttpJson)='object' AND json_type(HttpJson,'$.StatusCode')='integer' AND json_extract(HttpJson,'$.StatusCode') BETWEEN 100 AND 599 AND json_type(HttpJson,'$.FinalUrl')='text' AND length(json_extract(HttpJson,'$.FinalUrl')) BETWEEN 1 AND 4096 AND (json_extract(HttpJson,'$.FinalUrl') LIKE 'https://%' OR json_extract(HttpJson,'$.FinalUrl') LIKE 'http://%') AND (json_type(HttpJson,'$.ContentType')='null' OR json_type(HttpJson,'$.ContentType')='text' AND length(json_extract(HttpJson,'$.ContentType'))<=200) AND json_type(HttpJson,'$.ProjectedBodyBytes')='integer' AND json_extract(HttpJson,'$.ProjectedBodyBytes') BETWEEN 0 AND 65536 AND json_type(HttpJson,'$.BodySha256')='text' AND length(json_extract(HttpJson,'$.BodySha256'))=64 AND json_extract(HttpJson,'$.BodySha256') NOT GLOB '*[^0-9a-f]*' AND json_type(HttpJson,'$.Truncated') IN ('true','false'),0)";
    private static readonly string[] PriorVariants=["Persona","Preferences","CredentialCheck","SandboxCommand","SandboxControl","SandboxRead","BrowserCommand","CredentialRead","ArtifactPublication","ArtifactVerification","WorkspaceRead","WorkspaceMutation","AttachmentRead","BackgroundStart","BackgroundReport","CompletionRead","ReportSourceRead","AccountRead","AccountAction","MemoryOperation","ContinuityRead","ExperienceOperation","AutomationRead","AutomationMutation","AutomationAdmission","ManagementRead","ManagementMutation"];
    internal new static string ValuesConstraint => "(("+TenantPublicDownloads.ValuesConstraint+") AND json_extract(PayloadJson,'$.Operation.HttpRequest') IS NULL) OR COALESCE(("+HttpShape+HttpArgumentsShape+" AND "+string.Join(" AND ",PriorVariants.Select(p=>"json_extract(PayloadJson,'$.Operation."+p+"') IS NULL"))+"),0)";
    private const string HttpArgumentsShape = " AND json_type(json_extract(PayloadJson,'$.Operation.HttpRequest.ArgumentsJson'))='object' AND json_extract(json_extract(PayloadJson,'$.Operation.HttpRequest.ArgumentsJson'),'$.method') IN ('GET','HEAD','POST','PUT','PATCH','DELETE') AND json_type(json_extract(PayloadJson,'$.Operation.HttpRequest.ArgumentsJson'),'$.url')='text' AND length(json_extract(json_extract(PayloadJson,'$.Operation.HttpRequest.ArgumentsJson'),'$.url')) BETWEEN 1 AND 4096 AND (json_extract(json_extract(PayloadJson,'$.Operation.HttpRequest.ArgumentsJson'),'$.body') IS NULL OR length(CAST(json_extract(json_extract(PayloadJson,'$.Operation.HttpRequest.ArgumentsJson'),'$.body') AS BLOB))<=65536)";
    private const string HttpShape = "Kind IN (0,1) AND Status IN (0,1,2,3,4) AND Revision>=1 AND ExpiresAt>CreatedAt AND length(Fingerprint)=64 AND Fingerprint NOT GLOB '*[^0-9a-f]*' AND RunId IS NOT NULL AND SessionId IS NOT NULL AND AutomationId IS NULL AND CredentialId IS NULL AND SandboxProfileId IS NULL AND SandboxProfileRevision IS NULL AND Permission='http.request' AND ResourceType='agent' AND ResourceId=InstanceId AND length(CAST(PayloadJson AS BLOB))<=1048576 AND json_valid(PayloadJson) AND json_type(PayloadJson,'$.Operation.HttpRequest')='object' AND json_extract(PayloadJson,'$.Operation.HttpRequest.SessionId')=SessionId AND json_type(PayloadJson,'$.Operation.HttpRequest.ArgumentsJson')='text' AND length(CAST(json_extract(PayloadJson,'$.Operation.HttpRequest.ArgumentsJson') AS BLOB))<=524288 AND json_valid(json_extract(PayloadJson,'$.Operation.HttpRequest.ArgumentsJson')) AND json_type(PayloadJson,'$.Operation.HttpRequest.RequestHash')='text' AND length(json_extract(PayloadJson,'$.Operation.HttpRequest.RequestHash'))=64 AND json_extract(PayloadJson,'$.Operation.HttpRequest.RequestHash') NOT GLOB '*[^0-9a-f]*' AND json_extract(PayloadJson,'$.Operation.ExpectedInstanceRevision')>=1 AND json_extract(PayloadJson,'$.Operation.ExpectedPersonaRevision')>=1";
    private const string ReceiptGuards = """
        CREATE TRIGGER TR_TenantOperationReceipts_Http_INSERT BEFORE INSERT ON TenantOperationReceipts
        WHEN NEW.HttpJson IS NOT NULL OR NEW.ReasonCode IN ('http_request_completed','http_request_uncertain','http_request_not_dispatched')
            OR EXISTS(SELECT 1 FROM TenantApprovalRequests r WHERE r.RequestId=NEW.RequestId AND r.Permission='http.request')
        BEGIN
            SELECT RAISE(ABORT,'HTTP receipt requires its exact approved owned operation') WHERE NOT EXISTS
                (SELECT 1 FROM TenantApprovalRequests r JOIN TenantApprovalDecisions d ON d.RequestId=r.RequestId AND d.TenantId=r.TenantId
                WHERE r.RequestId=NEW.RequestId AND r.TenantId=NEW.TenantId AND r.Permission='http.request' AND r.Status=1
                    AND r.OperationId=NEW.OperationId AND lower(r.InstanceId)=lower(NEW.InstanceId) AND r.SessionId=NEW.SessionId
                    AND r.RootActivationId=NEW.RootActivationId AND r.ExecutorPrincipalId=NEW.ExecutorPrincipalId AND json_extract(r.PayloadJson,'$.Operation.ActionHash')=NEW.ActionHash
                    AND d.Status=1 AND d.ActionHash=NEW.ActionHash AND d.RequestRevision=r.Revision
                    AND json_type(r.PayloadJson,'$.Operation.HttpRequest')='object');
            SELECT RAISE(ABORT,'Completed HTTP observation requires reserved native dispatch and factual metadata')
                WHERE NEW.Outcome=0 AND (NEW.HttpJson IS NULL OR NOT EXISTS
                    (SELECT 1 FROM AgentRuns r WHERE r.AgentRunId=NEW.RunId AND r.TenantId=NEW.TenantId AND r.SessionId=NEW.SessionId
                    AND r.AgentInstanceId=NEW.InstanceId AND r.ExecutorPrincipalId=NEW.ExecutorPrincipalId AND r.RootActivationId=NEW.RootActivationId
                    AND json_extract(r.PayloadJson,'$.approvalRequestId')=lower(NEW.RequestId)
                    AND json_extract(r.PayloadJson,'$.sideEffect.toolCallId')='operation:' || lower(NEW.OperationId)
                    AND json_extract(r.PayloadJson,'$.sideEffect.actionHash')=NEW.ActionHash
                    AND json_type(r.PayloadJson,'$.sideEffect.externalEffect')='object'));
        END;
        CREATE TRIGGER TR_TenantOperationReceipts_Http_UPDATE BEFORE UPDATE ON TenantOperationReceipts
        WHEN OLD.HttpJson IS NOT NULL OR NEW.HttpJson IS NOT NULL
            OR EXISTS(SELECT 1 FROM TenantApprovalRequests r WHERE r.RequestId IN (OLD.RequestId,NEW.RequestId) AND r.Permission='http.request')
        BEGIN SELECT RAISE(ABORT,'HTTP receipts are immutable'); END;
        CREATE TRIGGER TR_TenantOperationReceipts_Http_DELETE BEFORE DELETE ON TenantOperationReceipts
        WHEN OLD.HttpJson IS NOT NULL OR EXISTS(SELECT 1 FROM TenantApprovalRequests r WHERE r.RequestId=OLD.RequestId AND r.Permission='http.request')
        BEGIN SELECT RAISE(ABORT,'HTTP receipts are retained'); END;
        """;
    protected override void BuildTargetModel(ModelBuilder modelBuilder)
    {
        base.BuildTargetModel(modelBuilder);
        modelBuilder.Entity("AgentCore.Infrastructure.Security.TenantApprovalRecord",e=>e.ToTable("TenantApprovalRequests",t=>t.HasCheckConstraint("CK_TenantApprovalRequest_Values",ValuesConstraint)));
        modelBuilder.Entity("AgentCore.Infrastructure.Security.TenantOperationReceiptRecord",e=> {
            e.Property<string>("HttpJson").HasColumnType("TEXT");
            e.ToTable("TenantOperationReceipts",t=>t.HasCheckConstraint("CK_TenantOperationReceipt_Http",ReceiptConstraint));
        });
    }
    protected override void Up(MigrationBuilder migrationBuilder)=>RebuildHttp(migrationBuilder,true);
    protected override void Down(MigrationBuilder migrationBuilder)=>RebuildHttp(migrationBuilder,false);
    private static void RebuildHttp(MigrationBuilder migrationBuilder,bool up)
    {
        if(!up)migrationBuilder.Sql("""
            CREATE TEMP TABLE PublicHttpDowngradeGuard(Rows INTEGER CHECK(Rows=0));
            INSERT INTO PublicHttpDowngradeGuard SELECT
                (SELECT count(*) FROM TenantApprovalRequests WHERE Permission='http.request')
                +(SELECT count(*) FROM TenantOperationReceipts WHERE HttpJson IS NOT NULL)
                +(SELECT count(*) FROM AgentRuns WHERE instr(PayloadJson,'tenantHttpRequest')>0 OR instr(PayloadJson,'send_http_request')>0)
                +(SELECT count(*) FROM AccessRolePermissions p JOIN AccessRoles r ON r.RoleId=p.RoleId WHERE p.PermissionKey='http.request' AND NOT(r.IsBuiltin=1 AND r.Key='owner' AND r.HumanOnly=1 AND r.TenantId IS NOT NULL));
            DROP TABLE PublicHttpDowngradeGuard;
            """);
        var (previous,guards)=FrozenPredecessor();var sql=new StringBuilder();
        if(!up)foreach(var suffix in new[] { "INSERT","UPDATE","DELETE" })sql.AppendLine("DROP TRIGGER TR_TenantOperationReceipts_Http_"+suffix+";");
        foreach(var name in guards.Keys)sql.AppendLine("DROP TRIGGER "+name+";");
        var ddl=ReplaceCheck(previous["table:TenantApprovalRequests"],up?ValuesConstraint:TenantPublicDownloads.ValuesConstraint);
        var offset=ddl.IndexOf("TenantApprovalRequests",StringComparison.Ordinal);
        sql.AppendLine(ddl[..offset]+"__http_TenantApprovalRequests"+ddl[(offset+"TenantApprovalRequests".Length)..]+";");
        sql.AppendLine("INSERT INTO __http_TenantApprovalRequests SELECT * FROM TenantApprovalRequests; DROP TABLE TenantApprovalRequests; ALTER TABLE __http_TenantApprovalRequests RENAME TO TenantApprovalRequests;");
        foreach(var index in previous.Where(p=>p.Key.StartsWith("index:",StringComparison.Ordinal) && p.Value.Contains(" ON \"TenantApprovalRequests\"",StringComparison.Ordinal)))sql.AppendLine(index.Value.TrimEnd(';')+";");
        sql.AppendLine(up?"ALTER TABLE TenantOperationReceipts ADD COLUMN HttpJson TEXT NULL CONSTRAINT CK_TenantOperationReceipt_Http CHECK ("+ReceiptConstraint+");":"ALTER TABLE TenantOperationReceipts DROP COLUMN HttpJson;");
        // Preserve all existing guards and their original SQL, including the public-download extension.
        foreach(var guard in guards.Values)sql.AppendLine(Extend(guard).TrimEnd().TrimEnd(';')+";");
        if(up)sql.AppendLine(ReceiptGuards);
        sql.AppendLine(up?"""
            UPDATE Tenants SET Revision=Revision+1 WHERE TenantId IN (SELECT r.TenantId FROM AccessRoles r WHERE r.IsBuiltin=1 AND r.Key='owner' AND r.HumanOnly=1 AND r.TenantId IS NOT NULL AND NOT EXISTS(SELECT 1 FROM AccessRolePermissions p WHERE p.RoleId=r.RoleId AND p.PermissionKey='http.request'));
            INSERT INTO AccessRolePermissions(RoleId,PermissionKey) SELECT r.RoleId,'http.request' FROM AccessRoles r WHERE r.IsBuiltin=1 AND r.Key='owner' AND r.HumanOnly=1 AND r.TenantId IS NOT NULL AND NOT EXISTS(SELECT 1 FROM AccessRolePermissions p WHERE p.RoleId=r.RoleId AND p.PermissionKey='http.request');
            """:"""
            UPDATE Tenants SET Revision=Revision+1 WHERE TenantId IN (SELECT r.TenantId FROM AccessRoles r JOIN AccessRolePermissions p ON p.RoleId=r.RoleId WHERE r.IsBuiltin=1 AND r.Key='owner' AND r.HumanOnly=1 AND r.TenantId IS NOT NULL AND p.PermissionKey='http.request');
            DELETE FROM AccessRolePermissions WHERE PermissionKey='http.request' AND RoleId IN (SELECT RoleId FROM AccessRoles WHERE IsBuiltin=1 AND Key='owner' AND HumanOnly=1 AND TenantId IS NOT NULL);
            """);
        migrationBuilder.Sql("PRAGMA foreign_keys=0;",suppressTransaction:true);
        migrationBuilder.Sql(sql.ToString());
        migrationBuilder.Sql("PRAGMA foreign_keys=1;",suppressTransaction:true);
    }
}
