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
    internal const int MaxCandidateBytes = 1_048_576;
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
            throw new AgentCoreException("ValidationError", findings[0].Message, 400)
            { ValidationField = findings[0].Field, ValidationCode = findings[0].Code };
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
            throw new AgentCoreException("ValidationError", findings[0].Message, 400)
            { ValidationField = findings[0].Field, ValidationCode = findings[0].Code };
        }
    }

    internal static IReadOnlyList<DefinitionValidationFinding> CollectPublicationFindings(
        AgentDefinitionCandidate candidate,
        ProviderAliasSet aliases,
        IModelCatalog catalog,
        IToolConfigurationGate configurationGate,
        IReadOnlyList<string>? sourceTools = null)
    {
        var contextProjection = CollectContextProjectionFindings(candidate);
        if (contextProjection.Length > 0) return contextProjection;
        candidate = CapabilityAuthorizationResolver.ResolveCandidate(candidate);
        var findings = new List<DefinitionValidationFinding>();
        findings.AddRange(CollectPersistenceFindings(candidate, aliases));
        if (findings.Count > 0)
        {
            return findings;
        }

        var definition = candidate.ToPublished(1);
        if (BrowserContractCutover.Diagnostic(definition) is { } diagnostic)
            findings.Add(new("agentInstructions", "browser_contract_retired", diagnostic, DefinitionValidationSeverity.Blocking));
        findings.AddRange(CollectModelBindingFindings(definition, catalog));
        if (findings.Count > 0)
        {
            return findings;
        }

        findings.AddRange(CollectToolFindings(definition, configurationGate, sourceTools));
        findings.AddRange(CollectSkillCapabilityFindings(candidate));
        return findings;
    }

    private static IReadOnlyList<DefinitionValidationFinding> CollectPersistenceFindings(
        AgentDefinitionCandidate candidate,
        ProviderAliasSet aliases)
    {
        if (System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(candidate).Length > MaxCandidateBytes)
            return [Blocking("candidate", "document_too_large", "Definition candidate must fit within 1 MiB.")];
        var contextProjection = CollectContextProjectionFindings(candidate);
        if (contextProjection.Length > 0) return contextProjection;
        candidate = CapabilityAuthorizationResolver.ResolveCandidate(candidate);
        var findings = new List<DefinitionValidationFinding>();
        findings.AddRange(CollectStructureFindings(candidate));
        if (findings.Count > 0)
        {
            return findings;
        }

        if (candidate.ExecutionPolicy?.Capabilities is not null)
            foreach (var name in candidate.ExecutionPolicy.ToolList)
                if (!ToolRegistry.TryGet(name, out _)) findings.Add(Blocking("executionPolicy.capabilities.resolvedCapabilities", "unregistered_capability", $"Capability '{name}' is not registered."));
        findings.AddRange(CollectAliasFindings(candidate.ToPublished(1), aliases));
        if (findings.Count > 0)
        {
            return findings;
        }

        findings.AddRange(CollectSecretFindings(candidate));
        return findings;
    }

    private static DefinitionValidationFinding[] CollectContextProjectionFindings(AgentDefinitionCandidate candidate) =>
        (candidate.ExecutionPolicy?.Projection?.AlwaysCapabilities ?? [])
            .Where(n => ToolRegistry.TryGet(n, out var descriptor) && !descriptor.Discoverable && n != ToolCatalog.CapabilitiesLoad)
            .Select(n => Blocking("executionPolicy.projection.alwaysCapabilities", "context_only_capability", $"{n} is controlled by execution context.")).ToArray();

    private static IEnumerable<DefinitionValidationFinding> CollectStructureFindings(AgentDefinitionCandidate candidate)
    {
        try
        {
            ValidateRequiredGraph(candidate);
            if (candidate.ExecutionPolicy?.Capabilities is { } authority)
            {
                var always = candidate.ExecutionPolicy.Projection?.AlwaysCapabilities ?? [];
                if (always.Any(n => !authority.ResolvedCapabilities.Contains(n, StringComparer.Ordinal)))
                    return [Blocking("executionPolicy.projection.alwaysCapabilities", "unauthorized_projection", "Always projected capabilities must be authorized.")];
                if (always.Count != always.Distinct(StringComparer.Ordinal).Count())
                    return [Blocking("executionPolicy.projection.alwaysCapabilities", "duplicate_projection", "Always projected capabilities must be unique.")];
            }
            AgentDefinitionValidator.ValidateCandidate(candidate);
            return [];
        }
        catch (AgentCoreException ex)
        {
            return [Blocking("candidate", "domain_shape", ex.Message)];
        }
        catch (ToolAllowlistValidationException)
        {
            return AgentDefinitionValidator.ToolAllowlistFindings(candidate.ExecutionPolicy!.ToolList)
                .Select(f => Blocking(candidate.ExecutionPolicy?.Capabilities is null ? "executionPolicy.toolAllowlist" : "executionPolicy.capabilities.resolvedCapabilities", f.Code, f.Message));
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
        var hasAgentKey = !SessionModelBinder.IsInheritedOffering(defaults?.OfferingId);
        // Inheriting an as-yet-unconfigured tenant default is valid authoring, not an executable pin.
        if (!hasAgentKey && string.IsNullOrWhiteSpace(defaults?.ReasoningEffort)) return [];
        var key = hasAgentKey ? defaults!.OfferingId!.Trim() : catalog.DefaultOfferingId;
        if (catalog.Get(key) is null)
        {
            return [Blocking("modelDefaults.offeringId", "unknown_model", $"Model '{key}' is not in the catalog.")];
        }

        try
        {
            _ = SessionModelBinder.Bind(
                catalog,
                requestedOfferingId: null,
                requestedEffort: null,
                ModelSelectionSource.TenantDefault,
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
        IToolConfigurationGate configurationGate,
        IReadOnlyList<string>? sourceTools)
    {
        foreach (var name in definition.ExecutionPolicy?.Projection?.AlwaysCapabilities ?? [])
            if (ToolRegistry.TryGet(name, out var d) && !d.Discoverable && name != ToolCatalog.CapabilitiesLoad)
                yield return Blocking("executionPolicy.projection.alwaysCapabilities", "context_only_capability", $"{name} is controlled by execution context.");
        foreach (var toolName in DefinitionExecutionPolicies.Of(definition).ToolList)
        {
            if (!ToolRegistry.TryGet(toolName, out var descriptor))
            {
                yield return Blocking(
                    "executionPolicy.toolAllowlist",
                    "unregistered_tool",
                    $"Tool '{toolName}' is not registered.");
                continue;
            }

            var publishable = definition.ExecutionPolicy?.Capabilities is not null || descriptor.OfferRule switch
            {
                ToolOfferRule.RoleAllowlist => true,
                ToolOfferRule.SessionAttachmentsWhenRoleAllows => true,
                // A published source may carry tools that this host cannot currently configure.
                // Runtime admission still blocks them; unrelated forks may retain the same grant.
                ToolOfferRule.ConfigurationWhenRoleAllows => configurationGate.IsConfigured(toolName)
                    || sourceTools?.Contains(toolName, StringComparer.Ordinal) == true,
                _ => false
            };
            if (!publishable)
            {
                yield return Blocking(
                    "executionPolicy.toolAllowlist",
                    "unconfigured_tool",
                    $"Tool '{toolName}' is not configured for publication.");
            }
        }
    }

    private static IEnumerable<DefinitionValidationFinding> CollectSecretFindings(AgentDefinitionCandidate candidate)
    {
        foreach (var finding in CollectSecretFieldFindings("agentInstructions", candidate.AgentInstructions))
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

        if (candidate.Skills is not null)
        {
            for (var index = 0; index < candidate.Skills.Count; index++)
            {
                var skill = candidate.Skills[index];
                if (skill is null)
                {
                    continue;
                }

                foreach (var finding in CollectSecretFieldFindings($"skills[{index}].name", skill.Name))
                {
                    yield return finding;
                }

                foreach (var finding in CollectSecretFieldFindings($"skills[{index}].description", skill.Description))
                {
                    yield return finding;
                }

                foreach (var finding in CollectSecretFieldFindings($"skills[{index}].procedure", skill.Procedure))
                {
                    yield return finding;
                }


            }
        }

        if (candidate.ExecutionPolicy is null)
        {
            yield break;
        }

        for (var index = 0; index < candidate.ExecutionPolicy.HarnessList.Count; index++)
        {
            foreach (var finding in CollectSecretFieldFindings(
                         $"executionPolicy.harness[{index}]",
                         candidate.ExecutionPolicy.HarnessList[index]))
            {
                yield return finding;
            }
        }

        for (var index = 0; index < candidate.ExecutionPolicy.KnowledgeList.Count; index++)
        {
            var source = candidate.ExecutionPolicy.KnowledgeList[index];
            foreach (var finding in CollectSecretFieldFindings(
                         $"executionPolicy.knowledge[{index}].identity",
                         source.Identity))
            {
                yield return finding;
            }

            foreach (var finding in CollectSecretFieldFindings(
                         $"executionPolicy.knowledge[{index}].title",
                         source.Title))
            {
                yield return finding;
            }

            foreach (var finding in CollectSecretFieldFindings(
                         $"executionPolicy.knowledge[{index}].citation",
                         source.Citation))
            {
                yield return finding;
            }
        }

        foreach (var finding in CollectSecretFieldFindings(
                     "executionPolicy.workspacePolicy.templateId",
                     candidate.ExecutionPolicy.WorkspacePolicy.TemplateId))
        {
            yield return finding;
        }
    }

    private static IEnumerable<DefinitionValidationFinding> CollectSkillCapabilityFindings(
        AgentDefinitionCandidate candidate)
    {
        if (candidate.Skills is null || candidate.Skills.Count == 0)
        {
            yield break;
        }

        var allowed = DefinitionExecutionPolicies.Of(candidate.ToPublished(1)).ToolList.ToHashSet(StringComparer.Ordinal);
        for (var index = 0; index < candidate.Skills.Count; index++)
        {
            var capabilities = candidate.Skills[index].RequiredCapabilities;
            if (capabilities is null)
            {
                continue;
            }

            for (var capabilityIndex = 0; capabilityIndex < capabilities.Count; capabilityIndex++)
            {
                var capability = capabilities[capabilityIndex];
                if (string.Equals(capability, SkillCapabilities.ChatRespond, StringComparison.Ordinal))
                {
                    continue;
                }

                var field = $"skills[{index}].requiredCapabilities[{capabilityIndex}]";
                if (!ToolRegistry.TryGet(capability, out _))
                {
                    yield return Blocking(
                        field,
                        "unknown_capability",
                        $"Capability '{capability}' is not a known capability.");
                    continue;
                }

                if (!allowed.Contains(capability))
                {
                    yield return Blocking(
                        field,
                        "capability_not_allowed",
                        $"Capability '{capability}' is not on the definition tool allowlist.");
                }
            }
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

        if (candidate.AgentInstructions is null)
        {
            throw AgentCoreErrors.Validation("agentInstructions is required.");
        }
    }

    private static DefinitionValidationFinding Blocking(string field, string code, string message) =>
        new(field, code, message, DefinitionValidationSeverity.Blocking);
}
