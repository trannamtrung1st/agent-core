using AgentCore.Application.Ports;

namespace AgentCore.Application.Admin;

public sealed record AdminAuthoringModelOption(
    string OfferingId,
    string DisplayName,
    IReadOnlyList<string> SupportedReasoningEfforts,
    string? DefaultReasoningEffort, string? ConnectionId = null, string? ConnectionLabel = null);

public sealed record AdminAuthoringOptions(
    IReadOnlyList<string> SpeechRecognizerAliases,
    IReadOnlyList<string> SpeechSynthesizerAliases,
    string? DefaultSpeechRecognizerAlias,
    string? DefaultSpeechSynthesizerAlias,
    string DefaultOfferingId,
    IReadOnlyList<AdminAuthoringModelOption> Models,
    IReadOnlyList<string> InterruptionClassifiers);

public sealed class AdminAuthoringOptionsService(ProviderAliasSet aliases)
{
    public AdminAuthoringOptions Get(IModelCatalog catalog)
    {
        var recognizers = Ordered(aliases.SpeechRecognizers);
        var synthesizers = Ordered(aliases.SpeechSynthesizers);
        return new AdminAuthoringOptions(
            recognizers,
            synthesizers,
            SingleOrNull(recognizers),
            SingleOrNull(synthesizers),
            catalog.DefaultOfferingId,
            catalog.Models
                .OrderBy(item => item.OfferingId, StringComparer.Ordinal)
                .Select(item => new AdminAuthoringModelOption(
                    item.OfferingId,
                    item.DisplayName,
                    item.SupportedReasoningEfforts,
                    item.DefaultReasoningEffort, item.ConnectionId, item.ConnectionLabel))
                .ToArray(),
            ["heuristic"]);
    }

    private static IReadOnlyList<string> Ordered(IReadOnlySet<string> aliases) =>
        aliases
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .OrderBy(item => item, StringComparer.Ordinal)
            .ToArray();

    private static string? SingleOrNull(IReadOnlyList<string> aliases) =>
        aliases.Count == 1 ? aliases[0] : null;
}
