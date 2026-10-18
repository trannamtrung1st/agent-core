using System.Text;
using System.Text.Json;
using AgentCore.Application.Agents;
using AgentCore.Application.Ports;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Security;

public sealed partial class TenantSandboxRuntimeReadService
{
    private static readonly JsonSerializerOptions ModelJson = new(JsonSerializerDefaults.Web);
    private sealed record ModelRead(AgentRun Run, TenantSecurityState Security, TenantSession Session, TenantAgent Agent, AgentDefinition Baseline);
    private sealed record ModelProfile(string Id, string Name, string Description, long Revision, bool Available, string Availability, bool IsDefault, bool Selected);

    public ValueTask<string> InspectForRunAsync(TenantRuntimeFrame frame, ModelToolCall call, int remainingBytes, CancellationToken ct = default) =>
        InspectCore(frame, call, remainingBytes, ct);
    private async ValueTask<string> InspectCore(TenantRuntimeFrame frame, ModelToolCall call, int remainingBytes, CancellationToken ct, ApprovedRead? consent = null)
    {
        var executionId = TenantSandboxInspectionContract.Parse(call);
        var read = await RequireModel(frame, call.Name, ct, consent?.Request.Operation.SandboxRead).ConfigureAwait(false);
        var tenant = read.Security.Tenant.TenantId;
        var snapshot = await storage.ReadTenantSandboxRuntimeAsync(tenant, frame.SessionId, executionId, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("resource_unavailable");
        if (snapshot.Owner != read.Session.Owner || snapshot.SessionRevision != read.Session.Snapshot.Revision) throw new SecurityConflictException("stale_revision");
        if (consent is not null)
        {
            await RequireBound(read, consent.Request, snapshot, ct).ConfigureAwait(false);
            await TenantSandboxReadOperationRules.RequireReviewerAsync(read.Security, read.Session.Owner, consent.Request.Operation.SandboxRead!,
                consent.Request.OriginalInitiatorPrincipalId, consent.Human, authorizer, ct).ConfigureAwait(false);
        }
        if (call.Name == ToolCatalog.SandboxProfiles) return await Profiles(frame, read, snapshot.Lease, remainingBytes, ct, consent?.Request.Operation.SandboxRead).ConfigureAwait(false);
        if (executionId is not null && snapshot.Execution is null) throw new AccessDeniedException("resource_unavailable");
        foreach (var id in new[] { snapshot.Lease?.Profile.Id, snapshot.Execution?.ProfileId }.OfType<string>().Distinct(StringComparer.Ordinal))
            if (!await CanReadProfile(read, id, ct, consent?.Request.Operation.SandboxRead, true).ConfigureAwait(false)) throw new AccessDeniedException("permission_denied");
        var target = snapshot.Lease?.SelectionWitness is not null ? snapshot.Lease.TransitionProfileId : null;
        var readableSelection = target is not null && await CanReadProfile(read, target, ct, consent?.Request.Operation.SandboxRead).ConfigureAwait(false);
        await FenceModel(frame, read, ct, consent?.Request.Operation.SandboxRead).ConfigureAwait(false);
        var view = Project(snapshot, read.Security.Tenant.Revision, readableSelection);
        var environment = view.Environment is null ? null : view.Environment with { Initialization = null };
        for (var streamBudget = 16384; ; streamBudget /= 2)
        {
            var e = view.Execution;
            var stdout = call.Name == ToolCatalog.SandboxProcessStatus ? ToolJsonResults.ClipUtf8Prefix(e?.Stdout ?? "", streamBudget) : "";
            var stderr = call.Name == ToolCatalog.SandboxProcessStatus ? ToolJsonResults.ClipUtf8Prefix(e?.Stderr ?? "", streamBudget) : "";
            var result = JsonSerializer.Serialize(new { sessionId = frame.SessionId, environment,
                initializationState = snapshot.Lease?.Initialization?.State,
                execution = e is null ? null : e with { CommandText = "", Stdout = stdout, Stderr = stderr,
                    Truncated = e.Truncated || call.Name == ToolCatalog.SandboxProcessStatus && (stdout != e.Stdout || stderr != e.Stderr) },
                observationOnly = true, liveAuthorityConfirmed = false }, ModelJson);
            if (Encoding.UTF8.GetByteCount(result) <= remainingBytes || streamBudget == 0) return ToolJsonResults.FitToBudget(remainingBytes, result);
        }
    }

    private async ValueTask<ModelRead> RequireModel(TenantRuntimeFrame frame, string tool, CancellationToken ct, PreparedSandboxRead? consent = null)
    {
        var read = await ReadContext(new(frame.Owner.TenantId ?? throw new AccessDeniedException("tenant_context_required"), frame.RunId), tool, ct).ConfigureAwait(false);
        TenantRuntimeRules.Require(frame, read.Session.Owner, read.Run, (time ?? TimeProvider.System).GetUtcNow());
        if (consent is null)
            foreach (var target in ReadTargets(read.Session.Owner, []))
            {
                var decision = await operations!.Check(read.Security, read.Run, target.Permission, target.Scope, ct, purpose: ExecutionAuthorizationPurpose.ToolBoundary).ConfigureAwait(false);
                if (decision.Decision != OperationDecisionKind.Allowed)
                    throw new AccessDeniedException(decision.Decision == OperationDecisionKind.RequiresApproval ? "sandbox_read_review_required" : decision.ReasonCode);
            }
        return read;
    }
    private async ValueTask<ModelRead> ReadContext(TenantRunReference reference, string tool, CancellationToken ct)
    {
        if (operations is null || runConfiguration is null) throw new AccessDeniedException("tenant_context_required");
        var (run, state) = await operations.ResolveAsync(reference, ct).ConfigureAwait(false);
        var session = await sessions.FindTenantSessionAsync(reference.TenantId, run.SessionId, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("resource_unavailable");
        var (agent, baseline) = await runConfiguration.BaselineAsync(reference.TenantId, run.AgentInstanceId, ct).ConfigureAwait(false);
        var current = InstanceSettingsResolver.Resolve(baseline, agent.Instance.SettingsOverrides);
        if (run.Admission.Configuration is null || !TenantSandboxInspectionContract.Allows(run.Admission.Configuration.Definition, tool)
            || !TenantSandboxInspectionContract.Allows(current, tool)) throw new AccessDeniedException("sandbox_policy_changed");
        if (session.Snapshot.ArchivedAt is not null || session.Snapshot.DurablyDeletedAt is not null || session.Snapshot.LifecycleStatus != SessionLifecycleStatus.Active
            || session.Snapshot.Status is SessionStatus.Ended or SessionStatus.Ending) throw new AccessDeniedException("resource_unavailable");
        await RequireRead(state, session.Owner, agent.PrincipalId, [], ct).ConfigureAwait(false);
        return new(run, state, session, agent, baseline);
    }
    private async ValueTask<bool> CanReadProfile(ModelRead read, string id, CancellationToken ct, PreparedSandboxRead? consent = null, bool requireReviewReason = false)
    {
        var scope = TenantSandboxProfileRules.Scope(read.Security.Tenant.TenantId, id);
        if (!(await authorizer.CheckTenantAsync(read.Security, read.Agent.PrincipalId, PermissionCatalog.Get("sandbox.read"), scope, ct).ConfigureAwait(false)).Allowed) return false;
        if (consent is not null) return TenantApprovalRules.ReadProfiles(consent).Any(p => p.Id == id);
        var decision = await operations!.Check(read.Security, read.Run, "sandbox.read", scope, ct, purpose: ExecutionAuthorizationPurpose.ToolBoundary).ConfigureAwait(false);
        if (requireReviewReason && decision.Decision != OperationDecisionKind.Allowed)
            throw new AccessDeniedException(decision.Decision == OperationDecisionKind.RequiresApproval ? "sandbox_read_review_required" : decision.ReasonCode);
        return decision.Decision == OperationDecisionKind.Allowed;
    }
    private async ValueTask FenceModel(TenantRuntimeFrame frame, ModelRead before, CancellationToken ct, PreparedSandboxRead? consent = null)
    {
        if (consent is not null)
        {
            var snapshot = await storage.ReadTenantSandboxRuntimeAsync(before.Security.Tenant.TenantId, frame.SessionId,
                consent.Kind == SandboxReadKind.Process ? consent.ExecutionId : null, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("resource_unavailable");
            if (snapshot.Owner != before.Session.Owner || consent.TargetHash != TenantSandboxReadOperationRules.TargetHash(consent.Kind, before.Session.Owner, snapshot))
                throw new SecurityConflictException("sandbox_read_target_changed");
        }
        var (run, state) = await operations!.ResolveAsync(new(before.Security.Tenant.TenantId, frame.RunId), ct).ConfigureAwait(false);
        var session = await sessions.FindTenantSessionAsync(state.Tenant.TenantId, frame.SessionId, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("resource_unavailable");
        TenantRuntimeRules.Require(frame, session.Owner, run, (time ?? TimeProvider.System).GetUtcNow());
        var (agent, _) = await runConfiguration!.BaselineAsync(state.Tenant.TenantId, frame.Owner.AgentInstanceId, ct).ConfigureAwait(false);
        if (run.Revision != before.Run.Revision || state.Tenant.Revision != before.Security.Tenant.Revision || session.Owner != before.Session.Owner
            || session.Snapshot.Revision != before.Session.Snapshot.Revision || agent.Instance.Revision != before.Agent.Instance.Revision)
            throw new SecurityConflictException("stale_revision");
        var final = await security.ReadTenantAsync(before.Security.Tenant.TenantId, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("tenant_unavailable");
        if (final.Tenant.Revision != before.Security.Tenant.Revision) throw new SecurityConflictException("stale_revision");
    }
    private async ValueTask<string> Profiles(TenantRuntimeFrame frame, ModelRead read, SandboxLease? lease, int budget, CancellationToken ct, PreparedSandboxRead? consent = null)
    {
        if (profileStore is null || images is null || readiness is null) throw new AccessDeniedException("tenant_effect_unavailable");
        var definition = read.Run.Admission.Configuration!.Definition;
        var current = InstanceSettingsResolver.Resolve(read.Baseline, read.Agent.Instance.SettingsOverrides);
        var selected = lease is null ? definition.SandboxPolicy?.DefaultProfileId : lease.CleanupReason == "reset" && lease.SelectionWitness is null ? null
            : lease.TransitionProfileId ?? lease.SelectedProfileId ?? lease.Profile.Id;
        var profiles = new List<ModelProfile>();
        foreach (var pinned in (definition.ExecutionSandboxProfiles ?? []).Take(16))
        {
            if (definition.SandboxPolicy?.AllowedProfileIds.Contains(pinned.Id, StringComparer.Ordinal) != true
                || current.SandboxPolicy?.AllowedProfileIds.Contains(pinned.Id, StringComparer.Ordinal) != true || !await CanReadProfile(read, pinned.Id, ct, consent, true).ConfigureAwait(false)) continue;
            var source = await profileStore.FindTenantSandboxProfileAsync(read.Security.Tenant.TenantId, pinned.Id, pinned.Revision, ct).ConfigureAwait(false);
            var latest = await profileStore.FindTenantSandboxProfileAsync(read.Security.Tenant.TenantId, pinned.Id, ct: ct).ConfigureAwait(false);
            if (source is null || latest is null) continue;
            var available = await images.InspectAsync(pinned, ct).ConfigureAwait(false);
            var compatible = true;
            try { TenantSandboxRuntimeRules.RequireCurrentPolicy(read.Run, read.Agent, read.Baseline, pinned, source, null); }
            catch (AccessDeniedException) { compatible = false; }
            var eligible = pinned.AdmittedForExecution == true && latest.Enabled && readiness.State == "Ready" && compatible && available.Eligible
                && available.CredentialRevision == pinned.RegistryCredentialRevision && available.SecretRevision == pinned.RegistrySecretRevision;
            profiles.Add(new(pinned.Id, pinned.Name, ToolJsonResults.ClipUtf8Prefix(pinned.Description, 160), pinned.Revision, eligible,
                eligible ? "Eligible" : pinned.AdmittedForExecution != true ? "NotAdmitted" : !latest.Enabled ? "Disabled" : !compatible ? "PolicyRestricted"
                    : readiness.State != "Ready" ? "ServerUnavailable" : available.Eligible ? "SourceChanged" : available.Status,
                pinned.Id == definition.SandboxPolicy?.DefaultProfileId, pinned.Id == selected));
        }
        await FenceModel(frame, read, ct, consent).ConfigureAwait(false);
        var total = profiles.Count;
        var visible = profiles.Select(p => p.Id).ToHashSet(StringComparer.Ordinal);
        var defaultId = definition.SandboxPolicy?.DefaultProfileId is { } savedDefault && visible.Contains(savedDefault) ? savedDefault : null;
        var selectedId = selected is not null && visible.Contains(selected) ? selected : null;
        while (true)
        {
            var result = JsonSerializer.Serialize(new { profiles, totalCount = total, truncated = profiles.Count < total,
                defaultProfileId = defaultId, selectedProfileId = selectedId, selectionRestricted = selected is not null && selectedId is null,
                descriptionsAreInformational = true, observationOnly = true, liveAuthorityConfirmed = false }, ModelJson);
            if (Encoding.UTF8.GetByteCount(result) <= budget || profiles.Count == 0) return ToolJsonResults.FitToBudget(budget, result);
            profiles.RemoveAt(profiles.Count - 1);
        }
    }
}
