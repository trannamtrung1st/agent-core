namespace AgentCore.Infrastructure.Persistence.Migrations;

public sealed partial class SpaceResourceApprovals
{
    private static readonly string[] NewGuardNames = ["TR_SpaceSources_Run_Insert", "TR_SpaceSources_Approval_Insert"];
    private const string SourceGuards = """
        CREATE TRIGGER TR_SpaceSources_Run_Insert BEFORE INSERT ON AgentRuns WHEN NEW.TenantId IS NOT NULL BEGIN
          SELECT RAISE(ABORT,'Run Space source requires its exact current owner, binding and immutable bytes') WHERE EXISTS (
            SELECT 1 FROM json_each(NEW.PayloadJson,'$.admission.configuration.resources') item
            WHERE (substr(json_extract(item.value,'$.key'),1,6)='space:' OR json_type(item.value,'$.spaceSource') IS NOT NULL)
              AND NOT COALESCE(EXISTS(SELECT 1 FROM SpaceFileResources r JOIN Spaces s ON s.TenantId=r.TenantId AND s.SpaceId=r.SpaceId
                JOIN SpaceMembershipAddedBindings b ON b.TenantId=r.TenantId AND b.SpaceId=r.SpaceId AND b.ResourceKey='space:'||lower(r.SpaceId)||':'||lower(r.ResourceId)
                WHERE r.TenantId=NEW.TenantId AND s.DeletedAt IS NULL AND r.DeletedAt IS NULL AND b.InstanceId=NEW.AgentInstanceId AND b.Kind=2
                  AND json_extract(NEW.PayloadJson,'$.admission.configuration.spaceContext.spaceId')=lower(r.SpaceId)
                  AND json_extract(item.value,'$.key')=b.ResourceKey AND json_type(item.value,'$.spaceSource')='object'
                  AND (SELECT count(*) FROM json_each(item.value,'$.spaceSource'))=4
                  AND json_extract(item.value,'$.spaceSource.tenantId')=lower(r.TenantId)
                  AND json_extract(item.value,'$.spaceSource.spaceId')=lower(r.SpaceId)
                  AND json_extract(item.value,'$.spaceSource.resourceId')=lower(r.ResourceId)
                  AND json_extract(item.value,'$.spaceSource.revision')=r.Revision
                  AND json_extract(item.value,'$.logicalPath')=r.LogicalPath AND json_extract(item.value,'$.kind')=r.Kind
                  AND json_extract(item.value,'$.mediaType')=r.MediaType AND json_extract(item.value,'$.byteLength')=r.ByteLength
                  AND json_extract(item.value,'$.contentSha256')=r.ContentSha256
                  AND json_extract(item.value,'$.virtualPath')='/space/resources/'||lower(r.SpaceId)||'/'||lower(r.ResourceId)||'/'||r.LogicalPath),0)
              AND NOT COALESCE(EXISTS(SELECT 1 FROM AgentRuns source
                WHERE json_extract(NEW.PayloadJson,'$.admission.activation.kind')=9
                  AND json_extract(NEW.PayloadJson,'$.admission.executionContext.origin')=9
                  AND source.AgentRunId=json_extract(NEW.PayloadJson,'$.admission.approvalResume.sourceRunId')
                  AND source.TenantId=NEW.TenantId AND source.SessionId=NEW.SessionId AND source.AgentInstanceId=NEW.AgentInstanceId
                  AND source.ExecutorPrincipalId=NEW.ExecutorPrincipalId AND source.RootActivationId=NEW.RootActivationId
                  AND source.OriginalInitiatorPrincipalId IS NEW.OriginalInitiatorPrincipalId
                  AND json_extract(source.PayloadJson,'$.admission.configuration')=json_extract(NEW.PayloadJson,'$.admission.configuration')),0));
        END;
        CREATE TRIGGER TR_SpaceSources_Approval_Insert BEFORE INSERT ON TenantApprovalRequests BEGIN
          SELECT RAISE(ABORT,'Consent Space source requires its exact current Run pin and membership') WHERE EXISTS (
            SELECT 1 FROM (
              SELECT json_extract(NEW.PayloadJson,'$.Operation.WorkspaceRead.ResourceKey') Key,json_extract(NEW.PayloadJson,'$.Operation.WorkspaceRead.ResourceRevision') Revision
              UNION ALL SELECT json_extract(NEW.PayloadJson,'$.Operation.ArtifactPublication.Source.ResourceKey'),json_extract(NEW.PayloadJson,'$.Operation.ArtifactPublication.Source.ResourceRevision')
              UNION ALL SELECT json_extract(member.value,'$.Key'),json_extract(member.value,'$.Revision') FROM json_each(json_extract(NEW.PayloadJson,'$.Operation.WorkspaceRead.ResourceMembersJson')) member
            ) source WHERE substr(source.Key,1,6)='space:' AND NOT COALESCE(EXISTS (
              SELECT 1 FROM SpaceFileResources r JOIN Spaces s ON s.TenantId=r.TenantId AND s.SpaceId=r.SpaceId
              JOIN SpaceMembershipAddedBindings b ON b.TenantId=r.TenantId AND b.SpaceId=r.SpaceId AND b.ResourceKey='space:'||lower(r.SpaceId)||':'||lower(r.ResourceId)
              JOIN AgentRuns run ON run.TenantId=r.TenantId AND run.AgentRunId=NEW.RunId AND run.AgentInstanceId=NEW.InstanceId
              JOIN json_each(run.PayloadJson,'$.admission.configuration.resources') item ON json_extract(item.value,'$.key')=source.Key
              WHERE r.TenantId=NEW.TenantId AND r.DeletedAt IS NULL AND s.DeletedAt IS NULL AND b.InstanceId=NEW.InstanceId AND b.Kind=2
                AND source.Key=b.ResourceKey AND source.Revision=r.Revision
                AND json_extract(NEW.PayloadJson,'$.Operation.SpaceContext.SpaceId')=lower(r.SpaceId)
                AND json_extract(item.value,'$.spaceSource.revision')=r.Revision
                AND json_extract(item.value,'$.contentSha256')=r.ContentSha256),0));
        END;
        """;
}
