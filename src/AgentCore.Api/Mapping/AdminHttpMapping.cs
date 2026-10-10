using System.Text.Json;
using AgentCore.Application.Admin;
using AgentCore.Application.Events;
using AgentCore.Domain.Events;
using AgentCore.Contracts.Http;
using AgentCore.Domain.Definitions;
using AgentCore.Domain.Triggers;

namespace AgentCore.Api.Mapping;

internal static class AdminHttpMapping
{
    public static AdminDefinitionInventoryItemResponse ToDefinitionItem(AdminDefinitionInventoryItem item) =>
        new(item.DefinitionId, item.Version, item.Source, item.Status, item.DisplayName, item.DraftCount);

    public static AdminAuthoringOptionsResponse ToAuthoringOptions(AdminAuthoringOptions options) =>
        new(
            options.LanguageModelAliases,
            options.SpeechRecognizerAliases,
            options.SpeechSynthesizerAliases,
            options.DefaultLanguageModelAlias,
            options.DefaultSpeechRecognizerAlias,
            options.DefaultSpeechSynthesizerAlias,
            options.DefaultModelKey,
            options.Models
                .Select(item => new AdminAuthoringModelOptionResponse(
                    item.Key,
                    item.DisplayName,
                    item.SupportedReasoningEfforts,
                    item.DefaultReasoningEffort))
                .ToArray(),
            options.InterruptionClassifiers);

    public static AdminInstanceInventoryItemResponse ToInstanceItem(AdminInstanceInventoryItem item) =>
        new(
            item.InstanceId.ToString("D"),
            item.DefinitionId,
            item.ActiveVersion,
            item.Lifecycle.ToString(),
            item.PersonaName,
            item.CreatedAt.ToString("o"),
            item.UpdatedAt.ToString("o"));

    public static AdminAgentInstanceResponse ToAgentInstance(AgentInstance instance) =>
        new(
            instance.InstanceId.ToString("D"),
            instance.DefinitionId,
            instance.ActiveVersion,
            instance.Lifecycle.ToString(),
            instance.Revision,
            instance.PersonaRevision,
            ToPersona(instance.Persona),
            instance.UnattendedModelCatalogKey,
            instance.UnattendedReasoningEffort, ExecutionBudgets: instance.ExecutionBudgets is null ? null : JsonSerializer.SerializeToElement(instance.ExecutionBudgets, new JsonSerializerOptions(JsonSerializerDefaults.Web)));

    public static AdminEffectiveConfigurationResponse ToEffectiveConfiguration(AdminEffectiveConfiguration config) =>
        new(
            config.DefinitionSource,
            config.DefinitionId,
            config.DefinitionVersion,
            config.DefinitionStatus,
            config.InstanceId.ToString("D"),
            config.InstanceLifecycle,
            config.InstanceRevision,
            config.PersonaRevision,
            ToPersona(config.Persona),
            ToProviderPreferences(config.ProviderPreferences),
            ToEffectiveModel(config.EffectiveModel),
            config.EffectiveToolAllowlist,
            config.HarnessReferences,
            config.WorkspaceTemplateId,
            config.KnowledgeSources
                .Select(item => new AdminKnowledgeSourceResponse(
                    item.Identity,
                    item.Title,
                    item.Citation,
                    item.ResolvedResourcePath))
                .ToArray(),
            ToMemoryPolicy(config.MemoryPolicy),
            config.TriggerPolicy is null ? null : ToTriggerPolicy(config.TriggerPolicy),
            new AdminDurableExecutionEligibilityResponse(
                config.DurableExecutionEligibility.InstanceActive,
                config.DurableExecutionEligibility.DefinitionResolved,
                config.DurableExecutionEligibility.TriggerPolicyEnabled,
                config.DurableExecutionEligibility.AllowsScheduleSource,
                config.DurableExecutionEligibility.AllowsApplicationEventSource,
                config.DurableExecutionEligibility.CanAcceptNewTriggeredWork),
            config.UnattendedModelCatalogKey,
            config.UnattendedReasoningEffort,
            config.Browser is { } browser ? new AdminBrowserConfigurationResponse(browser.ProviderId, browser.DisplayName, browser.Enabled, browser.Ready, browser.ProfileMode, browser.PolicyMode, browser.SupportedFeatures, browser.MaxSnapshotBytes, browser.MaxCaptureBytes, browser.MaxDownloadBytes, browser.Engine,
                browser.Limits is { } limits ? new AdminBrowserLimitsResponse(limits.OperationTimeoutMs, limits.CapturesPerScope,
                    limits.DownloadsPerScope, limits.FindMatches, limits.WaitTimeoutMs, limits.TextInputLength, limits.AutomaticSettleMs) : null) : null);

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
        new(
            item.DefinitionId,
            item.Version,
            item.Status.ToString(),
            item.MetadataRevision,
            item.PublishedAt.ToString("o"),
            ToPublishedSkills(item.Skills));

