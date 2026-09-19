namespace AgentCore.Infrastructure.Providers.OpenAICompatible;

/// <summary>
/// Field-presence summary for one streamed chat completion choice delta (no prompt or user content).
/// </summary>
public sealed record StreamChoiceDiagnostic(
    bool Content,
    bool Reasoning,
    bool ReasoningDetails,
    bool ToolCalls,
    string? FinishReason);
