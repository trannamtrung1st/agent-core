using AgentCore.Application.Agents;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Definitions;
using AgentCore.Domain.Security;
using AgentCore.Domain.Tasks;

namespace AgentCore.Application.Security;

public sealed record TenantAgentResetResult(Guid OperationId, Guid InstanceId, long InstanceRevision, int AppliedVersion,
    string Status, string RetainedData = "Conversations, memory, files, credentials, local Skills/resources, IAM and Chat visibility are retained.");

public sealed partial class TenantAgentConfigurationService
{
    public ValueTask<TenantAgent> SetChatVisibilityAsync(Guid actor, Guid tenant, Guid id, bool show,
        long securityRevision, long instanceRevision, CancellationToken ct = default, HumanSessionReference? initiator = null) => WithGate(id, async token =>
    {
        var state = await ChangeState(actor, tenant, id, securityRevision, token);
        return await Commit(actor, tenant, state, new(id, instanceRevision, ShowInChat: show), "agents.chat-visibility.update", token, initiator);
    }, ct);

    public ValueTask<TenantAgentResetResult> ResetDefaultsAsync(Guid actor, Guid tenant, Guid id, Guid operation,
        long securityRevision, long instanceRevision, CancellationToken ct = default, HumanSessionReference? initiator = null) => WithGate<TenantAgentResetResult>(id, async token =>
    {
        if (operation == Guid.Empty) throw new ArgumentException("A fresh operation ID is required.");
        await RequireOriginalHuman(actor, tenant, initiator, token);
        var state = await security.ReadTenantAsync(tenant, token) ?? throw new AccessDeniedException("tenant_unavailable");
        // Current authorization precedes receipt readback, including after revocation.
        foreach (var permission in new[] { "agents.read", "agents.update" })
            if (!(await authorizer.CheckTenantAsync(state, actor, PermissionCatalog.Get(permission), Scope(tenant, id), token)).Allowed)
                throw new AccessDeniedException("permission_denied");
        var snapshot = await Snapshot(tenant, id, token); ManagedGeneralAssistant.Require(snapshot.Agent);
        var reasonPrefix = $"configuration_reset:{securityRevision}:{instanceRevision}:";
        if (await agents.FindAgentOperationAsync(tenant, operation, token) is { } receipt)
        {
            if (receipt.ActorPrincipalId != actor || receipt.Action != ManagedGeneralAssistant.ResetAction || receipt.Target != id.ToString("D") || (!receipt.ReasonCode.StartsWith(reasonPrefix, StringComparison.Ordinal) || !int.TryParse(receipt.ReasonCode[reasonPrefix.Length..], out _)))
                throw new SecurityConflictException("operation_mismatch");
            await RequireOriginalHuman(actor, tenant, initiator, token);
            if ((await security.ReadTenantAsync(tenant, token))?.Tenant.Revision != state.Tenant.Revision) throw new SecurityConflictException("stale_revision");
            return new(operation, id, instanceRevision + 1, int.Parse(receipt.ReasonCode[reasonPrefix.Length..], System.Globalization.CultureInfo.InvariantCulture), "already_applied");
        }
        if (state.Tenant.Revision != securityRevision || snapshot.Agent.Instance.Revision != instanceRevision) throw new SecurityConflictException("stale_revision");
        var target = await builtIns.GetAsync(ManagedGeneralAssistant.DefinitionId, cancellationToken: token)
            ?? throw new SecurityConflictException("assistant_upgrade_requires_attention");
        var current = snapshot.Agent.Instance;
        if (current.ActiveVersion > target.Version) throw new SecurityConflictException("assistant_release_unavailable");
        var resetOwner = current with { SettingsOverrides = null, ExecutionBudgets = null, UnattendedModel = null, Cooperation = new(), Persona = target.Identity };
        var resetSnapshot = snapshot with {
            Agent = snapshot.Agent with { Instance = resetOwner },
            Skills = snapshot.Skills with { DefinitionStates = snapshot.Skills.DefinitionStates.Select(s => s with { EnabledOverride = null }).ToArray() },
            Resources = snapshot.Resources with { DefinitionStates = snapshot.Resources.DefinitionStates.Select(s => s with { EnabledOverride = null }).ToArray() }
        };
        var prepared = await PrepareVersionCompatibilityAsync(tenant, resetSnapshot, target, null, await modelCatalogs.ReadAsync(state, token), token);
        var management = current.HarnessManagement is { } old ? old with { Overrides = new(), Policy = old.Policy with { Frozen = false }, PolicyRevision = checked(old.PolicyRevision + 1) } : null;
        var change = new TenantAgentConfigurationChange(id, instanceRevision, Persona: target.Identity, ExpectedPersonaRevision: current.PersonaRevision,
            ActiveVersion: target.Version, DefinitionSkills: target.SkillList, AutomationPolicyChanges: prepared.AutomationChanges,
            SetSettingsOverrides: true, SetExecutionBudgets: true, SetUnattendedModel: true, Cooperation: new AgentCooperationPolicy(),
            HarnessManagement: management, ResetDefaults: true, Initiator: initiator,
            Isolation: new(new(instanceRevision, snapshot.Isolation.ExperienceRevision, snapshot.Isolation.MaintenanceRevision), false, false));
        var at = time.GetUtcNow(); var next = state with { Tenant = state.Tenant with { Revision = checked(state.Tenant.Revision + 1) } };
        var updated = await agents.TryChangeTenantAgentConfigurationAsync(next, state.Tenant.Revision, change, at,
            new(operation, tenant, actor, ManagedGeneralAssistant.ResetAction, id.ToString("D"), "allowed", reasonPrefix + target.Version.ToString(System.Globalization.CultureInfo.InvariantCulture), at), token)
            ?? throw new SecurityConflictException("stale_revision");
        if (sandbox is not null) await sandbox.EnforceAsync(id, token);
        return new(operation, id, updated.Instance.Revision, updated.Instance.ActiveVersion, "applied");
    }, ct);

