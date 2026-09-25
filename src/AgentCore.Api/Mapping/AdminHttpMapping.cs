using System.Text.Json;
using AgentCore.Application.Admin;
using AgentCore.Contracts.Http;
using AgentCore.Domain.Definitions;

namespace AgentCore.Api.Mapping;

internal static class AdminHttpMapping
{
    public static AdminDefinitionInventoryItemResponse ToDefinitionItem(AdminDefinitionInventoryItem item) =>
        new(item.DefinitionId, item.Version, item.Source, item.Status, item.DisplayName);

    public static AdminInstanceInventoryItemResponse ToInstanceItem(AdminInstanceInventoryItem item) =>
        new(
            item.InstanceId.ToString("D"),
            item.DefinitionId,
            item.ActiveVersion,
            item.Lifecycle.ToString(),
            item.Compatibility,
            item.PersonaName,
            item.CreatedAt.ToString("o"),
            item.UpdatedAt.ToString("o"));

    public static AdminEffectiveConfigurationResponse ToEffectiveConfiguration(AdminEffectiveConfiguration config) =>
        new(
            config.DefinitionSource,
            config.DefinitionId,
            config.DefinitionVersion,
            config.DefinitionStatus,
            config.InstanceId.ToString("D"),
            config.InstanceLifecycle,
            config.Compatibility,
            ToPersona(config.Persona),
            ToProviderPreferences(config.ProviderPreferences),
            ToEffectiveModel(config.EffectiveModel),
            config.EffectiveToolAllowlist,
            config.HarnessReferences,
            config.WorkspaceTemplateId,
            config.KnowledgeSources
                .Select(item => new AdminKnowledgeSourceResponse(item.Identity, item.Title, item.Citation))
                .ToArray(),
            ToMemoryPolicy(config.MemoryPolicy),
            config.TriggerPolicy is null ? null : ToTriggerPolicy(config.TriggerPolicy),
            new AdminDurableExecutionEligibilityResponse(
                config.DurableExecutionEligibility.InstanceActive,
                config.DurableExecutionEligibility.DefinitionResolved,
                config.DurableExecutionEligibility.TriggerPolicyEnabled,
                config.DurableExecutionEligibility.AllowsScheduleSource,
                config.DurableExecutionEligibility.AllowsApplicationEventSource,
                config.DurableExecutionEligibility.CanAcceptNewTriggeredWork));

    private static AdminPersonaResponse ToPersona(AgentIdentity persona) =>
        new(persona.Name, persona.Role, persona.Description, persona.Tone);

    private static AdminProviderPreferencesResponse ToProviderPreferences(ProviderPreferences preferences) =>
        new(
            preferences.LanguageModel,
            preferences.SpeechRecognizer,
            preferences.SpeechSynthesizer,
            preferences.InterruptionClassifier);

    private static AdminEffectiveModelResponse ToEffectiveModel(AdminEffectiveModel model) =>
        new(model.CatalogKey, model.DisplayName, model.SelectionSource, model.ReasoningEffort, model.ModelId);

    private static AdminMemoryPolicyResponse ToMemoryPolicy(MemoryPolicy policy) =>
        new(
            policy.SessionMemory,
            policy.IdentityUserPromotion,
            policy.IdentityUserRetrieval,
            policy.UserPromotion,
            policy.UserRetrieval);

    public static AdminDefinitionDraftSummaryResponse ToDraftSummary(AgentDefinitionDraftSummary item) =>
        new(
            item.DraftId.ToString("D"),
            item.DefinitionId,
            item.Revision,
            item.SourceKind.ToString(),
            item.SourceVersion,
            item.UpdatedAt.ToString("o"));

    public static AdminDefinitionDraftResponse ToDraft(AgentDefinitionDraft draft) =>
        new(
            draft.DraftId.ToString("D"),
            draft.DefinitionId,
            draft.Revision,
            draft.SourceKind.ToString(),
            draft.SourceVersion,
            draft.CreatedAt.ToString("o"),
            draft.UpdatedAt.ToString("o"),
            AdminDefinitionJson.WriteCandidate(draft.Candidate));

    public static AdminDefinitionPublicationSummaryResponse ToPublicationSummary(AgentDefinitionPublicationSummary item) =>
        new(item.DefinitionId, item.Version, item.Status.ToString(), item.PublishedAt.ToString("o"));

    private static AdminTriggerPolicyResponse ToTriggerPolicy(TriggerPolicy policy) =>
        new(
            policy.Enabled,
            policy.AllowUserScheduling,
            policy.AllowOneShot,
            policy.AllowDaily,
            policy.AllowWeekly,
            policy.AllowIndefiniteRecurrence,
            policy.MaxActiveRegistrations,
            policy.OneShotHorizonDays,
            policy.MinRecurrenceDays,
            policy.AllowedSourceKinds,
            policy.AllowFixedInterval,
            policy.MinFixedIntervalSeconds);
}
