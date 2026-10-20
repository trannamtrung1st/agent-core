using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;
using AgentCore.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AgentCore.Infrastructure.Security;

public sealed partial class SqliteIamStore : ITenantApprovalStore
{
    private DateTimeOffset ApprovalNow => (clock ?? TimeProvider.System).GetUtcNow();
    public async ValueTask<TenantApprovalRequest?> FindApprovalAsync(Guid tenantId, Guid requestId, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct).ConfigureAwait(false);
        return (await db.Set<TenantApprovalRecord>().AsNoTracking().SingleOrDefaultAsync(r => r.TenantId == tenantId && r.RequestId == requestId, ct).ConfigureAwait(false))?.Domain();
    }
    public async ValueTask<TenantApprovalRequest?> FindRootApprovalAsync(Guid tenantId, Guid rootId, Guid executorId, PreparedTenantOperation operation, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct).ConfigureAwait(false);
        return (await RootApproval(db, tenantId, rootId.ToString("D"), executorId, operation).AsNoTracking().FirstOrDefaultAsync(ct).ConfigureAwait(false))?.Domain();
    }
    private static IQueryable<TenantApprovalRecord> RootApproval(AgentCoreDbContext db, Guid tenant, string root, Guid executor, PreparedTenantOperation operation) =>
        SemanticApprovals(db, operation).Where(r => r.TenantId == tenant && r.RootActivationId == root && r.ExecutorPrincipalId == executor)
            .OrderBy(r => r.Status == (int)TenantApprovalStatus.Rejected || r.Status == (int)TenantApprovalStatus.Expired || r.Status == (int)TenantApprovalStatus.Cancelled ? 0 : r.Status == (int)TenantApprovalStatus.Pending ? 1 : 2)
            .ThenBy(r => r.CreatedAt).ThenBy(r => r.RequestId);
    private static IQueryable<TenantApprovalRecord> SemanticApprovals(AgentCoreDbContext db, PreparedTenantOperation operation) => operation.AutomationAdmission is { } admission ? db.Set<TenantApprovalRecord>().FromSqlInterpolated($"""
            SELECT * FROM TenantApprovalRequests WHERE AutomationId = {admission.Current.AutomationId.ToString("D")}
              AND Permission = {operation.Permission} AND json_extract(PayloadJson, '$.Operation.AutomationAdmission.Occurrence.OccurrenceId') = {admission.Occurrence.OccurrenceId.ToString("D")}
            """)
        : operation.AccountAction is { } action
        ? db.Set<TenantApprovalRecord>().FromSqlInterpolated($"""
            SELECT * FROM TenantApprovalRequests WHERE Permission = {operation.Permission} AND InstanceId = {operation.InstanceId.ToString("D")}
            AND json_extract(PayloadJson, '$.Operation.AccountAction.SessionId') = {action.SessionId.ToString("D")}
            AND json_extract(PayloadJson, '$.Operation.AccountAction.ToolName') = {action.ToolName}
            AND json_extract(PayloadJson, '$.Operation.AccountAction.ArgumentsJson') = {action.ArgumentsJson}
            AND json_extract(PayloadJson, '$.Operation.AccountAction.Credential.CredentialId') = {action.Credential.CredentialId.ToString("D")}
            """)
        : operation.AccountRead is { } accountRead
        ? db.Set<TenantApprovalRecord>().FromSqlInterpolated($"""
            SELECT * FROM TenantApprovalRequests WHERE Permission = 'credentials.use' AND InstanceId = {operation.InstanceId.ToString("D")}
            AND json_extract(PayloadJson, '$.Operation.AccountRead.SessionId') = {accountRead.SessionId.ToString("D")}
            AND json_extract(PayloadJson, '$.Operation.AccountRead.ToolName') = {accountRead.ToolName}
            AND json_extract(PayloadJson, '$.Operation.AccountRead.ArgumentsJson') = {accountRead.ArgumentsJson}
            AND json_extract(PayloadJson, '$.Operation.AccountRead.Credential.CredentialId') = {accountRead.Credential.CredentialId.ToString("D")}
            """)
        : operation.BackgroundReport is { } report
        ? db.Set<TenantApprovalRecord>().FromSqlInterpolated($"""
            SELECT * FROM TenantApprovalRequests WHERE ResourceType = 'tenant'
            AND json_extract(PayloadJson, '$.Operation.BackgroundReport.ChildRunId') = {report.ChildRunId.ToString("D")}
            AND json_extract(PayloadJson, '$.Operation.BackgroundReport.ParentSessionId') = {report.ParentSessionId.ToString("D")}
            """)
        : operation.BackgroundStart is { } background
        ? db.Set<TenantApprovalRecord>().FromSqlInterpolated($"""
            SELECT * FROM TenantApprovalRequests WHERE ResourceType = 'tenant'
            AND json_extract(PayloadJson, '$.Operation.BackgroundStart.SessionId') = {background.SessionId.ToString("D")}
            AND json_extract(PayloadJson, '$.Operation.BackgroundStart.Objective') = {background.Objective}
            AND json_extract(PayloadJson, '$.Operation.BackgroundStart.Title') = {background.Title}
            AND json_extract(PayloadJson, '$.Operation.BackgroundStart.ReportCompletion') = {background.ReportCompletion}
            """)
        : operation.WorkspaceMutation is { } mutation
        ? db.Set<TenantApprovalRecord>().FromSqlInterpolated($"""
            SELECT * FROM TenantApprovalRequests WHERE Permission = {operation.Permission} AND ResourceType = 'workspace' AND ResourceId = {operation.ResourceId}
            AND json_extract(PayloadJson, '$.Operation.WorkspaceMutation.SessionId') = {mutation.SessionId.ToString("D")}
            AND json_extract(PayloadJson, '$.Operation.WorkspaceMutation.ToolName') = {mutation.ToolName}
            AND json_extract(PayloadJson, '$.Operation.WorkspaceMutation.ArgumentsJson') = {mutation.ArgumentsJson}
            """)
        : operation.WorkspaceRead is { } workspace
        ? db.Set<TenantApprovalRecord>().FromSqlInterpolated($"""
            SELECT * FROM TenantApprovalRequests WHERE Permission = {operation.Permission} AND ResourceType = {operation.ResourceType} AND InstanceId = {operation.InstanceId.ToString("D")}
            AND json_extract(PayloadJson, '$.Operation.WorkspaceRead.SessionId') = {workspace.SessionId.ToString("D")}
            AND json_extract(PayloadJson, '$.Operation.WorkspaceRead.ToolName') = {workspace.ToolName}
            AND json_extract(PayloadJson, '$.Operation.WorkspaceRead.ArgumentsJson') = {workspace.ArgumentsJson}
            """)
        : operation.AttachmentRead is { } attachment
        ? db.Set<TenantApprovalRecord>().FromSqlInterpolated($"""
            SELECT * FROM TenantApprovalRequests WHERE Permission = 'attachments.read' AND ResourceType = 'attachment' AND ResourceId = {operation.ResourceId}
            AND json_extract(PayloadJson, '$.Operation.AttachmentRead.SessionId') = {attachment.SessionId.ToString("D")}
            AND json_extract(PayloadJson, '$.Operation.AttachmentRead.ArgumentsJson') = {attachment.ArgumentsJson}
            """)
         : operation.ReportSourceRead is { } readSource
        ? db.Set<TenantApprovalRecord>().FromSqlInterpolated($"""
            SELECT * FROM TenantApprovalRequests WHERE json_type(PayloadJson, '$.Operation.ReportSourceRead') IS 'object'
            AND json_extract(PayloadJson, '$.Operation.ReportSourceRead.ParentSessionId') = {readSource.ParentSessionId.ToString("D")}
            AND json_extract(PayloadJson, '$.Operation.ReportSourceRead.ChildSessionId') = {readSource.ChildSessionId.ToString("D")}
            AND json_extract(PayloadJson, '$.Operation.ReportSourceRead.InitialRunId') = {readSource.InitialRunId.ToString("D")}
            """)
        : operation.CompletionRead is { } completion
        ? db.Set<TenantApprovalRecord>().FromSqlInterpolated($"""
            SELECT * FROM TenantApprovalRequests WHERE json_type(PayloadJson, '$.Operation.CompletionRead') IS 'object'
            AND json_extract(PayloadJson, '$.Operation.CompletionRead.ParentSessionId') = {completion.ParentSessionId.ToString("D")}
            AND json_extract(PayloadJson, '$.Operation.CompletionRead.ChildSessionId') = {completion.ChildSessionId.ToString("D")}
            AND json_extract(PayloadJson, '$.Operation.CompletionRead.InitialRunId') = {completion.InitialRunId.ToString("D")}
            """)
        : operation.ArtifactVerification is { } verification
        ? db.Set<TenantApprovalRecord>().FromSqlInterpolated($"""
            SELECT * FROM TenantApprovalRequests WHERE Permission = 'artifacts.read' AND ResourceType = 'artifact' AND ResourceId = {operation.ResourceId}
            AND json_extract(PayloadJson, '$.Operation.ArtifactVerification') = {System.Text.Json.JsonSerializer.Serialize(verification)}
            """)
        : operation.ArtifactPublication is { } artifact
        ? db.Set<TenantApprovalRecord>().FromSqlInterpolated($"""
            SELECT * FROM TenantApprovalRequests WHERE Permission = 'artifacts.create' AND ResourceType = 'session'
            AND json_extract(PayloadJson, '$.Operation.ArtifactPublication.SessionId') = {artifact.SessionId.ToString("D")}
            AND json_extract(PayloadJson, '$.Operation.ArtifactPublication.ArgumentsJson') = {artifact.ArgumentsJson}
            """)
        : operation.SandboxCommand is { } sandbox
        ? db.Set<TenantApprovalRecord>().FromSqlInterpolated($"""
            SELECT * FROM TenantApprovalRequests WHERE Permission = 'sandbox.execute'
            AND json_extract(PayloadJson, '$.Operation.SandboxCommand.SessionId') = {sandbox.SessionId.ToString("D")}
            AND json_extract(PayloadJson, '$.Operation.SandboxCommand.ProfileId') = {sandbox.ProfileId}
            AND json_extract(PayloadJson, '$.Operation.SandboxCommand.CommandJson') = {sandbox.CommandJson}
            """)
        : operation.SandboxControl is { } control ? db.Set<TenantApprovalRecord>().FromSqlInterpolated($"""
            SELECT * FROM TenantApprovalRequests WHERE Permission = 'sandbox.manage'
            AND json_extract(PayloadJson, '$.Operation.SandboxControl.SessionId') = {control.SessionId.ToString("D")}
            AND json_extract(PayloadJson, '$.Operation.SandboxControl.Kind') = {(int)control.Kind}
            AND json_extract(PayloadJson, '$.Operation.SandboxControl.Generation') = {control.Generation.ToString("D")}
            AND json_extract(PayloadJson, '$.Operation.SandboxControl.ExecutionId') IS {control.ExecutionId?.ToString("D")}
            AND json_extract(PayloadJson, '$.Operation.SandboxControl.TargetHash') = {control.TargetHash}
            """)
        : operation.CredentialRead is { } metadata ? db.Set<TenantApprovalRecord>().FromSqlInterpolated($"""
            SELECT * FROM TenantApprovalRequests WHERE json_type(PayloadJson, '$.Operation.CredentialRead') IS 'object'
            AND json_extract(PayloadJson, '$.Operation.CredentialRead.SessionId') = {metadata.SessionId.ToString("D")}
            AND json_extract(PayloadJson, '$.Operation.CredentialRead.ArgumentsJson') = {metadata.ArgumentsJson}
            """)
        : operation.BrowserCommand is { } browser ? db.Set<TenantApprovalRecord>().FromSqlInterpolated($"""
            SELECT * FROM TenantApprovalRequests WHERE Permission = {operation.Permission} AND ResourceType = {operation.ResourceType} AND ResourceId = {operation.ResourceId}
            AND json_extract(PayloadJson, '$.Operation.BrowserCommand.SessionId') = {browser.SessionId.ToString("D")}
            AND json_extract(PayloadJson, '$.Operation.BrowserCommand.ToolName') = {browser.ToolName}
            AND json_extract(PayloadJson, '$.Operation.BrowserCommand.CommandHash') = {browser.CommandHash}
            """)
        : db.Set<TenantApprovalRecord>().FromSqlInterpolated($"""
            SELECT * FROM TenantApprovalRequests
            WHERE json_remove(json_extract(PayloadJson, '$.Operation.CanonicalActionJson'), '$.expectedInstanceRevision', '$.expectedPersonaRevision', '$.credentialCheck') = {operation.SemanticActionJson}
            """);
    public async ValueTask<TenantApprovalPage> ListApprovalsAsync(Guid tenantId, IReadOnlyList<Guid>? allowedRequests, IReadOnlyList<Guid>? allowedInstances, TenantApprovalQuery page, CancellationToken ct = default, IReadOnlyList<Guid>? allowedCredentials = null, TenantSandboxApprovalVisibility? sandboxVisibility = null, TenantBrowserApprovalVisibility? browserVisibility = null, TenantCredentialReadApprovalVisibility? credentialReadVisibility = null, IReadOnlyList<Guid>? artifactCreateSessions = null, TenantArtifactReadApprovalVisibility? artifactReadVisibility = null, TenantWorkspaceReadApprovalVisibility? workspaceReadVisibility = null, TenantWorkspaceMutationApprovalVisibility? workspaceMutationVisibility = null, TenantAttachmentReadApprovalVisibility? attachmentReadVisibility = null, bool backgroundCreationReadable = true, TenantBackgroundReportApprovalVisibility? reportVisibility = null, TenantAutomationApprovalVisibility? automationVisibility = null)
    {
        page.Validate();
        await using var db = await contexts.CreateDbContextAsync(ct).ConfigureAwait(false);
        await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        var attachmentJson = System.Text.Json.JsonSerializer.Serialize(attachmentReadVisibility?.Attachments?.Select(id => id.ToString("D")).ToArray());
        var attachmentSessionJson = System.Text.Json.JsonSerializer.Serialize(attachmentReadVisibility?.Sessions?.Select(id => id.ToString("D")).ToArray());
        var artifactVerifyJson = System.Text.Json.JsonSerializer.Serialize(artifactReadVisibility?.Artifacts?.Select(id => id.ToString("D")).ToArray());
        var artifactVerifySessionJson = System.Text.Json.JsonSerializer.Serialize(artifactReadVisibility?.Sessions?.Select(id => id.ToString("D")).ToArray());
        var artifactCreateJson = System.Text.Json.JsonSerializer.Serialize(artifactCreateSessions?.Select(id => id.ToString("D")).ToArray());
        var artifactReadJson = System.Text.Json.JsonSerializer.Serialize(sandboxVisibility?.Sessions?.Select(id => id.ToString("D")).ToArray());
        var readableJson = System.Text.Json.JsonSerializer.Serialize(sandboxVisibility?.Profiles);
        var browserReadJson = System.Text.Json.JsonSerializer.Serialize(browserVisibility?.ReadInstances?.Select(id => id.ToString("D")).ToArray());
        var browserUseJson = System.Text.Json.JsonSerializer.Serialize(browserVisibility?.UseInstances?.Select(id => id.ToString("D")).ToArray());
        var browserSessionJson = System.Text.Json.JsonSerializer.Serialize(browserVisibility?.Sessions?.Select(id => id.ToString("D")).ToArray());
        var metadataReadJson = System.Text.Json.JsonSerializer.Serialize(credentialReadVisibility?.Credentials?.Select(id => id.ToString("D")).ToArray());
        var metadataSessionJson = System.Text.Json.JsonSerializer.Serialize(credentialReadVisibility?.Sessions?.Select(id => id.ToString("D")).ToArray());
        // Browser compound visibility applies to protected Credential fills too, independently of the primary permission.
        var resourceJson = System.Text.Json.JsonSerializer.Serialize(workspaceReadVisibility?.Resources);
        var workspaceJson = System.Text.Json.JsonSerializer.Serialize(workspaceReadVisibility?.Workspaces);
        var homeAgentsJson = System.Text.Json.JsonSerializer.Serialize(workspaceReadVisibility?.HomeAgents?.Select(id => id.ToString("D")).ToArray());
        var workspaceSessionsJson = System.Text.Json.JsonSerializer.Serialize(workspaceReadVisibility?.Sessions?.Select(id => id.ToString("D")).ToArray());
        var mutationReadJson = System.Text.Json.JsonSerializer.Serialize(workspaceMutationVisibility?.Reads);
        var mutationWriteJson = System.Text.Json.JsonSerializer.Serialize(workspaceMutationVisibility?.Writes);
        var mutationDeleteJson = System.Text.Json.JsonSerializer.Serialize(workspaceMutationVisibility?.Deletes);
        var mutationAgentsJson = System.Text.Json.JsonSerializer.Serialize(workspaceMutationVisibility?.HomeAgents?.Select(id => id.ToString("D")).ToArray());
        var mutationSessionsJson = System.Text.Json.JsonSerializer.Serialize(workspaceMutationVisibility?.Sessions?.Select(id => id.ToString("D")).ToArray());
        var resourceOwners = await db.Set<TenantApprovalRecord>().FromSqlInterpolated($"""
            SELECT * FROM TenantApprovalRequests WHERE TenantId = {tenantId} AND
            (Permission = 'resources.read' OR json_type(PayloadJson, '$.Operation.WorkspaceRead.ResourceMembersJson') IS 'text'
                OR json_type(PayloadJson, '$.Operation.ArtifactPublication.Source.ResourceKey') IS 'text')
            """).AsNoTracking().Select(r => r.InstanceId).Distinct().ToArrayAsync(ct).ConfigureAwait(false);
        var ownedResources = new HashSet<string>(StringComparer.Ordinal);
        foreach (var owner in resourceOwners)
            foreach (var scope in await ChildCatalogAsync(db, tenantId, Guid.Parse(owner), null, ct).ConfigureAwait(false))
                if (scope.ResourceType == "resource") ownedResources.Add(scope.ResourceId!);
        var ownedResourceJson = System.Text.Json.JsonSerializer.Serialize(ownedResources);
        var reportCanCreate = reportVisibility?.CanCreate ?? true;
        var reportSessionsJson = System.Text.Json.JsonSerializer.Serialize(reportVisibility?.Sessions?.Select(id => id.ToString("D")).ToArray());
        var reportRunsJson = System.Text.Json.JsonSerializer.Serialize(reportVisibility?.Runs?.Select(id => id.ToString("D")).ToArray());
        var source = db.Set<TenantApprovalRecord>().FromSqlInterpolated($"""
            SELECT * FROM TenantApprovalRequests WHERE
            (json_type(PayloadJson, '$.Operation.BackgroundStart') IS NOT 'object' OR ({backgroundCreationReadable}
                AND ({artifactReadJson} = 'null' OR SessionId IN (SELECT value FROM json_each({artifactReadJson})))
                AND EXISTS (SELECT 1 FROM Sessions s WHERE s.SessionId = TenantApprovalRequests.SessionId AND s.DurablyDeletedAtUtc IS NULL)))
            AND (json_type(TenantApprovalRequests.PayloadJson, '$.Operation.CompletionRead') IS NOT 'object' OR (
                ({reportSessionsJson} = 'null' OR (SessionId IN (SELECT value FROM json_each({reportSessionsJson}))
                    AND json_extract(TenantApprovalRequests.PayloadJson, '$.Operation.CompletionRead.ChildSessionId') IN (SELECT value FROM json_each({reportSessionsJson}))))
                AND ({reportRunsJson} = 'null' OR (json_extract(TenantApprovalRequests.PayloadJson, '$.Operation.CompletionRead.InitialRunId') IN (SELECT value FROM json_each({reportRunsJson}))
                    AND json_extract(TenantApprovalRequests.PayloadJson, '$.Operation.CompletionRead.SourceRunId') IN (SELECT value FROM json_each({reportRunsJson}))))
                AND ({artifactVerifyJson} = 'null' OR NOT EXISTS (SELECT 1 FROM json_each(json_extract(TenantApprovalRequests.PayloadJson, '$.Operation.CompletionRead.ArtifactsJson')) file
                    WHERE json_extract(file.value, '$.ArtifactId') NOT IN (SELECT value FROM json_each({artifactVerifyJson}))))
                AND EXISTS (SELECT 1 FROM TenantSessions child JOIN Sessions saved ON saved.SessionId = child.SessionId
                    WHERE child.TenantId = TenantApprovalRequests.TenantId AND child.AgentInstanceId = TenantApprovalRequests.InstanceId
                    AND child.ExecutorPrincipalId = TenantApprovalRequests.ExecutorPrincipalId AND saved.DurablyDeletedAtUtc IS NULL
                    AND child.SessionId = json_extract(TenantApprovalRequests.PayloadJson, '$.Operation.CompletionRead.ChildSessionId'))
                AND EXISTS (SELECT 1 FROM AgentRuns initial WHERE initial.TenantId = TenantApprovalRequests.TenantId
                    AND initial.SessionId = json_extract(TenantApprovalRequests.PayloadJson, '$.Operation.CompletionRead.ChildSessionId')
                    AND initial.AgentRunId = json_extract(TenantApprovalRequests.PayloadJson, '$.Operation.CompletionRead.InitialRunId'))
                AND EXISTS (SELECT 1 FROM AgentRuns source WHERE source.TenantId = TenantApprovalRequests.TenantId
                    AND source.SessionId = json_extract(TenantApprovalRequests.PayloadJson, '$.Operation.CompletionRead.ChildSessionId')
                    AND source.AgentRunId = json_extract(TenantApprovalRequests.PayloadJson, '$.Operation.CompletionRead.SourceRunId'))))
            AND (json_type(TenantApprovalRequests.PayloadJson, '$.Operation.ReportSourceRead') IS NOT 'object' OR (
                ({reportSessionsJson} = 'null' OR (SessionId IN (SELECT value FROM json_each({reportSessionsJson}))
                    AND json_extract(TenantApprovalRequests.PayloadJson, '$.Operation.ReportSourceRead.ParentSessionId') IN (SELECT value FROM json_each({reportSessionsJson}))))
                AND ({reportRunsJson} = 'null' OR (RunId IN (SELECT value FROM json_each({reportRunsJson}))
                    AND json_extract(TenantApprovalRequests.PayloadJson, '$.Operation.ReportSourceRead.InitialRunId') IN (SELECT value FROM json_each({reportRunsJson}))))
                AND EXISTS (SELECT 1 FROM TenantSessions child JOIN Sessions saved ON saved.SessionId = child.SessionId
                    WHERE child.TenantId = TenantApprovalRequests.TenantId AND child.AgentInstanceId = TenantApprovalRequests.InstanceId
                    AND child.ExecutorPrincipalId = TenantApprovalRequests.ExecutorPrincipalId AND saved.DurablyDeletedAtUtc IS NULL AND child.SessionId = TenantApprovalRequests.SessionId)
                AND EXISTS (SELECT 1 FROM TenantSessions parent JOIN Sessions saved ON saved.SessionId = parent.SessionId
                    WHERE parent.TenantId = TenantApprovalRequests.TenantId AND parent.AgentInstanceId = TenantApprovalRequests.InstanceId
                    AND parent.ExecutorPrincipalId = TenantApprovalRequests.ExecutorPrincipalId AND saved.DurablyDeletedAtUtc IS NULL
                    AND parent.SessionId = json_extract(TenantApprovalRequests.PayloadJson, '$.Operation.ReportSourceRead.ParentSessionId'))))
            AND (json_type(PayloadJson, '$.Operation.BackgroundReport') IS NOT 'object' OR ({reportCanCreate}
                AND ({reportSessionsJson} = 'null' OR (SessionId IN (SELECT value FROM json_each({reportSessionsJson}))
                    AND json_extract(PayloadJson, '$.Operation.BackgroundReport.ParentSessionId') IN (SELECT value FROM json_each({reportSessionsJson}))))
                AND ({reportRunsJson} = 'null' OR RunId IN (SELECT value FROM json_each({reportRunsJson})))
                AND EXISTS (SELECT 1 FROM Sessions s WHERE s.SessionId = TenantApprovalRequests.SessionId AND s.DurablyDeletedAtUtc IS NULL)
                AND EXISTS (SELECT 1 FROM TenantSessions root JOIN Sessions s ON s.SessionId = root.SessionId WHERE root.TenantId = TenantApprovalRequests.TenantId
                    AND root.SessionId = json_extract(PayloadJson, '$.Operation.BackgroundReport.ParentSessionId') AND s.DurablyDeletedAtUtc IS NULL)))
            AND
            (json_type(PayloadJson, '$.Operation.WorkspaceMutation') IS NOT 'object' OR (
                ({mutationSessionsJson} = 'null' OR SessionId IN (SELECT value FROM json_each({mutationSessionsJson})))
                AND (NOT EXISTS (SELECT 1 FROM json_each(json_extract(PayloadJson, '$.Operation.WorkspaceMutation.TargetsJson')) t WHERE json_extract(t.value, '$.Working') = 0)
                    OR {mutationAgentsJson} = 'null' OR InstanceId IN (SELECT value FROM json_each({mutationAgentsJson})))
                AND NOT EXISTS (SELECT 1 FROM json_each(json_extract(PayloadJson, '$.Operation.WorkspaceMutation.TargetsJson')) t WHERE
                    (json_extract(t.value, '$.Permission') = 'workspaces.read' AND {mutationReadJson} <> 'null' AND
                        CASE WHEN json_extract(t.value, '$.Working') = 1 THEN 'working:' || SessionId ELSE 'home:' || InstanceId END NOT IN (SELECT value FROM json_each({mutationReadJson})))
                    OR (json_extract(t.value, '$.Permission') = 'workspaces.write' AND {mutationWriteJson} <> 'null' AND
                        CASE WHEN json_extract(t.value, '$.Working') = 1 THEN 'working:' || SessionId ELSE 'home:' || InstanceId END NOT IN (SELECT value FROM json_each({mutationWriteJson})))
                    OR (json_extract(t.value, '$.Permission') = 'workspaces.delete' AND {mutationDeleteJson} <> 'null' AND
                        CASE WHEN json_extract(t.value, '$.Working') = 1 THEN 'working:' || SessionId ELSE 'home:' || InstanceId END NOT IN (SELECT value FROM json_each({mutationDeleteJson}))))
                AND EXISTS (SELECT 1 FROM TenantSessions root JOIN Sessions saved ON saved.SessionId = root.SessionId
                    JOIN SystemIdentities agent ON agent.TenantId = root.TenantId AND agent.AgentInstanceId = root.AgentInstanceId AND agent.PrincipalId = root.ExecutorPrincipalId
                    WHERE root.TenantId = TenantApprovalRequests.TenantId AND root.SessionId = TenantApprovalRequests.SessionId
                        AND root.AgentInstanceId = TenantApprovalRequests.InstanceId AND root.ExecutorPrincipalId = TenantApprovalRequests.ExecutorPrincipalId AND saved.DurablyDeletedAtUtc IS NULL))) AND
            (json_type(PayloadJson, '$.Operation.WorkspaceRead') IS NOT 'object' OR (
                ((Permission = 'workspaces.read' AND ({workspaceJson} = 'null' OR ResourceId IN (SELECT value FROM json_each({workspaceJson}))))
                    OR (Permission = 'resources.read' AND ({resourceJson} = 'null' OR ResourceId IN (SELECT value FROM json_each({resourceJson})))
                        AND ResourceId IN (SELECT value FROM json_each({ownedResourceJson})))
                    OR (json_type(PayloadJson, '$.Operation.WorkspaceRead.AttachmentMembersJson') IS 'text'
                        AND ({attachmentSessionJson} = 'null' OR SessionId IN (SELECT value FROM json_each({attachmentSessionJson})))
                        AND NOT EXISTS (SELECT 1 FROM json_each(json_extract(PayloadJson, '$.Operation.WorkspaceRead.AttachmentMembersJson')) member
                            WHERE ({attachmentJson} <> 'null' AND json_extract(member.value, '$.AttachmentId') NOT IN (SELECT value FROM json_each({attachmentJson})))
                                OR NOT EXISTS (SELECT 1 FROM Attachments a WHERE a.TenantId = TenantApprovalRequests.TenantId AND a.SessionId = TenantApprovalRequests.SessionId
                                    AND a.AttachmentId = json_extract(member.value, '$.AttachmentId') AND a.EntryId = json_extract(member.value, '$.EntryId')
                                    AND a.Sha256Hex = json_extract(member.value, '$.Sha256Hex') AND a.State = 'Bound' AND a.ContentDeletedAtUtc IS NULL)))
                    OR (json_type(PayloadJson, '$.Operation.WorkspaceRead.ResourceMembersJson') IS 'text' AND NOT EXISTS (
                        SELECT 1 FROM json_each(json_extract(PayloadJson, '$.Operation.WorkspaceRead.ResourceMembersJson')) member
                        WHERE (InstanceId || ':' || json_extract(member.value, '$.Key')) NOT IN (SELECT value FROM json_each({ownedResourceJson}))
                            OR ({resourceJson} <> 'null' AND (InstanceId || ':' || json_extract(member.value, '$.Key')) NOT IN (SELECT value FROM json_each({resourceJson}))))))
                AND ({workspaceSessionsJson} = 'null' OR SessionId IN (SELECT value FROM json_each({workspaceSessionsJson})))
                AND (json_type(PayloadJson, '$.Operation.WorkspaceRead.AttachmentMembersJson') IS 'text' OR json_extract(PayloadJson, '$.Operation.WorkspaceRead.Working') = 1 OR {homeAgentsJson} = 'null' OR InstanceId IN (SELECT value FROM json_each({homeAgentsJson})))
                AND EXISTS (SELECT 1 FROM TenantSessions AS root JOIN Sessions AS saved ON saved.SessionId = root.SessionId
                    WHERE root.TenantId = TenantApprovalRequests.TenantId AND root.SessionId = TenantApprovalRequests.SessionId
                    AND root.AgentInstanceId = TenantApprovalRequests.InstanceId AND root.ExecutorPrincipalId = TenantApprovalRequests.ExecutorPrincipalId
                    AND saved.DurablyDeletedAtUtc IS NULL))) AND
            (Permission <> 'attachments.read' OR (
                ({attachmentJson} = 'null' OR ResourceId IN (SELECT value FROM json_each({attachmentJson})))
                AND ({attachmentSessionJson} = 'null' OR SessionId IN (SELECT value FROM json_each({attachmentSessionJson})))
                AND EXISTS (SELECT 1 FROM Attachments AS file
                    JOIN TenantSessions AS root ON root.TenantId = file.TenantId AND root.SessionId = file.SessionId
                    JOIN Sessions AS saved ON saved.SessionId = root.SessionId
                    WHERE file.TenantId = TenantApprovalRequests.TenantId AND file.SessionId = TenantApprovalRequests.SessionId
                    AND root.AgentInstanceId = TenantApprovalRequests.InstanceId AND root.ExecutorPrincipalId = TenantApprovalRequests.ExecutorPrincipalId
                    AND file.AttachmentId = TenantApprovalRequests.ResourceId AND file.State = 'Bound'
                    AND file.EntryId = json_extract(TenantApprovalRequests.PayloadJson, '$.Operation.AttachmentRead.EntryId') AND saved.DurablyDeletedAtUtc IS NULL))) AND
            (Permission <> 'artifacts.read' OR (
                ({artifactVerifyJson} = 'null' OR ResourceId IN (SELECT value FROM json_each({artifactVerifyJson})))
                AND ({artifactVerifySessionJson} = 'null' OR SessionId IN (SELECT value FROM json_each({artifactVerifySessionJson})))
                AND EXISTS (SELECT 1 FROM Artifacts AS file
                    JOIN AgentRuns AS source ON source.AgentRunId = file.AgentRunId AND source.TenantId = file.TenantId AND source.SessionId = file.SessionId
                    JOIN TenantSessions AS root ON root.TenantId = file.TenantId AND root.SessionId = file.SessionId
                    JOIN Sessions AS saved ON saved.SessionId = root.SessionId
                    WHERE file.TenantId = TenantApprovalRequests.TenantId AND file.SessionId = TenantApprovalRequests.SessionId
                    AND file.ArtifactId = TenantApprovalRequests.ResourceId AND saved.DurablyDeletedAtUtc IS NULL))) AND
            (Permission <> 'artifacts.create' OR (
                ({artifactReadJson} = 'null' OR SessionId IN (SELECT value FROM json_each({artifactReadJson})))
                AND ({artifactCreateJson} = 'null' OR SessionId IN (SELECT value FROM json_each({artifactCreateJson})))
                AND (json_type(PayloadJson, '$.Operation.ArtifactPublication.Source') IS NOT 'object' OR (
                    ((json_type(PayloadJson, '$.Operation.ArtifactPublication.Source.AttachmentId') IS 'text'
                        AND ({attachmentJson} = 'null' OR json_extract(PayloadJson, '$.Operation.ArtifactPublication.Source.AttachmentId') IN (SELECT value FROM json_each({attachmentJson})))
                        AND ({attachmentSessionJson} = 'null' OR SessionId IN (SELECT value FROM json_each({attachmentSessionJson})))
                        AND EXISTS (SELECT 1 FROM Attachments AS file WHERE file.TenantId = TenantApprovalRequests.TenantId
                            AND file.SessionId = TenantApprovalRequests.SessionId AND file.State = 'Bound' AND file.ContentDeletedAtUtc IS NULL
                            AND file.AttachmentId = json_extract(TenantApprovalRequests.PayloadJson, '$.Operation.ArtifactPublication.Source.AttachmentId')
                            AND file.EntryId = json_extract(TenantApprovalRequests.PayloadJson, '$.Operation.ArtifactPublication.Source.EntryId')))
                    OR (json_extract(PayloadJson, '$.Operation.ArtifactPublication.Source.AttachmentId') IS NULL
                        AND ({workspaceSessionsJson} = 'null' OR SessionId IN (SELECT value FROM json_each({workspaceSessionsJson})))
                    AND ((json_extract(PayloadJson, '$.Operation.ArtifactPublication.Source.ResourceKey') IS NULL
                        AND ({workspaceJson} = 'null' OR CASE WHEN json_extract(PayloadJson, '$.Operation.ArtifactPublication.Source.Working') = 1
                            THEN 'working:' || SessionId ELSE 'home:' || InstanceId END IN (SELECT value FROM json_each({workspaceJson}))))
                        OR (json_type(PayloadJson, '$.Operation.ArtifactPublication.Source.ResourceKey') IS 'text'
                            AND (InstanceId || ':' || json_extract(PayloadJson, '$.Operation.ArtifactPublication.Source.ResourceKey')) IN (SELECT value FROM json_each({ownedResourceJson}))
                            AND ({resourceJson} = 'null' OR (InstanceId || ':' || json_extract(PayloadJson, '$.Operation.ArtifactPublication.Source.ResourceKey')) IN (SELECT value FROM json_each({resourceJson})))))
                    AND (json_extract(PayloadJson, '$.Operation.ArtifactPublication.Source.Working') = 1
                        OR {homeAgentsJson} = 'null' OR InstanceId IN (SELECT value FROM json_each({homeAgentsJson})))))))
                AND EXISTS (SELECT 1 FROM TenantSessions AS root JOIN Sessions AS saved ON saved.SessionId = root.SessionId
                    WHERE root.TenantId = TenantApprovalRequests.TenantId AND root.SessionId = TenantApprovalRequests.SessionId
                    AND root.AgentInstanceId = TenantApprovalRequests.InstanceId AND root.ExecutorPrincipalId = TenantApprovalRequests.ExecutorPrincipalId AND saved.DurablyDeletedAtUtc IS NULL))) AND
            (json_type(PayloadJson, '$.Operation.CredentialRead') IS NOT 'object' OR (
                ({metadataSessionJson} = 'null' OR SessionId IN (SELECT value FROM json_each({metadataSessionJson})))
                AND ({metadataReadJson} = 'null' OR NOT EXISTS (
                    SELECT 1 FROM json_each(json_extract(PayloadJson, '$.Operation.CredentialRead.ReferencesJson')) AS credential
                    WHERE json_extract(credential.value, '$.CredentialId') NOT IN (SELECT value FROM json_each({metadataReadJson})))))) AND
            ({readableJson} = 'null' OR Permission <> 'sandbox.read' OR NOT EXISTS (
                SELECT 1 FROM json_each(json_extract(PayloadJson, '$.Operation.SandboxRead.ProfileReferencesJson')) AS profile
                WHERE json_extract(profile.value, '$.Id') NOT IN (SELECT value FROM json_each({readableJson}))))
            AND (json_type(PayloadJson, '$.Operation.BrowserCommand') IS NOT 'object' OR (
                ({browserReadJson} = 'null' OR InstanceId IN (SELECT value FROM json_each({browserReadJson})))
                AND (json_extract(PayloadJson, '$.Operation.BrowserCommand.ReadOnly') = 1 OR {browserUseJson} = 'null' OR InstanceId IN (SELECT value FROM json_each({browserUseJson})))
                AND ({browserSessionJson} = 'null' OR SessionId IN (SELECT value FROM json_each({browserSessionJson}))))
                AND (json_type(PayloadJson, '$.Operation.BrowserCommand.Artifacts') IS NOT 'object' OR (
                    ({artifactCreateJson} = 'null' OR SessionId IN (SELECT value FROM json_each({artifactCreateJson})))
                    AND EXISTS (SELECT 1 FROM TenantSessions root JOIN Sessions saved ON root.SessionId = saved.SessionId
                        WHERE root.TenantId = TenantApprovalRequests.TenantId AND root.SessionId = TenantApprovalRequests.SessionId
                            AND saved.DurablyDeletedAtUtc IS NULL)))
                AND NOT EXISTS (SELECT 1 FROM json_each(json_extract(PayloadJson, '$.Operation.BrowserCommand.ArtifactSourcesJson')) member
                    WHERE ({artifactVerifyJson} <> 'null' AND json_extract(member.value, '$.ArtifactId') NOT IN (SELECT value FROM json_each({artifactVerifyJson})))
                        OR NOT EXISTS (SELECT 1 FROM Artifacts a WHERE a.TenantId = TenantApprovalRequests.TenantId
                            AND a.SessionId = TenantApprovalRequests.SessionId AND a.ArtifactId = json_extract(member.value, '$.ArtifactId')
                            AND a.DisplayName = json_extract(member.value, '$.DisplayName') AND a.ContentType = json_extract(member.value, '$.ContentType')
                            AND a.ByteSize = json_extract(member.value, '$.ByteSize') AND a.Sha256Hex = json_extract(member.value, '$.Sha256Hex')
                            AND EXISTS (SELECT 1 FROM TenantSessions root JOIN Sessions saved ON saved.SessionId = root.SessionId
                                WHERE root.TenantId = a.TenantId AND root.SessionId = a.SessionId AND saved.DurablyDeletedAtUtc IS NULL)
                            AND ((a.AgentRunId IS NOT NULL AND EXISTS (SELECT 1 FROM AgentRuns r WHERE r.TenantId = a.TenantId
                                AND r.SessionId = a.SessionId AND r.AgentRunId = a.AgentRunId)) OR (a.AgentRunId IS NULL
                                AND a.CreatedByPrincipalId IS NOT NULL AND a.SourceAttachmentId IS NOT NULL AND a.WorkspaceLogicalPath IS NOT NULL
                                AND EXISTS (SELECT 1 FROM Attachments source WHERE source.TenantId = a.TenantId
                                    AND source.SessionId = a.SessionId AND source.AttachmentId = a.SourceAttachmentId))))))
            """);
        var query = source.AsNoTracking().Where(r => r.TenantId == tenantId);
        if (page.SessionId is { } session)
        {
            var id = session.ToString("D");
            query = query.Where(r => r.SessionId == id || db.Set<TenantApprovalRecord>().FromSqlInterpolated($"""
                SELECT * FROM TenantApprovalRequests WHERE json_extract(PayloadJson, '$.Operation.BackgroundReport.ParentSessionId') = {id} OR json_extract(PayloadJson, '$.Operation.ReportSourceRead.ParentSessionId') = {id}
                """).Any(parent => parent.RequestId == r.RequestId));
        }
        if (allowedRequests is not null) query = query.Where(r => allowedRequests.Contains(r.RequestId));
        if (allowedInstances is not null)
        { var ids = allowedInstances.Select(id => id.ToString("D")).ToArray(); query = query.Where(r => r.Permission != "agents.update" || ids.Contains(r.InstanceId)); }
        if (sandboxVisibility?.Instances is not null)
        { var ids = sandboxVisibility.Instances.Select(id => id.ToString("D")).ToArray(); query = query.Where(r => r.Permission != "sandbox.execute" || ids.Contains(r.InstanceId)); }
        if (sandboxVisibility?.ControlInstances is { } controls)
        { var ids = controls.Select(id => id.ToString("D")).ToArray(); query = query.Where(r => r.Permission != "sandbox.manage" || ids.Contains(r.InstanceId)); }
        if (sandboxVisibility?.ReadInstances is { } readInstances)
        { var ids = readInstances.Select(id => id.ToString("D")).ToArray(); query = query.Where(r => r.Permission != "sandbox.read" || ids.Contains(r.InstanceId)); }
        if (sandboxVisibility?.Profiles is { } profiles) query = query.Where(r => (r.Permission != "sandbox.execute" && r.Permission != "sandbox.manage") || profiles.Contains(r.SandboxProfileId!));
        if (sandboxVisibility?.Sessions is not null)
        { var ids = sandboxVisibility.Sessions.Select(id => id.ToString("D")).ToArray(); query = query.Where(r => (r.Permission != "sandbox.execute" && r.Permission != "sandbox.manage" && r.Permission != "sandbox.read") || ids.Contains(r.SessionId)); }
        if (allowedCredentials is not null)
        { var ids = allowedCredentials.Select(id => id.ToString("D")).ToArray(); query = query.Where(r => r.Permission == "credentials.read" || r.ResourceType != "credential" || ids.Contains(r.ResourceId)); }
        if (reportVisibility?.Sessions is not null || reportVisibility?.Runs is not null)
        {
            var visibleAccountSources = db.Set<TenantApprovalRecord>().FromSqlInterpolated($"""
                SELECT * FROM TenantApprovalRequests WHERE json_type(PayloadJson, '$.Operation.AccountRead.Source') IS NOT 'object' OR
                  ((json_type({reportSessionsJson}) = 'null' OR json_extract(PayloadJson, '$.Operation.AccountRead.Source.SessionId') IN (SELECT value FROM json_each({reportSessionsJson})))
                   AND (json_type({reportRunsJson}) = 'null' OR json_extract(PayloadJson, '$.Operation.AccountRead.Source.RunId') IN (SELECT value FROM json_each({reportRunsJson}))))
                """);
            query = query.Where(r => visibleAccountSources.Any(a => a.RequestId == r.RequestId));
        }
        if (credentialReadVisibility is not null)
        {
            var readSessions = System.Text.Json.JsonSerializer.Serialize(credentialReadVisibility.Sessions?.Select(id => id.ToString("D")).ToArray());
            var emails = System.Text.Json.JsonSerializer.Serialize(credentialReadVisibility.EmailCredentials?.Select(id => id.ToString("D")).ToArray());
            var drives = System.Text.Json.JsonSerializer.Serialize(credentialReadVisibility.DriveCredentials?.Select(id => id.ToString("D")).ToArray());
            var emailPurpose = (int)AgentCore.Domain.Credentials.CredentialPurpose.EmailAccount;
            var drivePurpose = (int)AgentCore.Domain.Credentials.CredentialPurpose.GoogleDriveAccount;
            var visibleAccounts = db.Set<TenantApprovalRecord>().FromSqlInterpolated($"""
                SELECT * FROM TenantApprovalRequests WHERE json_type(PayloadJson, '$.Operation.AccountRead') IS NOT 'object' OR
                    ((json_type({readSessions}) = 'null' OR SessionId IN (SELECT value FROM json_each({readSessions})))
                     AND ((json_extract(PayloadJson, '$.Operation.AccountRead.Purpose') = {emailPurpose}
                         AND (json_type({emails}) = 'null' OR CredentialId IN (SELECT value FROM json_each({emails}))))
                       OR (json_extract(PayloadJson, '$.Operation.AccountRead.Purpose') = {drivePurpose}
                         AND (json_type({drives}) = 'null' OR CredentialId IN (SELECT value FROM json_each({drives}))))))
                """);
            query = query.Where(r => visibleAccounts.Any(a => a.RequestId == r.RequestId));
            var visibleMetadata = db.Set<TenantApprovalRecord>().FromSqlInterpolated($"""
                SELECT * FROM TenantApprovalRequests WHERE json_type(PayloadJson, '$.Operation.CredentialRead') IS NOT 'object' OR
                    ((json_extract(json_extract(PayloadJson, '$.Operation.CredentialRead.ArgumentsJson'), '$.family') IS NOT 'email'
                      OR json_type({emails}) = 'null' OR NOT EXISTS (
                        SELECT 1 FROM json_each(json_extract(PayloadJson, '$.Operation.CredentialRead.ReferencesJson')) member
                        WHERE json_extract(member.value, '$.CredentialId') NOT IN (SELECT value FROM json_each({emails}))))
                     AND (json_extract(json_extract(PayloadJson, '$.Operation.CredentialRead.ArgumentsJson'), '$.family') IS NOT 'drive'
                      OR json_type({drives}) = 'null' OR NOT EXISTS (
                        SELECT 1 FROM json_each(json_extract(PayloadJson, '$.Operation.CredentialRead.ReferencesJson')) member
                        WHERE json_extract(member.value, '$.CredentialId') NOT IN (SELECT value FROM json_each({drives})))) )
                """);
            query = query.Where(r => visibleMetadata.Any(a => a.RequestId == r.RequestId));

        }
        if (credentialReadVisibility is not null)
        {
            var actionUses = System.Text.Json.JsonSerializer.Serialize(allowedCredentials?.Select(id => id.ToString("D")).ToArray());
            var actionReads = System.Text.Json.JsonSerializer.Serialize(credentialReadVisibility.Credentials?.Select(id => id.ToString("D")).ToArray());
            var actionSessions = System.Text.Json.JsonSerializer.Serialize(credentialReadVisibility.Sessions?.Select(id => id.ToString("D")).ToArray());
            var actionDriveReads = System.Text.Json.JsonSerializer.Serialize(credentialReadVisibility.DriveCredentials?.Select(id => id.ToString("D")).ToArray());
            var actionDrafts = System.Text.Json.JsonSerializer.Serialize(credentialReadVisibility.EmailDraftCredentials?.Select(id => id.ToString("D")).ToArray());
            var actionSends = System.Text.Json.JsonSerializer.Serialize(credentialReadVisibility.EmailSendCredentials?.Select(id => id.ToString("D")).ToArray());
            var actionWrites = System.Text.Json.JsonSerializer.Serialize(credentialReadVisibility.DriveWriteCredentials?.Select(id => id.ToString("D")).ToArray());
            var actionDeletes = System.Text.Json.JsonSerializer.Serialize(credentialReadVisibility.DriveDeleteCredentials?.Select(id => id.ToString("D")).ToArray());
            var actionQuery = db.Set<TenantApprovalRecord>().FromSqlInterpolated($"""
                SELECT * FROM TenantApprovalRequests WHERE json_type(PayloadJson, '$.Operation.AccountAction') IS NOT 'object' OR
                  ((json_type({actionUses}) = 'null' OR CredentialId IN (SELECT value FROM json_each({actionUses})))
                   AND (json_type({actionReads}) = 'null' OR CredentialId IN (SELECT value FROM json_each({actionReads})))
                   AND (json_type({actionSessions}) = 'null' OR SessionId IN (SELECT value FROM json_each({actionSessions})))
                   AND (json_extract(PayloadJson, '$.Operation.AccountAction.Purpose') <> 2 OR json_type({actionDriveReads}) = 'null' OR CredentialId IN (SELECT value FROM json_each({actionDriveReads})))
                   AND ((Permission = 'email.draft' AND (json_type({actionDrafts}) = 'null' OR CredentialId IN (SELECT value FROM json_each({actionDrafts}))))
                     OR (Permission = 'email.send' AND (json_type({actionSends}) = 'null' OR CredentialId IN (SELECT value FROM json_each({actionSends}))))
                     OR (Permission = 'drive.write' AND (json_type({actionWrites}) = 'null' OR CredentialId IN (SELECT value FROM json_each({actionWrites}))))
                     OR (Permission = 'drive.delete' AND (json_type({actionDeletes}) = 'null' OR CredentialId IN (SELECT value FROM json_each({actionDeletes}))))
                     OR Permission = 'workspaces.write')
                   AND (json_extract(PayloadJson, '$.Operation.AccountAction.ToolName') NOT IN ('drive.upload','drive.download') OR
                     ((substr(json_extract(json_extract(PayloadJson, '$.Operation.AccountAction.ArgumentsJson'),'$.path'),1,6) <> '/home/'
                         OR json_type({mutationAgentsJson}) = 'null' OR InstanceId IN (SELECT value FROM json_each({mutationAgentsJson})))
                      AND (json_extract(PayloadJson, '$.Operation.AccountAction.ToolName') <> 'drive.upload' OR json_type({mutationReadJson}) = 'null'
                         OR (CASE WHEN substr(json_extract(json_extract(PayloadJson, '$.Operation.AccountAction.ArgumentsJson'),'$.path'),1,9) = '/working/' THEN 'working:' || SessionId ELSE 'home:' || InstanceId END) IN (SELECT value FROM json_each({mutationReadJson})))
                      AND (json_extract(PayloadJson, '$.Operation.AccountAction.ToolName') <> 'drive.download' OR json_type({mutationWriteJson}) = 'null'
                         OR ResourceId IN (SELECT value FROM json_each({mutationWriteJson}))))))
                """);
            query = query.Where(r => actionQuery.Any(a => a.RequestId == r.RequestId));
        }
        if (automationVisibility is { } visibility)
        {
            var webhookJson = System.Text.Json.JsonSerializer.Serialize(visibility.WebhookSources?.Select(id => id.ToString("D")).ToArray());
            var coreJson = System.Text.Json.JsonSerializer.Serialize(visibility.CoreEvents?.Select(id => id.ToString("D")).ToArray());
            var sourceReadable = db.Set<TenantApprovalRecord>().FromSqlInterpolated($"""
                SELECT * FROM TenantApprovalRequests WHERE AutomationId IS NULL
                OR json_extract(PayloadJson, '$.Operation.AutomationAdmission.EventSource') IS NULL
                OR (json_extract(PayloadJson, '$.Operation.AutomationAdmission.EventSource.SourceKind') = 1
                    AND (json_type({webhookJson}) = 'null' OR EXISTS (SELECT 1 FROM json_each({webhookJson}) allowed
                        WHERE allowed.value = json_extract(PayloadJson, '$.Operation.AutomationAdmission.EventSource.ResourceId'))))
                OR (json_extract(PayloadJson, '$.Operation.AutomationAdmission.EventSource.SourceKind') = 3
                    AND (json_type({coreJson}) = 'null' OR NOT EXISTS (
                        SELECT 1 FROM json_each(PayloadJson, '$.Operation.AutomationAdmission.EventSource.EventIds') source
                        WHERE NOT EXISTS (SELECT 1 FROM json_each({coreJson}) allowed WHERE allowed.value = source.value))))
                """);
            query = query.Where(r => sourceReadable.Any(owned => owned.RequestId == r.RequestId));
            var agentIds = visibility.Agents?.Select(id => id.ToString("D")).ToArray();
            var readIds = visibility.ReadAutomations?.Select(id => id.ToString("D")).ToArray();
            var runIds = visibility.RunAutomations?.Select(id => id.ToString("D")).ToArray();
            query = query.Where(r => r.AutomationId == null || visibility.CanCreate && (agentIds == null || agentIds.Contains(r.InstanceId))
                && (readIds == null || readIds.Contains(r.AutomationId)) && (runIds == null || runIds.Contains(r.AutomationId))
                && db.Automations.IgnoreQueryFilters().Any(a => a.AutomationId == r.AutomationId && a.TenantId == r.TenantId
                    && a.AgentInstanceId == r.InstanceId && a.ExecutorPrincipalId == r.ExecutorPrincipalId));
        }
        if (page.Status is { } status) query = query.Where(r => r.Status == (int)status);
        if (page.Kind is { } kind) query = query.Where(r => r.Kind == (int)kind);
        var total = await query.CountAsync(ct).ConfigureAwait(false);
        var rows = await query.OrderByDescending(r => r.CreatedAt).ThenBy(r => r.RequestId).Skip(page.Page * page.PageSize).Take(page.PageSize).ToArrayAsync(ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
        return new(rows.Select(r => r.Domain()).ToArray(), total, page.Page, page.PageSize);
    }
    public async ValueTask<TenantOperationGrant?> FindOperationGrantAsync(Guid tenantId, Guid requestId, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct).ConfigureAwait(false);
        return await db.Set<TenantOperationGrant>().AsNoTracking().SingleOrDefaultAsync(r => r.TenantId == tenantId && r.RequestId == requestId, ct).ConfigureAwait(false);
    }

    public async ValueTask<TenantApprovalRequest?> TryCreateApprovalAsync(TenantSecurityState next, long expectedSecurityRevision,
        TenantApprovalRequest request, long expectedRunRevision, Guid? generation, AuthorizationAudit audit, CancellationToken ct = default)
    {
        if (request.TenantId != next.Tenant.TenantId || request.Status != TenantApprovalStatus.Pending || request.Revision != 1
            || audit.TenantId != request.TenantId || audit.ActorPrincipalId != request.ExecutorPrincipalId || audit.Action != "approvals.request"
            || audit.Target != request.RequestId.ToString("D")) throw new ArgumentException("Invalid approval request transaction.");
        TenantApprovalRequest? result = null;
        var written = await WriteTenantResourceAsync(next, expectedSecurityRevision, audit, async db =>
        {
            var duplicate = await RootApproval(db, request.TenantId, request.RootActivationId.ToString("D"), request.ExecutorPrincipalId, request.Operation).FirstOrDefaultAsync(ct).ConfigureAwait(false);
            if (duplicate is not null) { result = duplicate.Domain(); return false; }
            if (request.ExpiresAt <= ApprovalNow) throw new SecurityConflictException("approval_expired");
            var row = await OwnedRuns(db, request.TenantId).SingleOrDefaultAsync(r => r.AgentRunId == request.RunId.ToString("D"), ct).ConfigureAwait(false);
            var target = await OwnedAgents(db, request.TenantId).SingleOrDefaultAsync(r => r.Instance.InstanceId == request.Operation.InstanceId.ToString("D"), ct).ConfigureAwait(false);
            if (row is null || target is null || row.Revision != expectedRunRevision) return false;
            var run = AgentRunStoreMapping.ToDomain(row); var agent = Owned(target);
            TenantApprovalRules.Validate(request, run, agent);
            if (request.Operation.BackgroundReport is not null) await RequireBackgroundReportOperation(db, request, run, agent, ct).ConfigureAwait(false);
            if (request.Operation.BackgroundStart is not null) await RequireBackgroundCreationOperation(db, request, run, agent, ct).ConfigureAwait(false);
            if (request.Operation.ArtifactPublication is not null) await RequireArtifactOperation(db, request, run, agent, ct).ConfigureAwait(false);
            if (request.Operation.BrowserCommand is not null) await RequireBrowserOperation(db, request, run, agent, ct).ConfigureAwait(false);
            if (request.Operation.WorkspaceRead is not null) await RequireWorkspaceReadOperation(db, request, run, agent, ct).ConfigureAwait(false);
            if (request.Operation.WorkspaceMutation is not null) await RequireWorkspaceMutationOperation(db, request, run, agent, ct).ConfigureAwait(false);
            if (request.Operation.AttachmentRead is not null) await RequireAttachmentReadOperation(db, request, run, agent, ct).ConfigureAwait(false);
            if (request.Operation.ReportSourceRead is not null) await RequireReportSourceReadOperation(db, request, ct, pending: true).ConfigureAwait(false);
            if (request.Operation.CompletionRead is not null) await RequireCompletionReadOperation(db, request, run, agent, ct).ConfigureAwait(false);
            if (request.Operation.ArtifactVerification is not null) await RequireArtifactVerificationOperation(db, request, run, agent, ct).ConfigureAwait(false);
            if (request.Operation.AccountAction is not null) await RequireAccountActionOperation(db, request, run, agent, ct).ConfigureAwait(false);
        if (request.Operation.AccountRead is not null) await RequireAccountReadOperation(db, request, run, agent, ct).ConfigureAwait(false);
            if (request.Operation.CredentialRead is not null) await RequireCredentialReadOperation(db, request, run, agent, ct).ConfigureAwait(false);
            if (request.Operation.SandboxCommand is not null) await RequireSandboxOperation(db, request, run, agent, ct).ConfigureAwait(false);
            if (request.Operation.SandboxControl is not null) await RequireSandboxControlOperation(db, request, run, agent, ct).ConfigureAwait(false);
            if (request.Operation.SandboxRead is not null) await RequireSandboxReadOperation(db, request, run, agent, ct).ConfigureAwait(false);
            if (request.Operation.CredentialCheck is not null)
                TenantCredentialOperationRules.RequireRequest(request, run, agent, await CredentialOperationTarget(db, request, ct).ConfigureAwait(false));
            var state = (await ReadTenantAsync(db, request.TenantId, ct).ConfigureAwait(false))!;
            TenantSessionRules.RequireExecutor(state, agent);
            if (request.OriginalInitiatorPrincipalId is { } requester) await ValidateTenantActorAsync(db, state, requester, ct).ConfigureAwait(false);
            var cutoff = (ApprovalNow - TenantApprovalLimits.Window).ToUnixTimeMilliseconds();
            var recent = db.Set<TenantApprovalRecord>().Where(r => r.TenantId == request.TenantId
                && (r.Status == (int)TenantApprovalStatus.Pending || r.CreatedAt >= cutoff));
            var counts = await recent.GroupBy(r => r.TenantId).Select(g => new { Tenant = g.Count(),
                Executor = g.Count(r => r.ExecutorPrincipalId == request.ExecutorPrincipalId),
                Requester = g.Count(r => r.OriginalInitiatorPrincipalId == request.OriginalInitiatorPrincipalId) }).SingleOrDefaultAsync(ct).ConfigureAwait(false);
            var operationCount = await SemanticApprovals(db, request.Operation).CountAsync(r => r.TenantId == request.TenantId
                && (r.Status == (int)TenantApprovalStatus.Pending || r.CreatedAt >= cutoff), ct).ConfigureAwait(false);
            var rootCount = await db.Set<TenantApprovalRecord>().CountAsync(r => r.TenantId == request.TenantId
                && r.RootActivationId == request.RootActivationId.ToString("D"), ct).ConfigureAwait(false);
            TenantApprovalLimits.Require(counts?.Tenant ?? 0, counts?.Executor ?? 0, counts?.Requester ?? 0, operationCount, rootCount, request.OriginalInitiatorPrincipalId is not null);
            if (run.Status == AgentRunStatus.Running && run.Claim!.LeaseExpiresAtUtc <= ApprovalNow) throw new SecurityConflictException("execution_claim_expired");
            if ((request.Operation.BackgroundReport is null && request.Operation.ReportSourceRead is null))
                AgentRunStoreMapping.Apply(row, run.WaitForDurableApproval(expectedRunRevision, request.RequestId, generation, request.CreatedAt)); db.Add(TenantApprovalRecord.From(request)); result = request; return true;
        }, ct).ConfigureAwait(false);
        return written || result is not null && result.RequestId != request.RequestId ? result : null;
    }

    public ValueTask<bool> TryDecideApprovalAsync(TenantSecurityState next, long expectedSecurityRevision, TenantApprovalRequest request,
        long expectedRequestRevision, TenantApprovalDecision decision, TenantOperationGrant? grant, ApprovalProcessedSignal signal,
        TenantSessionInitiator human, AuthorizationAudit audit, CancellationToken ct = default) => WriteTenantResourceAsync(next, expectedSecurityRevision, audit, async db =>
    {
        if (request.TenantId != next.Tenant.TenantId) throw new ArgumentException("Invalid approval decision transaction.");
        await RequireHumanSessionAsync(db, request.TenantId, human, ct).ConfigureAwait(false);
        var row = await db.Set<TenantApprovalRecord>().SingleOrDefaultAsync(r => r.TenantId == request.TenantId && r.RequestId == request.RequestId, ct).ConfigureAwait(false);
        if (row is null || row.Revision != expectedRequestRevision || row.Status != (int)TenantApprovalStatus.Pending) return false;
        TenantApprovalRules.ValidateDecision(row.Domain(), request, expectedRequestRevision, decision, grant, signal, human, audit);
        if (request.Operation.AutomationAdmission is not null)
        {
            if (request.ExpiresAt <= ApprovalNow) throw new SecurityConflictException("approval_expired");
            if (request.Status != TenantApprovalStatus.Cancelled)
            {
                var admissionState = (await ReadTenantAsync(db, request.TenantId, ct).ConfigureAwait(false))!;
                _ = await RequireAutomationAdmissionOperation(db, request, admissionState, ct).ConfigureAwait(false);
                await TenantAutomationAdmissionApprovalRules.ReviewerAsync(admissionState, request, human.PrincipalId, new IamAuthorizer(this), ct).ConfigureAwait(false);
            }
            db.Entry(row).CurrentValues.SetValues(TenantApprovalRecord.From(request)); db.Add(decision); db.Add(signal); if (grant is not null) db.Add(grant); return true;
        }
        var runRow = await OwnedRuns(db, request.TenantId).SingleOrDefaultAsync(r => r.AgentRunId == request.RunId.ToString("D"), ct).ConfigureAwait(false);
        var target = await OwnedAgents(db, request.TenantId).SingleOrDefaultAsync(r => r.Instance.InstanceId == request.Operation.InstanceId.ToString("D"), ct).ConfigureAwait(false);
        if (runRow is null || target is null) return false;
        var run = AgentRunStoreMapping.ToDomain(runRow); var agent = Owned(target);
        if ((request.Operation.BackgroundReport is null && request.Operation.ReportSourceRead is null) && (run.ApprovalRequestId != request.RequestId || run.Status != AgentRunStatus.WaitingForApproval)) return false;
        TenantApprovalRules.Validate(request, run, agent, request.Status != TenantApprovalStatus.Cancelled);
        if (request.Operation.CredentialCheck is not null && request.Status != TenantApprovalStatus.Cancelled)
            TenantCredentialOperationRules.RequireRequest(request, run, agent, await CredentialOperationTarget(db, request, ct).ConfigureAwait(false));
        if (request.Operation.SandboxCommand is not null && request.Status != TenantApprovalStatus.Cancelled)
            await RequireSandboxOperation(db, request, run, agent, ct).ConfigureAwait(false);
        var state = (await ReadTenantAsync(db, request.TenantId, ct).ConfigureAwait(false))!;
        if (request.Status != TenantApprovalStatus.Cancelled)
        {
            if (request.Operation.ReportSourceRead is not null)
            {
                await RequireReportSourceReadOperation(db, request, ct, pending: true).ConfigureAwait(false);
                await TenantReportSourceReadRules.ReviewerAsync(state, request, decision.HumanPrincipalId, new IamAuthorizer(this), ct).ConfigureAwait(false);
            }
            if (request.Operation.BackgroundReport is not null)
            {
                await RequireBackgroundReportOperation(db, request, run, agent, ct).ConfigureAwait(false);
                await TenantBackgroundReportRules.ReviewerAsync(state, request, decision.HumanPrincipalId, new IamAuthorizer(this), ct).ConfigureAwait(false);
            }
            if (request.Operation.BackgroundStart is not null)
            {
                await RequireBackgroundCreationOperation(db, request, run, agent, ct).ConfigureAwait(false);
                await TenantBackgroundCreationRules.ReviewerAsync(state, request, decision.HumanPrincipalId, new IamAuthorizer(this), ct).ConfigureAwait(false);
            }
            if (request.Operation.ArtifactPublication is not null)
            {
                await RequireArtifactOperation(db, request, run, agent, ct).ConfigureAwait(false);
                await TenantArtifactReviewRules.ReviewerAsync(state, request, decision.HumanPrincipalId, new IamAuthorizer(this), ct, ownedSources: true).ConfigureAwait(false);
            }
            if (request.Operation.SandboxControl is not null)
            {
                await RequireSandboxControlOperation(db, request, run, agent, ct).ConfigureAwait(false);
                var session = (await ReadSandboxSession(db, request.TenantId, request.SessionId, ct).ConfigureAwait(false))!;
                await TenantSandboxControlOperationRules.RequirePermissionsAsync(state, session.Owner, request.Operation.SandboxControl.ProfileId,
                    decision.HumanPrincipalId, new IamAuthorizer(this), ct).ConfigureAwait(false);
            }
            if (request.Operation.SandboxRead is not null)
            {
                var owner = await RequireSandboxReadOperation(db, request, run, agent, ct).ConfigureAwait(false);
                await TenantSandboxReadOperationRules.RequireReviewerAsync(state, owner, request.Operation.SandboxRead, request.OriginalInitiatorPrincipalId,
                    decision.HumanPrincipalId, new IamAuthorizer(this), ct).ConfigureAwait(false);
            }
            if (request.Operation.WorkspaceMutation is not null)
            {
                await RequireWorkspaceMutationOperation(db, request, run, agent, ct).ConfigureAwait(false);
                await TenantWorkspaceMutationOperationRules.ReviewerAsync(state, request, decision.HumanPrincipalId, new IamAuthorizer(this), ct).ConfigureAwait(false);
            }
            if (request.Operation.WorkspaceRead is not null)
            {
                await RequireWorkspaceReadOperation(db, request, run, agent, ct).ConfigureAwait(false);
                await TenantWorkspaceReadOperationRules.ReviewerAsync(state, agent.Instance.InstanceId, request.Operation.WorkspaceRead, request.OriginalInitiatorPrincipalId,
                    decision.HumanPrincipalId, new IamAuthorizer(this), ct).ConfigureAwait(false);
            }
            if (request.Operation.AttachmentRead is not null)
            {
                await RequireAttachmentReadOperation(db, request, run, agent, ct).ConfigureAwait(false);
                await TenantAttachmentReadRules.ReviewerAsync(state, request.Operation.AttachmentRead, request.OriginalInitiatorPrincipalId, decision.HumanPrincipalId, new IamAuthorizer(this), ct).ConfigureAwait(false);
            }
            if (request.Operation.CompletionRead is not null)
            {
                await RequireCompletionReadOperation(db, request, run, agent, ct).ConfigureAwait(false);
                await TenantCompletionReadRules.ReviewerAsync(state, request.Operation.CompletionRead, request.OriginalInitiatorPrincipalId, decision.HumanPrincipalId, new IamAuthorizer(this), ct).ConfigureAwait(false);
            }
            if (request.Operation.ArtifactVerification is not null)
            {
                await RequireArtifactVerificationOperation(db, request, run, agent, ct).ConfigureAwait(false);
                await TenantArtifactVerificationRules.ReviewerAsync(state, request.Operation.ArtifactVerification, request.OriginalInitiatorPrincipalId, decision.HumanPrincipalId, new IamAuthorizer(this), ct).ConfigureAwait(false);
            }
            if (request.Operation.AccountAction is not null)
            {
                await RequireAccountActionOperation(db, request, run, agent, ct).ConfigureAwait(false);
                await TenantAccountActionRules.ReviewerAsync(state, request, decision.HumanPrincipalId, new IamAuthorizer(this), ct).ConfigureAwait(false);
            }
            if (request.Operation.AccountRead is not null)
            {
                await RequireAccountReadOperation(db, request, run, agent, ct).ConfigureAwait(false);
                await TenantAccountReadRules.ReviewerAsync(state, request, decision.HumanPrincipalId, new IamAuthorizer(this), ct).ConfigureAwait(false);
            }
            if (request.Operation.CredentialRead is not null)
            {
                await RequireCredentialReadOperation(db, request, run, agent, ct).ConfigureAwait(false);
                await TenantCredentialReadOperationRules.ReviewerAsync(state, request.Operation.CredentialRead, request.OriginalInitiatorPrincipalId,
                    decision.HumanPrincipalId, new IamAuthorizer(this), ct).ConfigureAwait(false);
            }
            if (request.Operation.BrowserCommand is { } browser)
            {
                await RequireBrowserOperation(db, request, run, agent, ct).ConfigureAwait(false);
                await TenantBrowserOperationRules.RequireReviewerAsync(state, agent.Instance.InstanceId, browser, request.OriginalInitiatorPrincipalId,
                    decision.HumanPrincipalId, new IamAuthorizer(this), ct, request.Kind).ConfigureAwait(false);
            }
            TenantSessionRules.RequireExecutor(state, agent); await ValidateTenantActorAsync(db, state, request.ExecutorPrincipalId, ct).ConfigureAwait(false);
            if (request.OriginalInitiatorPrincipalId is { } requester) await ValidateTenantActorAsync(db, state, requester, ct).ConfigureAwait(false);
        }
        if (request.ExpiresAt <= ApprovalNow) throw new SecurityConflictException("approval_expired");
        db.Entry(row).CurrentValues.SetValues(TenantApprovalRecord.From(request)); db.Add(decision); db.Add(signal);
        if (grant is not null) db.Add(grant); return true;
    }, ct);

    public async ValueTask<IReadOnlyList<ApprovalProcessedSignal>> PendingApprovalSignalsAsync(int limit, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct).ConfigureAwait(false);
        return await db.Set<ApprovalProcessedSignal>().AsNoTracking().Where(s => s.ProcessedAt == null).OrderBy(s => s.CreatedAt).ThenBy(s => s.SignalId).Take(Math.Clamp(limit, 1, 100)).ToArrayAsync(ct).ConfigureAwait(false);
    }
    public async ValueTask<bool> TryResumeApprovalAsync(ApprovalProcessedSignal signal, DateTimeOffset at, CancellationToken ct = default, TenantApprovalContinuationIds? continuation = null)
    {
        await using var db = await contexts.CreateDbContextAsync(ct).ConfigureAwait(false);
        await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        var saved = await db.Set<ApprovalProcessedSignal>().SingleOrDefaultAsync(s => s.SignalId == signal.SignalId, ct).ConfigureAwait(false);
        if (saved != signal || saved is null || saved.ProcessedAt is not null || at < saved.CreatedAt) return false;
        var request = await db.Set<TenantApprovalRecord>().SingleOrDefaultAsync(r => r.TenantId == signal.TenantId && r.RequestId == signal.RequestId, ct).ConfigureAwait(false);
        if (request is null || request.Revision != signal.DecisionRevision || request.Status == (int)TenantApprovalStatus.Pending || request.RunId != signal.RunId.ToString("D")) return false;
        var row = await OwnedRuns(db, signal.TenantId).SingleOrDefaultAsync(r => r.AgentRunId == request.RunId, ct).ConfigureAwait(false);
        if (row is null) return false;
        var run = AgentRunStoreMapping.ToDomain(row);
        if (run.ApprovalRequestId != signal.RequestId) return false;
        if (!run.IsTerminal)
        {
            if (run.Status != AgentRunStatus.WaitingForApproval) return false;
            if (run.Checkpoint is not null)
            {
                if (continuation is null) return false;
                var next = TenantApprovalContinuationRules.Create(run, request.Domain(), signal, continuation, at);
                var session = await db.Sessions.AsNoTracking().SingleAsync(s => s.SessionId == row.SessionId, ct).ConfigureAwait(false);
                db.Activations.Add(AgentRunStoreMapping.ToActivationRecord(SqliteMemoryStore.ToSnapshot(session, []), next));
                db.AgentRuns.Add(AgentRunStoreMapping.ToRecord(next));
                db.Add(new AuthorizationAudit(signal.SignalId, signal.TenantId, request.ExecutorPrincipalId, "agent-runs.approval-continuation", next.AgentRunId.ToString("D"), "allowed", "approval_continued", at));
                AgentRunStoreMapping.Apply(row, run.HandoffApprovalContinuation(run.Revision, signal.RequestId, next.AgentRunId, at));
            }
            else AgentRunStoreMapping.Apply(row, run.ResumeDurableApproval(run.Revision, signal.RequestId, at));
        }
        db.Entry(saved).CurrentValues.SetValues(saved with { Revision = checked(saved.Revision + 1), ProcessedAt = at });
        try { await db.SaveChangesAsync(ct).ConfigureAwait(false); await tx.CommitAsync(ct).ConfigureAwait(false); return true; }
        catch (DbUpdateConcurrencyException) { return false; }
        catch (DbUpdateException ex) when (IsConstraint(ex)) { return false; }
    }
    public async ValueTask<IReadOnlyList<TenantApprovalRequest>> DueApprovalsAsync(DateTimeOffset at, int limit, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct).ConfigureAwait(false); var millis = at.ToUnixTimeMilliseconds();
        return (await db.Set<TenantApprovalRecord>().AsNoTracking().Where(r => r.Status == (int)TenantApprovalStatus.Pending && r.ExpiresAt <= millis)
            .OrderBy(r => r.ExpiresAt).ThenBy(r => r.RequestId).Take(Math.Clamp(limit, 1, 100)).ToArrayAsync(ct).ConfigureAwait(false)).Select(r => r.Domain()).ToArray();
    }
    public async ValueTask<bool> TryExpireApprovalAsync(Guid tenantId, Guid requestId, long expectedRevision, ApprovalProcessedSignal signal, DateTimeOffset at, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct).ConfigureAwait(false); await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        var row = await db.Set<TenantApprovalRecord>().SingleOrDefaultAsync(r => r.TenantId == tenantId && r.RequestId == requestId, ct).ConfigureAwait(false);
        if (row is null || row.Revision != expectedRevision || row.Status != (int)TenantApprovalStatus.Pending) return false;
        var request = row.Domain(); if (request.ExpiresAt > at) return false;
        db.Entry(row).CurrentValues.SetValues(TenantApprovalRecord.From(TenantApprovalRules.Expire(request, expectedRevision, signal, at))); db.Add(signal);
        try { await db.SaveChangesAsync(ct).ConfigureAwait(false); await tx.CommitAsync(ct).ConfigureAwait(false); return true; }
        catch (DbUpdateConcurrencyException) { return false; }
        catch (DbUpdateException ex) when (IsConstraint(ex)) { return false; }
    }
}
