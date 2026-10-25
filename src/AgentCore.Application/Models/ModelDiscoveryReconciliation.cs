using AgentCore.Application.Ports;
using AgentCore.Domain.Models;

namespace AgentCore.Application.Models;

public static class ModelDiscoveryReconciliation
{
    public const int MaxModels = 2000;
    public static IReadOnlyList<ModelDiscoveryEntry> Freeze(ModelMetadataResult result)
    {
        if (result.Result is not ("metadata_available" or "synthetic_metadata_available" or "authentication_failed" or "metadata_unavailable")
            || result.Success != (result.Result is "metadata_available" or "synthetic_metadata_available") || result.Models is null || result.Models.Count > MaxModels
            || !result.Success && result.Models.Count != 0) throw new ArgumentException("Invalid model metadata outcome.");
        var values = result.Models.Select(m => new ModelDiscoveryEntry(ModelManagementRules.ExternalId(m.ExternalModelId),
            m.Label is null ? null : ModelManagementRules.Label(m.Label))).ToArray();
        if (values.Select(m => m.ExternalModelId).Distinct(StringComparer.Ordinal).Count() != values.Length)
            throw new ArgumentException("Model discovery contains duplicate identities.");
        return Array.AsReadOnly(values);
    }
    public static IReadOnlyList<ModelOffering> Reconcile(ProviderConnection connection, IReadOnlyList<ModelOffering> existing,
        IReadOnlyList<ModelDiscoveryEntry> observed, DateTimeOffset at, Func<Guid> id, bool synthetic = false)
    {
        var ids = observed.Select(m => m.ExternalModelId).ToHashSet(StringComparer.Ordinal);
        var result = existing.Select(o => o with { Observation = ids.Contains(o.ExternalModelId) ? ModelObservation.Observed : ModelObservation.NotReturned,
            LastSeenAt = ids.Contains(o.ExternalModelId) ? at : o.LastSeenAt }).ToList();
        var known = existing.Select(o => o.ExternalModelId).ToHashSet(StringComparer.Ordinal);
        foreach (var model in observed.Where(m => !known.Contains(m.ExternalModelId)))
        {
            var transport = connection.Kind switch
            {
                ModelProviderKind.OpenAI => ModelTransport.Responses,
                ModelProviderKind.Anthropic => ModelTransport.AnthropicMessages,
                ModelProviderKind.Google => ModelTransport.GeminiContent,
                _ => ModelTransport.ChatCompletions
            };
            // A listing proves identity/access only, never the executable Core contract.
            result.Add(ModelManagementRules.Freeze(new ModelOffering(id(), connection.TenantId, connection.ConnectionId, connection.Kind,
                null, model.ExternalModelId, model.Label ?? model.ExternalModelId[..Math.Min(model.ExternalModelId.Length, 120)], transport,
                new(false, false, ModelInstructionHierarchy.Unsupported, ModelSemanticMode.NativeStructuredOutput, false, false, [], null,
                    synthetic ? ModelMetadataSource.SyntheticFixture : ModelMetadataSource.NativeApi, at),
                false, false, 1, ModelObservation.Observed, at)));
        }
        if (result.Count > MaxModels) throw new ArgumentException("This connection's model catalog exceeds the supported bound.");
        return Array.AsReadOnly(result.ToArray());
    }
}
