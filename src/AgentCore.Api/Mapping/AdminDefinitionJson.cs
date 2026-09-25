using System.Text.Json;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Definitions;

namespace AgentCore.Api.Mapping;

internal static class AdminDefinitionJson
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    internal static AgentDefinitionCandidate ReadCandidate(JsonElement element)
    {
        try
        {
            return JsonSerializer.Deserialize<AgentDefinitionCandidate>(element.GetRawText(), Json)
                ?? throw AgentCoreErrors.Validation("Definition candidate body was empty.");
        }
        catch (JsonException ex)
        {
            throw AgentCoreErrors.Validation($"Definition candidate body is invalid: {ex.Message}");
        }
    }

    internal static JsonElement WriteCandidate(AgentDefinitionCandidate candidate) =>
        JsonSerializer.SerializeToElement(candidate, Json);
}
