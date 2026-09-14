using System.Text.RegularExpressions;

namespace AgentCore.Domain.Definitions;

public static class AgentDefinitionValidator
{
    private static readonly Regex IdPattern = new("^[a-z0-9-]{1,64}$", RegexOptions.Compiled);
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

        if (string.IsNullOrWhiteSpace(definition.ConversationPolicy.Language))
        {
            throw new ArgumentException("language is required.");
        }

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

        if (definition.InitiativePolicy.MaxPerSilencePeriod != 1)
        {
            throw new ArgumentException("maxPerSilencePeriod must be 1.");
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
