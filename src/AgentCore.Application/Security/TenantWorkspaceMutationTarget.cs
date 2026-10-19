using System.Text.Json;
using AgentCore.Application.Agents;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Security;

public sealed record TenantWorkspaceMutationObservationTarget(AgentRun Run, TenantSecurityState State, TenantAgent Agent,
    TenantWorkspaceMutationArguments Arguments, string Permission, AccessScope Scope, string ObservationHash);

// Preparation supplies private source evidence only. Primary mutation authority is decided by the approval owner.
public sealed class TenantWorkspaceMutationTarget(TenantOperationAuthorizer operations, ITenantSessionStore sessions,
    TenantRunConfigurationResolver configuration, IamAuthorizer iam, ITenantWorkspaceMutationObservationStore files, TimeProvider time)
{
    public static (string Permission, AccessScope Scope) Primary(AgentRun run, TenantWorkspaceMutationArguments args)
    {
        var target = args.Targets.FirstOrDefault(t => t.Permission == "workspaces.delete");
        if (target.Path is null) target = args.Targets.First(t => t.Permission == "workspaces.write");
        return (target.Permission, TenantWorkspaceRules.Scope(TenantWorkspaceEffectRules.Owner(run, target.Path)));
    }

    public static IReadOnlyList<WorkspaceMutationObservationProbe> Probes(TenantWorkspaceMutationArguments args)
    {
        if (args.Tool is ToolCatalog.WorkspaceWrite or ToolCatalog.WorkspacePatch) return [new(args.Path!, true, false)];
        return args.Operations.SelectMany(o => o.Op switch {
            "mkdir" => new[] { new WorkspaceMutationObservationProbe(o.Path!, false, false) },
            "delete" => [new(o.Path!, false, o.Recursive)],
            _ => [new(o.Source!, false, true), new(o.Destination!, false, false)]
        }).Distinct().ToArray();
    }

    public async ValueTask<TenantWorkspaceMutationObservationTarget> PrepareAsync(TenantRuntimeFrame frame, ModelToolCall call, CancellationToken ct = default)
    {
        var args = TenantWorkspaceMutationContract.Parse(frame.SessionId, call.Name, call.ArgumentsJson);
        var before = await Require(frame, args, ct).ConfigureAwait(false);
        var probes = Probes(args); long bytes = 0; var nodes = 0; var hashes = new List<object>();
        foreach (var group in probes.GroupBy(p => TenantWorkspaceEffectRules.Owner(before.Run, p.Path)))
        {
            WorkspaceMutationObservation observed;
            try { observed = await files.ObserveMutationTenantAsync(group.Key, group.ToArray(), ct).ConfigureAwait(false); }
            catch (Exception e) when (e is AgentCoreException or IOException or UnauthorizedAccessException)
            { throw new AccessDeniedException("workspace_observation_unavailable"); }
            if (observed.Hash is not { Length: 64 } || observed.Hash.Any(c => c is not (>= '0' and <= '9') and not (>= 'a' and <= 'f'))
                || observed.Nodes < 0 || observed.Bytes < 0 || observed.Nodes > WorkspaceMutationObservationLimits.MaxNodes - nodes
                || observed.Bytes > WorkspaceMutationObservationLimits.MaxBytes - bytes) throw new AccessDeniedException("workspace_observation_unavailable");
            nodes += observed.Nodes; bytes += observed.Bytes;
            hashes.Add(new { owner = group.Key, probes = group.ToArray(), observed.Hash });
        }
        ct.ThrowIfCancellationRequested(); var after = await Require(frame, args, ct).ConfigureAwait(false);
        if (after.State.Tenant.Revision != before.State.Tenant.Revision || after.Run.Revision != before.Run.Revision
            || after.Agent.Instance.Revision != before.Agent.Instance.Revision || after.Agent.Instance.PersonaRevision != before.Agent.Instance.PersonaRevision)
            throw new SecurityConflictException("workspace_source_changed");
        var hash = TenantWorkspaceMutationContract.Hash(JsonSerializer.Serialize(new { frame.Owner, frame.SessionId, args.ParametersHash, sources = hashes }));
        return before with { ObservationHash = hash };
    }

    private async ValueTask<TenantWorkspaceMutationObservationTarget> Require(TenantRuntimeFrame frame, TenantWorkspaceMutationArguments args, CancellationToken ct)
    {
        var reference = new TenantRunReference(frame.Owner.TenantId ?? throw new AccessDeniedException("tenant_context_required"), frame.RunId);
        var (run, state) = await operations.ResolveAsync(reference, ct).ConfigureAwait(false);
        var current = await configuration.CurrentPolicyAsync(reference.TenantId, run.AgentInstanceId, ct).ConfigureAwait(false);
        var session = await sessions.FindTenantSessionAsync(reference.TenantId, frame.SessionId, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("resource_unavailable");
        TenantRuntimeRules.Require(frame, session.Owner, run, time.GetUtcNow()); TenantWorkspaceEffectRules.Session(run, current.Agent, session);
        if (run.Admission.OutputContract != AgentRunOutputContract.ConversationResponse
            || run.Admission.ExecutionContext?.Origin is not (ExecutionOrigin.UserMessage or ExecutionOrigin.ApprovalContinuation)
            || !TenantWorkspaceMutationContract.Allows(run.Admission.Configuration!.Definition, args.Tool)
            || !TenantWorkspaceMutationContract.Allows(current.Definition, args.Tool)) throw new AccessDeniedException("capability_unavailable");
        var isolation = StateIsolationAccess.RestrictDefinition(run.Admission.Configuration.Definition, current.Definition).StateIsolationPolicy ?? StateIsolationPolicy.Default;
        foreach (var (path, permission) in args.Targets) TenantWorkspaceEffectRules.RequireHome(isolation, path, permission != "workspaces.read");
        var primary = Primary(run, args);
        foreach (var (key, scope) in TenantWorkspaceEffectRules.Targets(run, args))
        {
            var permission = PermissionCatalog.Get(key);
            var allowed = await iam.CheckOwnedTenantAsync(state, current.Agent.PrincipalId, permission, scope, ct).ConfigureAwait(false);
            if (allowed.Allowed) continue;
            if (key == primary.Permission && scope == primary.Scope && permission.Delegatable && !permission.HumanOnly && !permission.SystemOnly
                && allowed.ReasonCode == "permission_denied") continue;
            throw new AccessDeniedException(key.EndsWith(".read", StringComparison.Ordinal) ? "executor_read_required" : "permission_denied");
        }
        return new(run, state, current.Agent, args, primary.Permission, primary.Scope, "");
    }
}