    public static AdminDefinitionPublicationSummaryResponse ToPublicationSummary(AgentDefinitionPublication publication) =>
        new(
            publication.DefinitionId,
            publication.Version,
            publication.Status.ToString(),
            publication.MetadataRevision,
            publication.PublishedAt.ToString("o"),
            publication.Payload.SkillList
                .Select(skill => new AdminPublishedSkillResponse(
                    skill.Id,
                    skill.Name,
                    skill.RequiredCapabilities))
                .ToArray());

    private static IReadOnlyList<AdminPublishedSkillResponse> ToPublishedSkills(
        IReadOnlyList<PublishedSkillReference>? skills) =>
        (skills ?? [])
            .Select(skill => new AdminPublishedSkillResponse(skill.Id, skill.Name, skill.RequiredCapabilities))
            .ToArray();

    public static AdminDefinitionDraftValidationResponse ToValidation(DefinitionValidationResult result) =>
        new(
            result.DraftId.ToString("D"),
            result.DraftRevision,
            result.ConfigurationFingerprint,
            result.HasBlockingFindings,
            result.Findings
                .Select(finding => new AdminDefinitionValidationFindingResponse(
                    finding.Field,
                    finding.Code,
                    finding.Message,
                    finding.Severity.ToString()))
                .ToArray());

    public static AdminDefinitionEvaluationScenarioResponse ToEvaluationScenario(DefinitionEvaluationScenario scenario) =>
        new(
            scenario.ScenarioId,
            scenario.ScenarioVersion,
            scenario.Title,
            scenario.Prompt,
            scenario.RequirementLevel.ToString(),
            scenario.CheckType.ToString(),
            scenario.ToolName,
            scenario.UpdatedAt.ToString("o"));

    public static AdminDefinitionEvaluationResultResponse ToEvaluationResult(DefinitionEvaluationResult result) =>
        new(
            result.DraftId.ToString("D"),
            result.DraftRevision,
            result.ConfigurationFingerprint,
            result.ScenarioId,
            result.ScenarioVersion,
            result.RuntimeKind,
            result.Passed,
            result.Findings,
            result.RecordedAt.ToString("o"));

    public static AdminDefinitionDraftDiffResponse ToDiff(DefinitionDraftDiffResult result) =>
        new(
            result.DraftId.ToString("D"),
            result.DraftRevision,
            result.BaselineKind,
            result.BaselineVersion,
            result.Sections
                .Select(section => new AdminDefinitionDiffSectionResponse(
                    section.SectionId,
                    section.Label,
                    section.ChangeKind.ToString(),
                    section.BeforeSummary,
                    section.AfterSummary))
                .ToArray());

    public static AdminDefinitionDraftResourceResponse ToDraftResource(AgentDefinitionDraftResource item) =>
        new(
            item.ResourceId.ToString("D"),
            item.LogicalPath,
            item.Kind.ToString(),
            item.MediaType,
            item.ContentSha256,
            item.ByteLength,
            item.UpdatedAt.ToString("o"));

    public static AdminDefinitionResourceContentStoredResponse ToStoredContent(DefinitionResourceContentStored item) =>
        new(item.ContentSha256, item.ByteLength, item.MediaType);

