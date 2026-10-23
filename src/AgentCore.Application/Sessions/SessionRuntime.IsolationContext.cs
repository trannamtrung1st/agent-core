using AgentCore.Application.Context;
using AgentCore.Domain.Definitions;

namespace AgentCore.Application.Sessions;

public sealed partial class SessionRuntime
{
    private static ValueTask<EffectiveContextSnapshot> RestrictContinuityContextAsync(EffectiveContextSnapshot context,
        AgentDefinition effective, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (effective.StateIsolationPolicy?.ProfileEvidence == false) context = context.Remove(ContextSectionId.Profile);
        // Private continuity is reprojected only through current tenant source owners and witnesses.
        return ValueTask.FromResult(context.Remove(ContextSectionId.Memory).Remove(ContextSectionId.Experience).Remove(ContextSectionId.Continuity));
    }
}
