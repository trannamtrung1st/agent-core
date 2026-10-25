using System.Text;
using System.Text.Json;
using AgentCore.Application.Agents;
using AgentCore.Application.Execution;
using AgentCore.Application.Ports;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Security;

public sealed partial class TenantArtifactRuntimeReadService(TenantOperationAuthorizer operations, ITenantRunStore runs,
    ITenantSessionStore sessions, ITenantArtifactStore artifacts, TenantRunConfigurationResolver configurations,
    IIamStore security, IamAuthorizer iam, TimeProvider time, ITenantApprovalStore approvals, IIdGenerator ids, ITenantApprovedOperationStore? effects = null)
{
    private sealed record Read(AgentRun Run, TenantSecurityState State, TenantSession Session, TenantAgent Agent,
        AgentDefinition Definition, ArtifactRecord Artifact);

    public async ValueTask<string> InspectAsync(TenantRuntimeFrame frame, string arguments, int remainingBytes, CancellationToken ct = default)
    {
        try
        {
            var id = TenantArtifactVerificationContract.Parse(arguments);
            var before = await RequireAsync(frame, id, ct, audit: true).ConfigureAwait(false);
            var exact = TenantApprovalRules.PrepareArtifactVerification(ids.NewId(), before.Run.AgentInstanceId,
                Witness(before.Artifact), before.Agent.Instance.Revision, before.Agent.Instance.PersonaRevision);
            exact = TenantApprovalRules.ForRun(exact, before.Run);
            var prior = await approvals.FindRootApprovalAsync(before.State.Tenant.TenantId, before.Run.Admission.ExecutionContext!.RootActivationId,
                before.Agent.PrincipalId, exact, ct).ConfigureAwait(false);
            if (prior is not null) throw new AccessDeniedException(TenantApprovalRules.DuplicateReason(prior));
            await FenceAsync(frame, before, ct).ConfigureAwait(false);
            return Serialize(before.Artifact, remainingBytes);
        }
        catch (Exception e) when (e is AccessDeniedException or SecurityConflictException)
        {
            if (frame.Owner.TenantId is { } tenant)
                await operations.RecordFailureAsync(new(tenant, frame.RunId), "artifacts.read", e is AccessDeniedException denied ? denied.ReasonCode : ((SecurityConflictException)e).ReasonCode,
                    ExecutionAuthorizationPurpose.ToolBoundary, ct, frame.SessionId).ConfigureAwait(false);
            throw;
        }
    }
    private async ValueTask<Read> RequireAsync(TenantRuntimeFrame frame, Guid artifact, CancellationToken ct, bool audit = false, bool approved = false, bool live = true)
    {
        var tenant = frame.Owner.TenantId ?? throw new AccessDeniedException("tenant_context_required");
        var (run, state) = await operations.ResolveAsync(new(tenant, frame.RunId), ct).ConfigureAwait(false);
        var session = await sessions.FindTenantSessionAsync(tenant, frame.SessionId, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("resource_unavailable");
        if (live) TenantRuntimeRules.Require(frame, session.Owner, run, time.GetUtcNow());
        if (run.Owner != frame.Owner || run.SessionId != frame.SessionId || session.Owner.AgentInstanceId != run.AgentInstanceId
            || session.Owner.ExecutorPrincipalId != run.Owner.ExecutorPrincipalId || !TenantTaskExecutionContract.Allows(run, run.Admission.Configuration!.Definition)
            || session.Snapshot.DurablyDeletedAt is not null || session.Snapshot.ArchivedAt is not null
            || session.Snapshot.LifecycleStatus != SessionLifecycleStatus.Active || session.Snapshot.Status is SessionStatus.Ending or SessionStatus.Ended)
            throw new AccessDeniedException("resource_unavailable");
        var current = await configurations.ResolveAsync(tenant, run.AgentInstanceId, ct).ConfigureAwait(false);
        if (current.Agent.PrincipalId != run.Owner.ExecutorPrincipalId || !TenantTaskExecutionContract.Allows(run, current.Configuration.Configuration.Definition) || !TenantArtifactVerificationContract.Allows(run.Admission.Configuration!.Definition)
            || !TenantArtifactVerificationContract.Allows(current.Configuration.Configuration.Definition)) throw new AccessDeniedException("capability_unavailable");
        var item = await artifacts.FindTenantAsync(tenant, frame.SessionId, artifact, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("resource_unavailable");
        if (item.TenantId != tenant || item.SessionId != frame.SessionId) throw new AccessDeniedException("resource_unavailable");
        var read = new Read(run, state, session, current.Agent, current.Configuration.Configuration.Definition, item);
        TenantArtifactVerificationRules.Require(Witness(item), run, current.Agent, read.Definition, session, item);
        await PermissionsAsync(read.State, Witness(item), read.Agent.PrincipalId, ct).ConfigureAwait(false);
        if (!approved)
            foreach (var (permission, scope) in Targets(tenant, Witness(item)))
            {
                var decision = audit ? await operations.Check(state, run, permission, scope, ct, purpose: ExecutionAuthorizationPurpose.ToolBoundary).ConfigureAwait(false)
                    : await operations.Evaluate(state, run.Admission.ExecutionContext!, permission, scope, ct).ConfigureAwait(false);
                if (decision.Decision == OperationDecisionKind.RequiresApproval) throw new AccessDeniedException("artifact_verification_review_required");
                if (decision.Decision != OperationDecisionKind.Allowed) throw new AccessDeniedException(decision.ReasonCode);
            }
        return read;
    }
    private async ValueTask FenceAsync(TenantRuntimeFrame frame, Read before, CancellationToken ct, bool approved = false)
    {
        var after = await RequireAsync(frame, before.Artifact.ArtifactId, ct, approved: approved).ConfigureAwait(false);
        if (after.Run.Revision != before.Run.Revision || after.State.Tenant.Revision != before.State.Tenant.Revision
            || after.Session.Owner != before.Session.Owner || after.Session.Snapshot.Revision != before.Session.Snapshot.Revision
            || after.Agent.Instance.Revision != before.Agent.Instance.Revision || after.Artifact != before.Artifact)
            throw new SecurityConflictException("artifact_source_changed");
        if ((await security.ReadTenantAsync(after.State.Tenant.TenantId, ct).ConfigureAwait(false))?.Tenant.Revision != after.State.Tenant.Revision)
            throw new SecurityConflictException("artifact_source_changed");
        var final = await runs.FindTenantRunAsync(after.State.Tenant.TenantId, frame.RunId, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("run_unavailable");
        TenantRuntimeRules.Require(frame, after.Session.Owner, final, time.GetUtcNow());
        if (final.Revision != after.Run.Revision) throw new SecurityConflictException("artifact_source_changed");
    }
    public async ValueTask RequireStoredAsync(AgentRun run, CancellationToken ct = default)
    {
        if (run.Checkpoint is null || !AgentRunToolCallCheckpoint.TryRead(run.Checkpoint, out var messages)) return;
        var frame = new TenantRuntimeFrame(run.Owner, run.SessionId, run.AgentRunId, run.Claim?.Generation ?? Guid.Empty);
        foreach (var message in messages!.Where(m => m.Role == ModelRole.Tool && m.Name == ToolCatalog.ArtifactsVerify))
        {
            using var doc = JsonDocument.Parse(message.Text); var root = doc.RootElement;
            if (!root.TryGetProperty("tenantArtifactRead", out var marker) || marker.ValueKind != JsonValueKind.True) continue;
            var witness = root.GetProperty("artifactRead").Deserialize<PreparedArtifactVerification>() ?? throw new AccessDeniedException("artifact_source_changed");
            var read = await RequireAsync(frame, witness.ArtifactId, ct).ConfigureAwait(false);
            if (Witness(read.Artifact) != witness) throw new AccessDeniedException("artifact_source_changed");
            await FenceAsync(frame, read, ct).ConfigureAwait(false);
        }
        await RequireStoredApprovedAsync(run, messages!, ct).ConfigureAwait(false);
    }
    private static string Serialize(ArtifactRecord item, int remainingBytes)
    {
        var text = JsonSerializer.Serialize(new { artifactId = item.ArtifactId, sessionId = item.SessionId, displayName = item.DisplayName,
            contentType = item.ContentType, byteSize = item.ByteSize, sha256Hex = item.Sha256Hex, createdAt = item.CreatedAt,
            tenantArtifactRead = true, artifactRead = Witness(item) });
        return Encoding.UTF8.GetByteCount(text) <= remainingBytes ? text : """{"error":"output_limit"}""";
    }
    internal static PreparedArtifactVerification Witness(ArtifactRecord item) => TenantArtifactVerificationRules.Witness(item);
    private static IEnumerable<(string Permission, AccessScope Scope)> Targets(Guid tenant, PreparedArtifactVerification read) => TenantArtifactVerificationRules.Targets(tenant, read);
    private ValueTask PermissionsAsync(TenantSecurityState state, PreparedArtifactVerification read, Guid principal, CancellationToken ct) =>
        TenantArtifactVerificationRules.PermissionsAsync(state, read, principal, iam, ct);
}
