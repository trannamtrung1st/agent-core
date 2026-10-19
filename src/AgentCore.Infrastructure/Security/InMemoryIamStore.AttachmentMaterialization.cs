using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Security;

namespace AgentCore.Infrastructure.Security;

public sealed partial class InMemoryIamStore
{
    public ValueTask<ArtifactRecord?> TryPublishMaterializationAsync(TenantAttachmentMutation proof, AttachmentRecord source, ArtifactRecord artifact, ReadOnlyMemory<byte> bytes, CancellationToken ct = default)
    {
        var frozen = bytes.ToArray(); TenantAttachmentMaterializationRules.Publication(proof, source, artifact, frozen);
        var files = attachmentBacking ?? throw new AccessDeniedException("tenant_effect_unavailable");
        lock (_gate) lock (sessionBacking.AdmissionGate) lock (instances.CredentialGate) lock (files.TenantGate)
        {
            ct.ThrowIfCancellationRequested(); RequireHumanSession(proof.Owner.TenantId, proof.Human);
            var state = _tenants.GetValueOrDefault(proof.Owner.TenantId) ?? throw new AccessDeniedException("resource_unavailable");
            var session = sessionBacking.ReadTenantSession(proof.Owner.TenantId, proof.Owner.SessionId) ?? throw new AccessDeniedException("resource_unavailable");
            var agent = FindTenantAgentAsync(proof.Owner.TenantId, proof.Owner.AgentInstanceId, ct).Result ?? throw new AccessDeniedException("resource_unavailable");
            if (files.RetainedTenant(proof.Owner.TenantId, proof.Owner.SessionId, source.AttachmentId) != source) throw new AccessDeniedException("resource_unavailable");
            TenantAttachmentRules.Session(proof.Owner, session, agent, true);
            TenantAttachmentMaterializationRules.RequireAsync(state, session, agent, proof.Human.PrincipalId, source, new IamAuthorizer(this), (clock ?? TimeProvider.System).GetUtcNow(), ct).GetAwaiter().GetResult();
            TenantAttachmentRules.Content(source, files.ReadTenantContentAsync(proof.Owner.TenantId, proof.Owner.SessionId, source.AttachmentId, ct).GetAwaiter().GetResult());
            if (state.Tenant.Revision != proof.SecurityRevision || agent.Instance.Revision != proof.InstanceRevision) return ValueTask.FromResult<ArtifactRecord?>(null);
            if (_audit.Any(a => a.AuditId == proof.RequestId)) throw new SecurityConflictException("identity_conflict");
            var saved = (artifactBacking ?? throw new AccessDeniedException("tenant_effect_unavailable")).PublishHumanLocal(proof, source, artifact, frozen, ct);
            _tenants[state.Tenant.TenantId] = state with { Tenant = state.Tenant with { Revision = checked(state.Tenant.Revision + 1) } }; _audit.Add(proof.Audit);
            return ValueTask.FromResult<ArtifactRecord?>(saved);
        }
    }
}
