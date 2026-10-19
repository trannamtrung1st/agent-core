using AgentCore.Application.Events;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Definitions;

namespace AgentCore.Application.Sessions;

public sealed partial class SessionRuntime
{
    // Core's provider/effect recovery starts after checkpoint, cwd and projection preparation.
    // Supervise that whole worker lifetime so an earlier fault cannot abandon its claimed Run.
    private async Task PumpModelAsync(ILanguageModel model, ModelRequest request, EventContext cause, AgentTrigger trigger,
        IReadOnlyList<EffectiveSkill> catalog, IReadOnlyList<string> skills, int turn, CancellationToken ct)
    {
        try { await PumpModelCoreAsync(model, request, cause, trigger, catalog, skills, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            await QueueBrainFailureAsync(cause, trigger, request.ResponseId, turn, recoverable: false, requireActiveResponse: true).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            var diagnostic = _diagnostics.NewId();
            LogConversationFailure(cause, request.ResponseId, diagnostic, "Session", "ModelPreparationFailed",
                "The agent could not prepare a response.", error);
            await QueueBrainFailureAsync(cause, trigger, request.ResponseId, turn, recoverable: true, diagnostic,
                authorityUnavailable: error is AccessDeniedException or SecurityConflictException or AgentCoreException { Code: "Forbidden" },
                requireActiveResponse: true).ConfigureAwait(false);
        }
    }
}
