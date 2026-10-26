using System.Text.Json;
using AgentCore.Application.Admin;
using AgentCore.Application.Agents;
using AgentCore.Application.Models;
using AgentCore.Application.Ports;
using AgentCore.Application.Tools;
using AgentCore.Domain.Definitions;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Security;

public sealed partial class TenantAgentConfigurationService
{
    internal async ValueTask<byte[]?> ManagementResourceCopyBytesAsync(TenantManagementResourceContext context, TenantManagementMutationArguments args, CancellationToken ct)
    {
        if (args.Tool != "resources.copy") return null;
        var key = args.Value.GetProperty("key").GetString()!;
        var view = InstanceResourceRules.Views(EffectiveChildDefinition(context.Source.Snapshot, context.Source.Reader.Baseline), context.Published,
            context.Source.Snapshot.Resources, context.Source.Snapshot.Skills).SingleOrDefault(r => r.Key == key) ?? throw new AccessDeniedException("resource_unavailable");
        return await Content(view, ct).ConfigureAwait(false);
    }
    internal async ValueTask StageManagementResourceAsync(PreparedManagementMutation value, CancellationToken ct)
    {
        if (value is not { ToolName: "resources.upsert", Resource: { Changed: true, InstanceResource: { } resource } }) return;
        var args = TenantManagementMutationContract.Parse(value.ToolName, value.ArgumentsJson);
        var bytes = System.Text.Encoding.UTF8.GetBytes(args.Value.GetProperty("content").GetString()!);
        InstanceResourceRules.Verify(resource.ContentSha256, resource.ByteLength, bytes);
        try { await (content ?? throw new AccessDeniedException("resource_content_unavailable")).StoreVerifiedAsync(resource.ContentSha256, bytes, ct).ConfigureAwait(false); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { throw new AccessDeniedException("resource_content_unavailable"); }
    }
    internal async ValueTask ValidateManagementSkillAsync(TenantManagementSource source, TenantManagementMutationArguments args, DateTimeOffset at, CancellationToken ct)
    {
        var prepared = TenantManagementSkillRules.Prepare(source, args, at);
        var definition = EffectiveChildDefinition(source.Snapshot, source.Reader.Baseline);
        _ = InstanceResourceRules.Resolve(definition, await Resources(source.Reader.Security.Tenant.TenantId, source.Reader.Baseline, ct).ConfigureAwait(false),
            source.Snapshot.Resources, prepared.Next);
    }
    // Human HTTP and model exact-operation preparation use the same validation and reconciliation.
    internal async ValueTask<TenantAgentConfigurationChange> PrepareSettingsChangeAsync(Guid actor, Guid tenant,
        TenantAgentConfigurationSnapshot snapshot, AgentDefinition definition, string section,
        IReadOnlyDictionary<string, JsonElement> set, IReadOnlyList<string> clear, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(set); ArgumentNullException.ThrowIfNull(clear);
        var current = snapshot.Agent.Instance;
        if (section == "sandboxPolicy")
        {
            foreach (var key in new[] { "sandbox.read", "sandbox.manage" })
                await authorizer.RequireAsync(actor, key, Scope(tenant, current.InstanceId), ct).ConfigureAwait(false);
            definition = await PrepareSandboxPatch(actor, tenant, current, definition, set, clear, ct).ConfigureAwait(false);
        }
        if (aliases is null) throw Sessions.AgentCoreErrors.Validation("Configured provider choices are unavailable.");
        var models = await ModelCatalogForAgentAsync(actor, tenant, current.InstanceId, ct).ConfigureAwait(false);
        var (next, effective) = InstanceSettingsRules.ResolvePatch(definition, current.SettingsOverrides ?? new(), section, set, clear, models, aliases);
        InstanceSettingsRules.ValidateBrowser(effective, browser);
        _ = EffectiveSkillCatalogResolver.Resolve(effective, snapshot.Skills);
        _ = InstanceResourceRules.Resolve(effective, await Resources(tenant, definition, ct).ConfigureAwait(false), snapshot.Resources, snapshot.Skills);
        var plan = await automationPolicy.PlanTenantAsync(tenant, security as ITenantAutomationStore ?? throw new InvalidOperationException("Canonical Automation store is unavailable."), current, effective, time.GetUtcNow(), ct).ConfigureAwait(false);
        return new(current.InstanceId, current.Revision, SetSettingsOverrides: true, SettingsOverrides: next, AutomationPolicyChanges: plan);
    }

    internal async ValueTask<TenantAgentConfigurationChange> PrepareManagementCommandAsync(TenantManagementSource source,
        TenantManagementMutationArguments arguments, CancellationToken ct)
    {
        var reader = source.Reader; var owner = reader.Agent.Instance; var args = arguments.Value;
        RequireWritable(owner, arguments.ExpectedInstanceRevision);
        if (arguments.Tool == "settings.patch")
            return await PrepareSettingsChangeAsync(reader.Agent.PrincipalId, reader.Security.Tenant.TenantId, source.Snapshot, reader.Baseline,
                args.GetProperty("section").GetString()!, args.GetProperty("set").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone()),
                args.GetProperty("clear").EnumerateArray().Select(p => p.GetString()!).ToArray(), ct).ConfigureAwait(false);
        if (arguments.Tool == "automation.preferences.patch")
        {
            var preference = args.GetProperty("modelPreference").Deserialize<AgentCore.Domain.Models.ModelPreference>(new JsonSerializerOptions(JsonSerializerDefaults.Web));
            var models = await modelCatalogs.ReadAsync(reader.Security, ct).ConfigureAwait(false);
            ExecutionModelPolicy.RequireSelectable(models, preference);
            return new(owner.InstanceId, owner.Revision, SetUnattendedModel: true, UnattendedModel: preference);
        }
        if (arguments.Tool == "management.freeze")
            return new(owner.InstanceId, owner.Revision, HarnessManagement: HarnessManagementRules.PrepareFreeze(owner, reader.Baseline));
        var view = HarnessManagementRules.View(owner, reader.Baseline);
        var overrides = view.Overrides ?? new();
        overrides = overrides with { Mode = new(Enum.Parse<HarnessManagementMode>(args.GetProperty("mode").GetString()!, false)),
            Scopes = new(args.GetProperty("scopes").EnumerateArray().Select(a => Enum.Parse<HarnessManagementScope>(a.GetString()!, false)).ToArray()),
            AutoApply = new(args.GetProperty("managedAutoApply").EnumerateArray().Select(a => Enum.Parse<ManagementAutoApply>(a.GetString()!, false)).ToArray()) };
        return new(owner.InstanceId, owner.Revision, HarnessManagement: HarnessManagementRules.Prepare(owner, reader.Baseline, overrides, null, agent: true));
    }
}
