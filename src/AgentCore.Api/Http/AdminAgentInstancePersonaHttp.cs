using System.Text.Json;
using AgentCore.Application.Identity;
using AgentCore.Application.Sessions;

namespace AgentCore.Api.Http;

internal static class AdminAgentInstancePersonaHttp
{
    private static readonly HashSet<string> AllowedProperties = new(StringComparer.Ordinal)
    {
        "expectedRevision",
        "expectedPersonaRevision",
        "name",
        "role",
        "description",
        "tone",
    };

    public static async ValueTask<AdminParsedPersonaUpdate> ReadStrictPersonaUpdateAsync(
        HttpRequest request,
        CancellationToken cancellationToken)
    {
        using var document = await JsonDocument.ParseAsync(request.Body, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw AgentCoreErrors.Validation("request body must be a JSON object.");
        }

        foreach (var property in root.EnumerateObject())
        {
            if (!AllowedProperties.Contains(property.Name))
            {
                throw AgentCoreErrors.Validation($"Unknown property '{property.Name}'.");
            }
        }

        var expectedRevision = ReadPositiveLong(root, "expectedRevision");
        var expectedPersonaRevision = ReadPositiveLong(root, "expectedPersonaRevision");
        var persona = AgentInstancePersonaEditor.Parse(
            ReadRequiredString(root, "name"),
            ReadRequiredString(root, "role"),
            ReadRequiredString(root, "description"),
            ReadRequiredString(root, "tone"));
        return new AdminParsedPersonaUpdate(expectedRevision, expectedPersonaRevision, persona);
    }

    private static long ReadPositiveLong(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out var parsed))
        {
            throw AgentCoreErrors.Validation($"{name} must be a positive integer.");
        }

        if (parsed < 1)
        {
            throw AgentCoreErrors.Validation($"{name} must be a positive integer.");
        }

        return parsed;
    }

    private static string ReadRequiredString(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String)
        {
            throw AgentCoreErrors.Validation($"{name} is required.");
        }

        return value.GetString() ?? string.Empty;
    }
}

internal sealed record AdminParsedPersonaUpdate(
    long ExpectedRevision,
    long ExpectedPersonaRevision,
    AgentCore.Domain.Definitions.AgentIdentity Persona);
