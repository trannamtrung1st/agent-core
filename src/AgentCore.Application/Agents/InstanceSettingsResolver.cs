using System.Text.Json;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Definitions;

namespace AgentCore.Application.Agents;

public sealed record InstanceSettingConstraint(string Reason, bool? RequiredBoolean = null, double? Minimum = null, double? Maximum = null, IReadOnlyList<string>? Choices = null);

public static class InstanceSettingsResolver
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.Strict };
    public static IReadOnlyList<string> Sections { get; } = ["instructions", "conversationPolicy", "behaviorPolicy", "initiativePolicy", "voice", "modelDefaults", "memoryPolicy", "capabilities", "triggerPolicy", "providerPreferences", "sandboxPolicy", "stateIsolationPolicy"];
    public static IReadOnlyList<string> Fields(string section) => section switch
    {
        "stateIsolationPolicy" => ["conversationContextScope", "homeAccess", "workingCleanup", "browserIsolation", "agentStateVisibility", "experienceRecord", "experienceRecall", "profileEvidence", "crossSessionContinuity"],
        "sandboxPolicy" => ["allowedProfileIds", "profileId", "initializationScript", "cpu", "memoryMiB", "maxCommandSeconds", "idleTtlSeconds", "maxLifetimeSeconds", "storageBytes"],
        "instructions" => ["agentInstructions"],
        "conversationPolicy" => ["responseLength", "askOneQuestionAtATime", "language", "maxOutputTokens"],
        "behaviorPolicy" => ["interruptionStyle", "acknowledgeInterruption", "avoidUnsupportedClaims"],
        "initiativePolicy" => ["enabled", "silenceThresholdMs", "cooldownMs", "maxPerSilencePeriod", "triggers", "maxConsecutiveProactiveTurns", "maxSilentEvaluations", "maxInactivityMs"],
        "voice" => ["enabled", "voiceId", "speakingRate"],
        "modelDefaults" => ["catalogKey", "reasoningEffort"],
        "memoryPolicy" => ["sessionMemory", "identityUserPromotion", "identityUserRetrieval", "userPromotion", "userRetrieval"],
        "capabilities" => ["selected", "always", "allowUnreadUnsupportedTypes"],
        "triggerPolicy" => ["enabled", "allowUserScheduling", "allowOneShot", "allowDaily", "allowWeekly", "allowIndefiniteRecurrence", "maxActiveRegistrations", "oneShotHorizonDays", "minRecurrenceDays", "allowedSourceKinds", "allowFixedInterval", "minFixedIntervalSeconds"],
        "providerPreferences" => ["languageModel", "speechRecognizer", "speechSynthesizer", "interruptionClassifier"],
        _ => throw AgentCoreErrors.Validation("Unknown settings section.")
    };

    public static InstanceSettingsOverrides Patch(InstanceSettingsOverrides current, string section,
        IReadOnlyDictionary<string, JsonElement> set, IReadOnlyList<string> clear)
    {
        var allowed = Fields(section);
        if (set.Count + clear.Count > allowed.Count || clear.Distinct(StringComparer.Ordinal).Count() != clear.Count
            || set.Keys.Concat(clear).Any(k => !allowed.Contains(k, StringComparer.Ordinal))
            || clear.Any(set.ContainsKey)) throw AgentCoreErrors.Validation("Settings fields are unknown, duplicated or both set and cleared.");
        var next = current;
        try
        {
            foreach (var field in set.Keys.Concat(clear))
            {
                var remove = !set.ContainsKey(field);
                next = (section, field) switch
                {
                    ("stateIsolationPolicy", "conversationContextScope") => next with { ConversationContextScope = remove ? null : new(Read<string>(set[field])) },
                    ("stateIsolationPolicy", "homeAccess") => next with { HomeAccess = remove ? null : new(Read<string>(set[field])) },
                    ("stateIsolationPolicy", "workingCleanup") => next with { WorkingCleanup = remove ? null : new(Read<string>(set[field])) },
                    ("stateIsolationPolicy", "browserIsolation") => next with { BrowserIsolation = remove ? null : new(Read<string>(set[field])) },
                    ("stateIsolationPolicy", "agentStateVisibility") => next with { AgentStateVisibility = remove ? null : new(Read<string>(set[field])) },
                    ("stateIsolationPolicy", "experienceRecord") => next with { ExperienceRecord = remove ? null : new(Read<bool>(set[field])) },
                    ("stateIsolationPolicy", "experienceRecall") => next with { ExperienceRecall = remove ? null : new(Read<bool>(set[field])) },
                    ("stateIsolationPolicy", "crossSessionContinuity") => next with { CrossSessionContinuity = remove ? null : new(Read<bool>(set[field])) },
                    ("stateIsolationPolicy", "profileEvidence") => next with { ProfileEvidence = remove ? null : new(Read<bool>(set[field])) },
                    ("sandboxPolicy", "allowedProfileIds") => next with { SandboxAllowedProfileIds = remove ? null : new(Read<string[]>(set[field])) },
                    ("sandboxPolicy", "profileId") => next with { SandboxProfileId = remove ? null : new(Read<string>(set[field])) },
                    ("sandboxPolicy", "initializationScript") => next with { SandboxInitializationScript = remove ? null : new(set[field].ValueKind == JsonValueKind.Null ? null : Read<string>(set[field])) },
                    ("sandboxPolicy", "cpu") => next with { SandboxCpu = remove ? null : new(Read<double>(set[field])) },
                    ("sandboxPolicy", "memoryMiB") => next with { SandboxMemoryMiB = remove ? null : new(Read<int>(set[field])) },
                    ("sandboxPolicy", "maxCommandSeconds") => next with { SandboxMaxCommandSeconds = remove ? null : new(Read<int>(set[field])) },
                    ("sandboxPolicy", "idleTtlSeconds") => next with { SandboxIdleTtlSeconds = remove ? null : new(Read<int>(set[field])) },
                    ("sandboxPolicy", "maxLifetimeSeconds") => next with { SandboxMaxLifetimeSeconds = remove ? null : new(Read<int>(set[field])) },
                    ("sandboxPolicy", "storageBytes") => next with { SandboxStorageBytes = remove ? null : new(Read<long>(set[field])) },

                    ("instructions", "agentInstructions") => next with { AgentInstructions = remove ? null : new(Read<string>(set[field])) },
                    ("conversationPolicy", "responseLength") => next with { ResponseLength = remove ? null : new(Read<string>(set[field])) },
                    ("conversationPolicy", "askOneQuestionAtATime") => next with { AskOneQuestionAtATime = remove ? null : new(Read<bool>(set[field])) },
                    ("conversationPolicy", "language") => next with { Language = remove ? null : new(Read<string>(set[field])) },
                    ("conversationPolicy", "maxOutputTokens") => next with { MaxOutputTokens = remove ? null : new(Read<int>(set[field])) },
                    ("behaviorPolicy", "interruptionStyle") => next with { InterruptionStyle = remove ? null : new(Read<string>(set[field])) },
                    ("behaviorPolicy", "acknowledgeInterruption") => next with { AcknowledgeInterruption = remove ? null : new(Read<bool>(set[field])) },
                    ("behaviorPolicy", "avoidUnsupportedClaims") => next with { AvoidUnsupportedClaims = remove ? null : new(Read<bool>(set[field])) },
                    ("initiativePolicy", "enabled") => next with { InitiativeEnabled = remove ? null : new(Read<bool>(set[field])) },
                    ("initiativePolicy", "silenceThresholdMs") => next with { SilenceThresholdMs = remove ? null : new(Read<int>(set[field])) },
                    ("initiativePolicy", "cooldownMs") => next with { CooldownMs = remove ? null : new(Read<int>(set[field])) },
                    ("initiativePolicy", "maxPerSilencePeriod") => next with { MaxPerSilencePeriod = remove ? null : new(Read<int>(set[field])) },
                    ("initiativePolicy", "triggers") => next with { InitiativeTriggers = remove ? null : new(Read<string[]>(set[field])) },
                    ("initiativePolicy", "maxConsecutiveProactiveTurns") => next with { MaxConsecutiveProactiveTurns = remove ? null : new(Read<int?>(set[field])) },
                    ("initiativePolicy", "maxSilentEvaluations") => next with { MaxSilentEvaluations = remove ? null : new(Read<int?>(set[field])) },
                    ("initiativePolicy", "maxInactivityMs") => next with { MaxInactivityMs = remove ? null : new(Read<int?>(set[field])) },
                    ("voice", "enabled") => next with { VoiceEnabled = remove ? null : new(Read<bool>(set[field])) },
                    ("voice", "voiceId") => next with { VoiceId = remove ? null : new(Read<string>(set[field])) },
                    ("voice", "speakingRate") => next with { SpeakingRate = remove ? null : new(Read<double>(set[field])) },
                    ("modelDefaults", "catalogKey") => next with { ModelCatalogKey = remove ? null : new(set[field].ValueKind == JsonValueKind.Null ? null : Read<string>(set[field])) },
                    ("modelDefaults", "reasoningEffort") => next with { ReasoningEffort = remove ? null : new(set[field].ValueKind == JsonValueKind.Null ? null : Read<string>(set[field])) },
                    ("memoryPolicy", "sessionMemory") => next with { MemorySessionMemory = remove ? null : new(Read<bool>(set[field])) },
                    ("memoryPolicy", "identityUserPromotion") => next with { MemoryIdentityUserPromotion = remove ? null : new(Read<bool>(set[field])) },
                    ("memoryPolicy", "identityUserRetrieval") => next with { MemoryIdentityUserRetrieval = remove ? null : new(Read<bool>(set[field])) },
                    ("memoryPolicy", "userPromotion") => next with { MemoryUserPromotion = remove ? null : new(Read<bool>(set[field])) },
                    ("memoryPolicy", "userRetrieval") => next with { MemoryUserRetrieval = remove ? null : new(Read<bool>(set[field])) },
                    ("capabilities", "selected") => next with { SelectedCapabilities = remove ? null : new(Read<string[]>(set[field])) },
                    ("capabilities", "allowUnreadUnsupportedTypes") => next with { AllowUnreadUnsupportedTypes = remove ? null : new(Read<bool>(set[field])) },
                    ("capabilities", "always") => next with { AlwaysCapabilities = remove ? null : new(Read<string[]>(set[field])) },
                    ("triggerPolicy", "enabled") => next with { TriggerEnabled = remove ? null : new(Read<bool>(set[field])) },
                    ("triggerPolicy", "allowUserScheduling") => next with { AllowUserScheduling = remove ? null : new(Read<bool>(set[field])) },
                    ("triggerPolicy", "allowOneShot") => next with { AllowOneShot = remove ? null : new(Read<bool>(set[field])) },
                    ("triggerPolicy", "allowDaily") => next with { AllowDaily = remove ? null : new(Read<bool>(set[field])) },
                    ("triggerPolicy", "allowWeekly") => next with { AllowWeekly = remove ? null : new(Read<bool>(set[field])) },
                    ("triggerPolicy", "allowIndefiniteRecurrence") => next with { AllowIndefiniteRecurrence = remove ? null : new(Read<bool>(set[field])) },
                    ("triggerPolicy", "maxActiveRegistrations") => next with { MaxActiveRegistrations = remove ? null : new(Read<int>(set[field])) },
                    ("triggerPolicy", "oneShotHorizonDays") => next with { OneShotHorizonDays = remove ? null : new(Read<int>(set[field])) },
                    ("triggerPolicy", "minRecurrenceDays") => next with { MinRecurrenceDays = remove ? null : new(Read<int>(set[field])) },
                    ("triggerPolicy", "allowedSourceKinds") => next with { AllowedSourceKinds = remove ? null : new(Read<string[]>(set[field])) },
                    ("triggerPolicy", "allowFixedInterval") => next with { AllowFixedInterval = remove ? null : new(Read<bool>(set[field])) },
                    ("triggerPolicy", "minFixedIntervalSeconds") => next with { MinFixedIntervalSeconds = remove ? null : new(Read<int>(set[field])) },
                    ("providerPreferences", "languageModel") => next with { ProviderLanguageModel = remove ? null : new(Read<string>(set[field])) },
                    ("providerPreferences", "speechRecognizer") => next with { ProviderSpeechRecognizer = remove ? null : new(set[field].ValueKind == JsonValueKind.Null ? null : Read<string>(set[field])) },
                    ("providerPreferences", "speechSynthesizer") => next with { ProviderSpeechSynthesizer = remove ? null : new(set[field].ValueKind == JsonValueKind.Null ? null : Read<string>(set[field])) },
                    ("providerPreferences", "interruptionClassifier") => next with { ProviderInterruptionClassifier = remove ? null : new(Read<string>(set[field])) },
                    _ => throw AgentCoreErrors.Validation("Unknown settings field.")
                };
            }
        }
        catch (JsonException) { throw AgentCoreErrors.Validation("Settings value has the wrong type."); }
        return next;
    }
    private static T Read<T>(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Null && default(T) is not null) throw new JsonException();
        var parsed = value.Deserialize<T>(Json);
        if (parsed is null && (typeof(T) == typeof(string) || typeof(T) == typeof(string[]))) throw new JsonException();
        if (parsed is string[] items && items.Any(string.IsNullOrWhiteSpace)) throw new JsonException();
        return parsed!;
    }

    private static TriggerPolicy TriggerOf(AgentDefinition d) => d.TriggerPolicy ?? new(false, false, false, false, false, false, 32, 30, 1, [], false, 60);

    // The same published limits govern saves and the controls shown by Admin.
    public static IReadOnlyDictionary<string, InstanceSettingConstraint> Constraints(AgentDefinition baseline, string section)
    {
        var result = new Dictionary<string, InstanceSettingConstraint>(StringComparer.Ordinal);
        void CannotEnable(string field, string label, bool allowed)
        {
            if (!allowed) result[field] = new($"{label} is disabled by the selected Definition. Adopt a Definition that permits it before enabling it for this Instance.", RequiredBoolean: false);
        }
        switch (section)
        {
            case "stateIsolationPolicy":
                var state = baseline.StateIsolationPolicy ?? StateIsolationPolicy.Default;
                result["conversationContextScope"] = new("Instance continuity can only be narrowed.", Choices: state.ConversationContextScope == ConversationContextScope.CurrentRequest ? ["CurrentRequest"] : ["Session", "CurrentRequest"]);
                result["homeAccess"] = new("Instance home access can only be narrowed.", Choices: Enum.GetValues<HomeAccess>().Where(v => v >= state.HomeAccess).Select(v => v.ToString()).ToArray());
                result["workingCleanup"] = new("Cleanup waits for safe terminal eligibility.", Choices: ["OnSessionDeletion", "AfterTerminal"]);
                result["browserIsolation"] = new("Browser isolation cannot broaden the Definition or host policy.", Choices: state.BrowserIsolation == BrowserIsolation.EphemeralSession ? ["EphemeralSession"] : state.BrowserIsolation == BrowserIsolation.PersistentAgent ? ["PersistentAgent", "EphemeralSession"] : ["HostDefault", "PersistentAgent", "EphemeralSession"]);
                result["agentStateVisibility"] = new("State visibility cannot exceed the Definition maximum.", Choices: Enum.GetValues<AgentStateVisibility>().Where(v => v <= state.VisibilityCeiling).Select(v => v.ToString()).ToArray());
                CannotEnable("experienceRecord", "Experience recording", state.ExperienceRecord);
                CannotEnable("experienceRecall", "Experience recall", state.ExperienceRecall);
                CannotEnable("crossSessionContinuity", "Cross-Session continuity", state.CrossSessionContinuity);
                CannotEnable("profileEvidence", "Profile evidence", state.ProfileEvidence);
                break;
            case "sandboxPolicy":
                result["allowedProfileIds"] = new("Instance choices may only narrow the Definition allowed list. Clear to inherit.", Choices: baseline.SandboxPolicy?.AllowedProfileIds ?? []);
                result["profileId"] = new("Choose only a profile authorized by this Definition. Live generations retain their pinned setup until expiry or reset.", Choices: baseline.SandboxPolicy?.AllowedProfileIds ?? []);
                if (baseline.ExecutionSandboxProfile is { } p)
                {
                    result["cpu"] = new("Cannot exceed the Global Profile CPU ceiling.", Minimum:0.25, Maximum:p.Cpu);
                    result["memoryMiB"] = new("Cannot exceed the Global Profile memory ceiling.", Minimum:256, Maximum:p.MemoryMiB);
                    result["maxCommandSeconds"] = new("Cannot exceed the Global Profile command ceiling.", Minimum:1, Maximum:p.MaxCommandSeconds);
                    result["idleTtlSeconds"] = new("Idle time starts after completed work; cannot exceed the Global Profile ceiling.", Minimum:60, Maximum:p.IdleTtlSeconds);
                    result["maxLifetimeSeconds"] = new("Cannot exceed the Global Profile lifetime ceiling or be shorter than idle expiry.", Minimum:60, Maximum:p.MaxLifetimeSeconds);
                    result["storageBytes"] = new("Sampled canonical /working budget; writes can exceed a sample and remain after termination. Cannot exceed the Global Profile ceiling.", Minimum:1, Maximum:p.StorageBytes);
                }
                break;
            case "behaviorPolicy":
                if (baseline.BehaviorPolicy.AvoidUnsupportedClaims)
                    result["avoidUnsupportedClaims"] = new("Avoid unsupported claims is required by the selected Definition and cannot be disabled for this Instance.", RequiredBoolean: true);
                break;
            case "initiativePolicy": CannotEnable("enabled", "Initiative", baseline.InitiativePolicy.Enabled); break;
            case "voice": CannotEnable("enabled", "Voice", baseline.Voice.Enabled); break;
            case "capabilities": CannotEnable("allowUnreadUnsupportedTypes", "Unread unsupported attachments", DefinitionExecutionPolicies.Of(baseline).AttachmentPolicy.AllowUnreadUnsupportedTypes); break;
            case "memoryPolicy":
                var memory = baseline.MemoryPolicy ?? MemoryPolicy.Disabled;
                CannotEnable("sessionMemory", "Session memory", memory.SessionMemory);
                CannotEnable("identityUserPromotion", "Instance memory promotion", memory.IdentityUserPromotion);
                CannotEnable("identityUserRetrieval", "Instance memory retrieval", memory.IdentityUserRetrieval);
                CannotEnable("userPromotion", "User memory promotion", memory.UserPromotion);
                CannotEnable("userRetrieval", "User memory retrieval", memory.UserRetrieval);
                break;
            case "triggerPolicy":
                var trigger = TriggerOf(baseline);
                CannotEnable("enabled", "Triggers", trigger.Enabled);
                CannotEnable("allowUserScheduling", "User scheduling", trigger.AllowUserScheduling);
                CannotEnable("allowOneShot", "One-shot schedules", trigger.AllowOneShot);
                CannotEnable("allowDaily", "Daily schedules", trigger.AllowDaily);
                CannotEnable("allowWeekly", "Weekly schedules", trigger.AllowWeekly);
                CannotEnable("allowIndefiniteRecurrence", "Indefinite recurrence", trigger.AllowIndefiniteRecurrence);
                CannotEnable("allowFixedInterval", "Fixed intervals", trigger.AllowFixedInterval);
                result["maxActiveRegistrations"] = new($"Maximum active schedules cannot exceed the selected Definition’s limit of {trigger.MaxActiveRegistrations}.", Maximum: trigger.MaxActiveRegistrations);
                result["oneShotHorizonDays"] = new($"One-shot horizon cannot exceed the selected Definition’s limit of {trigger.OneShotHorizonDays} days.", Maximum: trigger.OneShotHorizonDays);
                result["minRecurrenceDays"] = new($"Minimum recurrence must be at least {trigger.MinRecurrenceDays} days, as required by the selected Definition.", Minimum: trigger.MinRecurrenceDays);
                result["minFixedIntervalSeconds"] = new($"Minimum fixed interval must be at least {trigger.MinFixedIntervalSeconds} seconds, as required by the selected Definition.", Minimum: trigger.MinFixedIntervalSeconds);
                break;
        }
        return result;
    }

    public static IReadOnlyDictionary<string, AgentCore.Domain.Conversation.RuntimeFieldSource> Sources(AgentDefinition definition,
        InstanceSettingsOverrides? overrides, long instanceRevision, long personaRevision)
    {
        var sources = new Dictionary<string, AgentCore.Domain.Conversation.RuntimeFieldSource>(StringComparer.Ordinal);
        foreach (var section in Sections)
        {
            var changed = overrides is null ? new Dictionary<string, object?>() : Overrides(overrides, section);
            foreach (var field in Fields(section)) sources[section + "." + field] = changed.ContainsKey(field)
                ? new("Instance", instanceRevision.ToString(System.Globalization.CultureInfo.InvariantCulture))
                : new("Definition", definition.Id + ":" + definition.Version);
        }
        foreach (var field in new[] { "goals", "executionPolicy.harness", "executionPolicy.knowledge", "executionPolicy.tools" })
            sources[field] = new("Definition", definition.Id + ":" + definition.Version);
        foreach (var field in new[] { "name", "role", "description", "tone" })
            sources["identity." + field] = new("Instance", personaRevision.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return sources;
    }

    public static AgentDefinition Resolve(AgentDefinition baseline, InstanceSettingsOverrides? overrides)
    {
        if (overrides is null) return baseline;
        var o = overrides;
        if (o.SandboxProfileId is { } selectedProfile && baseline.SandboxPolicy?.AllowedProfileIds.Contains(selectedProfile.Value, StringComparer.Ordinal) != true)
            throw AgentCoreErrors.Validation(Constraints(baseline, "sandboxPolicy")["profileId"].Reason);
        SandboxSetupResolver.ValidateOverrides(o);
        var trigger = TriggerOf(baseline);
        var d = baseline with
        {
            SandboxPolicy = SandboxSetupResolver.ResolvePolicy(baseline.SandboxPolicy, o),
            ExecutionSandboxProfile = baseline.ExecutionSandboxProfile is { } sandbox ? SandboxSetupResolver.Resolve(sandbox, o, sandbox.InstanceRevision) : null,
            AgentInstructions = o.AgentInstructions is { } instructions ? instructions.Value : baseline.AgentInstructions,
            ConversationPolicy = baseline.ConversationPolicy with
            {
                ResponseLength = o.ResponseLength is { } responseLength ? responseLength.Value : baseline.ConversationPolicy.ResponseLength,
                AskOneQuestionAtATime = o.AskOneQuestionAtATime is { } askOneQuestionAtATime ? askOneQuestionAtATime.Value : baseline.ConversationPolicy.AskOneQuestionAtATime,
                Language = o.Language is { } language ? language.Value : baseline.ConversationPolicy.Language,
                MaxOutputTokens = o.MaxOutputTokens is { } maxOutputTokens ? maxOutputTokens.Value : baseline.ConversationPolicy.MaxOutputTokens,
            },
            StateIsolationPolicy = ResolveState(baseline, o),
            BehaviorPolicy = baseline.BehaviorPolicy with
            {
                InterruptionStyle = o.InterruptionStyle is { } interruptionStyle ? interruptionStyle.Value : baseline.BehaviorPolicy.InterruptionStyle,
                AcknowledgeInterruption = o.AcknowledgeInterruption is { } acknowledgeInterruption ? acknowledgeInterruption.Value : baseline.BehaviorPolicy.AcknowledgeInterruption,
                AvoidUnsupportedClaims = o.AvoidUnsupportedClaims is { } avoidUnsupportedClaims ? avoidUnsupportedClaims.Value : baseline.BehaviorPolicy.AvoidUnsupportedClaims,
            },
            InitiativePolicy = baseline.InitiativePolicy with
            {
                Enabled = o.InitiativeEnabled is { } initiativeEnabled ? initiativeEnabled.Value : baseline.InitiativePolicy.Enabled,
                SilenceThresholdMs = o.SilenceThresholdMs is { } silenceThresholdMs ? silenceThresholdMs.Value : baseline.InitiativePolicy.SilenceThresholdMs,
                CooldownMs = o.CooldownMs is { } cooldownMs ? cooldownMs.Value : baseline.InitiativePolicy.CooldownMs,
                MaxPerSilencePeriod = o.MaxPerSilencePeriod is { } maxPerSilencePeriod ? maxPerSilencePeriod.Value : baseline.InitiativePolicy.MaxPerSilencePeriod,
                Triggers = o.InitiativeTriggers is { } initiativeTriggers ? initiativeTriggers.Value : baseline.InitiativePolicy.Triggers,
                MaxConsecutiveProactiveTurns = o.MaxConsecutiveProactiveTurns is { } maxConsecutiveProactiveTurns ? maxConsecutiveProactiveTurns.Value : baseline.InitiativePolicy.MaxConsecutiveProactiveTurns,
                MaxSilentEvaluations = o.MaxSilentEvaluations is { } maxSilentEvaluations ? maxSilentEvaluations.Value : baseline.InitiativePolicy.MaxSilentEvaluations,
                MaxInactivityMs = o.MaxInactivityMs is { } maxInactivityMs ? maxInactivityMs.Value : baseline.InitiativePolicy.MaxInactivityMs,
            },
            Voice = baseline.Voice with
            {
                Enabled = o.VoiceEnabled is { } voiceEnabled ? voiceEnabled.Value : baseline.Voice.Enabled,
                VoiceId = o.VoiceId is { } voiceId ? voiceId.Value : baseline.Voice.VoiceId,
                SpeakingRate = o.SpeakingRate is { } speakingRate ? speakingRate.Value : baseline.Voice.SpeakingRate,
            },
            TriggerPolicy = baseline.TriggerPolicy is null && !Fields("triggerPolicy").Any(f => Overrides(o, "triggerPolicy").ContainsKey(f)) ? null : trigger with
            {
                Enabled = o.TriggerEnabled is { } fieldTriggerEnabled ? fieldTriggerEnabled.Value : trigger.Enabled,
                AllowUserScheduling = o.AllowUserScheduling is { } fieldAllowUserScheduling ? fieldAllowUserScheduling.Value : trigger.AllowUserScheduling,
                AllowOneShot = o.AllowOneShot is { } fieldAllowOneShot ? fieldAllowOneShot.Value : trigger.AllowOneShot,
                AllowDaily = o.AllowDaily is { } fieldAllowDaily ? fieldAllowDaily.Value : trigger.AllowDaily,
                AllowWeekly = o.AllowWeekly is { } fieldAllowWeekly ? fieldAllowWeekly.Value : trigger.AllowWeekly,
                AllowIndefiniteRecurrence = o.AllowIndefiniteRecurrence is { } fieldAllowIndefiniteRecurrence ? fieldAllowIndefiniteRecurrence.Value : trigger.AllowIndefiniteRecurrence,
                MaxActiveRegistrations = o.MaxActiveRegistrations is { } fieldMaxActiveRegistrations ? fieldMaxActiveRegistrations.Value : trigger.MaxActiveRegistrations,
                OneShotHorizonDays = o.OneShotHorizonDays is { } fieldOneShotHorizonDays ? fieldOneShotHorizonDays.Value : trigger.OneShotHorizonDays,
                MinRecurrenceDays = o.MinRecurrenceDays is { } fieldMinRecurrenceDays ? fieldMinRecurrenceDays.Value : trigger.MinRecurrenceDays,
                AllowedSourceKinds = o.AllowedSourceKinds is { } fieldAllowedSourceKinds ? fieldAllowedSourceKinds.Value : trigger.AllowedSourceKinds,
                AllowFixedInterval = o.AllowFixedInterval is { } fieldAllowFixedInterval ? fieldAllowFixedInterval.Value : trigger.AllowFixedInterval,
                MinFixedIntervalSeconds = o.MinFixedIntervalSeconds is { } fieldMinFixedIntervalSeconds ? fieldMinFixedIntervalSeconds.Value : trigger.MinFixedIntervalSeconds,
            },
            ProviderPreferences = baseline.ProviderPreferences with
            {
                LanguageModel = o.ProviderLanguageModel is { } fieldProviderLanguageModel ? fieldProviderLanguageModel.Value : baseline.ProviderPreferences.LanguageModel,
                SpeechRecognizer = o.ProviderSpeechRecognizer is { } fieldProviderSpeechRecognizer ? fieldProviderSpeechRecognizer.Value : baseline.ProviderPreferences.SpeechRecognizer,
                SpeechSynthesizer = o.ProviderSpeechSynthesizer is { } fieldProviderSpeechSynthesizer ? fieldProviderSpeechSynthesizer.Value : baseline.ProviderPreferences.SpeechSynthesizer,
                InterruptionClassifier = o.ProviderInterruptionClassifier is { } fieldProviderInterruptionClassifier ? fieldProviderInterruptionClassifier.Value : baseline.ProviderPreferences.InterruptionClassifier,
            },
            ModelDefaults = (baseline.ModelDefaults ?? new AgentModelDefaults(null, null)) with
            {
                CatalogKey = o.ModelCatalogKey is { } modelCatalogKey ? modelCatalogKey.Value : (baseline.ModelDefaults ?? new AgentModelDefaults(null, null)).CatalogKey,
                ReasoningEffort = o.ReasoningEffort is { } reasoningEffort ? reasoningEffort.Value : (baseline.ModelDefaults ?? new AgentModelDefaults(null, null)).ReasoningEffort,
            },
            MemoryPolicy = (baseline.MemoryPolicy ?? MemoryPolicy.Disabled) with
            {
                SessionMemory = o.MemorySessionMemory is { } memorySessionMemory ? memorySessionMemory.Value : (baseline.MemoryPolicy ?? MemoryPolicy.Disabled).SessionMemory,
                IdentityUserPromotion = o.MemoryIdentityUserPromotion is { } memoryIdentityUserPromotion ? memoryIdentityUserPromotion.Value : (baseline.MemoryPolicy ?? MemoryPolicy.Disabled).IdentityUserPromotion,
                IdentityUserRetrieval = o.MemoryIdentityUserRetrieval is { } memoryIdentityUserRetrieval ? memoryIdentityUserRetrieval.Value : (baseline.MemoryPolicy ?? MemoryPolicy.Disabled).IdentityUserRetrieval,
                UserPromotion = o.MemoryUserPromotion is { } memoryUserPromotion ? memoryUserPromotion.Value : (baseline.MemoryPolicy ?? MemoryPolicy.Disabled).UserPromotion,
                UserRetrieval = o.MemoryUserRetrieval is { } memoryUserRetrieval ? memoryUserRetrieval.Value : (baseline.MemoryPolicy ?? MemoryPolicy.Disabled).UserRetrieval,
            },
        };
        var environment = DefinitionExecutionPolicies.Of(baseline);
        if (o.SelectedCapabilities is { } selected)
        {
            if (selected.Value is null || selected.Value.Distinct(StringComparer.Ordinal).Count() != selected.Value.Length
                || selected.Value.Any(c => !environment.ToolList.Contains(c, StringComparer.Ordinal)))
                throw AgentCoreErrors.Validation("Instance capability selection must be a subset of Definition authorization.");
            d = d with { ExecutionPolicy = environment with {
                ToolAllowlist = environment.Capabilities is null ? selected.Value.ToArray() : environment.ToolAllowlist,
                Capabilities = environment.Capabilities is null ? null : environment.Capabilities with { ResolvedCapabilities = selected.Value.ToArray() },
                Projection = environment.Projection is null ? null : environment.Projection with { AlwaysCapabilities = environment.Projection.AlwaysCapabilities.Where(selected.Value.Contains).ToArray() }
            } };
        }
        if (o.AlwaysCapabilities is { } always)
        {
            var effectiveEnvironment = DefinitionExecutionPolicies.Of(d);
            if (always.Value is null || effectiveEnvironment.Capabilities is null
                || always.Value.Distinct(StringComparer.Ordinal).Count() != always.Value.Length
                || always.Value.Any(c => !effectiveEnvironment.ToolList.Contains(c, StringComparer.Ordinal)))
                throw AgentCoreErrors.Validation("Always projection must use selected, authorized capabilities.");
            d = d with { ExecutionPolicy = effectiveEnvironment with { Projection = new(always.Value.ToArray()) } };
        }
        if (o.AllowUnreadUnsupportedTypes is { } unread)
        {
            d = d with { ExecutionPolicy = DefinitionExecutionPolicies.Of(d) with { Attachments = new(unread.Value) } };
        }
        foreach (var section in Sections)
        {
            var values = Values(d, section);
            foreach (var (field, constraint) in Constraints(baseline, section))
                if (constraint.RequiredBoolean is { } required && values[field] is bool boolean && boolean != required
                    || constraint.Minimum is { } minimum && values[field] is int low && low < minimum
                    || constraint.Maximum is { } maximum && values[field] is int high && high > maximum)
                    throw AgentCoreErrors.Validation(constraint.Reason);
        }
        if (o.SandboxProfileId is { } sandboxSelection && baseline.SandboxPolicy?.AllowedProfileIds.Contains(sandboxSelection.Value, StringComparer.Ordinal) != true)
            throw AgentCoreErrors.Validation(Constraints(baseline,"sandboxPolicy")["profileId"].Reason);
        if (d.InitiativePolicy.Triggers.Any(t => !baseline.InitiativePolicy.Triggers.Contains(t, StringComparer.Ordinal)))
            throw AgentCoreErrors.Validation("Initiative triggers must be permitted by the selected Definition.");
        var t = TriggerOf(d);
        if (t.AllowedSourceKinds.Any(k => !trigger.AllowedSourceKinds.Contains(k, StringComparer.Ordinal)))
            throw AgentCoreErrors.Validation("Trigger sources must be permitted by the selected Definition.");
        if (!d.Voice.Enabled) d = d with { ProviderPreferences = d.ProviderPreferences with { SpeechRecognizer = null, SpeechSynthesizer = null } };
        try { AgentDefinitionValidator.Validate(d); }
        catch (ArgumentException e) { throw AgentCoreErrors.Validation(e.Message); }
        return d;
    }

    public static IReadOnlyDictionary<string, object?> Values(AgentDefinition d, string section) => section switch
    {
        "stateIsolationPolicy" => StateValues(d.StateIsolationPolicy ?? StateIsolationPolicy.Default),
        "sandboxPolicy" => new Dictionary<string, object?> { ["profileId"] = d.SandboxPolicy?.DefaultProfileId, ["allowedProfileIds"] = d.SandboxPolicy?.AllowedProfileIds ?? [],
            ["initializationScript"] = d.ExecutionSandboxProfile?.InstanceInitializationScript,
            ["cpu"] = d.ExecutionSandboxProfile?.Cpu, ["memoryMiB"] = d.ExecutionSandboxProfile?.MemoryMiB,
            ["maxCommandSeconds"] = d.ExecutionSandboxProfile?.MaxCommandSeconds, ["idleTtlSeconds"] = d.ExecutionSandboxProfile?.IdleTtlSeconds,
            ["maxLifetimeSeconds"] = d.ExecutionSandboxProfile?.MaxLifetimeSeconds, ["storageBytes"] = d.ExecutionSandboxProfile?.StorageBytes },
        "instructions" => new Dictionary<string, object?> {
            ["agentInstructions"] = d.AgentInstructions,
        },
        "conversationPolicy" => new Dictionary<string, object?> {
            ["responseLength"] = d.ConversationPolicy.ResponseLength,
            ["askOneQuestionAtATime"] = d.ConversationPolicy.AskOneQuestionAtATime,
            ["language"] = d.ConversationPolicy.Language,
            ["maxOutputTokens"] = d.ConversationPolicy.MaxOutputTokens,
        },
        "behaviorPolicy" => new Dictionary<string, object?> {
            ["interruptionStyle"] = d.BehaviorPolicy.InterruptionStyle,
            ["acknowledgeInterruption"] = d.BehaviorPolicy.AcknowledgeInterruption,
            ["avoidUnsupportedClaims"] = d.BehaviorPolicy.AvoidUnsupportedClaims,
        },
        "initiativePolicy" => new Dictionary<string, object?> {
            ["enabled"] = d.InitiativePolicy.Enabled,
            ["silenceThresholdMs"] = d.InitiativePolicy.SilenceThresholdMs,
            ["cooldownMs"] = d.InitiativePolicy.CooldownMs,
            ["maxPerSilencePeriod"] = d.InitiativePolicy.MaxPerSilencePeriod,
            ["triggers"] = d.InitiativePolicy.Triggers,
            ["maxConsecutiveProactiveTurns"] = d.InitiativePolicy.MaxConsecutiveProactiveTurns,
            ["maxSilentEvaluations"] = d.InitiativePolicy.MaxSilentEvaluations,
            ["maxInactivityMs"] = d.InitiativePolicy.MaxInactivityMs,
        },
        "voice" => new Dictionary<string, object?> {
            ["enabled"] = d.Voice.Enabled,
            ["voiceId"] = d.Voice.VoiceId,
            ["speakingRate"] = d.Voice.SpeakingRate,
        },
        "modelDefaults" => new Dictionary<string, object?> {
            ["catalogKey"] = d.ModelDefaults?.CatalogKey,
            ["reasoningEffort"] = d.ModelDefaults?.ReasoningEffort,
        },
        "memoryPolicy" => new Dictionary<string, object?> {
            ["sessionMemory"] = (d.MemoryPolicy ?? MemoryPolicy.Disabled).SessionMemory,
            ["identityUserPromotion"] = (d.MemoryPolicy ?? MemoryPolicy.Disabled).IdentityUserPromotion,
            ["identityUserRetrieval"] = (d.MemoryPolicy ?? MemoryPolicy.Disabled).IdentityUserRetrieval,
            ["userPromotion"] = (d.MemoryPolicy ?? MemoryPolicy.Disabled).UserPromotion,
            ["userRetrieval"] = (d.MemoryPolicy ?? MemoryPolicy.Disabled).UserRetrieval,
        },
        "capabilities" => new Dictionary<string, object?> {
            ["selected"] = DefinitionExecutionPolicies.Of(d).ToolList,
            ["always"] = d.ExecutionPolicy?.Projection?.AlwaysCapabilities ?? [],
            ["allowUnreadUnsupportedTypes"] = DefinitionExecutionPolicies.Of(d).AttachmentPolicy.AllowUnreadUnsupportedTypes,
        },
        "triggerPolicy" => new Dictionary<string, object?> {
            ["enabled"] = TriggerOf(d).Enabled,
            ["allowUserScheduling"] = TriggerOf(d).AllowUserScheduling,
            ["allowOneShot"] = TriggerOf(d).AllowOneShot,
            ["allowDaily"] = TriggerOf(d).AllowDaily,
            ["allowWeekly"] = TriggerOf(d).AllowWeekly,
            ["allowIndefiniteRecurrence"] = TriggerOf(d).AllowIndefiniteRecurrence,
            ["maxActiveRegistrations"] = TriggerOf(d).MaxActiveRegistrations,
            ["oneShotHorizonDays"] = TriggerOf(d).OneShotHorizonDays,
            ["minRecurrenceDays"] = TriggerOf(d).MinRecurrenceDays,
            ["allowedSourceKinds"] = TriggerOf(d).AllowedSourceKinds,
            ["allowFixedInterval"] = TriggerOf(d).AllowFixedInterval,
            ["minFixedIntervalSeconds"] = TriggerOf(d).MinFixedIntervalSeconds,
        },
        "providerPreferences" => new Dictionary<string, object?> {
            ["languageModel"] = d.ProviderPreferences.LanguageModel,
            ["speechRecognizer"] = d.ProviderPreferences.SpeechRecognizer,
            ["speechSynthesizer"] = d.ProviderPreferences.SpeechSynthesizer,
            ["interruptionClassifier"] = d.ProviderPreferences.InterruptionClassifier,
        },
        _ => throw AgentCoreErrors.Validation("Unknown settings section.")
    };
    public static IReadOnlyDictionary<string, object?> Overrides(InstanceSettingsOverrides o, string section)
    {
        var result = new Dictionary<string, object?>();
        switch (section)
        {
            case "stateIsolationPolicy":
                if (o.CrossSessionContinuity is { } crossSession) result["crossSessionContinuity"] = crossSession.Value;
                if (o.ConversationContextScope is { } stateConversationContextScope) result["conversationContextScope"] = stateConversationContextScope.Value;
                if (o.HomeAccess is { } stateHomeAccess) result["homeAccess"] = stateHomeAccess.Value;
                if (o.WorkingCleanup is { } stateWorkingCleanup) result["workingCleanup"] = stateWorkingCleanup.Value;
                if (o.BrowserIsolation is { } stateBrowserIsolation) result["browserIsolation"] = stateBrowserIsolation.Value;
                if (o.AgentStateVisibility is { } stateAgentStateVisibility) result["agentStateVisibility"] = stateAgentStateVisibility.Value;
                if (o.ExperienceRecord is { } stateExperienceRecord) result["experienceRecord"] = stateExperienceRecord.Value;
                if (o.ExperienceRecall is { } stateExperienceRecall) result["experienceRecall"] = stateExperienceRecall.Value;
                if (o.ProfileEvidence is { } stateProfileEvidence) result["profileEvidence"] = stateProfileEvidence.Value;
                break;
            case "sandboxPolicy":
                if (o.SandboxProfileId is { } profile) result["profileId"] = profile.Value;
                if (o.SandboxAllowedProfileIds is { } allowedProfiles) result["allowedProfileIds"] = allowedProfiles.Value;
                if (o.SandboxInitializationScript is { } initializationScript) result["initializationScript"] = initializationScript.Value;
                if (o.SandboxCpu is { } cpu) result["cpu"] = cpu.Value;
                if (o.SandboxMemoryMiB is { } memoryMiB) result["memoryMiB"] = memoryMiB.Value;
                if (o.SandboxMaxCommandSeconds is { } maxCommandSeconds) result["maxCommandSeconds"] = maxCommandSeconds.Value;
                if (o.SandboxIdleTtlSeconds is { } idleTtlSeconds) result["idleTtlSeconds"] = idleTtlSeconds.Value;
                if (o.SandboxMaxLifetimeSeconds is { } maxLifetimeSeconds) result["maxLifetimeSeconds"] = maxLifetimeSeconds.Value;
                if (o.SandboxStorageBytes is { } storageBytes) result["storageBytes"] = storageBytes.Value;
                break;
            case "instructions":
                if (o.AgentInstructions is { } agentInstructions) result["agentInstructions"] = agentInstructions.Value;
                break;
            case "conversationPolicy":
                if (o.ResponseLength is { } responseLength) result["responseLength"] = responseLength.Value;
                if (o.AskOneQuestionAtATime is { } askOneQuestionAtATime) result["askOneQuestionAtATime"] = askOneQuestionAtATime.Value;
                if (o.Language is { } language) result["language"] = language.Value;
                if (o.MaxOutputTokens is { } maxOutputTokens) result["maxOutputTokens"] = maxOutputTokens.Value;
                break;
            case "behaviorPolicy":
                if (o.InterruptionStyle is { } interruptionStyle) result["interruptionStyle"] = interruptionStyle.Value;
                if (o.AcknowledgeInterruption is { } acknowledgeInterruption) result["acknowledgeInterruption"] = acknowledgeInterruption.Value;
                if (o.AvoidUnsupportedClaims is { } avoidUnsupportedClaims) result["avoidUnsupportedClaims"] = avoidUnsupportedClaims.Value;
                break;
            case "initiativePolicy":
                if (o.InitiativeEnabled is { } initiativeEnabled) result["enabled"] = initiativeEnabled.Value;
                if (o.SilenceThresholdMs is { } silenceThresholdMs) result["silenceThresholdMs"] = silenceThresholdMs.Value;
                if (o.CooldownMs is { } cooldownMs) result["cooldownMs"] = cooldownMs.Value;
                if (o.MaxPerSilencePeriod is { } maxPerSilencePeriod) result["maxPerSilencePeriod"] = maxPerSilencePeriod.Value;
                if (o.InitiativeTriggers is { } initiativeTriggers) result["triggers"] = initiativeTriggers.Value;
                if (o.MaxConsecutiveProactiveTurns is { } maxConsecutiveProactiveTurns) result["maxConsecutiveProactiveTurns"] = maxConsecutiveProactiveTurns.Value;
                if (o.MaxSilentEvaluations is { } maxSilentEvaluations) result["maxSilentEvaluations"] = maxSilentEvaluations.Value;
                if (o.MaxInactivityMs is { } maxInactivityMs) result["maxInactivityMs"] = maxInactivityMs.Value;
                break;
            case "voice":
                if (o.VoiceEnabled is { } voiceEnabled) result["enabled"] = voiceEnabled.Value;
                if (o.VoiceId is { } voiceId) result["voiceId"] = voiceId.Value;
                if (o.SpeakingRate is { } speakingRate) result["speakingRate"] = speakingRate.Value;
                break;
            case "modelDefaults":
                if (o.ModelCatalogKey is { } modelCatalogKey) result["catalogKey"] = modelCatalogKey.Value;
                if (o.ReasoningEffort is { } reasoningEffort) result["reasoningEffort"] = reasoningEffort.Value;
                break;
            case "memoryPolicy":
                if (o.MemorySessionMemory is { } memorySessionMemory) result["sessionMemory"] = memorySessionMemory.Value;
                if (o.MemoryIdentityUserPromotion is { } memoryIdentityUserPromotion) result["identityUserPromotion"] = memoryIdentityUserPromotion.Value;
                if (o.MemoryIdentityUserRetrieval is { } memoryIdentityUserRetrieval) result["identityUserRetrieval"] = memoryIdentityUserRetrieval.Value;
                if (o.MemoryUserPromotion is { } memoryUserPromotion) result["userPromotion"] = memoryUserPromotion.Value;
                if (o.MemoryUserRetrieval is { } memoryUserRetrieval) result["userRetrieval"] = memoryUserRetrieval.Value;
                break;
            case "capabilities":
                if (o.AllowUnreadUnsupportedTypes is { } unread) result["allowUnreadUnsupportedTypes"] = unread.Value;
                if (o.SelectedCapabilities is { } selectedCapabilities) result["selected"] = selectedCapabilities.Value;
                if (o.AlwaysCapabilities is { } alwaysCapabilities) result["always"] = alwaysCapabilities.Value;
                break;
            case "triggerPolicy":
                if (o.TriggerEnabled is { } overrideTriggerEnabled) result["enabled"] = overrideTriggerEnabled.Value;
                if (o.AllowUserScheduling is { } overrideAllowUserScheduling) result["allowUserScheduling"] = overrideAllowUserScheduling.Value;
                if (o.AllowOneShot is { } overrideAllowOneShot) result["allowOneShot"] = overrideAllowOneShot.Value;
                if (o.AllowDaily is { } overrideAllowDaily) result["allowDaily"] = overrideAllowDaily.Value;
                if (o.AllowWeekly is { } overrideAllowWeekly) result["allowWeekly"] = overrideAllowWeekly.Value;
                if (o.AllowIndefiniteRecurrence is { } overrideAllowIndefiniteRecurrence) result["allowIndefiniteRecurrence"] = overrideAllowIndefiniteRecurrence.Value;
                if (o.MaxActiveRegistrations is { } overrideMaxActiveRegistrations) result["maxActiveRegistrations"] = overrideMaxActiveRegistrations.Value;
                if (o.OneShotHorizonDays is { } overrideOneShotHorizonDays) result["oneShotHorizonDays"] = overrideOneShotHorizonDays.Value;
                if (o.MinRecurrenceDays is { } overrideMinRecurrenceDays) result["minRecurrenceDays"] = overrideMinRecurrenceDays.Value;
                if (o.AllowedSourceKinds is { } overrideAllowedSourceKinds) result["allowedSourceKinds"] = overrideAllowedSourceKinds.Value;
                if (o.AllowFixedInterval is { } overrideAllowFixedInterval) result["allowFixedInterval"] = overrideAllowFixedInterval.Value;
                if (o.MinFixedIntervalSeconds is { } overrideMinFixedIntervalSeconds) result["minFixedIntervalSeconds"] = overrideMinFixedIntervalSeconds.Value;
                break;
            case "providerPreferences":
                if (o.ProviderLanguageModel is { } overrideProviderLanguageModel) result["languageModel"] = overrideProviderLanguageModel.Value;
                if (o.ProviderSpeechRecognizer is { } overrideProviderSpeechRecognizer) result["speechRecognizer"] = overrideProviderSpeechRecognizer.Value;
                if (o.ProviderSpeechSynthesizer is { } overrideProviderSpeechSynthesizer) result["speechSynthesizer"] = overrideProviderSpeechSynthesizer.Value;
                if (o.ProviderInterruptionClassifier is { } overrideProviderInterruptionClassifier) result["interruptionClassifier"] = overrideProviderInterruptionClassifier.Value;
                break;
            default: throw AgentCoreErrors.Validation("Unknown settings section.");
        }
        return result;
    }
    private static IReadOnlyDictionary<string, object?> StateValues(StateIsolationPolicy p) => new Dictionary<string, object?> {
        ["conversationContextScope"] = p.ConversationContextScope.ToString(), ["homeAccess"] = p.HomeAccess.ToString(),
        ["workingCleanup"] = p.WorkingCleanup.ToString(), ["browserIsolation"] = p.BrowserIsolation.ToString(),
        ["agentStateVisibility"] = p.AgentStateVisibility.ToString(), ["experienceRecord"] = p.ExperienceRecord,
        ["experienceRecall"] = p.ExperienceRecall, ["profileEvidence"] = p.ProfileEvidence, ["crossSessionContinuity"] = p.CrossSessionContinuity
    };

    private static StateIsolationPolicy? ResolveState(AgentDefinition baseline, InstanceSettingsOverrides o)
    {
        if (baseline.StateIsolationPolicy is null && Overrides(o, "stateIsolationPolicy").Count == 0) return null;
        var p = baseline.StateIsolationPolicy ?? StateIsolationPolicy.Default;
        T Parse<T>(InstanceField<string>? field, T fallback) where T : struct, Enum => field is null ? fallback
            : Enum.TryParse<T>(field.Value, false, out var value) && Enum.IsDefined(value) && field.Value == value.ToString()
                ? value : throw AgentCoreErrors.Validation(Constraints(baseline, "stateIsolationPolicy")[JsonNamingPolicy.CamelCase.ConvertName(typeof(T).Name)].Reason);
        p = p with {
            ConversationContextScope = Parse(o.ConversationContextScope, p.ConversationContextScope),
            HomeAccess = Parse(o.HomeAccess, p.HomeAccess), WorkingCleanup = Parse(o.WorkingCleanup, p.WorkingCleanup),
            BrowserIsolation = Parse(o.BrowserIsolation, p.BrowserIsolation), AgentStateVisibility = Parse(o.AgentStateVisibility, p.AgentStateVisibility),
            ExperienceRecord = o.ExperienceRecord?.Value ?? p.ExperienceRecord, ExperienceRecall = o.ExperienceRecall?.Value ?? p.ExperienceRecall,
            ProfileEvidence = o.ProfileEvidence?.Value ?? p.ProfileEvidence, CrossSessionContinuity = o.CrossSessionContinuity?.Value ?? p.CrossSessionContinuity
        };
        var values = StateValues(p);
        foreach (var (key, constraint) in Constraints(baseline, "stateIsolationPolicy"))
            if (constraint.Choices is { } choices && values[key] is string value && !choices.Contains(value, StringComparer.Ordinal)
                || constraint.RequiredBoolean is { } required && values[key] is bool boolean && boolean != required)
                throw AgentCoreErrors.Validation(constraint.Reason);
        return p;
    }

}
