using System.Text.Json;
using System.Text.Json.Serialization;
using AgentCore.Domain.Models;

namespace AgentCore.Infrastructure.Persistence;

internal static class ModelBindingJson
{
    private static readonly JsonSerializerOptions Json = new()
    { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, MaxDepth = 16 };
    public static string? Write(ModelBinding? binding, string? offeringId, string? connectionId, string? externalId)
    {
        if (binding is null) return null;
        binding.RequireIdentity(offeringId!, connectionId!, externalId!);
        return JsonSerializer.Serialize(binding, Json);
    }
    public static ModelBinding? Read(string? json, string offeringId, string connectionId, string externalId)
    {
        if (json is null) return null;
        if (json.Length > 16384) throw new InvalidOperationException("Persisted model binding exceeds its bound.");
        var binding = JsonSerializer.Deserialize<ModelBinding>(json, Json) ?? throw new InvalidOperationException("Persisted model binding is invalid.");
        binding.RequireIdentity(offeringId, connectionId, externalId); return binding;
    }
}