    // Called only by the host coordinator. Compatibility failure leaves the old record intact;
    // current admission refuses its obsolete mandatory policy until operators fix the cause.
    public ValueTask<TenantAgent> AdoptSystemDefaultsAsync(Guid tenant, Guid id, AgentDefinition target, CancellationToken ct = default) => WithGate(id, async token =>
    {
        var state = await security.ReadTenantAsync(tenant, token) ?? throw new AccessDeniedException("tenant_unavailable");
        var snapshot = await Snapshot(tenant, id, token); ManagedGeneralAssistant.Require(snapshot.Agent);
        var latest = await builtIns.GetAsync(ManagedGeneralAssistant.DefinitionId, cancellationToken: token);
        if (latest is null || target.Id != latest.Id || target.Version != latest.Version) throw new ArgumentException("Use the current shipped system release.");
        if (snapshot.Agent.Instance.ActiveVersion == latest.Version) return snapshot.Agent;
        if (snapshot.Agent.Instance.ActiveVersion > latest.Version) throw new SecurityConflictException("assistant_release_unavailable");
        var prepared = await PrepareVersionCompatibilityAsync(tenant, snapshot, latest, null, await modelCatalogs.ReadAsync(state, token), token, managedUpgrade: true);
        var current = snapshot.Agent.Instance; var at = time.GetUtcNow();
        var change = new TenantAgentConfigurationChange(id, current.Revision, ActiveVersion: latest.Version, DefinitionSkills: latest.SkillList,
            AutomationPolicyChanges: prepared.AutomationChanges, SystemAdoption: true,
            Persona: current.PersonaCustomized ? null : latest.Identity, ExpectedPersonaRevision: current.PersonaCustomized ? null : current.PersonaRevision);
        var next = state with { Tenant = state.Tenant with { Revision = checked(state.Tenant.Revision + 1) } };
        var updated = await agents.TryAdoptGeneralAssistantAsync(next, state.Tenant.Revision, change, at,
            new(ids.NewId(), tenant, Guid.Empty, ManagedGeneralAssistant.AdoptAction, id.ToString("D"), "allowed", "system_release", at), token)
            ?? throw new SecurityConflictException("stale_revision");
        if (sandbox is not null) await sandbox.EnforceAsync(id, token);
        return updated;
    }, ct);
}
