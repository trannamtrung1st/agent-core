using System.Text;
using AgentCore.Application.Agents;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Sessions;
using AgentCore.Application.Workspaces;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;

namespace AgentCore.Infrastructure.Workspaces;

public sealed partial class FileSessionWorkspace
{
    public async ValueTask PrepareAsync(TenantRuntimeFrame frame, Guid executionId, CancellationToken ct = default)
    {
        var authority = _tenantAuthority ?? throw new AccessDeniedException("tenant_effect_unavailable");
        async ValueTask<TenantSandboxExecutionAuthorityView> Require()
        {
            var current = await authority.RequireAsync(frame, executionId, ct);
            if (!current.Execution.InitializesGeneration || current.Execution.CommandStartedAt is not null
                || current.Lease.State != SandboxLeaseState.Provisioning || current.Execution.Phase != "provisioning"
                || !current.Execution.FilesystemEffectsMayHaveOccurred)
                throw new SecurityConflictException("sandbox_provisioning_mismatch");
            return current;
        }
        await Require();
        var gate = _provisioning.GetOrAdd(frame.SessionId, _ => new(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            var current = await Require();
            var definition = current.Run.Admission.Configuration!.Definition;
            var session = new CanonicalWorkspaceFilesystem(AgentWorkspacePhysicalPaths.SessionRoot(_root, frame.Owner.AgentInstanceId, frame.SessionId), "/working");
            session.Ensure();
            const string marker = "/working/.provisioned";
            const string format = "tenant-workspace-1";
            var hasMarker = session.List("/working").Entries.Any(e => e.Path == marker);
            if (hasMarker)
            {
                if (Encoding.UTF8.GetString(await session.ReadUnlinkedAsync(marker, 256, ct)) != format)
                    throw new SecurityConflictException("sandbox_workspace_marker_mismatch");
                Working(frame.Owner.AgentInstanceId, frame.SessionId).RequireDirectory("/working");
                await Require(); return;
            }
            var template = DefinitionExecutionPolicies.Of(definition).WorkspacePolicy.TemplateId;
            var resources = (definition.ExecutionResources ?? []).Where(r => r.Kind == AgentDefinitionResourceKind.Template
                && !string.IsNullOrEmpty(template) && r.LogicalPath.StartsWith(template + "/", StringComparison.Ordinal)).ToArray();
            var targets = resources.Select(r => (Resource: r, Path: "/working/" + r.LogicalPath[(template!.Length + 1)..])).ToArray();
            if (targets.GroupBy(t => t.Path, StringComparer.Ordinal).Any(g => g.Count() > 1))
                throw new SecurityConflictException("sandbox_template_path_conflict");
            var files = Working(frame.Owner.AgentInstanceId, frame.SessionId);
            await Require(); files.Ensure();
            foreach (var target in targets.OrderBy(t => t.Path, StringComparer.Ordinal))
            {
                await Require();
                var bytes = await (_publicationResources ?? throw new AccessDeniedException("sandbox_template_unavailable")).ReadPinnedAsync(target.Resource, ct);
                await Require();
                if (!await files.TryWriteNewAsync(target.Path, bytes, AgentWorkspaceLimits.MaxFileBytes, _maxWritableBytes, ct))
                {
                    // A partial copy can converge only on identical ordinary bytes; never replace retained user data.
                    var retained = await files.ReadUnlinkedAsync(target.Path, AgentWorkspaceLimits.MaxFileBytes, ct);
                    if (!bytes.AsSpan().SequenceEqual(retained))
                        throw new SecurityConflictException("sandbox_template_destination_occupied");
                }
                await Require();
            }
            await Require();
            if (!await session.TryWriteNewAsync(marker, Encoding.UTF8.GetBytes(format), 256, long.MaxValue, ct))
                throw new SecurityConflictException("sandbox_workspace_marker_mismatch");
            await Require();
        }
        finally { gate.Release(); }
    }
}
