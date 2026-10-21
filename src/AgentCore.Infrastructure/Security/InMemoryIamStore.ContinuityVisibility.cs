using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Memory;
using AgentCore.Domain.Security;

namespace AgentCore.Infrastructure.Security;
public sealed partial class InMemoryIamStore
{
    private bool ContinuityApprovalVisible(TenantApprovalRequest request, TenantContinuityApprovalVisibility? visibility)
    {
        if (visibility is null || request.Operation.ContinuityRead is not { } read) return false;
        bool Agent(Guid id) => visibility.Agents is null || visibility.Agents.Contains(id);
        bool Memory(Guid id) => visibility.MemoryAgents is null || visibility.MemoryAgents.Contains(id);
        bool Session(Guid id) => visibility.Sessions is null || visibility.Sessions.Contains(id);
        return Agent(request.Operation.InstanceId) && (visibility.ChatAgents is null || visibility.ChatAgents.Contains(request.Operation.InstanceId)) && Session(read.SessionId) && Session(read.RootSessionId)
            && TenantContinuityRules.Sessions(read).All(s => Session(s.SessionId) && sessionBacking.ReadTenantSession(request.TenantId, s.SessionId) is { } owned && owned.Owner.AgentInstanceId == request.Operation.InstanceId)
            && (read.Memory is not { } memory || (memory.UserPrincipalId is null || memory.UserPrincipalId == visibility.HumanPrincipalId)
                && Memory(request.Operation.InstanceId) && TenantMemoryOperationRules.Sources(memory).All(s => learnedMemories.GetValueOrDefault((request.TenantId, s.MemoryId)) is { } fact
                    && Agent(fact.AgentInstanceId) && Memory(fact.AgentInstanceId) && (fact.Scope != MemoryScope.Session || Session(fact.SessionId))));
    }
}
