using System.Text.Json;
using System.Text.Json.Serialization;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Definitions;

namespace AgentCore.Infrastructure.Definitions;

public sealed class FileAgentDefinitionStore : IAgentDefinitionStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        ReadCommentHandling = JsonCommentHandling.Skip
    };

    private readonly IReadOnlyDictionary<(string Id, int Version), AgentDefinition> _definitions;

    public FileAgentDefinitionStore(string directory, ProviderAliasSet aliases)
    {
        if (!Directory.Exists(directory))
        {
            throw AgentCoreErrors.Persistence($"Agent directory '{directory}' was not found.");
        }

        var loaded = new Dictionary<(string, int), AgentDefinition>();
        foreach (var path in Directory.EnumerateFiles(directory, "*.json", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(directory, path);
            if (relative.StartsWith("knowledge" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                || relative.StartsWith("knowledge" + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            var json = File.ReadAllText(path);
            AgentDefinition definition;
            try
            {
                definition = JsonSerializer.Deserialize<AgentDefinition>(json, JsonOptions)
                    ?? throw AgentCoreErrors.Validation($"Definition file '{path}' was empty.");
            }
            catch (JsonException ex)
            {
                throw AgentCoreErrors.Validation($"Definition file '{path}' is invalid: {ex.Message}");
            }

            try
            {
                AgentDefinitionValidator.Validate(definition);
                ValidateAliases(definition, aliases);
            }
            catch (ArgumentException ex)
            {
                throw AgentCoreErrors.Validation($"Definition file '{path}' is invalid: {ex.Message}");
            }

            var key = (definition.Id, definition.Version);
            if (!loaded.TryAdd(key, definition))
            {
                throw AgentCoreErrors.Validation($"Duplicate agent definition {definition.Id} v{definition.Version}.");
            }
        }

        if (loaded.Count == 0)
        {
            throw AgentCoreErrors.Validation("No agent definitions were loaded.");
        }

        _definitions = loaded;
    }

    public ValueTask<IReadOnlyList<AgentDefinition>> ListAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult<IReadOnlyList<AgentDefinition>>(_definitions.Values.ToArray());
    }

    public ValueTask<AgentDefinition?> GetAsync(
        string id,
        int? version = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (version is { } exact)
        {
            return ValueTask.FromResult(_definitions.TryGetValue((id, exact), out var found) ? found : null);
        }

        var latest = _definitions.Values
            .Where(definition => string.Equals(definition.Id, id, StringComparison.Ordinal))
            .OrderByDescending(definition => definition.Version)
            .FirstOrDefault();
        return ValueTask.FromResult(latest);
    }

    private static void ValidateAliases(AgentDefinition definition, ProviderAliasSet aliases)
    {
        if (!aliases.LanguageModels.Contains(definition.ProviderPreferences.LanguageModel))
        {
            throw new ArgumentException(
                $"languageModel alias '{definition.ProviderPreferences.LanguageModel}' is not configured.");
        }

        if (definition.ProviderPreferences.SpeechRecognizer is { } stt
            && !aliases.SpeechRecognizers.Contains(stt))
        {
            throw new ArgumentException($"speechRecognizer alias '{stt}' is not configured.");
        }

        if (definition.ProviderPreferences.SpeechSynthesizer is { } tts
            && !aliases.SpeechSynthesizers.Contains(tts))
        {
            throw new ArgumentException($"speechSynthesizer alias '{tts}' is not configured.");
        }
    }
}
