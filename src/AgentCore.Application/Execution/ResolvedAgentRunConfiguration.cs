using AgentCore.Application.Agents;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;

namespace AgentCore.Application.Execution;

public sealed record ResolvedAgentRunConfiguration(AgentRunConfiguration Configuration, AgentIdentity Persona,
    IReadOnlyList<EffectiveSkill> Skills, ExecutionBudgetPolicy? InstanceBudgets);
