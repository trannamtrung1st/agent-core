using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentCore.Domain.Definitions;

namespace AgentCore.Application.Admin;

internal static class DefinitionDraftConfigurationFingerprint
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    internal static string Compute(
        AgentDefinitionCandidate candidate,
        IReadOnlyList<AgentDefinitionDraftResource> resources)
    {
        var builder = new StringBuilder();
        builder.Append(JsonSerializer.Serialize(candidate, Json));
        builder.Append('\n');
        foreach (var resource in resources.OrderBy(item => item.LogicalPath, StringComparer.Ordinal))
        {
            builder.Append(resource.LogicalPath);
            builder.Append('|');
            builder.Append(resource.Kind);
            builder.Append('|');
            builder.Append(resource.ContentSha256);
            builder.Append('\n');
        }

        var bytes = Encoding.UTF8.GetBytes(builder.ToString());
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
