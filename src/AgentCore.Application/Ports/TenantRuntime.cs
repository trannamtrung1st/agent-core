using AgentCore.Application.Execution;
using AgentCore.Domain.Conversation;

namespace AgentCore.Application.Ports;

// Trusted server-held ownership references. Never part of model arguments or a human cookie.
public sealed record TenantRuntimeFrame(AgentRunOwner Owner, Guid SessionId, Guid RunId, Guid Generation);

public interface ITenantRuntimeStore
{
    // Internal child admission. The owning transaction rechecks the exact parent claim,
    // current executor permissions/configuration and immutable causal identity.
    ValueTask<AgentRunAdmissionResult> AdmitTenantImmediateAsync(TenantRuntimeFrame parent, SessionSnapshot snapshot,
        AgentRun child, Guid auditId, CancellationToken ct = default) => throw new NotSupportedException();
    ValueTask<SessionSnapshot?> LoadTenantRuntimeAsync(TenantRuntimeFrame frame, CancellationToken ct = default);
    ValueTask SaveTenantRuntimeAsync(TenantRuntimeFrame frame, SessionSnapshot snapshot, long expectedRevision, CancellationToken ct = default);
    ValueTask<ConversationHistoryPage?> ReadTenantRuntimeHistoryAsync(TenantRuntimeFrame frame, long? after, long? before, int limit, CancellationToken ct = default);
    ValueTask<IReadOnlyList<AgentRun>> ListRunnableTenantRunsAsync(DateTimeOffset at, int limit, CancellationToken ct = default);
    ValueTask<AgentRun> ApplyTenantRuntimeCommandAsync(AgentRunOwner owner, Guid runId, AgentRunCommand command, Guid diagnosticId, CancellationToken ct = default, Guid? runtimeGeneration = null);
}
