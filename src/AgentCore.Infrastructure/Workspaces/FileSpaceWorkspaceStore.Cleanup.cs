using AgentCore.Application.Security;
using AgentCore.Domain.Conversation;

namespace AgentCore.Infrastructure.Workspaces;

public sealed partial class FileSpaceWorkspaceStore
{
    public async ValueTask PurgeDeletedSpaceAsync(TenantWorkspaceReference.SpaceHome owner, CancellationToken ct = default)
    {
        using var mutationGate = await AcquireMutationGateAsync(ct, owner).ConfigureAwait(false);
        if (!await tenantSpaces.CanPurgeDeletedSpaceWorkspaceAsync(owner, ct).ConfigureAwait(false))
            throw new SecurityConflictException("space_workspace_cleanup_pending");
        CanonicalWorkspaceFilesystem.PurgeOwner(AgentWorkspacePhysicalPaths.SpaceHomeDirectory(_root, owner.TenantId, owner.SpaceId), ct);
    }

    // Bounded, process-local gates cover Core-mediated mutation and tombstone cleanup, including
    // /home and /private aliases. Direct shell writes retain ordinary filesystem concurrency.
    private static readonly SemaphoreSlim[] MutationGates = Enumerable.Range(0, 127).Select(_ => new SemaphoreSlim(1, 1)).ToArray();
    private static int GateIndex(TenantWorkspaceReference owner) => (int)((uint)HashCode.Combine(owner.TenantId, owner.OwnerId, owner.IsWorking) % MutationGates.Length);
    private static async ValueTask<IDisposable> AcquireMutationGateAsync(CancellationToken ct, params TenantWorkspaceReference[] owners)
    {
        var indices = owners.Select(GateIndex).Distinct().Order().ToArray(); var acquired = new List<SemaphoreSlim>();
        try
        {
            foreach (var index in indices) { var gate = MutationGates[index]; await gate.WaitAsync(ct).ConfigureAwait(false); acquired.Add(gate); }
            return new MutationGateLease(acquired);
        }
        catch { foreach (var gate in acquired.AsEnumerable().Reverse()) gate.Release(); throw; }
    }
    private sealed class MutationGateLease(IReadOnlyList<SemaphoreSlim> gates) : IDisposable
    {
        public void Dispose() { foreach (var gate in gates.Reverse()) gate.Release(); }
    }
}
