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
        return (read.Experience is not { } experience || (experience.UserPrincipalId is null || experience.UserPrincipalId == visibility.HumanPrincipalId)
                && Memory(request.Operation.InstanceId) && TenantExperienceFactRules.Sources(experience).All(s => tenantExperiences.GetValueOrDefault((request.TenantId, s.ExperienceId)) is { } item
                    && item.AgentInstanceId == request.Operation.InstanceId && (item.UserPrincipalId is null || item.UserPrincipalId == visibility.HumanPrincipalId)))
            && Agent(request.Operation.InstanceId) && (visibility.ChatAgents is null || visibility.ChatAgents.Contains(request.Operation.InstanceId)) && Session(read.SessionId) && Session(read.RootSessionId)
            && TenantContinuityRules.Sessions(read).All(s => Session(s.SessionId) && sessionBacking.ReadTenantSession(request.TenantId, s.SessionId) is { } owned && owned.Owner.AgentInstanceId == request.Operation.InstanceId)
            && (read.Memory is not { } memory || (memory.UserPrincipalId is null || memory.UserPrincipalId == visibility.HumanPrincipalId)
                && Memory(request.Operation.InstanceId) && TenantMemoryOperationRules.Sources(memory).All(s => learnedMemories.GetValueOrDefault((request.TenantId, s.MemoryId)) is { } fact
                    && Agent(fact.AgentInstanceId) && Memory(fact.AgentInstanceId) && (fact.Scope != MemoryScope.Session || Session(fact.SessionId))));
    }
    private bool ExperienceApprovalVisible(TenantApprovalRequest request, TenantContinuityApprovalVisibility? visibility)
    {
        if (visibility is null || request.Operation.ExperienceOperation is not { } value) return false;
        bool Session(Guid id) => visibility.Sessions is null || visibility.Sessions.Contains(id);
        if (visibility.Agents is not null && !visibility.Agents.Contains(request.Operation.InstanceId)
            || visibility.ChatAgents is not null && !visibility.ChatAgents.Contains(request.Operation.InstanceId) || !Session(value.SessionId) || !Session(value.RootSessionId)) return false;
        if (value.ToolName != "experience.source" && value.UserPrincipalId is { } owner && owner != visibility.HumanPrincipalId) return false;
        if (value.Source is { } source && (!Session(source.SessionId) || source.PrivateUserPrincipalId is { } privateOwner && privateOwner != visibility.HumanPrincipalId
            || source.SourceRunId is { } run && visibility.Runs is not null && !visibility.Runs.Contains(run)
            || sessionBacking.ReadTenantSession(request.TenantId, source.SessionId) is not { } owned || owned.Owner.AgentInstanceId != request.Operation.InstanceId)) return false;
        return value.ToolName != "experience.consolidate" || (visibility.MemoryAgents is null || visibility.MemoryAgents.Contains(request.Operation.InstanceId))
            && TenantExperienceRules.Sources(value).All(s => tenantExperiences.GetValueOrDefault((request.TenantId, s.ExperienceId)) is { } fact
                && fact.AgentInstanceId == request.Operation.InstanceId && (fact.UserPrincipalId is null || fact.UserPrincipalId == visibility.HumanPrincipalId));
    }

}