    public static AdminDefinitionPublicationResourceResponse ToPublicationResource(AgentDefinitionPublicationResource item) =>
        new(
            item.ResourceId.ToString("D"),
            item.LogicalPath,
            item.Kind.ToString(),
            item.MediaType,
            item.ContentSha256,
            item.ByteLength);

    public static AdminLearnedMemoryListResponse ToLearnedMemoryList(AdminLearnedMemoryListResult result) =>
        new(
            result.Scope.ToString(),
            result.Items.Select(ToLearnedMemoryItem).ToArray());

    public static AdminLearnedMemoryItemResponse ToLearnedMemoryItem(AdminLearnedMemoryItem item) =>
        new(
            item.MemoryId.ToString("D"),
            item.Kind.ToString(),
            item.Subject,
            item.Content,
            new AdminLearnedMemoryProvenanceResponse(
                item.ProvenanceSource,
                item.OriginSessionId?.ToString("D"),
                item.OriginMemoryId?.ToString("D"),
                item.RecordedAt.ToString("o"), (item.DerivedFromMemoryIds ?? []).Select(id => id.ToString("D")).ToArray(), item.MaintenanceOrigin,
                item.MaintenanceAgentInstanceId?.ToString("D"), item.MaintenanceSessionId?.ToString("D"), item.MaintenanceAgentRunId?.ToString("D")),
            item.UpdatedAt.ToString("o"), item.Status.ToString());

    public static AdminLearnedMemoryResetResponse ToLearnedMemoryReset(AdminLearnedMemoryResetResult result) =>
        new(result.Scope.ToString(), result.ItemsRemoved);

    public static AdminAutomationRegistrationListResponse ToAutomationRegistrations(
        IReadOnlyList<AdminAutomationRegistration> items) =>
        new(items.Select(ToAutomationRegistration).ToArray());

    public static AdminAutomationRegistrationResponse ToAutomationRegistration(AdminAutomationRegistration item) =>
        new(
            item.AutomationId.ToString("D"),
            item.Intent,
            ToAutomationStatus(item.Status),
            item.ScheduleKind,
            item.TimeZoneId,
            item.ScheduleSummary,
            item.NextOccurrenceAtUtc?.ToString("o"),
            item.Revision,
            item.SuspensionReason,
            new AdminAutomationProvenanceResponse(
                item.Provenance.AuthorizationOrigin,
                item.Provenance.SourceSessionId,
                item.Provenance.CreatedAt.ToString("o"),
                item.Provenance.UpdatedAt.ToString("o")),
            item.ModelOverrideCatalogKey,
            item.ModelOverrideReasoningEffort,
            item.ModelSource);

    private static string ToAutomationStatus(AutomationStatus status) => status switch
    {
        AutomationStatus.Active => "active",
        AutomationStatus.Completed => "completed",
        AutomationStatus.Cancelled => "cancelled",
        AutomationStatus.Expired => "expired",
        AutomationStatus.SuspendedPolicy => "suspendedPolicy",
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, null)
    };

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

    public static AdminEventResponse ToEvent(AdminEvent item)
    {
        using var document = JsonDocument.Parse(item.SummaryJson);
        return new AdminEventResponse(
            item.EventId.ToString("D"),
            item.OperationId.ToString("D"),
            item.OccurredAt.ToString("o"),
            item.ActorKind.ToString(),
            item.Operation.ToString(),
            item.TargetType,
            item.TargetId,
            item.Revision,
            item.Version,
            document.RootElement.Clone());
    }

    public static AdminWebhookEventResponse ToWebhookEvent(WebhookEvent source, int subscriberCount = 0, DateTimeOffset? lastReceived = null, int activeSubscriberCount = 0) =>
        new(source.ResourceId.ToString("D"), source.DisplayName, source.EventKey, source.Status.ToString(), source.Revision,
            source.CreatedAtUtc.ToString("O"), source.UpdatedAtUtc.ToString("O"), subscriberCount, lastReceived?.ToString("O"), activeSubscriberCount);
    public static AdminWebhookEventCredentialResponse ToWebhookEventCredential(ExternalEventCredential credential) =>
        new(credential.ResourceId.ToString("D"), credential.EventKey, credential.Token, credential.Status.ToString());
}
