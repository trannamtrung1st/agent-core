using System.Security.Cryptography;
using System.Text.Json;
using AgentCore.Domain.Definitions;

namespace AgentCore.Application.Admin;

public static class AdminPersonaHistoryFingerprint
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static string Compute(AgentIdentity persona)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(persona, Json);
        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }
}
