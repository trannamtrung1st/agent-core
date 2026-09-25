using System.Text.Json;
using AgentCore.Domain.Definitions;

namespace AgentCore.Infrastructure.Definitions;

internal static class AgentDefinitionAdminSnapshots
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    internal static AgentDefinitionCandidate Freeze(AgentDefinitionCandidate candidate) =>
        Clone(candidate, DeserializeCandidate);

    internal static AgentDefinition Freeze(AgentDefinition definition) =>
        Clone(definition, DeserializeDefinition);

    internal static AgentDefinitionDraft Freeze(AgentDefinitionDraft draft) =>
        draft with { Candidate = Freeze(draft.Candidate) };

    internal static AgentDefinitionPublication Freeze(AgentDefinitionPublication publication) =>
        publication with { Payload = Freeze(publication.Payload) };

    private static T Clone<T>(T value, Func<string, T> deserialize) =>
        deserialize(JsonSerializer.Serialize(value, Json));

    private static AgentDefinitionCandidate DeserializeCandidate(string json) =>
        JsonSerializer.Deserialize<AgentDefinitionCandidate>(json, Json)
        ?? throw new InvalidOperationException("Candidate snapshot was empty.");

    private static AgentDefinition DeserializeDefinition(string json) =>
        JsonSerializer.Deserialize<AgentDefinition>(json, Json)
        ?? throw new InvalidOperationException("Definition snapshot was empty.");
}
