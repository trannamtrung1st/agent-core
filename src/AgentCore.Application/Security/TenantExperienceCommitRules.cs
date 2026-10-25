using AgentCore.Application.Ports;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Experience;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Security;

public static class TenantExperienceCommitRules
{
    public static PreparedTenantOperation Require(TenantExperienceOperationObservation observed, TenantExperienceCommit commit, DateTimeOffset at)
    {
        var run = observed.Reader.Run; var target = observed.Reader.Agent;
        TenantConfigurationContinuationRules.RequireDirectWrite(run);
        TenantLocalEffectRules.RequireDirectRun(run, commit.Generation, at);
        var prepared = TenantApprovalRules.PrepareExperience(commit.OperationId, run.AgentInstanceId, observed.Value, target.Instance.Revision, target.Instance.PersonaRevision);
        prepared = TenantApprovalRules.ForRun(prepared, run);
        if (observed.Arguments.Tool != TenantExperienceToolContract.Record || commit.RunId != run.AgentRunId || commit.ExpectedRunRevision != run.Revision
            || commit.ExpectedInstanceRevision != target.Instance.Revision || commit.Prepared != observed.Value || commit.OperationId == Guid.Empty
            || string.IsNullOrWhiteSpace(commit.SourceCallId) || commit.SourceCallId.Length > 200
            || commit.ParametersHash != TenantMemoryOperationRules.Hash(observed.Arguments.CanonicalArguments) || commit.ActionHash != prepared.ActionHash)
            throw new SecurityConflictException("operation_mismatch");
        return prepared;
    }
    public static void Transaction(TenantSecurityState next, long expected, AgentRun run, AuthorizationAudit audit)
    {
        if (next.Tenant.TenantId != run.Owner.TenantId || next.Tenant.Revision != checked(expected + 1) || audit.TenantId != next.Tenant.TenantId
            || audit.ActorPrincipalId != run.Owner.ExecutorPrincipalId || audit.Action != "operations.experience.record" || audit.Target != run.AgentInstanceId.ToString("D")
            || audit.Decision != "allowed" || audit.ReasonCode != TenantExperienceRules.Recorded) throw new ArgumentException("Invalid direct Experience transaction.");
    }
    public static void SourcesShape(AgentRun run, IReadOnlyList<TenantExperienceSourceReference> sources, IReadOnlyList<TenantExperienceFactSource> facts)
    {
        if (sources.Count > 100 || facts.Count > 100 || sources.Distinct().Count() != sources.Count || facts.Distinct().Count() != facts.Count
            || sources.Any(s => s.TenantId != run.Owner.TenantId) || facts.Any(s => s.TenantId != run.Owner.TenantId || s.ExperienceId == Guid.Empty
                || s.Revision < 1 || !TenantMemoryOperationRules.ValidHash(s.ContentHash))) throw new AccessDeniedException("experience_source_unavailable");
        foreach (var source in sources) TenantExperienceRules.SourceShape(source.Witness);
    }
    public static bool FactMatches(TenantExperienceFactSource source, TenantExperienceReference item) => source.TenantId == item.TenantId && source.ExperienceId == item.ExperienceId
        && source.ContentHash == item.ContentHash && source.Revision == item.Revision && item.Visibility == ExperienceVisibility.Eligible;
}
