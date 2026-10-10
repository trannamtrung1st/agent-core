using System.Text.Json;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Definitions;

namespace AgentCore.Application.Agents;

public sealed record InstanceSettingConstraint(string Reason, bool? RequiredBoolean = null, int? Minimum = null, int? Maximum = null);

public static class InstanceSettingsResolver
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.Strict };
    public static IReadOnlyList<string> Sections { get; } = ["instructions", "conversationPolicy", "behaviorPolicy", "initiativePolicy", "voice", "modelDefaults", "memoryPolicy", "capabilities", "triggerPolicy", "providerPreferences"];
    public static IReadOnlyList<string> Fields(string section) => section switch
    {
        "instructions" => ["systemInstructions"],
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
                    ("instructions", "systemInstructions") => next with { SystemInstructions = remove ? null : new(Read<string>(set[field])) },
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
            case "behaviorPolicy":
                if (baseline.BehaviorPolicy.AvoidUnsupportedClaims)
                    result["avoidUnsupportedClaims"] = new("Avoid unsupported claims is required by the selected Definition and cannot be disabled for this Instance.", RequiredBoolean: true);
                break;
            case "initiativePolicy": CannotEnable("enabled", "Initiative", baseline.InitiativePolicy.Enabled); break;
            case "voice": CannotEnable("enabled", "Voice", baseline.Voice.Enabled); break;
            case "capabilities": CannotEnable("allowUnreadUnsupportedTypes", "Unread unsupported attachments", RoleEnvironments.Of(baseline).AttachmentPolicy.AllowUnreadUnsupportedTypes); break;
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

    public static AgentDefinition Resolve(AgentDefinition baseline, InstanceSettingsOverrides? overrides)
    {
        if (overrides is null) return baseline;
        var o = overrides;
        var trigger = TriggerOf(baseline);
        var d = baseline with
        {
            SystemInstructions = o.SystemInstructions is { } instructions ? instructions.Value : baseline.SystemInstructions,
            ConversationPolicy = baseline.ConversationPolicy with
            {
                ResponseLength = o.ResponseLength is { } responseLength ? responseLength.Value : baseline.ConversationPolicy.ResponseLength,
                AskOneQuestionAtATime = o.AskOneQuestionAtATime is { } askOneQuestionAtATime ? askOneQuestionAtATime.Value : baseline.ConversationPolicy.AskOneQuestionAtATime,
                Language = o.Language is { } language ? language.Value : baseline.ConversationPolicy.Language,
                MaxOutputTokens = o.MaxOutputTokens is { } maxOutputTokens ? maxOutputTokens.Value : baseline.ConversationPolicy.MaxOutputTokens,
            },
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
        var environment = RoleEnvironments.Of(baseline);
        if (o.SelectedCapabilities is { } selected)
        {
            if (selected.Value is null || selected.Value.Distinct(StringComparer.Ordinal).Count() != selected.Value.Length
                || selected.Value.Any(c => !environment.ToolList.Contains(c, StringComparer.Ordinal)))
                throw AgentCoreErrors.Validation("Instance capability selection must be a subset of Definition authorization.");
            d = d with { Environment = environment with {
                ToolAllowlist = environment.Capabilities is null ? selected.Value.ToArray() : environment.ToolAllowlist,
                Capabilities = environment.Capabilities is null ? null : environment.Capabilities with { ResolvedCapabilities = selected.Value.ToArray() },
                Projection = environment.Projection is null ? null : environment.Projection with { AlwaysCapabilities = environment.Projection.AlwaysCapabilities.Where(selected.Value.Contains).ToArray() }
            } };
        }
        if (o.AlwaysCapabilities is { } always)
        {
            var effectiveEnvironment = RoleEnvironments.Of(d);
            if (always.Value is null || effectiveEnvironment.Capabilities is null
                || always.Value.Distinct(StringComparer.Ordinal).Count() != always.Value.Length
                || always.Value.Any(c => !effectiveEnvironment.ToolList.Contains(c, StringComparer.Ordinal)))
                throw AgentCoreErrors.Validation("Always projection must use selected, authorized capabilities.");
            d = d with { Environment = effectiveEnvironment with { Projection = new(always.Value.ToArray()) } };
        }
        if (o.AllowUnreadUnsupportedTypes is { } unread)
        {
            d = d with { Environment = RoleEnvironments.Of(d) with { Attachments = new(unread.Value) } };
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
        "instructions" => new Dictionary<string, object?> {
            ["systemInstructions"] = d.SystemInstructions,
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
            ["selected"] = RoleEnvironments.Of(d).ToolList,
            ["always"] = d.Environment?.Projection?.AlwaysCapabilities ?? [],
            ["allowUnreadUnsupportedTypes"] = RoleEnvironments.Of(d).AttachmentPolicy.AllowUnreadUnsupportedTypes,
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
            case "instructions":
                if (o.SystemInstructions is { } systemInstructions) result["systemInstructions"] = systemInstructions.Value;
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
}
