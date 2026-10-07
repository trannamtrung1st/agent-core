using System.Text.Json;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Definitions;

namespace AgentCore.Api.Mapping;

internal static class AdminDefinitionJson
{
    private static readonly (string Field, string Label, int Min, int Max, bool Optional)[] PolicyNumbers =
    [
        ("maxActiveRegistrations", "Max active registrations", 1, 32, false),
        ("oneShotHorizonDays", "One-shot horizon days", 1, 365, false),
        ("minRecurrenceDays", "Minimum recurrence days", 1, 365, false),
        ("minFixedIntervalSeconds", "Minimum fixed interval seconds", 60, 604800, true)
    ];
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    internal static AgentDefinitionCandidate ReadCandidate(JsonElement element)
    {
        var policy = Property(element, "triggerPolicy");
        if (policy.ValueKind == JsonValueKind.Object)
        {
            foreach (var rule in PolicyNumbers)
            {
                var value = Property(policy, rule.Field);
                if (value.ValueKind == JsonValueKind.Undefined && rule.Optional) continue;
                var integer = value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out _);
                if (!integer || value.GetInt32() < rule.Min || value.GetInt32() > rule.Max)
                    throw new AgentCoreException("ValidationError",
                        $"{rule.Label} must be a whole number from {rule.Min} to {rule.Max}.", 400)
                    { ValidationField = $"triggerPolicy.{rule.Field}", ValidationCode = integer ? "out_of_range" : "invalid_integer" };
            }
        }
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

    private static JsonElement Property(JsonElement element, string name)
    {
        var value = default(JsonElement);
        if (element.ValueKind == JsonValueKind.Object)
            foreach (var property in element.EnumerateObject())
                if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) value = property.Value;
        return value;
    }

    internal static JsonElement WriteCandidate(AgentDefinitionCandidate candidate) =>
        JsonSerializer.SerializeToElement(candidate, Json);
}
