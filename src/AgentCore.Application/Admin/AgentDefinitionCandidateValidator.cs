using System.Text.RegularExpressions;
using AgentCore.Application.Agents;
using AgentCore.Application.Models;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Application.Tools;
using AgentCore.Domain.Definitions;

namespace AgentCore.Application.Admin;

internal static class AgentDefinitionCandidateValidator
{
    private static readonly string[] SecretSentinels =
    [
        "OPENROUTER_API_KEY",
        "OPENROUTER_SECRET",
        "OWNER_CAPABILITY_SENTINEL"
    ];

    private static readonly Regex[] EmbeddedProviderKeyPatterns =
    [
        new(@"sk-[A-Za-z0-9]{20,}", RegexOptions.Compiled | RegexOptions.CultureInvariant),
        new(@"sk-or-v1-[A-Za-z0-9_-]{15,}", RegexOptions.Compiled | RegexOptions.CultureInvariant),
        new(@"sk-proj-[A-Za-z0-9_-]{15,}", RegexOptions.Compiled | RegexOptions.CultureInvariant),
    ];

    internal static void ValidateForPersistence(AgentDefinitionCandidate candidate, ProviderAliasSet aliases)
    {
        ValidateStructure(candidate);
        ValidateAliases(candidate.ToPublished(1), aliases);
        RejectEmbeddedSecrets(candidate);
    }

    internal static void ValidateForPublication(
        AgentDefinitionCandidate candidate,
        ProviderAliasSet aliases,
        IModelCatalog catalog,
        IToolConfigurationGate configurationGate)
    {
        ValidateForPersistence(candidate, aliases);
        var definition = candidate.ToPublished(1);
        _ = SessionModelBinder.PinDefault(catalog, definition);
        ValidateResolvableTools(definition, configurationGate);
    }

    private static void ValidateStructure(AgentDefinitionCandidate candidate)
    {
        ValidateRequiredGraph(candidate);
        try
        {
            AgentDefinitionValidator.ValidateCandidate(candidate);
        }
        catch (ArgumentException ex)
        {
            throw AgentCoreErrors.Validation(ex.Message);
        }
        catch (NullReferenceException)
        {
            throw AgentCoreErrors.Validation("Definition candidate is missing required members.");
        }
    }

    private static void ValidateRequiredGraph(AgentDefinitionCandidate candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate.DefinitionId))
        {
            throw AgentCoreErrors.Validation("definitionId is required.");
        }

        if (candidate.Identity is null)
        {
            throw AgentCoreErrors.Validation("identity is required.");
        }

        if (candidate.Identity.Name is null
            || candidate.Identity.Role is null
            || candidate.Identity.Description is null
            || candidate.Identity.Tone is null)
        {
            throw AgentCoreErrors.Validation("identity fields are required.");
        }

        if (candidate.Goals is null)
        {
            throw AgentCoreErrors.Validation("goals are required.");
        }

        if (candidate.Goals.Any(goal => goal is null))
        {
            throw AgentCoreErrors.Validation("goals must not contain null entries.");
        }

        if (candidate.BehaviorPolicy is null
            || candidate.ConversationPolicy is null
            || candidate.InitiativePolicy is null
            || candidate.Voice is null
            || candidate.ProviderPreferences is null
            || candidate.Metadata is null)
        {
            throw AgentCoreErrors.Validation("Definition candidate is missing required policy members.");
        }

        if (candidate.SystemInstructions is null)
        {
            throw AgentCoreErrors.Validation("systemInstructions is required.");
        }
    }

    private static void ValidateAliases(AgentDefinition definition, ProviderAliasSet aliases)
    {
        if (!aliases.LanguageModels.Contains(definition.ProviderPreferences.LanguageModel))
        {
            throw AgentCoreErrors.Validation(
                $"languageModel alias '{definition.ProviderPreferences.LanguageModel}' is not configured.");
        }

        if (definition.ProviderPreferences.SpeechRecognizer is { } stt
            && !aliases.SpeechRecognizers.Contains(stt))
        {
            throw AgentCoreErrors.Validation($"speechRecognizer alias '{stt}' is not configured.");
        }

        if (definition.ProviderPreferences.SpeechSynthesizer is { } tts
            && !aliases.SpeechSynthesizers.Contains(tts))
        {
            throw AgentCoreErrors.Validation($"speechSynthesizer alias '{tts}' is not configured.");
        }
    }

    private static void ValidateResolvableTools(AgentDefinition definition, IToolConfigurationGate configurationGate)
    {
        foreach (var toolName in RoleEnvironments.Of(definition).ToolList)
        {
            if (!ToolRegistry.TryGet(toolName, out var descriptor))
            {
                throw AgentCoreErrors.Validation($"Tool '{toolName}' is not registered.");
            }

            var offered = descriptor.OfferRule switch
            {
                ToolOfferRule.RoleAllowlist => true,
                ToolOfferRule.ConfigurationWhenRoleAllows => configurationGate.IsConfigured(toolName),
                _ => false
            };
            if (!offered)
            {
                throw AgentCoreErrors.Validation($"Tool '{toolName}' is not configured for publication.");
            }
        }
    }

    private static void RejectEmbeddedSecrets(AgentDefinitionCandidate candidate)
    {
        RejectSecretTokens(candidate.SystemInstructions);
        RejectSecretTokens(candidate.Identity.Name);
        RejectSecretTokens(candidate.Identity.Role);
        RejectSecretTokens(candidate.Identity.Description);
        RejectSecretTokens(candidate.Identity.Tone);
        foreach (var goal in candidate.Goals)
        {
            RejectSecretTokens(goal);
        }

        foreach (var value in candidate.Metadata.Values)
        {
            RejectSecretTokens(value);
        }

        if (candidate.Environment is null)
        {
            return;
        }

        foreach (var harness in candidate.Environment.HarnessList)
        {
            RejectSecretTokens(harness);
        }

        foreach (var source in candidate.Environment.KnowledgeList)
        {
            RejectSecretTokens(source.Identity);
            RejectSecretTokens(source.Title);
            RejectSecretTokens(source.Citation);
        }

        RejectSecretTokens(candidate.Environment.WorkspacePolicy.TemplateId);
    }

    private static void RejectSecretTokens(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return;
        }

        foreach (var sentinel in SecretSentinels)
        {
            if (value.Contains(sentinel, StringComparison.OrdinalIgnoreCase))
            {
                throw AgentCoreErrors.Validation("Definition content contains a disallowed secret reference.");
            }
        }

        foreach (var pattern in EmbeddedProviderKeyPatterns)
        {
            if (pattern.IsMatch(value))
            {
                throw AgentCoreErrors.Validation("Definition content contains a disallowed secret reference.");
            }
        }
    }
}
