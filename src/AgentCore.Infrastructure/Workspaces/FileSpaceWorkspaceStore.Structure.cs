using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Sessions;
using AgentCore.Application.Workspaces;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;

namespace AgentCore.Infrastructure.Workspaces;

public sealed partial class FileSpaceWorkspaceStore
{
    private static string Concrete(TenantWorkspaceReference owner, string path)
    {
        path = TenantWorkspaceRules.Path(owner, path, file: true);
        if (path.IndexOfAny(['*', '?', '[', ']']) >= 0) throw AgentCoreErrors.Validation("Use a concrete workspace path.");
        return path;
    }
    private long RootBudget(TenantWorkspaceReference owner) => !owner.IsWorking ? maxSpaceBytes : WorkspaceLimits.MaxWritableBytes;

    public async ValueTask<WorkspacePatchResult> PatchTenantAsync(TenantWorkspaceReference owner, string path,
        IReadOnlyList<WorkspaceTextEdit> edits, Func<CancellationToken, ValueTask> authorizeCommit, CancellationToken ct = default)
    {
        using var mutationGate = await AcquireMutationGateAsync(ct, owner).ConfigureAwait(false);
        ArgumentNullException.ThrowIfNull(authorizeCommit);
        path = TenantWorkspaceRules.Path(owner, path, file: true);
        if (edits.Count is < 1 or > WorkspaceLimits.MaxPatchEdits) throw AgentCoreErrors.Validation("Patch requires 1–32 edits.");
        var saved = edits.ToArray();
        if (saved.Any(e => e is null || string.IsNullOrEmpty(e.OldText) || e.NewText is null)) throw AgentCoreErrors.Validation("Use exact text edits.");
        async ValueTask Gate(CancellationToken token)
        {
            await TenantFiles(owner, token, write: true).ConfigureAwait(false); await authorizeCommit(token).ConfigureAwait(false);
            await TenantFiles(owner, token, write: true).ConfigureAwait(false); token.ThrowIfCancellationRequested();
        }
        await Gate(ct).ConfigureAwait(false);
        var files = await TenantFiles(owner, ct, write: true).ConfigureAwait(false);
        var original = await files.ReadAsync(path, maxFileBytes, ct).ConfigureAwait(false);
        await Gate(ct).ConfigureAwait(false);
        var replacement = WorkspaceTextPatches.Apply(original, saved, maxFileBytes);
        await files.WriteAsync(path, replacement, maxFileBytes, RootBudget(owner), ct, async token => {
            await Gate(token).ConfigureAwait(false);
            var current = await files.ReadAsync(path, maxFileBytes, token).ConfigureAwait(false);
            if (!current.AsSpan().SequenceEqual(original)) throw new SecurityConflictException("workspace_source_changed");
            await Gate(token).ConfigureAwait(false);
        }).ConfigureAwait(false);
        // Factual acknowledgement of the completed primitive, with no post-effect private read.
        return new(path, replacement.LongLength, saved.Length);
    }

    public async ValueTask<WorkspaceStructureResult> StructureTenantAsync(TenantWorkspaceReference owner,
        IReadOnlyList<WorkspaceStructuralOperation> operations, Func<CancellationToken, ValueTask> authorizeCommit, CancellationToken ct = default)
    {
        using var mutationGate = await AcquireMutationGateAsync(ct, owner).ConfigureAwait(false);
        ArgumentNullException.ThrowIfNull(authorizeCommit);
        if (operations.Count is < 1 or > WorkspaceStructureLimits.MaxOperations) throw AgentCoreErrors.Validation("Use 1–16 filesystem operations.");
        var saved = operations.ToArray();
        foreach (var operation in saved)
        {
            if (operation is null) throw AgentCoreErrors.Validation("Use an exact filesystem operation.");
            foreach (var path in new[] { operation.Path, operation.Source, operation.Destination }.OfType<string>()) Concrete(owner, path);
        }
        if (JsonSerializer.SerializeToUtf8Bytes(saved).Length > WorkspaceStructureLimits.MaxRequestBytes) throw AgentCoreErrors.Validation("Use a bounded filesystem batch.");
        var files = await TenantFiles(owner, ct, write: true).ConfigureAwait(false);
        return await files.StructureAuthorizedAsync(saved, maxFileBytes, RootBudget(owner), async token => {
            await TenantFiles(owner, token, write: true).ConfigureAwait(false); await authorizeCommit(token).ConfigureAwait(false);
            await TenantFiles(owner, token, write: true).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
    }

    public async ValueTask CopyTenantAsync(TenantWorkspaceReference sourceOwner, string source, TenantWorkspaceReference destinationOwner,
        string destination, Func<CancellationToken, ValueTask> authorizeCommit, CancellationToken ct = default)
    {
        using var mutationGate = await AcquireMutationGateAsync(ct, sourceOwner, destinationOwner).ConfigureAwait(false);
        ArgumentNullException.ThrowIfNull(authorizeCommit);
        if (sourceOwner.TenantId != destinationOwner.TenantId
            || sourceOwner is TenantWorkspaceReference.SessionWorking sourceWorking && destinationOwner is TenantWorkspaceReference.SessionWorking destinationWorking && sourceWorking.SessionId != destinationWorking.SessionId) throw new AccessDeniedException("resource_unavailable");
        source = Concrete(sourceOwner, source); destination = Concrete(destinationOwner, destination);
        var from = await TenantFiles(sourceOwner, ct).ConfigureAwait(false);
        var to = await TenantFiles(destinationOwner, ct, write: true).ConfigureAwait(false);
        await from.CopyToAuthorizedAsync(source, to, destination, async token => {
            await TenantFiles(sourceOwner, token).ConfigureAwait(false); await TenantFiles(destinationOwner, token, write: true).ConfigureAwait(false);
            await authorizeCommit(token).ConfigureAwait(false);
            await TenantFiles(sourceOwner, token).ConfigureAwait(false); await TenantFiles(destinationOwner, token, write: true).ConfigureAwait(false);
        }, ct, maxFileBytes, RootBudget(destinationOwner)).ConfigureAwait(false);
    }
}
