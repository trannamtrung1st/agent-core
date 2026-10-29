using AgentCore.Application.Execution;
using AgentCore.Application.Models;
using AgentCore.Application.Ports;
using AgentCore.Application.Tools;

namespace AgentCore.Application.Sessions;

internal sealed record ModelRoundPhase(
    bool CompletionRequested,
    bool InitialBackgroundRun,
    bool CheckpointCapacityReached,
    bool CompletionReport,
    bool Cleanup,
    string? FinalizationReason);

/// <summary>Pure phase-specific projection. The pump owns phase transitions, authority and execution.</summary>
internal static class ModelRoundRequestPolicy
{
    internal static ModelRequest ForRepair(ModelRequest request, bool toolsOffered) => request with
    {
        Tools = null,
        ToolChoice = ModelToolChoice.Auto,
        ToolChoiceName = null,
        ResponseContract = null,
        MaxOutputTokens = ModelOutputBudgets.ForTurn(request.MaxOutputTokens, toolsOffered)
    };

    internal static ModelRequest ForPhase(ModelRequest working, ModelRoundPhase phase)
    {
        if (phase.CompletionRequested) working = working with { Tools = null, ToolChoice = ModelToolChoice.Auto, ToolChoiceName = null };
        if (phase.InitialBackgroundRun) working = working with { ResponseContract = null };
        if (phase.CheckpointCapacityReached) working = working with { Tools = phase.InitialBackgroundRun ? [AgentCore.Application.Work.WorkCompletionRequest.Contract] : null };
        if (phase.CompletionReport) working = working with { Tools = null };
        if (phase.Cleanup && phase.FinalizationReason is null && working.Tools is { } cleanupTools)
            working = working with { Tools = RunFinalization.CleanupTools(cleanupTools) };
        if (phase.FinalizationReason is not null) working = phase.InitialBackgroundRun
            ? working with { Tools = [AgentCore.Application.Work.WorkCompletionRequest.Contract],
                ToolChoice = ModelToolChoice.Named, ToolChoiceName = ToolCatalog.WorkComplete, ResponseContract = null,
                MaxOutputTokens = 4096 }
            : working with { Tools = null, ToolChoice = ModelToolChoice.Auto, ToolChoiceName = null,
                MaxOutputTokens = Math.Min(working.MaxOutputTokens, 2048) };
        return working;
    }
}
