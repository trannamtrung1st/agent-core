using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Experience;
using AgentCore.Domain.Security;

namespace AgentCore.Infrastructure.Security;
public sealed partial class InMemoryIamStore
{
    private TenantExperienceFactObservation ObserveExperienceFacts(AgentRun run, TenantContinuityArguments args, PreparedExperienceRead? expected, Guid? human, CancellationToken ct)
    {
        var reader = MemoryReader(run, ct); var settings = Isolation(run.AgentInstanceId);
        if (human is null) TenantExperienceRules.Eligible(reader, settings, TenantExperienceToolContract.Source);
        else
        {
            ValidateAgentActor(reader.Security, human.Value);
            if (expected is null || TenantExperienceFactRules.Sources(expected).Any(i => i.UserPrincipalId is { } owner && owner != human)) throw new AccessDeniedException("approval_unavailable");
        }
        TenantExperienceRules.RawAsync(reader, null, true, new IamAuthorizer(this), ct, human).AsTask().GetAwaiter().GetResult();
        var ids = expected is null ? null : TenantExperienceFactRules.Sources(expected).Select(i => i.ExperienceId).ToHashSet();
        var rows = OwnedExperience(reader).Where(i => ids is not null ? ids.Contains(i.ExperienceId) : i.Visibility == ExperienceVisibility.Eligible && (args.Id is null || i.ExperienceId == args.Id))
            .OrderByDescending(i => i.UpdatedAt).ThenBy(i => i.ExperienceId).Take(101).ToArray();
        return TenantExperienceFactRules.Observe(reader, settings, args, rows.Take(100), expected, human is not null, rows.Length > 100);
    }
    public ValueTask<TenantExperienceFactObservation> ObserveTenantExperienceFactsAsync(TenantRunReference reference, Guid? generation, TenantContinuityArguments arguments,
        PreparedExperienceRead? expected = null, Guid? inspectionHuman = null, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested(); lock (_gate) lock (sessionBacking.AdmissionGate) lock (instances.CredentialGate) lock (definitions.TenantGate) lock (isolationBacking.IsolationGate)
        {
            var run = sessionBacking.ReadRetainedTenantOperationRun(reference.TenantId, reference.RunId) ?? throw new AccessDeniedException("experience_unavailable");
            if (generation is { } claim) _ = RequireMemoryFrame(new(run.Owner, run.SessionId, run.AgentRunId, claim));
            return ValueTask.FromResult(ObserveExperienceFacts(run, TenantContinuityToolContract.Parse(arguments.Tool, arguments.CanonicalArguments), expected, inspectionHuman, ct));
        }
    }
}
