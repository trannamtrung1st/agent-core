using AgentCore.Application.Agents;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;
using AgentCore.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AgentCore.Infrastructure.Security;

public sealed partial class SqliteIamStore
{
    private async ValueTask<IReadOnlyList<ArtifactRecord>> RequireCompletionReadOperation(AgentCoreDbContext db, TenantApprovalRequest request, AgentRun parent, TenantAgent agent, CancellationToken ct)
    {
        var read = request.Operation.CompletionRead!;
        var own = await CompletionSession(db, request.TenantId, parent.SessionId, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("session_unavailable");
        TenantWaitRules.RequireSession(own);
        var child = await TenantWaitRules.ChildAsync(parent, await CompletionSession(db, request.TenantId, read.ChildSessionId, ct).ConfigureAwait(false),
            (id, token) => CompletionRun(db, request.TenantId, id, token), ct).ConfigureAwait(false) ?? throw new AccessDeniedException("completion_source_changed");
        var files = new List<ArtifactRecord>();
        foreach (var expected in TenantCompletionReadRules.Artifacts(read))
        {
            var row = await SqliteArtifactStore.Owned(db, request.TenantId, read.ChildSessionId).SingleOrDefaultAsync(a => a.ArtifactId == expected.ArtifactId.ToString("D"), ct).ConfigureAwait(false);
            var item = row is null ? null : SqliteArtifactStore.FromRow(row);
            if (item is null || item.AgentRunId != read.SourceRunId || TenantArtifactVerificationRules.Witness(item) != expected)
                throw new AccessDeniedException("completion_source_changed");
            files.Add(item);
        }
        var current = InstanceSettingsResolver.Resolve(await TenantAgentDefinitionBaseline(db, agent, ct).ConfigureAwait(false), agent.Instance.SettingsOverrides);
        TenantCompletionReadRules.Require(read, parent, agent, current, child);
        if (!TenantCompletionReadRules.Same(read, TenantCompletionReadRules.Witness(parent, child, files))) throw new AccessDeniedException("completion_source_changed");
        await TenantCompletionReadRules.PermissionsAsync((await ReadTenantAsync(db, request.TenantId, ct).ConfigureAwait(false))!, read, agent.PrincipalId, new IamAuthorizer(this), ct).ConfigureAwait(false);
        return files;
    }
    private async Task<BackgroundCompletionCandidate?> AuthorizeCompletionSource(AgentCoreDbContext db, AgentRun parent, BackgroundCompletionCandidate child, CancellationToken ct)
    {
        var tenant = parent.Owner.TenantId!.Value; var state = (await ReadTenantAsync(db, tenant, ct).ConfigureAwait(false))!;
        var source = child.CurrentTaskRun ?? child.Run;
        var row = source.IsTerminal && source.Result?.OutcomeKind != AgentRunOutcomeKind.Continued
            ? await RootApprovalAsync(db, tenant, parent.Admission.ExecutionContext!.RootActivationId.ToString("D"), parent.Owner.ExecutorPrincipalId!.Value,
                TenantCompletionReadRules.Lookup(parent, child), ct).ConfigureAwait(false) : null;
        if (row is null) return await TenantWaitRules.CanReadAsync(state, parent, child, new IamAuthorizer(this), ct).ConfigureAwait(false) ? child : null;
        var request = row.Domain();
        if (!TenantCompletionReadReferences.Contains(parent, source.SessionId, source.AgentRunId, request.RequestId)) return null;
        var receipt = (await db.Set<TenantOperationReceiptRecord>().AsNoTracking().SingleOrDefaultAsync(r => r.TenantId == tenant && r.OperationId == request.Operation.OperationId, ct).ConfigureAwait(false))?.Domain();
        var decision = await db.Set<TenantApprovalDecision>().AsNoTracking().SingleOrDefaultAsync(d => d.TenantId == tenant && d.RequestId == request.RequestId, ct).ConfigureAwait(false);
        if (receipt is null || decision is null || await db.Set<TenantOperationGrant>().AnyAsync(g => g.RequestId == request.RequestId, ct).ConfigureAwait(false)) return null;
        try
        {
            var agent = Owned(await OwnedAgents(db, tenant).SingleAsync(a => a.Instance.InstanceId == parent.AgentInstanceId.ToString("D"), ct).ConfigureAwait(false));
            if (agent.Instance.Revision != request.Operation.ExpectedInstanceRevision || agent.Instance.PersonaRevision != request.Operation.ExpectedPersonaRevision)
                throw new AccessDeniedException("operation_witness_changed");
            var files = await RequireCompletionReadOperation(db, request, parent, agent, ct).ConfigureAwait(false);
            await TenantCompletionReadService.RequireApprovedAsync(parent, state, request.Operation.CompletionRead!, request, receipt, decision, new IamAuthorizer(this), ct).ConfigureAwait(false);
            return child with { CompletionReadRequestId = request.RequestId, ReviewedArtifacts = files };
        }
        catch (AccessDeniedException) { return null; }
        catch (SecurityConflictException) { return null; }
    }

    private async ValueTask<PreparedCompletionRead> RequireCompletionSourceProof(AgentCoreDbContext db, AgentRun parent, Guid session, Guid source, Guid request, CancellationToken ct)
    {
        var tenant = parent.Owner.TenantId!.Value;
        var child = await TenantWaitRules.ChildAsync(parent, await CompletionSession(db, tenant, session, ct).ConfigureAwait(false),
            (id, token) => CompletionRun(db, tenant, id, token), ct).ConfigureAwait(false);
        var allowed = child is null ? null : await AuthorizeCompletionSource(db, parent, child, ct).ConfigureAwait(false);
        if (allowed is null || allowed.CompletionReadRequestId != request || (allowed.CurrentTaskRun ?? allowed.Run).AgentRunId != source)
            throw new AccessDeniedException("completion_source_unavailable");
        return TenantCompletionReadRules.Witness(parent, allowed, allowed.ReviewedArtifacts!);
    }

}
