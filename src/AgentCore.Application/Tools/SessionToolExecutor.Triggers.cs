using AgentCore.Application.Triggers;
using AgentCore.Domain.Triggers;

namespace AgentCore.Application.Tools;

public sealed partial class SessionToolExecutor
{
    public async ValueTask<ScheduleConversationContext?> TryReconstructScheduleConversationContextAsync(
        Guid? agentInstanceId,
        Guid? profileId,
        CancellationToken cancellationToken = default)
    {
        if (triggerRegistrations is null || agentInstanceId is not Guid instance || profileId is not Guid profile)
        {
            return null;
        }

        return await ScheduleConversationContext.TryReconstructLatestReferentAsync(
            triggerRegistrations,
            new TriggerOwner(instance, profile),
            cancellationToken).ConfigureAwait(false);
    }
}
