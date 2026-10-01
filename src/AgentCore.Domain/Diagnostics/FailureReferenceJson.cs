using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentCore.Domain.Diagnostics;

public static class FailureReferenceJson
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static string? Serialize(FailureReference? reference)
    {
        if (reference is null)
        {
            return null;
        }

        return JsonSerializer.Serialize(
            new FailureReferenceDto(
                reference.DiagnosticId,
                reference.CorrelationId,
                reference.Category,
                reference.Code,
                reference.FailureReason,
                reference.ProviderResponseChannel),
            Json);
    }

    public static FailureReference? Deserialize(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new ArgumentException("Failure reference JSON must be an object.");
        }

        Guid? diagnosticId = null;
        Guid? correlationId = null;
        string? category = null;
        string? code = null;
        string? failureReason = null;
        string? providerResponseChannel = null;
        foreach (var property in document.RootElement.EnumerateObject())
        {
            switch (property.Name)
            {
                case "diagnosticId":
                    diagnosticId = ReadGuid(property.Value, "diagnosticId");
                    break;
                case "correlationId" when property.Value.ValueKind == JsonValueKind.Null:
                    break;
                case "correlationId":
                    correlationId = ReadGuid(property.Value, "correlationId");
                    break;
                case "category":
                    category = property.Value.GetString();
                    break;
                case "code":
                    code = property.Value.GetString();
                    break;
                case "failureReason" when property.Value.ValueKind == JsonValueKind.Null:
                    break;
                case "failureReason":
                    failureReason = property.Value.GetString();
                    break;
                case "providerResponseChannel" when property.Value.ValueKind == JsonValueKind.Null:
                    break;
                case "providerResponseChannel":
                    providerResponseChannel = property.Value.GetString();
                    break;
                default:
                    throw new ArgumentException("Failure reference JSON contains an unknown field.");
            }
        }

        if (diagnosticId is not { } id)
        {
            throw new ArgumentException("Failure reference JSON requires diagnosticId.");
        }

        return new FailureReference(
            id,
            category ?? throw new ArgumentException("Failure reference JSON requires category."),
            code ?? throw new ArgumentException("Failure reference JSON requires code."),
            correlationId,
            failureReason,
            providerResponseChannel);
    }

    private static Guid ReadGuid(JsonElement value, string name)
    {
        if (value.ValueKind != JsonValueKind.String || !Guid.TryParse(value.GetString(), out var id) || id == Guid.Empty)
        {
            throw new ArgumentException($"Failure reference JSON {name} is invalid.");
        }

        return id;
    }

    private sealed record FailureReferenceDto(
        Guid DiagnosticId,
        Guid? CorrelationId,
        string Category,
        string Code,
        string? FailureReason,
        string? ProviderResponseChannel);
}
