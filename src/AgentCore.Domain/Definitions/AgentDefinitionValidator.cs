using System.Text.RegularExpressions;
using AgentCore.Domain.Triggers;

namespace AgentCore.Domain.Definitions;

public static class AgentDefinitionValidator
{
    public const int MaxToolAllowlistEntries = 32;

    private static readonly Regex IdPattern = new("^[a-z0-9-]{1,64}$", RegexOptions.Compiled);
    private static readonly Regex ToolPattern = new("^[a-z][a-z0-9._]{0,63}$", RegexOptions.Compiled);
    private static readonly HashSet<string> InterruptionStyles =
        ["acknowledgeThenContinue", "answerNewTurn"];
    private static readonly HashSet<string> ResponseLengths = ["concise", "balanced"];
    private static readonly HashSet<string> Triggers =
        ["longSilence", "environmentUpdate", "unfinishedInteraction"];

    public static void Validate(AgentDefinition definition)
    {
        if (definition.SchemaVersion != 1)
        {
            throw new ArgumentException($"Unsupported schemaVersion {definition.SchemaVersion}.");
        }

        if (!IdPattern.IsMatch(definition.Id))
        {
            throw new ArgumentException("id must be lowercase [a-z0-9-] length 1..64.");
        }

        if (definition.Version < 1)
        {
            throw new ArgumentException("version must be a positive integer.");
        }

        ValidateIdentity(definition.Identity);
        if (definition.Goals.Count is < 1 or > 10 || definition.Goals.Any(goal => string.IsNullOrWhiteSpace(goal) || goal.Length > 500))
        {
            throw new ArgumentException("goals must be 1..10 nonempty strings of at most 500 characters.");
        }

        if (definition.SystemInstructions.Length is 0 or > 8000)
        {
            throw new ArgumentException("systemInstructions must be 1..8000 characters.");
        }

        if (!InterruptionStyles.Contains(definition.BehaviorPolicy.InterruptionStyle))
        {
            throw new ArgumentException("interruptionStyle is invalid.");
        }

        if (!ResponseLengths.Contains(definition.ConversationPolicy.ResponseLength))
        {
            throw new ArgumentException("responseLength is invalid.");
        }

        ConversationLanguagePolicy.Validate(definition.ConversationPolicy.Language);

        if (definition.ConversationPolicy.MaxOutputTokens is < 1 or > 4096)
        {
            throw new ArgumentException("maxOutputTokens must be 1..4096.");
        }

        if (definition.InitiativePolicy.SilenceThresholdMs is < 1000 or > 120000)
        {
            throw new ArgumentException("silenceThresholdMs is out of range.");
        }

        if (definition.InitiativePolicy.CooldownMs is < 5000 or > 600000)
        {
            throw new ArgumentException("cooldownMs is out of range.");
        }

        if (definition.InitiativePolicy.MaxPerSilencePeriod is < 1 or > 8)
        {
            throw new ArgumentException("maxPerSilencePeriod must be 1..8.");
        }

        if (definition.InitiativePolicy.MaxConsecutiveProactiveTurns is < 0 or > 32)
        {
            throw new ArgumentException("maxConsecutiveProactiveTurns must be 0..32 when set.");
        }

        if (definition.InitiativePolicy.MaxSilentEvaluations is < 1 or > 64)
        {
            throw new ArgumentException("maxSilentEvaluations must be 1..64 when set.");
        }

        if (definition.InitiativePolicy.MaxInactivityMs is < 60_000 or > 86_400_000)
        {
            throw new ArgumentException("maxInactivityMs must be 60000..86400000 when set.");
        }

        if (definition.InitiativePolicy.Triggers.Count != definition.InitiativePolicy.Triggers.Distinct(StringComparer.Ordinal).Count()
            || definition.InitiativePolicy.Triggers.Any(trigger => !Triggers.Contains(trigger)))
        {
            throw new ArgumentException("initiative triggers are invalid.");
        }

        if (definition.Voice.SpeakingRate is < 0.5 or > 2.0)
        {
            throw new ArgumentException("speakingRate must be 0.5..2.0.");
        }

        if (string.IsNullOrWhiteSpace(definition.ProviderPreferences.LanguageModel))
        {
            throw new ArgumentException("languageModel alias is required.");
        }

        if (definition.Voice.Enabled)
        {
            if (string.IsNullOrWhiteSpace(definition.ProviderPreferences.SpeechRecognizer)
                || string.IsNullOrWhiteSpace(definition.ProviderPreferences.SpeechSynthesizer))
            {
                throw new ArgumentException("Voice.Enabled requires speechRecognizer and speechSynthesizer aliases.");
            }
        }
        else if (definition.ProviderPreferences.SpeechRecognizer is not null
                 || definition.ProviderPreferences.SpeechSynthesizer is not null)
        {
            throw new ArgumentException("Text-only definitions must omit speech provider aliases.");
        }

        if (definition.Metadata.Count > 16 || definition.Metadata.Values.Any(value => value.Length > 256))
        {
            throw new ArgumentException("metadata exceeds allowed size.");
        }

        if (definition.ModelDefaults is { } modelDefaults)
        {
            if (modelDefaults.CatalogKey is { Length: > 0 } key && !IdPattern.IsMatch(key))
            {
                throw new ArgumentException("modelDefaults.catalogKey must be lowercase [a-z0-9-] length 1..64.");
            }

            if (modelDefaults.ReasoningEffort is { Length: > 0 } effort
                && effort.Length > 32)
            {
                throw new ArgumentException("modelDefaults.reasoningEffort is invalid.");
            }
        }

        ValidateEnvironment(RoleEnvironments.Of(definition));
        ValidateTriggerPolicy(definition.TriggerPolicy);
    }

