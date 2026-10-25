using System.Text.Json;
using AgentCore.Application.Ports;
using Microsoft.EntityFrameworkCore;
using AgentCore.Domain.Security;
using AgentCore.Infrastructure.Persistence;

namespace AgentCore.Infrastructure.Security;

public sealed partial class SqliteIamStore
{
    private static IQueryable<TenantApprovalRecord> HiddenManagementResourceChanges(AgentCoreDbContext db, TenantManagementApprovalVisibility visibility,
        TenantWorkspaceReadApprovalVisibility? workspace, TenantAttachmentReadApprovalVisibility? attachment, TenantBrowserApprovalVisibility? browser)
    {
        static string Ids(IEnumerable<Guid>? values) => JsonSerializer.Serialize(values?.Select(id => id.ToString("D")) ?? []);
        static string Keys(IEnumerable<string>? values) => JsonSerializer.Serialize(values ?? []);
        var readAll = visibility.Resources is { Agents: null }; var readAgents = Ids(visibility.Resources?.Agents); var readChildren = Keys(visibility.Resources?.Children);
        var writeAll = visibility.ResourceChanges is { Agents: null }; var writeAgents = Ids(visibility.ResourceChanges?.Agents); var writeChildren = Keys(visibility.ResourceChanges?.Children);
        var deleteAll = visibility.ResourceDeletes is { Agents: null }; var deleteAgents = Ids(visibility.ResourceDeletes?.Agents); var deleteChildren = Keys(visibility.ResourceDeletes?.Children);
        var workspaceAll = workspace is { Workspaces: null }; var workspaces = Keys(workspace?.Workspaces);
        var resourceAll = workspace is { Resources: null }; var resources = Keys(workspace?.Resources);
        var homeAll = workspace is { HomeSpaces: null }; var homes = Ids(workspace?.HomeSpaces);
        var resourceAgentAll = workspace is { ResourceAgents: null }; var resourceAgents = Ids(workspace?.ResourceAgents);
        var workspaceSessionAll = workspace is { Sessions: null }; var workspaceSessions = Ids(workspace?.Sessions);
        var attachmentAll = attachment is { Attachments: null }; var attachments = Ids(attachment?.Attachments);
        var attachmentSessionAll = attachment is { Sessions: null }; var attachmentSessions = Ids(attachment?.Sessions);
        var browserAll = browser is { ReadInstances: null }; var browsers = Ids(browser?.ReadInstances);
        var browserSessionAll = browser is { Sessions: null }; var browserSessions = Ids(browser?.Sessions);
        return db.Set<TenantApprovalRecord>().FromSqlInterpolated($"""
            SELECT * FROM TenantApprovalRequests WHERE json_type(PayloadJson,'$.Operation.ManagementMutation.Resource')='object' AND
            (NOT ({readAll} OR InstanceId IN (SELECT value FROM json_each({readAgents})) OR ResourceId IN (SELECT value FROM json_each({readChildren})))
             OR NOT (CASE WHEN json_extract(PayloadJson,'$.Operation.ManagementMutation.ToolName')='resources.delete'
                THEN {deleteAll} OR InstanceId IN (SELECT value FROM json_each({deleteAgents})) OR ResourceId IN (SELECT value FROM json_each({deleteChildren}))
                ELSE {writeAll} OR InstanceId IN (SELECT value FROM json_each({writeAgents})) OR ResourceId IN (SELECT value FROM json_each({writeChildren})) END)
             OR json_extract(PayloadJson,'$.Operation.ManagementMutation.Resource.CreateScope')=1 AND NOT
                ({readAll} AND CASE WHEN json_extract(PayloadJson,'$.Operation.ManagementMutation.ToolName')='resources.delete' THEN {deleteAll} ELSE {writeAll} END)
             OR NOT ({readAll} AND CASE WHEN json_extract(PayloadJson,'$.Operation.ManagementMutation.ToolName')='resources.delete' THEN {deleteAll} ELSE {writeAll} END)
                AND NOT EXISTS (SELECT 1 FROM AgentInstances owner WHERE owner.InstanceId=TenantApprovalRequests.InstanceId AND
                 (owner.Revision=json_extract(TenantApprovalRequests.PayloadJson,'$.Operation.ExpectedInstanceRevision') OR EXISTS (
                  SELECT 1 FROM TenantOperationReceipts receipt WHERE TenantApprovalRequests.Status=1 AND receipt.RequestId=TenantApprovalRequests.RequestId
                   AND receipt.TenantId=TenantApprovalRequests.TenantId AND receipt.InstanceId=TenantApprovalRequests.InstanceId
                   AND receipt.ActionHash=json_extract(TenantApprovalRequests.PayloadJson,'$.Operation.ActionHash') AND receipt.Outcome=0 AND receipt.ReasonCode='management_mutation_completed'
                   AND receipt.ManagementResourceJson IS NOT NULL AND receipt.InstanceRevision=owner.Revision)))
             OR json_type(PayloadJson,'$.Operation.ManagementMutation.Resource.Source')='object'
                AND json_extract(PayloadJson,'$.Operation.ManagementMutation.Resource.Source.Name')<>'conversation:user' AND
                (NOT EXISTS (SELECT 1 FROM TenantSessions source JOIN Sessions saved ON saved.SessionId=source.SessionId
                  WHERE source.TenantId=TenantApprovalRequests.TenantId AND source.SessionId=TenantApprovalRequests.SessionId AND saved.DurablyDeletedAtUtc IS NULL)
                 OR CASE
                 WHEN json_type(PayloadJson,'$.Operation.ManagementMutation.Resource.Source.Workspace')='object' THEN NOT (
                  ({workspaceSessionAll} OR SessionId IN (SELECT value FROM json_each({workspaceSessions})))
                  AND (json_extract(PayloadJson,'$.Operation.ManagementMutation.Resource.Source.Workspace.ResourceKey') NOT GLOB 'instance:*'
                   OR EXISTS (SELECT 1 FROM AgentInstanceResources binding WHERE binding.InstanceId=TenantApprovalRequests.InstanceId
                    AND ('instance:'||binding.ResourceId)=json_extract(TenantApprovalRequests.PayloadJson,'$.Operation.ManagementMutation.Resource.Source.Workspace.ResourceKey'))
                   OR json_extract(PayloadJson,'$.Operation.ManagementMutation.Resource.Source.Workspace.ResourceKey') IS NULL)
                  AND (json_extract(PayloadJson,'$.Operation.ManagementMutation.Resource.Source.Workspace.Working')=1
                    OR (json_type(PayloadJson,'$.Operation.ManagementMutation.Resource.Source.Workspace.ResourceKey')='text' AND ({resourceAgentAll} OR InstanceId IN (SELECT value FROM json_each({resourceAgents}))))
                    OR (json_extract(PayloadJson,'$.Operation.ManagementMutation.Resource.Source.Workspace.ResourceKey') IS NULL AND ({homeAll} OR (CASE WHEN substr(json_extract(json_extract(PayloadJson,'$.Operation.ManagementMutation.Resource.Source.Workspace.ArgumentsJson'),'$.path'),1,9)='/private/' THEN json_extract(PayloadJson,'$.Operation.SpaceContext.PrivateSpaceId') ELSE json_extract(PayloadJson,'$.Operation.SpaceContext.SpaceId') END) IN (SELECT value FROM json_each({homes})))))
                  AND CASE WHEN json_type(PayloadJson,'$.Operation.ManagementMutation.Resource.Source.Workspace.ResourceKey')='text'
                   THEN {resourceAll} OR (InstanceId||':'||json_extract(PayloadJson,'$.Operation.ManagementMutation.Resource.Source.Workspace.ResourceKey')) IN (SELECT value FROM json_each({resources}))
                   ELSE {workspaceAll} OR (CASE WHEN json_extract(PayloadJson,'$.Operation.ManagementMutation.Resource.Source.Workspace.Working')=1 THEN 'working:'||SessionId ELSE 'home:'||(CASE WHEN substr(json_extract(json_extract(PayloadJson,'$.Operation.ManagementMutation.Resource.Source.Workspace.ArgumentsJson'),'$.path'),1,9)='/private/' THEN json_extract(PayloadJson,'$.Operation.SpaceContext.PrivateSpaceId') ELSE json_extract(PayloadJson,'$.Operation.SpaceContext.SpaceId') END) END) IN (SELECT value FROM json_each({workspaces})) END)
                 WHEN json_type(PayloadJson,'$.Operation.ManagementMutation.Resource.Source.Attachment')='object' THEN NOT (
                  ({attachmentSessionAll} OR SessionId IN (SELECT value FROM json_each({attachmentSessions})))
                  AND ({attachmentAll} OR json_extract(PayloadJson,'$.Operation.ManagementMutation.Resource.Source.Attachment.AttachmentId') IN (SELECT value FROM json_each({attachments})))
                  AND EXISTS (SELECT 1 FROM Attachments input WHERE input.TenantId=TenantApprovalRequests.TenantId AND input.SessionId=TenantApprovalRequests.SessionId
                   AND input.AttachmentId=json_extract(TenantApprovalRequests.PayloadJson,'$.Operation.ManagementMutation.Resource.Source.Attachment.AttachmentId')
                   AND input.State='Bound' AND input.ContentDeletedAtUtc IS NULL))
                 ELSE NOT (({browserAll} OR InstanceId IN (SELECT value FROM json_each({browsers}))) AND ({browserSessionAll} OR SessionId IN (SELECT value FROM json_each({browserSessions})))) END))
            """);
    }
}
