using AgentCore.Application.Ports;

namespace AgentCore.Infrastructure.Security;

public sealed partial class InMemoryIamStore
{
    public ValueTask<TenantRunAdmissionResult?> FindTenantAcceptedHumanInputAsync(TenantSessionRoot owner, Guid principal, Guid message,
        string text, IReadOnlyList<Guid> attachments, TenantInputInterruption? interruption, AgentCore.Domain.Conversation.SessionMode mode, CancellationToken ct = default, IReadOnlyList<AgentCore.Domain.Conversation.UserMessagePart>? parts = null)
    {
        ct.ThrowIfCancellationRequested(); lock (_gate) lock (sessionBacking.AdmissionGate)
            return ValueTask.FromResult(sessionBacking.AcceptedTenantInput(owner, principal, message, text, attachments, interruption, mode, parts));
    }
}