    private static void ValidateTriggerPolicy(TriggerPolicy? policy)
    {
        if (policy is null)
        {
            return;
        }

        if (policy.MaxActiveRegistrations is < 1 or > 32)
        {
            throw new ArgumentException("maxActiveRegistrations must be 1..32.");
        }

        if (policy.OneShotHorizonDays is < 1 or > 365)
        {
            throw new ArgumentException("oneShotHorizonDays must be 1..365.");
        }

        if (policy.MinRecurrenceDays is < 1 or > 365)
        {
            throw new ArgumentException("minRecurrenceDays must be 1..365.");
        }

        if (policy.MinFixedIntervalSeconds is < TriggerLimits.MinFixedIntervalSeconds or > TriggerLimits.MaxFixedIntervalSeconds)
        {
            throw new ArgumentException("minFixedIntervalSeconds must be between 60 and 604800.");
        }

        var sources = policy.AllowedSourceKinds;
        if (sources.Count != sources.Distinct(StringComparer.Ordinal).Count()
            || sources.Any(source => source is not ("schedule" or "applicationEvent")))
        {
            throw new ArgumentException("trigger source kinds are invalid.");
        }

        if (policy.Enabled
            && policy.AllowUserScheduling
            && !policy.AllowOneShot
            && !policy.AllowDaily
            && !policy.AllowWeekly
            && !policy.AllowFixedInterval)
        {
            throw new ArgumentException("enabled scheduling requires a schedule type.");
        }
    }

    private static void ValidateEnvironment(RoleEnvironment environment)
    {
        if (environment.HarnessList.Count > 8
            || environment.HarnessList.Any(item => !IdPattern.IsMatch(item))
            || environment.HarnessList.Count != environment.HarnessList.Distinct(StringComparer.Ordinal).Count())
        {
            throw new ArgumentException("harness entries are invalid.");
        }

        if (environment.KnowledgeList.Count > 8
            || environment.KnowledgeList.Select(item => item.Identity).Distinct(StringComparer.Ordinal).Count()
                != environment.KnowledgeList.Count)
        {
            throw new ArgumentException("knowledgeSources are invalid.");
        }

        foreach (var source in environment.KnowledgeList)
        {
            if (!IdPattern.IsMatch(source.Identity)
                || source.Title.Length is < 1 or > 256
                || source.Citation.Length is < 1 or > 256)
            {
                throw new ArgumentException("knowledge source identity/title/citation is invalid.");
            }
        }

        if (environment.ToolList.Count > MaxToolAllowlistEntries
            || environment.ToolList.Count != environment.ToolList.Distinct(StringComparer.Ordinal).Count()
            || environment.ToolList.Any(tool => !ToolPattern.IsMatch(tool)))
        {
            throw new ArgumentException("toolAllowlist is invalid.");
        }

        var template = environment.WorkspacePolicy.TemplateId;
        if (template is not null && !IdPattern.IsMatch(template))
        {
            throw new ArgumentException("workspace templateId is invalid.");
        }
    }

    private static void ValidateIdentity(AgentIdentity identity)
    {
        if (identity.Name.Length is < 1 or > 256
            || identity.Role.Length is < 1 or > 256
            || identity.Tone.Length is < 1 or > 256
            || identity.Description.Length is < 1 or > 1024)
        {
            throw new ArgumentException("identity field lengths are invalid.");
        }
    }
}
