using AgentCore.Domain.Conversation;

namespace AgentCore.Application.Ports;

public sealed record WorkspaceMutationObservationProbe(string Path, bool FollowLinks, bool Recursive);
public sealed record WorkspaceMutationObservation(string Hash, int Nodes, long Bytes);

// Private source evidence only. This facet neither returns readable file data nor grants an effect.
public interface ITenantWorkspaceMutationObservationStore
{
    ValueTask<WorkspaceMutationObservation> ObserveMutationTenantAsync(TenantWorkspaceReference owner,
        IReadOnlyList<WorkspaceMutationObservationProbe> probes, CancellationToken ct = default);
}

public static class WorkspaceMutationObservationLimits
{
    public const int MaxProbes = 32;
    public const int MaxNodes = 4096;
    public const int MaxDepth = 64;
    public const long MaxBytes = AgentWorkspaceLimits.MaxSpaceBytes;
}
