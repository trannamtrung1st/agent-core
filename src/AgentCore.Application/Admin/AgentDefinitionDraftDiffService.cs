using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Definitions;

namespace AgentCore.Application.Admin;

public sealed class AgentDefinitionDraftDiffService(
    AgentDefinitionLifecycleService lifecycle,
    AgentDefinitionResourceService resources,
    IBuiltInAgentDefinitionStore builtIns,
    IAgentDefinitionAdminStore admin)
{
    public async ValueTask<DefinitionDraftDiffResult> GetDraftDiffAsync(
        Guid draftId,
        CancellationToken cancellationToken = default)
    {
        var draft = await lifecycle.GetDraftAsync(draftId, cancellationToken).ConfigureAwait(false);
        var revisionSnapshot = draft.Revision;
        var baseline = await ResolveBaselineCandidateAsync(draft, cancellationToken).ConfigureAwait(false);
        var draftResources = await resources.ListDraftResourcesAsync(draftId, cancellationToken).ConfigureAwait(false);
        draft = await lifecycle.GetDraftAsync(draftId, cancellationToken).ConfigureAwait(false);
        if (draft.Revision != revisionSnapshot)
        {
            throw AgentCoreErrors.Conflict("Draft changed during diff; retry diff.");
        }

        var baselineResources = await ResolveBaselineResourcesAsync(draft, cancellationToken).ConfigureAwait(false);

        var sections = new List<DefinitionDiffSection>();
        if (baseline is null)
        {
            sections.AddRange(AddedCandidateSections(draft.Candidate));
            sections.Add(AddedResourcesSection(draftResources));
        }
        else
        {
            sections.AddRange(CompareCandidateSections(draft.Candidate, baseline));
            sections.Add(CompareResources(draftResources, baselineResources));
        }

        return new DefinitionDraftDiffResult(
            draft.DraftId,
            draft.Revision,
            draft.SourceKind.ToString(),
            draft.SourceVersion,
            sections);
    }

    private async ValueTask<AgentDefinitionCandidate?> ResolveBaselineCandidateAsync(
        AgentDefinitionDraft draft,
        CancellationToken cancellationToken)
    {
        return draft.SourceKind switch
        {
            DefinitionDraftSourceKind.New => null,
            DefinitionDraftSourceKind.ForkBuiltIn when draft.SourceVersion is int version =>
                AgentDefinitionCandidate.FromDefinition(
                    await builtIns.GetAsync(draft.DefinitionId, version, cancellationToken).ConfigureAwait(false)
                    ?? throw AgentCoreErrors.NotFound("Built-in definition version was not found.")),
            DefinitionDraftSourceKind.ForkDurable when draft.SourceVersion is int version =>
                AgentDefinitionCandidate.FromDefinition(
                    (await admin.GetPublicationAsync(draft.DefinitionId, version, cancellationToken).ConfigureAwait(false)
                     ?? throw AgentCoreErrors.NotFound("Durable publication was not found.")).Payload),
            _ => throw AgentCoreErrors.Validation("Draft fork baseline is not available.")
        };
    }

    private async ValueTask<IReadOnlyList<AgentDefinitionDraftResource>> ResolveBaselineResourcesAsync(
        AgentDefinitionDraft draft,
        CancellationToken cancellationToken)
    {
        if (draft.SourceKind != DefinitionDraftSourceKind.ForkDurable || draft.SourceVersion is not int version)
        {
            return [];
        }

        var rows = await resources.ListPublicationResourcesAsync(draft.DefinitionId, version, cancellationToken)
            .ConfigureAwait(false);
        return rows
            .Select(row => new AgentDefinitionDraftResource(
                row.ResourceId,
                draft.DraftId,
                row.LogicalPath,
                row.Kind,
                row.MediaType,
                row.ContentSha256,
                row.ByteLength,
                DateTimeOffset.MinValue))
            .ToArray();
    }

    private static IEnumerable<DefinitionDiffSection> CompareCandidateSections(
        AgentDefinitionCandidate current,
        AgentDefinitionCandidate baseline)
    {
        yield return TextSection("instructions", "Instructions", baseline.SystemInstructions, current.SystemInstructions);
        yield return TextSection("goals", "Goals", FormatGoals(baseline.Goals), FormatGoals(current.Goals));
        yield return TextSection(
            "identity",
            "Identity seed",
            FormatIdentity(baseline.Identity),
            FormatIdentity(current.Identity));
        yield return TextSection(
            "behavior",
            "Behavior policy",
            FormatBehaviorPolicy(baseline.BehaviorPolicy),
            FormatBehaviorPolicy(current.BehaviorPolicy));
        yield return TextSection(
            "conversation",
            "Conversation policy",
            FormatConversationPolicy(baseline.ConversationPolicy),
            FormatConversationPolicy(current.ConversationPolicy));
        yield return TextSection(
            "initiative",
            "Initiative policy",
            FormatInitiativePolicy(baseline.InitiativePolicy),
            FormatInitiativePolicy(current.InitiativePolicy));
        yield return TextSection(
            "providers",
            "Provider preferences",
            FormatProviderPreferences(baseline.ProviderPreferences),
            FormatProviderPreferences(current.ProviderPreferences));
        yield return TextSection(
            "modelDefaults",
            "Model defaults",
            FormatModelDefaults(baseline.ModelDefaults),
            FormatModelDefaults(current.ModelDefaults));
        yield return TextSection(
            "capabilities",
            "Capabilities",
            FormatEnvironment(baseline.Environment),
            FormatEnvironment(current.Environment));
        yield return TextSection(
            "memoryPolicy",
            "Memory policy",
            FormatMemoryPolicy(baseline.MemoryPolicy),
            FormatMemoryPolicy(current.MemoryPolicy));
        yield return TextSection(
            "triggerPolicy",
            "Trigger policy",
            FormatTriggerPolicy(baseline.TriggerPolicy),
            FormatTriggerPolicy(current.TriggerPolicy));
    }

    private static IEnumerable<DefinitionDiffSection> AddedCandidateSections(AgentDefinitionCandidate current)
    {
        yield return AddedSection("instructions", "Instructions", current.SystemInstructions);
        yield return AddedSection("goals", "Goals", FormatGoals(current.Goals));
        yield return AddedSection("identity", "Identity seed", FormatIdentity(current.Identity));
        yield return AddedSection("behavior", "Behavior policy", FormatBehaviorPolicy(current.BehaviorPolicy));
        yield return AddedSection("conversation", "Conversation policy", FormatConversationPolicy(current.ConversationPolicy));
        yield return AddedSection("initiative", "Initiative policy", FormatInitiativePolicy(current.InitiativePolicy));
        yield return AddedSection("providers", "Provider preferences", FormatProviderPreferences(current.ProviderPreferences));
        yield return AddedSection("modelDefaults", "Model defaults", FormatModelDefaults(current.ModelDefaults));
        yield return AddedSection("capabilities", "Capabilities", FormatEnvironment(current.Environment));
        yield return AddedSection("memoryPolicy", "Memory policy", FormatMemoryPolicy(current.MemoryPolicy));
        yield return AddedSection("triggerPolicy", "Trigger policy", FormatTriggerPolicy(current.TriggerPolicy));
    }

    private static DefinitionDiffSection AddedResourcesSection(IReadOnlyList<AgentDefinitionDraftResource> current)
    {
        var after = FormatResources(current);
        return Section(
            "resources",
            "Harness resources",
            DefinitionDiffChangeKind.Added,
            null,
            Summarize(after));
    }

    private static DefinitionDiffSection CompareResources(
        IReadOnlyList<AgentDefinitionDraftResource> current,
        IReadOnlyList<AgentDefinitionDraftResource> baseline)
    {
        var before = FormatResources(baseline);
        var after = FormatResources(current);
        return TextSection("resources", "Harness resources", before, after);
    }

    private static DefinitionDiffSection TextSection(
        string id,
        string label,
        string before,
        string after) =>
        Section(id, label, Classify(before, after), Summarize(before), Summarize(after));

    private static DefinitionDiffSection AddedSection(string id, string label, string after) =>
        Section(id, label, DefinitionDiffChangeKind.Added, null, Summarize(after));

    private static DefinitionDiffSection Section(
        string id,
        string label,
        DefinitionDiffChangeKind kind,
        string? before,
        string? after) =>
        new(id, label, kind, before, after);

    private static DefinitionDiffChangeKind Classify(string before, string after) =>
        string.Equals(before, after, StringComparison.Ordinal)
            ? DefinitionDiffChangeKind.Unchanged
            : DefinitionDiffChangeKind.Modified;

    private static string Summarize(string value)
    {
        if (value.Length <= 240)
        {
            return value;
        }

        return value[..240] + "…";
    }

    private static string FormatGoals(IReadOnlyList<string> goals) =>
        goals.Count == 0 ? "(none)" : string.Join(" | ", goals);

    private static string FormatIdentity(AgentIdentity identity) =>
        $"name={identity.Name}; role={identity.Role}; description={identity.Description}; tone={identity.Tone}";

    private static string FormatBehaviorPolicy(BehaviorPolicy policy) =>
        $"interruption={policy.InterruptionStyle}; acknowledgeInterruption={policy.AcknowledgeInterruption}; avoidUnsupportedClaims={policy.AvoidUnsupportedClaims}";

    private static string FormatConversationPolicy(ConversationPolicy policy) =>
        $"responseLength={policy.ResponseLength}; askOneQuestionAtATime={policy.AskOneQuestionAtATime}; language={policy.Language}; maxOutputTokens={policy.MaxOutputTokens}";

    private static string FormatInitiativePolicy(InitiativePolicy policy)
    {
        var triggers = policy.Triggers.Count == 0 ? "(none)" : string.Join(", ", policy.Triggers);
        return
            $"enabled={policy.Enabled}; silenceThresholdMs={policy.SilenceThresholdMs}; cooldownMs={policy.CooldownMs}; maxPerSilencePeriod={policy.MaxPerSilencePeriod}; triggers=[{triggers}]; maxConsecutiveProactiveTurns={policy.MaxConsecutiveProactiveTurns?.ToString() ?? "(default)"}; maxSilentEvaluations={policy.MaxSilentEvaluations?.ToString() ?? "(default)"}; maxInactivityMs={policy.MaxInactivityMs?.ToString() ?? "(default)"}";
    }

    private static string FormatTriggerPolicy(TriggerPolicy? policy)
    {
        if (policy is null)
        {
            return "(default)";
        }

        var allowed = policy.AllowedSourceKinds.Count == 0
            ? "(none)"
            : string.Join(", ", policy.AllowedSourceKinds);
        return
            $"enabled={policy.Enabled}; allowUserScheduling={policy.AllowUserScheduling}; allowOneShot={policy.AllowOneShot}; allowDaily={policy.AllowDaily}; allowWeekly={policy.AllowWeekly}; allowIndefiniteRecurrence={policy.AllowIndefiniteRecurrence}; maxActiveRegistrations={policy.MaxActiveRegistrations}; oneShotHorizonDays={policy.OneShotHorizonDays}; minRecurrenceDays={policy.MinRecurrenceDays}; allowedSourceKinds=[{allowed}]; allowFixedInterval={policy.AllowFixedInterval}; minFixedIntervalSeconds={policy.MinFixedIntervalSeconds}";
    }

    private static string FormatProviderPreferences(ProviderPreferences preferences) =>
        $"languageModel={preferences.LanguageModel}; speechRecognizer={preferences.SpeechRecognizer ?? "(none)"}; speechSynthesizer={preferences.SpeechSynthesizer ?? "(none)"}; interruptionClassifier={preferences.InterruptionClassifier}";

    private static string FormatModelDefaults(AgentModelDefaults? defaults) =>
        defaults is null
            ? "(default)"
            : $"catalogKey={defaults.CatalogKey ?? "(none)"}; reasoningEffort={defaults.ReasoningEffort ?? "(none)"}";

    private static string FormatMemoryPolicy(MemoryPolicy? policy)
    {
        if (policy is null)
        {
            return "(default)";
        }

        return
            $"sessionMemory={policy.SessionMemory}; identityUserPromotion={policy.IdentityUserPromotion}; identityUserRetrieval={policy.IdentityUserRetrieval}; userPromotion={policy.UserPromotion}; userRetrieval={policy.UserRetrieval}";
    }

    private static string FormatEnvironment(RoleEnvironment? environment)
    {
        if (environment is null)
        {
            return "(none)";
        }

        var tools = environment.ToolList.Count == 0 ? "(none)" : string.Join(", ", environment.ToolList);
        var harness = environment.HarnessList.Count == 0 ? "(none)" : string.Join(", ", environment.HarnessList);
        var knowledge = environment.KnowledgeList.Count == 0
            ? "(none)"
            : string.Join(", ", environment.KnowledgeList.Select(item => item.Identity));
        var template = environment.WorkspacePolicy.TemplateId ?? "(none)";
        return $"tools=[{tools}]; harness=[{harness}]; knowledge=[{knowledge}]; template={template}";
    }

    private static string FormatResources(IReadOnlyList<AgentDefinitionDraftResource> resources)
    {
        if (resources.Count == 0)
        {
            return "(none)";
        }

        return string.Join(
            " | ",
            resources
                .OrderBy(item => item.LogicalPath, StringComparer.Ordinal)
                .Select(item => $"{item.LogicalPath} ({item.Kind}, sha256={item.ContentSha256[..12]}…)"));
    }
}
