using AgentCore.Application.Ports;

namespace AgentCore.Application.Memory;

/// <summary>
/// Whether the selected model can deliver a structured memory proposal.
/// Policy allows storage. This says whether the model can reliably ask for it.
/// </summary>
public static class MemoryProposalChannels
{
    public static bool IsReliable(ModelCapabilities? capabilities) =>
        capabilities?.StructuredOutput == true || capabilities?.Tools == true;
}
