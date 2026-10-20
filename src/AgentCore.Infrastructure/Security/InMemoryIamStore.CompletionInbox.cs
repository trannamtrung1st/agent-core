using AgentCore.Application.Agents;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using AgentCore.Infrastructure.Persistence;

namespace AgentCore.Infrastructure.Security;

public sealed partial class InMemoryIamStore
{
    private AgentRun RequireCompletionParent(TenantRuntimeFrame frame, string tool, CancellationToken ct)
    {
        RequireRuntimeOwner(frame.Owner); var tenant = frame.Owner.TenantId!.Value;
        var session = sessionBacking.ReadTenantSession(tenant, frame.SessionId) ?? throw new AccessDeniedException("session_unavailable");
        var run = sessionBacking.ReadTenantRun(tenant, frame.RunId) ?? throw new AccessDeniedException("run_unavailable");
        TenantRuntimeRules.Require(frame, session.Owner, run, (clock ?? TimeProvider.System).GetUtcNow()); TenantWaitRules.RequireSession(session);
        var agent = FindTenantAgentAsync(tenant, run.AgentInstanceId, ct).Result!;
        var baseline = Guid.TryParse(agent.Instance.DefinitionId, out var definition)
            ? definitions.TenantPublication(tenant, definition, agent.Instance.ActiveVersion)?.Payload
            : builtIns?.GetAsync(agent.Instance.DefinitionId, agent.Instance.ActiveVersion, ct).AsTask().GetAwaiter().GetResult();
        if (baseline is null) throw new AccessDeniedException("definition_unavailable");
        TenantCompletionRules.Require(run, InstanceSettingsResolver.Resolve(baseline, agent.Instance.SettingsOverrides), tool);
        TenantExecutionLineage.RequireRequesterAsync(run, _tenants[tenant], new IamAuthorizer(this), this, ct).AsTask().GetAwaiter().GetResult();
        return run;
    }
    private TenantCompletionView? CompletionView(AgentRun parent, Guid session, DateTimeOffset now, CancellationToken ct)
    {
        var tenant = parent.Owner.TenantId!.Value;
        var source = TenantWaitRules.ChildAsync(parent, sessionBacking.ReadTenantSession(tenant, session),
            (id, _) => ValueTask.FromResult(sessionBacking.ReadRetainedTenantOperationRun(tenant, id)), ct).AsTask().GetAwaiter().GetResult();
        source = source is null ? null : AuthorizeCompletionSource(parent, source, ct);
        if (source is null) return null;
        var row = sessionBacking.AgentRuns.CompletionReceipts.GetValueOrDefault((source.CurrentTaskRun ?? source.Run).AgentRunId);
        var item = row is null ? null : CompletionInboxMapping.Refresh(CompletionInboxMapping.Read(row), sessionBacking.AgentRuns.Runs.Values.ToArray(), now);
        if (row is not null && item!.Owner != parent.Owner) return null;
        if (row is not null) CompletionInboxMapping.Write(row, item!);
        return new(source, item);
    }
    public ValueTask<TenantCompletionPage> ReadTenantCompletionsAsync(TenantRuntimeFrame frame, string tool, Guid? childSessionId,
        Guid? cursor, bool pendingOnly, int limit, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested(); lock (_gate) lock (sessionBacking.AdmissionGate) lock (instances.CredentialGate) lock (definitions.TenantGate)
        {
            var parent = RequireCompletionParent(frame, tool, ct); var now = (clock ?? TimeProvider.System).GetUtcNow();
            var candidates = childSessionId is { } id ? new[] { id } : sessionBacking.AgentRuns.Runs.Values
                .Where(r => r.Owner == frame.Owner && r.Admission.ExecutionContext?.Origin == ExecutionOrigin.ImmediateBackground
                    && r.Admission.Activation.SourceSessionId == frame.SessionId).Select(r => r.SessionId).Distinct().ToArray();
            var visible = candidates.Select(id => CompletionView(parent, id, now, ct)).OfType<TenantCompletionView>().ToArray();
            var page = TenantCompletionRules.Page(visible, childSessionId, cursor, pendingOnly, limit);
            if (childSessionId is not null)
            {
                var source = page.Items[0];
                var files = TenantCompletionRules.ArtifactsAsync(artifactBacking, _tenants[frame.Owner.TenantId!.Value], parent, source.Source, new IamAuthorizer(this), ct).AsTask().GetAwaiter().GetResult();
                page = page with { Items = [source with { Artifacts = files }] };
            }
            return ValueTask.FromResult(page);
        }
    }
    public ValueTask<TenantCompletionView> MutateTenantCompletionAsync(TenantRuntimeFrame frame, Guid childSessionId,
        long expectedRevision, string toolCallId, Guid token, string? usage, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested(); lock (_gate) lock (sessionBacking.AdmissionGate) lock (instances.CredentialGate) lock (definitions.TenantGate)
        {
            var parent = RequireCompletionParent(frame, usage is null ? ToolCatalog.BackgroundTake : ToolCatalog.BackgroundAcknowledge, ct);
            var now = (clock ?? TimeProvider.System).GetUtcNow();
            var view = CompletionView(parent, childSessionId, now, ct) ?? throw AgentCore.Application.Sessions.AgentCoreErrors.NotFound("Owned readable background task was not found.");
            TenantCompletionRules.RequirePendingMutation(parent, childSessionId, expectedRevision, toolCallId, token, usage);
            var next = TenantCompletionRules.Mutate(view, parent, expectedRevision, toolCallId, token, usage, now);
            var files = TenantCompletionRules.ArtifactsAsync(artifactBacking, _tenants[frame.Owner.TenantId!.Value], parent, view.Source, new IamAuthorizer(this), ct).AsTask().GetAwaiter().GetResult();
            ct.ThrowIfCancellationRequested(); CompletionInboxMapping.Write(sessionBacking.AgentRuns.CompletionReceipts[next.ChildAgentRunId], next);
            return ValueTask.FromResult(view with { Inbox = next, Artifacts = files });
        }
    }
    public ValueTask<bool> HasTenantCompletionClaimAsync(TenantRuntimeFrame frame, bool acknowledgedOnly, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested(); lock (_gate) lock (sessionBacking.AdmissionGate)
        {
            RequireRuntimeOwner(frame.Owner); var now = (clock ?? TimeProvider.System).GetUtcNow();
            _ = sessionBacking.LoadTenantRuntime(frame, now);
            return ValueTask.FromResult(sessionBacking.AgentRuns.CompletionReceipts.Values.Select(CompletionInboxMapping.Read).Any(i => i.Owner == frame.Owner
                && i.ParentSessionId == frame.SessionId && i.Status == CompletionInboxStatus.Claimed && i.ClaimRunId == frame.RunId
                && i.ClaimGeneration == frame.Generation && i.ClaimExpiresAtUtc > now && (!acknowledgedOnly || i.Acknowledgment is not null)));
        }
    }
    private void RequireTenantOutputSources(AgentRun parent, CancellationToken ct)
    {
        RequireCredentialReadSources(parent, ct);
        RequireAccountReadSources(parent, ct);
        RequireAccountActionSources(parent, ct);
        var now = (clock ?? TimeProvider.System).GetUtcNow();
        if (parent.Admission.OutputContract == AgentRunOutputContract.CompletionReport)
        {
            var source = TenantReportReadRules.SourceAsync(parent,
                (id, _) => ValueTask.FromResult(sessionBacking.ReadRetainedTenantOperationRun(parent.Owner.TenantId!.Value, id)),
                (id, _) => ValueTask.FromResult(CompletionSource(parent.Owner, id)), ct).AsTask().GetAwaiter().GetResult();
            RequireTenantReportRead(_tenants[parent.Owner.TenantId!.Value], source, ct);
        }
        foreach (var item in sessionBacking.AgentRuns.CompletionReceipts.Values.Select(CompletionInboxMapping.Read)
            .Where(i => i.Owner == parent.Owner && i.ClaimRunId == parent.AgentRunId && i.Status == CompletionInboxStatus.Claimed && i.Acknowledgment is not null && i.ClaimExpiresAtUtc > now))
            if (CompletionView(parent, item.ChildSessionId, now, ct)?.Inbox?.ChildAgentRunId != item.ChildAgentRunId)
                throw new AccessDeniedException("completion_source_unavailable");
        TenantCompletionRules.RequireSourcesAsync(parent, _tenants[parent.Owner.TenantId!.Value], new IamAuthorizer(this),
            (id, _) => ValueTask.FromResult(sessionBacking.ReadTenantSession(parent.Owner.TenantId.Value, id)),
            (id, _) => ValueTask.FromResult(sessionBacking.ReadRetainedTenantOperationRun(parent.Owner.TenantId.Value, id)), artifactBacking, ct, (session, source, request, token) => RequireCompletionSourceProof(parent, session, source, request, token)).AsTask().GetAwaiter().GetResult();
    }
}
