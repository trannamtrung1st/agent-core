using System.Text.RegularExpressions;
using AgentCore.Application.Agents;
using AgentCore.Application.Models;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
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
        var findings = CollectPersistenceFindings(candidate, aliases);
        if (findings.Count > 0)
        {
            throw AgentCoreErrors.Validation(findings[0].Message);
        }
    }

    internal static void ValidateForPublication(
        AgentDefinitionCandidate candidate,
        ProviderAliasSet aliases,
        IModelCatalog catalog,
        IToolConfigurationGate configurationGate)
    {
        var findings = CollectPublicationFindings(candidate, aliases, catalog, configurationGate);
        if (findings.Count > 0)
        {
            throw AgentCoreErrors.Validation(findings[0].Message);
        }
    }

    internal static IReadOnlyList<DefinitionValidationFinding> CollectPublicationFindings(
        AgentDefinitionCandidate candidate,
        ProviderAliasSet aliases,
        IModelCatalog catalog,
        IToolConfigurationGate configurationGate)
    {
        var findings = new List<DefinitionValidationFinding>();
        findings.AddRange(CollectPersistenceFindings(candidate, aliases));
        if (findings.Count > 0)
        {
            return findings;
        }

        var definition = candidate.ToPublished(1);
        findings.AddRange(CollectModelBindingFindings(definition, catalog));
        if (findings.Count > 0)
        {
            return findings;
        }

        findings.AddRange(CollectToolFindings(definition, configurationGate));
        return findings;
    }

    private static IReadOnlyList<DefinitionValidationFinding> CollectPersistenceFindings(
        AgentDefinitionCandidate candidate,
        ProviderAliasSet aliases)
    {
        var findings = new List<DefinitionValidationFinding>();
        findings.AddRange(CollectStructureFindings(candidate));
        if (findings.Count > 0)
        {
            return findings;
        }

        findings.AddRange(CollectAliasFindings(candidate.ToPublished(1), aliases));
        if (findings.Count > 0)
        {
            return findings;
        }

        findings.AddRange(CollectSecretFindings(candidate));
        return findings;
    }

    private static IEnumerable<DefinitionValidationFinding> CollectStructureFindings(AgentDefinitionCandidate candidate)
    {
        try
        {
            ValidateRequiredGraph(candidate);
            AgentDefinitionValidator.ValidateCandidate(candidate);
            return [];
        }
        catch (AgentCoreException ex)
        {
            return [Blocking("candidate", "domain_shape", ex.Message)];
        }
        catch (ArgumentException ex)
        {
            return [Blocking("candidate", "domain_shape", ex.Message)];
        }
        catch (NullReferenceException)
        {
            return [Blocking("candidate", "domain_shape", "Definition candidate is missing required members.")];
        }
    }

    private static IEnumerable<DefinitionValidationFinding> CollectAliasFindings(
        AgentDefinition definition,
        ProviderAliasSet aliases)
    {
        if (!aliases.LanguageModels.Contains(definition.ProviderPreferences.LanguageModel))
        {
            yield return Blocking(
                "providerPreferences.languageModel",
                "unknown_provider_alias",
                $"languageModel alias '{definition.ProviderPreferences.LanguageModel}' is not configured.");
        }

        if (definition.ProviderPreferences.SpeechRecognizer is { } stt
            && !aliases.SpeechRecognizers.Contains(stt))
        {
            yield return Blocking(
                "providerPreferences.speechRecognizer",
                "unknown_provider_alias",
                $"speechRecognizer alias '{stt}' is not configured.");
        }

        if (definition.ProviderPreferences.SpeechSynthesizer is { } tts
            && !aliases.SpeechSynthesizers.Contains(tts))
        {
            yield return Blocking(
                "providerPreferences.speechSynthesizer",
                "unknown_provider_alias",
                $"speechSynthesizer alias '{tts}' is not configured.");
        }
    }

    private static IEnumerable<DefinitionValidationFinding> CollectModelBindingFindings(
        AgentDefinition definition,
        IModelCatalog catalog)
    {
        var defaults = definition.ModelDefaults;
        var hasAgentKey = !SessionModelBinder.IsDefaultKey(defaults?.CatalogKey);
        var key = hasAgentKey ? defaults!.CatalogKey!.Trim() : catalog.DefaultKey;
        if (catalog.Get(key) is null)
        {
            return [Blocking("modelDefaults.catalogKey", "unknown_model", $"Model '{key}' is not in the catalog.")];
        }

        try
        {
            _ = SessionModelBinder.Bind(
                catalog,
                requestedKey: null,
                requestedEffort: null,
                ModelSelectionSource.SystemDefault,
                defaults);
            return [];
        }
        catch (AgentCoreException ex)
        {
            return [Blocking("modelDefaults.reasoningEffort", "unsupported_reasoning_effort", ex.Message)];
        }
    }

    private static IEnumerable<DefinitionValidationFinding> CollectToolFindings(
        AgentDefinition definition,
        IToolConfigurationGate configurationGate)
    {
        foreach (var toolName in RoleEnvironments.Of(definition).ToolList)
        {
            if (!ToolRegistry.TryGet(toolName, out var descriptor))
            {
                yield return Blocking(
                    "environment.toolAllowlist",
                    "unregistered_tool",
                    $"Tool '{toolName}' is not registered.");
                continue;
            }

            var offered = descriptor.OfferRule switch
            {
                ToolOfferRule.RoleAllowlist => true,
                ToolOfferRule.ConfigurationWhenRoleAllows => configurationGate.IsConfigured(toolName),
                _ => false
            };
            if (!offered)
            {
                yield return Blocking(
                    "environment.toolAllowlist",
                    "unconfigured_tool",
                    $"Tool '{toolName}' is not configured for publication.");
            }
        }
    }

    private static IEnumerable<DefinitionValidationFinding> CollectSecretFindings(AgentDefinitionCandidate candidate)
    {
        foreach (var finding in CollectSecretFieldFindings("systemInstructions", candidate.SystemInstructions))
        {
            yield return finding;
        }

        if (candidate.Identity is not null)
        {
            foreach (var finding in CollectSecretFieldFindings("identity.name", candidate.Identity.Name))
            {
                yield return finding;
            }

            foreach (var finding in CollectSecretFieldFindings("identity.role", candidate.Identity.Role))
            {
                yield return finding;
            }

            foreach (var finding in CollectSecretFieldFindings("identity.description", candidate.Identity.Description))
            {
                yield return finding;
            }

            foreach (var finding in CollectSecretFieldFindings("identity.tone", candidate.Identity.Tone))
            {
                yield return finding;
            }
        }

        if (candidate.Goals is not null)
        {
            for (var index = 0; index < candidate.Goals.Count; index++)
            {
                foreach (var finding in CollectSecretFieldFindings($"goals[{index}]", candidate.Goals[index]))
                {
                    yield return finding;
                }
            }
        }

        if (candidate.Metadata is not null)
        {
            foreach (var (key, value) in candidate.Metadata)
            {
                foreach (var finding in CollectSecretFieldFindings($"metadata.{key}", value))
                {
                    yield return finding;
                }
            }
        }

        if (candidate.Environment is null)
        {
            yield break;
        }

        for (var index = 0; index < candidate.Environment.HarnessList.Count; index++)
        {
            foreach (var finding in CollectSecretFieldFindings(
                         $"environment.harness[{index}]",
                         candidate.Environment.HarnessList[index]))
            {
                yield return finding;
            }
        }

        for (var index = 0; index < candidate.Environment.KnowledgeList.Count; index++)
        {
            var source = candidate.Environment.KnowledgeList[index];
            foreach (var finding in CollectSecretFieldFindings(
                         $"environment.knowledge[{index}].identity",
                         source.Identity))
            {
                yield return finding;
            }

            foreach (var finding in CollectSecretFieldFindings(
                         $"environment.knowledge[{index}].title",
                         source.Title))
            {
                yield return finding;
            }

            foreach (var finding in CollectSecretFieldFindings(
                         $"environment.knowledge[{index}].citation",
                         source.Citation))
            {
                yield return finding;
            }
        }

        foreach (var finding in CollectSecretFieldFindings(
                     "environment.workspacePolicy.templateId",
                     candidate.Environment.WorkspacePolicy.TemplateId))
        {
            yield return finding;
        }
    }

    private static IEnumerable<DefinitionValidationFinding> CollectSecretFieldFindings(string field, string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            yield break;
        }

        foreach (var sentinel in SecretSentinels)
        {
            if (value.Contains(sentinel, StringComparison.OrdinalIgnoreCase))
            {
                yield return Blocking(field, "secret_reference", "Definition content contains a disallowed secret reference.");
                yield break;
            }
        }

        foreach (var pattern in EmbeddedProviderKeyPatterns)
        {
            if (pattern.IsMatch(value))
            {
                yield return Blocking(field, "secret_reference", "Definition content contains a disallowed secret reference.");
                yield break;
            }
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

    private static DefinitionValidationFinding Blocking(string field, string code, string message) =>
        new(field, code, message, DefinitionValidationSeverity.Blocking);
}
