using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;
using AgentCore.Infrastructure.Persistence;

namespace AgentCore.Infrastructure.Security;

public sealed partial class InMemoryIamStore : ITenantAttachmentMutationStore
{
    public ValueTask<AttachmentRecord?> TryUploadAttachmentAsync(TenantAttachmentMutation proof, TenantAttachmentUpload upload, CancellationToken ct = default)
    {
        upload = upload with { Bytes = upload.Bytes.ToArray() }; TenantAttachmentRules.Mutation(proof, "attachments.create");
        var files = attachmentBacking ?? throw new AccessDeniedException("tenant_effect_unavailable");
        lock (_gate) lock (sessionBacking.AdmissionGate) lock (instances.CredentialGate) lock (files.TenantGate)
        {
            ct.ThrowIfCancellationRequested(); var state = _tenants.GetValueOrDefault(proof.Owner.TenantId) ?? throw new AccessDeniedException("resource_unavailable");
            RequireHumanSession(proof.Owner.TenantId, proof.Human);
            var session = sessionBacking.ReadTenantSession(proof.Owner.TenantId, proof.Owner.SessionId) ?? throw new AccessDeniedException("resource_unavailable");
            var agent = FindTenantAgentAsync(proof.Owner.TenantId, proof.Owner.AgentInstanceId, ct).Result ?? throw new AccessDeniedException("resource_unavailable");
            TenantAttachmentRules.Session(proof.Owner, session, agent, true);
            TenantAttachmentRules.PermissionsAsync(state, proof.Human.PrincipalId, proof.Owner.SessionId, null, "attachments.create", new IamAuthorizer(this), ct).GetAwaiter().GetResult();
            TenantAttachmentRules.Upload(proof, upload, (clock ?? TimeProvider.System).GetUtcNow());
            if (files.FindUpload(proof.Owner.TenantId, proof.Owner.SessionId, proof.RequestId) is { } prior)
                return ValueTask.FromResult<AttachmentRecord?>(TenantAttachmentRules.SameUpload(prior, upload.Record) && TenantAttachmentRules.Available(prior, (clock ?? TimeProvider.System).GetUtcNow()) ? prior : throw new SecurityConflictException("attachment_upload_mismatch"));
            if (_audit.Any(a => a.AuditId == proof.RequestId)) throw new SecurityConflictException("identity_conflict");
            if (state.Tenant.Revision != proof.SecurityRevision || session.Snapshot.Revision != proof.SessionRevision || agent.Instance.Revision != proof.InstanceRevision) return ValueTask.FromResult<AttachmentRecord?>(null);
            var item = files.PublishTenant(proof.Owner, upload, ct);
            _tenants[state.Tenant.TenantId] = state with { Tenant = state.Tenant with { Revision = checked(state.Tenant.Revision + 1) } }; _audit.Add(proof.Audit);
            return ValueTask.FromResult<AttachmentRecord?>(item);
        }
    }
    public ValueTask<bool> TryAbortAttachmentAsync(TenantAttachmentMutation proof, Guid attachmentId, CancellationToken ct = default)
    {
        TenantAttachmentRules.Mutation(proof, "attachments.delete", attachmentId); var files = attachmentBacking ?? throw new AccessDeniedException("tenant_effect_unavailable");
        lock (_gate) lock (sessionBacking.AdmissionGate) lock (instances.CredentialGate) lock (files.TenantGate)
        {
            ct.ThrowIfCancellationRequested(); var state = _tenants.GetValueOrDefault(proof.Owner.TenantId) ?? throw new AccessDeniedException("resource_unavailable");
            RequireHumanSession(proof.Owner.TenantId, proof.Human);
            var session = sessionBacking.ReadTenantSession(proof.Owner.TenantId, proof.Owner.SessionId) ?? throw new AccessDeniedException("resource_unavailable");
            var agent = FindTenantAgentAsync(proof.Owner.TenantId, proof.Owner.AgentInstanceId, ct).Result ?? throw new AccessDeniedException("resource_unavailable");
            TenantAttachmentRules.Session(proof.Owner, session, agent, true);
            if (_audit.SingleOrDefault(a => a.AuditId == proof.RequestId) is { } prior)
                return ValueTask.FromResult(prior.TenantId == proof.Owner.TenantId && prior.ActorPrincipalId == proof.Human.PrincipalId && prior.Action == proof.Audit.Action && prior.Target == proof.Audit.Target
                    ? true : throw new SecurityConflictException("identity_conflict"));
            var item = files.RetainedTenant(proof.Owner.TenantId, proof.Owner.SessionId, attachmentId) ?? throw new AccessDeniedException("resource_unavailable");
            if (!TenantAttachmentRules.Available(item, (clock ?? TimeProvider.System).GetUtcNow())) throw new AccessDeniedException("resource_unavailable");
            TenantAttachmentRules.PermissionsAsync(state, proof.Human.PrincipalId, proof.Owner.SessionId, attachmentId, "attachments.delete", new IamAuthorizer(this), ct).GetAwaiter().GetResult();
            if (item.State != AttachmentState.Pending) throw new SecurityConflictException("attachment_already_bound");
            if (state.Tenant.Revision != proof.SecurityRevision || session.Snapshot.Revision != proof.SessionRevision || agent.Instance.Revision != proof.InstanceRevision) return ValueTask.FromResult(false);
            files.AbortTenant(item, ct);
            _tenants[state.Tenant.TenantId] = state with { Tenant = state.Tenant with { Revision = checked(state.Tenant.Revision + 1) },
                Assignments = state.Assignments.Where(a => a.Scope != TenantAttachmentRules.Scope(state.Tenant.TenantId, attachmentId)).ToArray() }; _audit.Add(proof.Audit); return ValueTask.FromResult(true);
        }
    }
}
