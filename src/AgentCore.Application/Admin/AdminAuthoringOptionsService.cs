using AgentCore.Application.Ports;

namespace AgentCore.Application.Admin;

public sealed record AdminAuthoringModelOption(
    string Key,
    string DisplayName,
    IReadOnlyList<string> SupportedReasoningEfforts,
    string? DefaultReasoningEffort);

public sealed record AdminAuthoringOptions(
    IReadOnlyList<string> LanguageModelAliases,
    IReadOnlyList<string> SpeechRecognizerAliases,
    IReadOnlyList<string> SpeechSynthesizerAliases,
    string? DefaultLanguageModelAlias,
    string? DefaultSpeechRecognizerAlias,
    string? DefaultSpeechSynthesizerAlias,
    string DefaultModelKey,
    IReadOnlyList<AdminAuthoringModelOption> Models,
    IReadOnlyList<string> InterruptionClassifiers);

public sealed class AdminAuthoringOptionsService(ProviderAliasSet aliases, IModelCatalog catalog)
{
    public AdminAuthoringOptions Get()
    {
        var languageModels = Ordered(aliases.LanguageModels);
        var recognizers = Ordered(aliases.SpeechRecognizers);
        var synthesizers = Ordered(aliases.SpeechSynthesizers);
        return new AdminAuthoringOptions(
            languageModels,
            recognizers,
            synthesizers,
            DefaultLanguageModel(languageModels),
            SingleOrNull(recognizers),
            SingleOrNull(synthesizers),
            catalog.DefaultKey,
            catalog.Models
                .OrderBy(item => item.Key, StringComparer.Ordinal)
                .Select(item => new AdminAuthoringModelOption(
                    item.Key,
                    item.DisplayName,
                    item.SupportedReasoningEfforts,
                    item.DefaultReasoningEffort))
                .ToArray(),
            ["heuristic"]);
    }

    private static IReadOnlyList<string> Ordered(IReadOnlySet<string> aliases) =>
        aliases
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .OrderBy(item => item, StringComparer.Ordinal)
            .ToArray();

    private static string? DefaultLanguageModel(IReadOnlyList<string> aliases)
    {
        if (aliases.Contains("primary-llm"))
        {
            return "primary-llm";
        }

        return SingleOrNull(aliases);
    }

    private static string? SingleOrNull(IReadOnlyList<string> aliases) =>
        aliases.Count == 1 ? aliases[0] : null;
}
