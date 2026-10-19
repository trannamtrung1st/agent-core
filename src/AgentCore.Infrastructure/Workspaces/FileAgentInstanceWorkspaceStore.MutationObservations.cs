using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Application.Security;

namespace AgentCore.Infrastructure.Workspaces;

public sealed partial class FileAgentInstanceWorkspaceStore : ITenantWorkspaceMutationObservationStore
{
    public async ValueTask<WorkspaceMutationObservation> ObserveMutationTenantAsync(TenantWorkspaceReference owner,
        IReadOnlyList<WorkspaceMutationObservationProbe> probes, CancellationToken ct = default)
    {
        if (probes.Count is < 1 or > WorkspaceMutationObservationLimits.MaxProbes) throw AgentCoreErrors.Validation("Use bounded mutation observations.");
        var saved = probes.ToArray();
        foreach (var probe in saved)
        {
            if (probe is null) throw AgentCoreErrors.Validation("Use exact mutation observations.");
            TenantWorkspaceRules.Path(owner, probe.Path, file: true);
        }
        var files = await TenantFiles(owner, ct).ConfigureAwait(false);
        var result = await files.ObserveMutationAsync(saved, maxFileBytes, RootBudget(owner), ct).ConfigureAwait(false);
        await TenantFiles(owner, ct).ConfigureAwait(false); ct.ThrowIfCancellationRequested();
        return result;
    }
}
