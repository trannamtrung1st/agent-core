using AgentCore.Application.Memory;
using AgentCore.Application.Ports;

namespace AgentCore.Application.Execution;

/// <summary>
/// Controller disposition for the current activation. It is not an application action
/// and it is not <see cref="AgentCore.Application.Ports.AgentDecision"/>.
/// </summary>
public enum AgentStepDisposition
{
    Continue,
    Wait,
    Complete,
    Blocked
}

public abstract record AgentAction;

/// <summary>
/// Terminal Chat response. Destination is bound by the controller, so this action has
/// no session, profile, tenant, or recipient.
/// </summary>
public sealed record ChatRespondAction(
    string DisplayText,
    ModelSpeechProjection Speech,
    IReadOnlyList<ModelResponseBlock> Blocks) : AgentAction;

/// <summary>
/// Provider-neutral terminal step. Memory proposals travel beside actions and are not actions.
/// The step has no hidden-reasoning field.
/// </summary>
public sealed record AgentStep(
    AgentStepDisposition Disposition,
    IReadOnlyList<AgentAction> Actions,
    IReadOnlyList<MemoryProposal> MemoryProposals);
