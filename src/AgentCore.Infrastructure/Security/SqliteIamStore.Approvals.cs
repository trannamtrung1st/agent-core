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
    private static IQueryable<TenantApprovalRecord> SemanticApprovals(AgentCoreDbContext db, PreparedTenantOperation operation) => operation.WorkspaceMutation is { } mutation
        ? db.Set<TenantApprovalRecord>().FromSqlInterpolated($"""
            SELECT * FROM TenantApprovalRequests WHERE Permission = {operation.Permission} AND ResourceType = 'workspace' AND ResourceId = {operation.ResourceId}
            AND json_extract(PayloadJson, '$.Operation.WorkspaceMutation.SessionId') = {mutation.SessionId.ToString("D")}
            AND json_extract(PayloadJson, '$.Operation.WorkspaceMutation.ToolName') = {mutation.ToolName}
            AND json_extract(PayloadJson, '$.Operation.WorkspaceMutation.ArgumentsJson') = {mutation.ArgumentsJson}
            """)
        : operation.WorkspaceRead is { } workspace
        ? db.Set<TenantApprovalRecord>().FromSqlInterpolated($"""
            SELECT * FROM TenantApprovalRequests WHERE Permission = 'workspaces.read' AND ResourceType = 'workspace' AND ResourceId = {operation.ResourceId}
            AND json_extract(PayloadJson, '$.Operation.WorkspaceRead.SessionId') = {workspace.SessionId.ToString("D")}
            AND json_extract(PayloadJson, '$.Operation.WorkspaceRead.ToolName') = {workspace.ToolName}
            AND json_extract(PayloadJson, '$.Operation.WorkspaceRead.ArgumentsJson') = {workspace.ArgumentsJson}
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
    public async ValueTask<TenantApprovalPage> ListApprovalsAsync(Guid tenantId, IReadOnlyList<Guid>? allowedRequests, IReadOnlyList<Guid>? allowedInstances, TenantApprovalQuery page, CancellationToken ct = default, IReadOnlyList<Guid>? allowedCredentials = null, TenantSandboxApprovalVisibility? sandboxVisibility = null, TenantBrowserApprovalVisibility? browserVisibility = null, TenantCredentialReadApprovalVisibility? credentialReadVisibility = null, IReadOnlyList<Guid>? artifactCreateSessions = null, TenantArtifactReadApprovalVisibility? artifactReadVisibility = null, TenantWorkspaceReadApprovalVisibility? workspaceReadVisibility = null, TenantWorkspaceMutationApprovalVisibility? workspaceMutationVisibility = null)
    {
        page.Validate();
        await using var db = await contexts.CreateDbContextAsync(ct).ConfigureAwait(false);
        await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
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
        var workspaceJson = System.Text.Json.JsonSerializer.Serialize(workspaceReadVisibility?.Workspaces);
        var homeAgentsJson = System.Text.Json.JsonSerializer.Serialize(workspaceReadVisibility?.HomeAgents?.Select(id => id.ToString("D")).ToArray());
        var workspaceSessionsJson = System.Text.Json.JsonSerializer.Serialize(workspaceReadVisibility?.Sessions?.Select(id => id.ToString("D")).ToArray());
        var mutationReadJson = System.Text.Json.JsonSerializer.Serialize(workspaceMutationVisibility?.Reads);
        var mutationWriteJson = System.Text.Json.JsonSerializer.Serialize(workspaceMutationVisibility?.Writes);
        var mutationDeleteJson = System.Text.Json.JsonSerializer.Serialize(workspaceMutationVisibility?.Deletes);
        var mutationAgentsJson = System.Text.Json.JsonSerializer.Serialize(workspaceMutationVisibility?.HomeAgents?.Select(id => id.ToString("D")).ToArray());
        var mutationSessionsJson = System.Text.Json.JsonSerializer.Serialize(workspaceMutationVisibility?.Sessions?.Select(id => id.ToString("D")).ToArray());
        var source = db.Set<TenantApprovalRecord>().FromSqlInterpolated($"""
            SELECT * FROM TenantApprovalRequests WHERE
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
            (Permission <> 'workspaces.read' OR (
                ({workspaceJson} = 'null' OR ResourceId IN (SELECT value FROM json_each({workspaceJson})))
                AND ({workspaceSessionsJson} = 'null' OR SessionId IN (SELECT value FROM json_each({workspaceSessionsJson})))
                AND (json_extract(PayloadJson, '$.Operation.WorkspaceRead.Working') = 1 OR {homeAgentsJson} = 'null' OR InstanceId IN (SELECT value FROM json_each({homeAgentsJson})))
                AND EXISTS (SELECT 1 FROM TenantSessions AS root JOIN Sessions AS saved ON saved.SessionId = root.SessionId
                    WHERE root.TenantId = TenantApprovalRequests.TenantId AND root.SessionId = TenantApprovalRequests.SessionId
                    AND root.AgentInstanceId = TenantApprovalRequests.InstanceId AND root.ExecutorPrincipalId = TenantApprovalRequests.ExecutorPrincipalId
                    AND saved.DurablyDeletedAtUtc IS NULL))) AND
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
                AND EXISTS (SELECT 1 FROM TenantSessions AS root JOIN Sessions AS saved ON saved.SessionId = root.SessionId
                    WHERE root.TenantId = TenantApprovalRequests.TenantId AND root.SessionId = TenantApprovalRequests.SessionId AND saved.DurablyDeletedAtUtc IS NULL))) AND
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
                AND ({browserSessionJson} = 'null' OR SessionId IN (SELECT value FROM json_each({browserSessionJson})))))
            """);
        var query = source.AsNoTracking().Where(r => r.TenantId == tenantId);
        if (page.SessionId is { } session) query = query.Where(r => r.SessionId == session.ToString("D"));
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
            if (request.Operation.ArtifactPublication is not null) await RequireArtifactOperation(db, request, run, agent, ct).ConfigureAwait(false);
            if (request.Operation.BrowserCommand is not null) await RequireBrowserOperation(db, request, run, agent, ct).ConfigureAwait(false);
            if (request.Operation.WorkspaceRead is not null) await RequireWorkspaceReadOperation(db, request, run, agent, ct).ConfigureAwait(false);
            if (request.Operation.WorkspaceMutation is not null) await RequireWorkspaceMutationOperation(db, request, run, agent, ct).ConfigureAwait(false);
            if (request.Operation.ArtifactVerification is not null) await RequireArtifactVerificationOperation(db, request, run, agent, ct).ConfigureAwait(false);
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
            TenantApprovalLimits.Require(counts?.Tenant ?? 0, counts?.Executor ?? 0, counts?.Requester ?? 0, operationCount, rootCount);
            if (run.Status == AgentRunStatus.Running && run.Claim!.LeaseExpiresAtUtc <= ApprovalNow) throw new SecurityConflictException("execution_claim_expired");
            var saved = run.WaitForDurableApproval(expectedRunRevision, request.RequestId, generation, request.CreatedAt);
            AgentRunStoreMapping.Apply(row, saved); db.Add(TenantApprovalRecord.From(request)); result = request; return true;
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
        var runRow = await OwnedRuns(db, request.TenantId).SingleOrDefaultAsync(r => r.AgentRunId == request.RunId.ToString("D"), ct).ConfigureAwait(false);
        var target = await OwnedAgents(db, request.TenantId).SingleOrDefaultAsync(r => r.Instance.InstanceId == request.Operation.InstanceId.ToString("D"), ct).ConfigureAwait(false);
        if (runRow is null || target is null) return false;
        var run = AgentRunStoreMapping.ToDomain(runRow); var agent = Owned(target);
        if (run.ApprovalRequestId != request.RequestId || run.Status != AgentRunStatus.WaitingForApproval) return false;
        TenantApprovalRules.Validate(request, run, agent, request.Status != TenantApprovalStatus.Cancelled);
        if (request.Operation.CredentialCheck is not null && request.Status != TenantApprovalStatus.Cancelled)
            TenantCredentialOperationRules.RequireRequest(request, run, agent, await CredentialOperationTarget(db, request, ct).ConfigureAwait(false));
        if (request.Operation.SandboxCommand is not null && request.Status != TenantApprovalStatus.Cancelled)
            await RequireSandboxOperation(db, request, run, agent, ct).ConfigureAwait(false);
        var state = (await ReadTenantAsync(db, request.TenantId, ct).ConfigureAwait(false))!;
        if (request.Status != TenantApprovalStatus.Cancelled)
        {
            if (request.Operation.ArtifactPublication is not null)
            {
                await RequireArtifactOperation(db, request, run, agent, ct).ConfigureAwait(false);
                await TenantArtifactReviewRules.ReviewerAsync(state, request, decision.HumanPrincipalId, new IamAuthorizer(this), ct).ConfigureAwait(false);
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
            if (request.Operation.ArtifactVerification is not null)
            {
                await RequireArtifactVerificationOperation(db, request, run, agent, ct).ConfigureAwait(false);
                await TenantArtifactVerificationRules.ReviewerAsync(state, request.Operation.ArtifactVerification, request.OriginalInitiatorPrincipalId, decision.HumanPrincipalId, new IamAuthorizer(this), ct).ConfigureAwait(false);
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
