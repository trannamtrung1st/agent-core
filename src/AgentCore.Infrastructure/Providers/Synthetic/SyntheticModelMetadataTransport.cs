using AgentCore.Application.Models;
using AgentCore.Application.Ports;
using AgentCore.Domain.Models;

namespace AgentCore.Infrastructure.Providers.Synthetic;

// Explicit offline test profile; never installed as recovery for a Real provider failure.
public sealed class SyntheticModelMetadataTransport : IModelMetadataTransport
{
    public async ValueTask<ModelMetadataResult> ReadAsync(ProviderConnection connection, ModelCredentialWitness credential,
        bool discover, Func<CancellationToken, ValueTask> authorizeDispatch, CancellationToken ct = default)
    {
        await authorizeDispatch(ct);
        return new(true, "synthetic_metadata_available", discover ? BuiltinModelPresets.All.Where(p => p.Kind == connection.Kind)
            .Select(p => new ModelDiscoveryEntry(p.ExternalModelId, p.Label)).ToArray() : []);
    }
}
