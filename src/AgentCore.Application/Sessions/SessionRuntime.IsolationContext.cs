using AgentCore.Application.Context;
using AgentCore.Application.Memory;
using AgentCore.Domain.Definitions;

namespace AgentCore.Application.Sessions;

public sealed partial class SessionRuntime
{
    private async ValueTask<EffectiveContextSnapshot> RestrictContinuityContextAsync(EffectiveContextSnapshot context,
        AgentDefinition effective, CancellationToken ct)
    {
        var policy = effective.StateIsolationPolicy ?? StateIsolationPolicy.Default;
        if (!policy.ProfileEvidence) context = context.Remove(ContextSectionId.Profile);
        if (!policy.ExperienceRecall) context = context.Remove(ContextSectionId.Experience);
        if (!policy.CrossSessionContinuity) context = context.Remove(ContextSectionId.Continuity);
        if (_runtimeOwner is not null) context = context.Remove(ContextSectionId.Memory);
        else if (effective.MemoryPolicy != ExecutionDefinition.MemoryPolicy)
        {
            // A live restriction can narrow individual scopes; refresh that Evidence block through
            // its existing policy-aware reader rather than replaying an earlier, broader projection.
            var learned = await SessionMemoryPrompt.LoadAsync(_structuredMemory, SessionId, effective, _profile,
                _snapshot.Entries, ct, _snapshot.AgentInstanceId);
            var text = SessionMemoryPrompt.Render(learned);
            context = context.Remove(ContextSectionId.Memory);
            if (text.Length > 0) context = context.Upsert(ContextSection.Evidence(ContextSectionId.Memory, ContextSourceKind.Memory, text));
        }
        return context;
    }
}
