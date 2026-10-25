using AgentCore.Application.Execution;
using AgentCore.Application.Models;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Experience;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Security;
public sealed class TenantExperienceReviewService(IIamStore security, IdentityService identity, ITenantExperienceReviewStore store,
    TenantRunConfigurationResolver configurations, ITenantModelCatalogReader modelCatalogs, IIdGenerator ids, TimeProvider time)
{
    public async ValueTask<TenantExperienceReviewSource> ObserveAsync(Guid tenant, Guid agent, HumanSessionReference human,
        ExperienceSourceKind kind, Guid sourceId, CancellationToken ct = default)
    {
        await TenantHumanSessionAuthority.RequireAsync(security, identity, tenant, human, ct).ConfigureAwait(false);
        var source = await store.ObserveOwnTenantExperienceReviewAsync(tenant, agent, human, kind, sourceId, ct).ConfigureAwait(false);
        if ((await TenantHumanSessionAuthority.RequireAsync(security, identity, tenant, human, ct).ConfigureAwait(false)).Tenant.Revision != source.SecurityRevision)
            throw new SecurityConflictException("stale_revision");
        return source;
    }
    public async ValueTask<TenantExperienceReviewReceipt> RequestAsync(Guid tenant, Guid agentId, HumanSessionReference human,
        ExperienceSourceKind kind, Guid sourceId, long expectedSecurityRevision, long expectedInstanceRevision, string sourceFingerprint, CancellationToken ct = default)
    {
        var source = await ObserveAsync(tenant, agentId, human, kind, sourceId, ct).ConfigureAwait(false);
        if (sourceFingerprint != TenantExperienceReviewRules.Fingerprint(source)) throw new SecurityConflictException("experience_source_changed");
        if (source.SecurityRevision != expectedSecurityRevision || source.InstanceRevision != expectedInstanceRevision) throw new SecurityConflictException("stale_revision");
        var (agent, resolved) = await configurations.ResolveAsync(tenant, agentId, ct).ConfigureAwait(false);
        var state = await TenantHumanSessionAuthority.RequireAsync(security, identity, tenant, human, ct).ConfigureAwait(false);
        if (state.Tenant.Revision != source.SecurityRevision) throw new SecurityConflictException("stale_revision");
        var models = await modelCatalogs.ReadAsync(state, ct).ConfigureAwait(false);
        var selection = ExecutionModelPolicy.Resolve(models, resolved.Configuration.Definition, agent.Instance, null);
        if (!selection.Accepted || selection.Pin is not { } model || models.Get(model.OfferingId)?.Tools != true)
            throw AgentCoreErrors.Validation("An available tool-capable unattended model is required.");
        var at = DateTimeOffset.FromUnixTimeMilliseconds(time.GetUtcNow().ToUnixTimeMilliseconds());
        var admission = TenantExperienceReviewRules.Build(source, agent, resolved, new(model.OfferingId, model.ConnectionId, model.ExternalModelId, model.ReasoningEffort, model.Binding), at);
        return await store.TryAdmitOwnTenantExperienceReviewAsync(admission, human, new(ids.NewId(), tenant, human.PrincipalId,
            TenantExperienceReviewRules.AuditAction, admission.Run.AgentRunId.ToString("D"), "allowed", "experience_review_admitted", at), ct).ConfigureAwait(false)
            ?? throw new SecurityConflictException("stale_revision");
    }
}
