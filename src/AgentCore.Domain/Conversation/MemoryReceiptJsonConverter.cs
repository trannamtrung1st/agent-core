using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentCore.Domain.Conversation;

internal sealed class MemoryReceiptJsonConverter : JsonConverter<MemoryReceipt>
{
    public override MemoryReceipt Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
        {
            throw new JsonException("Expected memory receipt object.");
        }

        string? outcome = null;
        string? operation = null;
        string? source = null;
        string? subject = null;
        IReadOnlyList<string>? scopes = null;
        string? presentation = null;

        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject)
            {
                break;
            }

            if (reader.TokenType != JsonTokenType.PropertyName)
            {
                throw new JsonException("Expected property name.");
            }

            var name = reader.GetString();
            reader.Read();
            switch (name)
            {
                case "outcome":
                    outcome = reader.GetString();
                    break;
                case "operation":
                    operation = reader.GetString();
                    break;
                case "source":
                    source = reader.GetString();
                    break;
                case "subject":
                    subject = reader.GetString();
                    break;
                case "scopes":
                    scopes = ReadScopes(ref reader);
                    break;
                case "scope":
                    var legacy = reader.GetString();
                    scopes = string.IsNullOrWhiteSpace(legacy) ? null : new[] { legacy.Trim() };
                    break;
                case "presentation":
                    presentation = reader.GetString();
                    break;
                default:
                    reader.Skip();
                    break;
            }
        }

        return new MemoryReceipt(
            outcome ?? string.Empty,
            operation ?? string.Empty,
            source ?? string.Empty,
            subject ?? string.Empty,
            scopes is { Count: > 0 } ? scopes : null,
            presentation ?? string.Empty);
    }

    public override void Write(Utf8JsonWriter writer, MemoryReceipt value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteString("outcome", value.Outcome);
        writer.WriteString("operation", value.Operation);
        writer.WriteString("source", value.Source);
        writer.WriteString("subject", value.Subject);
        if (value.Scopes is { Count: > 0 })
        {
            writer.WritePropertyName("scopes");
            writer.WriteStartArray();
            foreach (var scope in value.Scopes)
            {
                writer.WriteStringValue(scope);
            }

            writer.WriteEndArray();
        }

        writer.WriteString("presentation", value.Presentation);
        writer.WriteEndObject();
    }

    private static IReadOnlyList<string>? ReadScopes(ref Utf8JsonReader reader)
    {
        if (reader.TokenType == JsonTokenType.Null)
        {
            return null;
        }

        if (reader.TokenType != JsonTokenType.StartArray)
        {
            throw new JsonException("Expected scopes array.");
        }

        var scopes = new List<string>();
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndArray)
            {
                break;
            }

            if (reader.TokenType == JsonTokenType.String)
            {
                var value = reader.GetString();
                if (!string.IsNullOrWhiteSpace(value))
                {
                    scopes.Add(value.Trim());
                }
            }
        }

        return scopes.Count == 0 ? null : scopes;
    }
}
