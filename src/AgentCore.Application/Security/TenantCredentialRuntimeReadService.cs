using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentCore.Application.Agents;
using AgentCore.Application.Credentials;
using AgentCore.Application.Execution;
using AgentCore.Application.Ports;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Credentials;
using AgentCore.Domain.Definitions;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Security;

public sealed partial class TenantCredentialRuntimeReadService(TenantOperationAuthorizer operations, ITenantRunStore runs,
    ITenantSessionStore sessions, ITenantCredentialBindingStore bindings, TenantRunConfigurationResolver configuration,
    IIamStore security, IamAuthorizer authorizer, TimeProvider time, ITenantApprovalStore? approvals = null, ITenantApprovedOperationStore? effects = null)
{
    private sealed record Read(AgentRun Run, TenantSecurityState State, TenantSession Session, TenantAgent Agent,
        AgentDefinition Current, IReadOnlyList<TenantCredentialBinding> Bindings);

    public async ValueTask<string> InspectAsync(TenantRuntimeFrame frame, string arguments, int remainingBytes, CancellationToken ct, string? tool = null)
    {
        try { return await InspectCoreAsync(frame, arguments, remainingBytes, ct, tool).ConfigureAwait(false); }
        catch (Exception ex) when (ex is AccessDeniedException or SecurityConflictException)
        {
            if (frame.Owner.TenantId is { } tenant)
                await operations.RecordFailureAsync(new(tenant, frame.RunId), "credentials.read", ex is AccessDeniedException denied ? denied.ReasonCode : ((SecurityConflictException)ex).ReasonCode,
                    ExecutionAuthorizationPurpose.ToolBoundary, ct, frame.SessionId).ConfigureAwait(false);
            throw;
        }
    }
    private async ValueTask<string> InspectCoreAsync(TenantRuntimeFrame frame, string arguments, int remainingBytes, CancellationToken ct, string? tool)
    {
        if (Encoding.UTF8.GetByteCount(arguments) > 4096) throw new ArgumentException("Use bounded Credential discovery arguments.");
        var input = TenantCredentialDiscoveryContract.Parse(arguments, tool); var request = input.Page; var family = input.Family;
        var before = await RequireAsync(frame, ct, audit: true).ConfigureAwait(false); RequireFamily(before, family); var allowed = new List<TenantCredentialBinding>();
        foreach (var bound in before.Bindings.OrderBy(b => b.Binding.Reference, StringComparer.Ordinal))
        {
            if (!Eligible(before, bound, family) || request.Cursor is not null && string.CompareOrdinal(bound.Binding.Reference, request.Cursor) <= 0) continue;
            if (!await AllowedAsync(before, bound, ct, audit: true, family: family).ConfigureAwait(false)) continue;
            allowed.Add(bound); if (allowed.Count > request.Limit) break;
        }
        var after = await RequireAsync(frame, ct).ConfigureAwait(false);
        if (before.Run.Revision != after.Run.Revision || before.State.Tenant.Revision != after.State.Tenant.Revision
            || before.Agent.Instance.Revision != after.Agent.Instance.Revision || before.Session.Snapshot.Revision != after.Session.Snapshot.Revision)
            throw new SecurityConflictException("credential_source_changed");
        foreach (var bound in allowed)
        {
            var current = after.Bindings.SingleOrDefault(b => b.Binding.BindingId == bound.Binding.BindingId);
            if (current is null || Fingerprint(current) != Fingerprint(bound) || !Eligible(after, current, family) || !await AllowedAsync(after, current, ct, family: family).ConfigureAwait(false))
                throw new AccessDeniedException("credential_source_changed");
        }
        await FenceAsync(frame, after, ct).ConfigureAwait(false);
        return Serialize(allowed, request, remainingBytes, family);
    }
    private static string Serialize(IReadOnlyList<TenantCredentialBinding> allowed, CredentialDiscoveryRequest request, int remainingBytes, string? family = null)
    {
        var metadata = allowed.Select(b => new BoundCredentialMetadata(b.Binding.Reference, b.Credential.Value.DisplayName,
            b.Credential.Value.Kind.ToString(), Metadata(b.Credential.Value, family))).ToArray();
        var page = CredentialDiscovery.Serialize(metadata, request, Math.Max(0, remainingBytes - 256 - allowed.Count * 256));
        var node = JsonNode.Parse(page)!.AsObject();
        if (node["items"] is JsonArray items)
        {
            node["tenantCredentialRead"] = true;
            node["credentialReadFamily"] = family;
            node["credentialReads"] = JsonSerializer.SerializeToNode(allowed.Take(items.Count + (node["hasMore"]!.GetValue<bool>() ? 1 : 0)).Select(Fingerprint));
        }
        var text = node.ToJsonString();
        return Encoding.UTF8.GetByteCount(text) <= remainingBytes ? text : """{"error":"output_limit"}""";
    }
    public async ValueTask RequireStoredAsync(AgentRun run, CancellationToken ct = default)
    {
        if (run.Checkpoint is null || !AgentRunToolCallCheckpoint.TryRead(run.Checkpoint, out var messages)) return;
        Read? current = null;
        foreach (var message in messages!.Where(m => m.Role == ModelRole.Tool && TenantCredentialDiscoveryContract.IsTool(m.Name ?? "")))
        {
            using var json = JsonDocument.Parse(message.Text); var root = json.RootElement;
            if (!root.TryGetProperty("tenantCredentialRead", out var marker) || marker.ValueKind != JsonValueKind.True) continue;
            current ??= await RequireAsync(new(run.Owner, run.SessionId, run.AgentRunId, run.Claim!.Generation), ct).ConfigureAwait(false);
            var family = TenantCredentialReadReferences.Family(root); RequireFamily(current, family);
            if (TenantCredentialDiscoveryContract.Family(message.Name ?? "") != family) throw new AccessDeniedException("credential_source_changed");
            var witnesses = root.GetProperty("credentialReads").Deserialize<TenantCredentialReadWitness[]>() ?? throw new AccessDeniedException("credential_source_changed");
            if (witnesses.Length > 101) throw new AccessDeniedException("credential_source_changed");
            foreach (var witness in witnesses)
            {
                var bound = current.Bindings.SingleOrDefault(b => Fingerprint(b) == witness);
                if (bound is null || !Eligible(current, bound, family) || !await AllowedAsync(current, bound, ct, family: family).ConfigureAwait(false))
                    throw new AccessDeniedException("credential_source_changed");
            }
        }
        if (current is not null) await FenceAsync(new(run.Owner, run.SessionId, run.AgentRunId, run.Claim!.Generation), current, ct).ConfigureAwait(false);
        await RequireStoredApprovedAsync(run, messages!, ct).ConfigureAwait(false);
    }
    private async ValueTask<Read> RequireAsync(TenantRuntimeFrame frame, CancellationToken ct, bool audit = false, bool approved = false, bool live = true)
    {
        var (run, state) = await operations.ResolveAsync(new(frame.Owner.TenantId ?? throw new AccessDeniedException("tenant_context_required"), frame.RunId), ct).ConfigureAwait(false);
        var session = await sessions.FindTenantSessionAsync(state.Tenant.TenantId, frame.SessionId, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("resource_unavailable");
        if (live) TenantRuntimeRules.Require(frame, session.Owner, run, time.GetUtcNow());
        if (session.Snapshot.DurablyDeletedAt is not null || session.Snapshot.ArchivedAt is not null
            || session.Snapshot.LifecycleStatus != SessionLifecycleStatus.Active || session.Snapshot.Status is SessionStatus.Ending or SessionStatus.Ended)
            throw new AccessDeniedException("resource_unavailable");
        var sessionScope = TenantSessionRules.Scope(state.Tenant.TenantId, frame.SessionId);
        if (!(await authorizer.CheckTenantAsync(state, run.Owner.ExecutorPrincipalId!.Value, PermissionCatalog.Get("sessions.read"),
            sessionScope, ct).ConfigureAwait(false)).Allowed) throw new AccessDeniedException("permission_denied");
        if (!approved)
        {
            var sessionRead = audit ? await operations.Check(state, run, "sessions.read", sessionScope, ct, purpose: ExecutionAuthorizationPurpose.ToolBoundary).ConfigureAwait(false)
                : await operations.Evaluate(state, run.Admission.ExecutionContext!, "sessions.read", sessionScope, ct).ConfigureAwait(false);
            if (sessionRead.Decision == OperationDecisionKind.RequiresApproval) throw new AccessDeniedException("credential_read_review_required");
            if (sessionRead.Decision != OperationDecisionKind.Allowed) throw new AccessDeniedException(sessionRead.ReasonCode);
        }
        var (agent, baseline) = await configuration.BaselineAsync(state.Tenant.TenantId, run.AgentInstanceId, ct).ConfigureAwait(false);
        var snapshot = await configuration.CurrentCredentialBindingsAsync(run, ct, bindings).ConfigureAwait(false)
            ?? throw new AccessDeniedException("resource_unavailable");
        if (snapshot.Agent.PrincipalId != run.Owner.ExecutorPrincipalId || snapshot.Agent.Instance.Revision != agent.Instance.Revision)
            throw new SecurityConflictException("credential_source_changed");
        return new(run, state, session, agent, InstanceSettingsResolver.ResolveForAgent(baseline, agent), snapshot.Bindings);
    }
    private static bool Eligible(Read read, TenantCredentialBinding bound, string? family = null) => TenantCredentialReadOperationRules.Eligible(read.Run, read.Agent, read.Current, bound)
        && TenantCredentialDiscoveryContract.Matches(family, bound.Credential.Value.Purpose);
    private static void RequireFamily(Read read, string? family)
    {
        if (family is not null && (!TenantTaskExecutionContract.Allows(read.Run, read.Current)
            || !TenantCredentialDiscoveryContract.Allows(read.Current, family) || !TenantCredentialDiscoveryContract.Allows(read.Run.Admission.Configuration!.Definition, family)))
            throw new AccessDeniedException("capability_unavailable");
    }
    private static IReadOnlyDictionary<string, string> Metadata(Credential value, string? family)
    {
        if (family is null) return value.Metadata;
        var result = new Dictionary<string, string>(value.Metadata, StringComparer.Ordinal) {
            ["accountPurpose"] = value.Purpose.ToString(), ["emailAddress"] = value.EmailAccount?.EmailAddress ?? value.DriveAccount!.EmailAddress,
            ["connectionStatus"] = value.EmailAccount?.ConnectionStatus.ToString() ?? value.DriveAccount!.ConnectionStatus.ToString(),
            ["authEpoch"] = (value.EmailAccount?.AuthEpoch ?? value.DriveAccount!.AuthEpoch).ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["synthetic"] = (value.EmailAccount?.Synthetic ?? value.DriveAccount!.Synthetic) ? "true" : "false",
            ["accountUse"] = "Separate authorization required"
        };
        return result;
    }
    private async ValueTask<bool> AllowedAsync(Read read, TenantCredentialBinding bound, CancellationToken ct, bool audit = false, string? family = null)
    {
        var scope = TenantCredentialRules.Scope(read.State.Tenant.TenantId, bound.Credential.Value.CredentialId);
        foreach (var permission in family is null ? new[] { "credentials.read" } : new[] { "credentials.read", family + ".read" })
        {
            if (!(await authorizer.CheckTenantAsync(read.State, read.Run.Owner.ExecutorPrincipalId!.Value, PermissionCatalog.Get(permission), scope, ct).ConfigureAwait(false)).Allowed) return false;
        }
        foreach (var permission in family is null ? new[] { "credentials.read" } : new[] { "credentials.read", family + ".read" })
        {
            var decision = audit ? await operations.Check(read.State, read.Run, permission, scope, ct, purpose: ExecutionAuthorizationPurpose.ToolBoundary).ConfigureAwait(false)
                : await operations.Evaluate(read.State, read.Run.Admission.ExecutionContext!, permission, scope, ct).ConfigureAwait(false);
            if (decision.Decision == OperationDecisionKind.RequiresApproval) throw new AccessDeniedException("credential_read_review_required");
            if (decision.Decision != OperationDecisionKind.Allowed) return false;
        }
        return true;
    }
    private async ValueTask FenceAsync(TenantRuntimeFrame frame, Read before, CancellationToken ct)
    {
        var (current, state) = await operations.ResolveAsync(new(before.State.Tenant.TenantId, frame.RunId), ct).ConfigureAwait(false);
        TenantRuntimeRules.Require(frame, before.Session.Owner, current, time.GetUtcNow());
        if (current.Revision != before.Run.Revision || state.Tenant.Revision != before.State.Tenant.Revision
            || (await security.ReadTenantAsync(before.State.Tenant.TenantId, ct).ConfigureAwait(false))?.Tenant.Revision != state.Tenant.Revision)
            throw new SecurityConflictException("credential_source_changed");
        var final = await runs.FindTenantRunAsync(before.State.Tenant.TenantId, frame.RunId, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("run_unavailable");
        TenantRuntimeRules.Require(frame, before.Session.Owner, final, time.GetUtcNow());
        if (final.Revision != current.Revision) throw new SecurityConflictException("credential_source_changed");
    }
    private static TenantCredentialReadWitness Fingerprint(TenantCredentialBinding bound) => new(bound.Binding.BindingId, bound.Credential.Value.CredentialId, bound.Binding.Revision, bound.Credential.Value.Revision);
}
