using AgentCore.Application.Execution;
using AgentCore.Domain.Conversation;

namespace AgentCore.Api.Realtime;

public sealed partial class TenantSessionLiveDelivery
{
    private async ValueTask<bool> CanReleasePrivateSourcesAsync(AgentRun run, CancellationToken ct)
    {
        var state = AgentRunToolCallCheckpoint.ReadExecutionState(run.Checkpoint);
        var learnedSources = AgentCore.Application.Security.TenantMemoryOperationReferences.Read(run.Checkpoint);
        var historical = AgentCore.Application.Security.TenantHistoricalReferences.Read(run.Checkpoint);
        var experience = AgentCore.Application.Security.TenantExperienceReferences.Sources(run.Checkpoint).Count > 0 || AgentCore.Application.Security.TenantExperienceReferences.Facts(run.Checkpoint).Count > 0;
        if (state.TenantProfileSource is null && learnedSources is not { Count: > 0 } && historical is not { Count: > 0 } && !experience && run.Admission.ComposerInput is null) return true;
        if (state.TenantProfileSource is not null && profiles is null || learnedSources is { Count: > 0 } && memories is null) return false;
        if ((historical.Count > 0 || experience) && continuitySources is null) return false;
        if (run.Admission.ComposerInput is not null && composer is null) return false;
        try
        {
            if (run.Admission.ComposerInput is not null) await composer!.RequireAsync(run, ct).ConfigureAwait(false);
            if (state.TenantProfileSource is { } source) await profiles!.RequireAsync(run, source, ct).ConfigureAwait(false);
            if (learnedSources is { Count: > 0 } learned) await memories!.RequireAsync(run, learned, ct).ConfigureAwait(false);
            if (historical.Count > 0) await continuitySources!.RequireAsync(run, historical, null, ct).ConfigureAwait(false);
            if (experience) await continuitySources!.RequireExperienceAsync(run, ct).ConfigureAwait(false);
            return true;
        }
        catch (Exception error) when (error is AgentCore.Application.Security.AccessDeniedException or AgentCore.Application.Security.SecurityConflictException) { return false; }
    }
}
