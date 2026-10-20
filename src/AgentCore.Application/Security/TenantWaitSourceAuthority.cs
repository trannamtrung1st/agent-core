using System.Text.Json;
using AgentCore.Application.Execution;
using AgentCore.Application.Ports;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;

namespace AgentCore.Application.Security;

public sealed class TenantWaitSourceAuthority(IIamStore security, ITenantSessionStore sessions, ITenantRunStore runs, IamAuthorizer iam, ITenantArtifactStore? artifacts = null, TenantCompletionReadService? completionReads = null)
{
    public static bool HasStoredSources(AgentRun run) => AgentRunToolCallCheckpoint.TryRead(run.Checkpoint, out var messages)
        && messages!.Any(m => m.Role == ModelRole.Tool && (m.Name == ToolCatalog.ExecutionWait || TenantCompletionToolContract.IsTool(m.Name ?? "")) && m.Text.Contains("\"sourceAgentRunId\":\"", StringComparison.Ordinal)) || TenantCompletionReadReferences.Read(run).Count > 0;
    public async ValueTask RequireAsync(AgentRun run, IReadOnlyList<ModelMessage> messages, CancellationToken ct)
    {
        var tenant = run.Owner.TenantId ?? throw new AccessDeniedException("tenant_context_required");
        var sources = new HashSet<(Guid Session, Guid Run, Guid? Request)>();
        foreach (var message in messages.Where(m => m.Role == ModelRole.Tool && m.Name == ToolCatalog.ExecutionWait))
        {
            using var json = JsonDocument.Parse(message.Text);
            if (!json.RootElement.TryGetProperty("statuses", out var statuses)) continue;
            foreach (var item in statuses.EnumerateArray())
                if (item.TryGetProperty("sourceAgentRunId", out var id) && id.ValueKind == JsonValueKind.String)
                    sources.Add((item.GetProperty("backgroundSessionId").GetGuid(), id.GetGuid(), item.TryGetProperty("readRequestId", out var readId) && readId.ValueKind == JsonValueKind.String ? readId.GetGuid() : null));
        }
        if (sources.Count == 0 && TenantCompletionRules.StoredSources(run).Count == 0) return;
        var state = await security.ReadTenantAsync(tenant, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("tenant_unavailable");
        await TenantCompletionRules.RequireSourcesAsync(run, state, iam,
            (id, token) => sessions.FindTenantSessionAsync(tenant, id, token), (id, token) => runs.FindTenantRunAsync(tenant, id, token), artifacts, ct, completionReads is null ? null : (session, source, request, token) => completionReads.RequireStoredSourceAsync(run, session, source, request, token)).ConfigureAwait(false);
        foreach (var (sessionId, sourceId, requestId) in sources)
        {
            if (requestId is { } reviewed)
            {
                if (completionReads is null) throw new AccessDeniedException("completion_source_unavailable");
                await completionReads.RequireStoredSourceAsync(run, sessionId, sourceId, reviewed, ct).ConfigureAwait(false);
                continue;
            }
            var child = await TenantWaitRules.ChildAsync(run, await sessions.FindTenantSessionAsync(tenant, sessionId, ct).ConfigureAwait(false),
                (id, token) => runs.FindTenantRunAsync(tenant, id, token), ct).ConfigureAwait(false);
            var savedSource = await runs.FindTenantRunAsync(tenant, sourceId, ct).ConfigureAwait(false);
            if (child is null || savedSource?.Owner != run.Owner || savedSource.SessionId != sessionId
                || (await TenantBackgroundLineage.InitialAsync(savedSource, (id, token) => runs.FindTenantRunAsync(tenant, id, token), ct).ConfigureAwait(false)).AgentRunId != child.Run.AgentRunId
                || !await TenantWaitRules.CanReadAsync(state, run, child with { CurrentTaskRun = savedSource }, iam, ct).ConfigureAwait(false))
                throw new AccessDeniedException("wait_source_unavailable");
        }
        if ((await security.ReadTenantAsync(tenant, ct).ConfigureAwait(false))?.Tenant.Revision != state.Tenant.Revision)
            throw new SecurityConflictException("wait_source_changed");
    }

    public ValueTask RequireStoredAsync(AgentRun run, CancellationToken ct) =>
        AgentRunToolCallCheckpoint.TryRead(run.Checkpoint, out var messages) ? RequireAsync(run, messages!, ct) : ValueTask.CompletedTask;
}
