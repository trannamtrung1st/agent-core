using AgentCore.Application.Models;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Application.Tools;
using AgentCore.Application.Triggers;
using AgentCore.Domain.Definitions;
using AgentCore.Domain.Triggers;

namespace AgentCore.Application.Admin;

/// <summary>
/// Admin effective-configuration projection with its memory-policy default.
/// This is not the universal runtime configuration object. Session bind, durable work,
/// and draft evaluation compose their own state from the same resolution primitives.
/// </summary>
internal static class EffectiveConfigurationComposer
{
    public static MemoryPolicy MemoryPolicyOf(AgentDefinition definition) =>
        definition.MemoryPolicy ?? MemoryPolicy.Disabled;

    public static AdminEffectiveConfiguration ComposeAdmin(
        AgentInstance instance,
        AgentDefinition definition,
        IModelCatalog catalog,
        IToolConfigurationGate configurationGate,
        string definitionSource,
        string definitionStatus)
    {
        definition = Agents.InstanceSettingsResolver.Resolve(definition, instance.SettingsOverrides);
        var environment = DefinitionExecutionPolicies.Of(definition);
        var trigger = definition.TriggerPolicy;
        var instanceActive = instance.Lifecycle == AgentInstanceLifecycle.Active;
        var scheduleAllowed = OccurrenceCompatibility.Allows(definition, TriggerSourceKind.Schedule);
        var applicationEventAllowed = OccurrenceCompatibility.Allows(definition, TriggerSourceKind.ApplicationEvent);
        Domain.Conversation.SessionModelSelection? model = null; ModelDescriptor? descriptor = null; string? unavailable = null;
        try { model = SessionModelBinder.PinDefault(catalog, definition); descriptor = catalog.Get(model.OfferingId); }
        catch (AgentCoreException error) when (error.Code is "ModelUnavailable" or "ModelRouteStale" or "ModelCapabilityUnsupported" or "ValidationError" or "ModelRoutingUnavailable")
        { unavailable = error.Code; }
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
            ModelSupportsTools: descriptor?.Tools == true,
            ModelSupportsVision: descriptor?.Vision == true,
            AgentWorkspaceAvailable: instanceActive);
        var offeredTools = (definition.ExecutionPolicy?.Capabilities is null ? ToolCatalog.For(definition, adminContext, configurationGate) : ToolCatalog.Eligible(definition, adminContext, configurationGate))
            .Select(item => item.Name)
            .OrderBy(item => item, StringComparer.Ordinal)
            .ToArray();

        return new AdminEffectiveConfiguration(
            DefinitionSource: definitionSource,
            DefinitionId: definition.Id,
            DefinitionVersion: definition.Version,
            DefinitionStatus: definitionStatus,
            InstanceId: instance.InstanceId,
            InstanceLifecycle: instance.Lifecycle.ToString(),
            InstanceRevision: instance.Revision,
            PersonaRevision: instance.PersonaRevision,
            Persona: instance.Persona,
            ProviderPreferences: definition.ProviderPreferences,
            EffectiveModel: new AdminEffectiveModel(
                definition.ModelDefaults?.Mode == "auto" ? "" : model?.OfferingId ?? definition.ModelDefaults?.OfferingId ?? catalog.DefaultOfferingId,
                definition.ModelDefaults?.Mode == "auto" ? "Auto · resolves for each new Run" : descriptor?.DisplayName ?? "Model unavailable",
                definition.ModelDefaults?.Mode == "auto" ? "auto" : model is null ? SessionModelBinder.IsInheritedOffering(definition.ModelDefaults?.OfferingId) ? "tenantDefault" : "agentDefault" : SessionModelBinder.ToWire(model.SelectionSource),
                model?.ReasoningEffort ?? definition.ModelDefaults?.Reasoning?.Effort,
                model?.ExternalModelId, Available: model is not null && descriptor is not null,
                UnavailableReason: unavailable),
            EffectiveToolAllowlist: offeredTools,
            HarnessReferences: environment.HarnessList.OrderBy(item => item, StringComparer.Ordinal).ToArray(),
            WorkspaceTemplateId: environment.WorkspacePolicy.TemplateId,
            KnowledgeSources: environment.KnowledgeList
                .Select(item => new AdminKnowledgeSource(
                    item.Identity,
                    item.Title,
                    item.Citation,
                    KnowledgeSourcePaths.ResolveBackingPath(item)))
                .ToArray(),
            MemoryPolicy: MemoryPolicyOf(definition),
            TriggerPolicy: trigger,
            DurableExecutionEligibility: new AdminDurableExecutionEligibility(
                InstanceActive: instanceActive,
                DefinitionResolved: true,
                TriggerPolicyEnabled: trigger?.Enabled == true,
                AllowsScheduleSource: scheduleAllowed,
                AllowsApplicationEventSource: applicationEventAllowed,
                CanAcceptNewTriggeredWork: instanceActive && descriptor?.Tools == true && (scheduleAllowed || applicationEventAllowed)),
            UnattendedModel: instance.UnattendedModel);
    }
}
