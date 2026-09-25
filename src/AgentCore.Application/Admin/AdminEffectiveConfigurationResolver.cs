using AgentCore.Application.Models;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Application.Tools;
using AgentCore.Application.Triggers;
using AgentCore.Domain.Definitions;
using AgentCore.Domain.Triggers;

namespace AgentCore.Application.Admin;

internal static class AdminEffectiveConfigurationResolver
{
    public static AdminEffectiveConfiguration Resolve(
        AgentInstance instance,
        AgentDefinition definition,
        IModelCatalog catalog,
        IToolConfigurationGate configurationGate)
    {
        var environment = RoleEnvironments.Of(definition);
        var trigger = definition.TriggerPolicy;
        var memory = definition.MemoryPolicy ?? MemoryPolicy.Disabled;
        var instanceActive = instance.Lifecycle == AgentInstanceLifecycle.Active;
        var scheduleAllowed = OccurrenceCompatibility.Allows(definition, TriggerSourceKind.Schedule);
        var applicationEventAllowed = OccurrenceCompatibility.Allows(definition, TriggerSourceKind.ApplicationEvent);
        var model = SessionModelBinder.PinDefault(catalog, definition);
        var descriptor = catalog.Get(model.CatalogKey)
            ?? throw AgentCoreErrors.Validation($"Model '{model.CatalogKey}' is not in the catalog.");
        var adminContext = new AgentContext(
            definition,
            [],
            string.Empty,
            null,
            Domain.Conversation.SessionMode.Text,
            null,
            false,
            null,
            new AgentTrigger(Guid.Empty, TriggerKind.UserTurn, null),
            ModelSupportsTools: descriptor.Tools);
        var offeredTools = ToolCatalog.For(definition, adminContext, configurationGate)
            .Select(item => item.Name)
            .OrderBy(item => item, StringComparer.Ordinal)
            .ToArray();

        return new AdminEffectiveConfiguration(
            DefinitionSource: AdminDefinitionSources.BuiltIn,
            DefinitionId: definition.Id,
            DefinitionVersion: definition.Version,
            DefinitionStatus: AdminDefinitionStatuses.Published,
            InstanceId: instance.InstanceId,
            InstanceLifecycle: instance.Lifecycle.ToString(),
            Compatibility: instance.Compatibility,
            Persona: instance.Persona,
            ProviderPreferences: definition.ProviderPreferences,
            EffectiveModel: new AdminEffectiveModel(
                model.CatalogKey,
                descriptor.DisplayName,
                SessionModelBinder.ToWire(model.SelectionSource),
                model.ReasoningEffort,
                model.ModelId),
            EffectiveToolAllowlist: offeredTools,
            HarnessReferences: environment.HarnessList.OrderBy(item => item, StringComparer.Ordinal).ToArray(),
            WorkspaceTemplateId: environment.WorkspacePolicy.TemplateId,
            KnowledgeSources: environment.KnowledgeList
                .Select(item => new AdminKnowledgeSource(item.Identity, item.Title, item.Citation))
                .ToArray(),
            MemoryPolicy: memory,
            TriggerPolicy: trigger,
            DurableExecutionEligibility: new AdminDurableExecutionEligibility(
                InstanceActive: instanceActive,
                DefinitionResolved: true,
                TriggerPolicyEnabled: trigger?.Enabled == true,
                AllowsScheduleSource: scheduleAllowed,
                AllowsApplicationEventSource: applicationEventAllowed,
                CanAcceptNewTriggeredWork: instanceActive && (scheduleAllowed || applicationEventAllowed)));
    }
}
