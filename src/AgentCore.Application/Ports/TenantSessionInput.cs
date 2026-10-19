using AgentCore.Domain.Conversation;

namespace AgentCore.Application.Ports;

// Server-prepared original-human references and frozen admission. Never model arguments.
public sealed record TenantHumanInput(TenantSessionRoot Owner, TenantSessionInitiator Human, long SecurityRevision,
    TenantHumanRunAdmission Admission);
public sealed record TenantHumanInputCommit(TenantRunAdmissionResult Admission, SessionSnapshot Snapshot, long SecurityRevision);
public interface ITenantSessionInputControl
{
    ValueTask<TenantHumanInputCommit> SubmitAsync(TenantHumanInput input, CancellationToken ct = default);
}
public interface ITenantRuntimeInputStore
{
    ValueTask<TenantHumanInputCommit> SaveTenantRuntimeInputAsync(TenantRuntimeFrame frame, SessionSnapshot snapshot,
        long expectedRevision, TenantHumanInput input, Guid auditId, CancellationToken ct = default);
}
public interface ITenantRuntimeInputWriter
{
    ValueTask<TenantHumanInputCommit> SaveInputAsync(SessionSnapshot snapshot, long expectedRevision, TenantHumanInput input, CancellationToken ct);
}
