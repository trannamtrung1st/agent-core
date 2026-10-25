using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Models;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Models;

public sealed record ModelMetadataTestView(bool MetadataAvailable, string Result, int ModelsObserved, DateTimeOffset TestedAt);

public sealed partial class TenantModelManagementService
{
    public async ValueTask<ModelMetadataTestView> TestAsync(HumanSessionReference human, Guid tenant, Guid connectionId,
        bool discover, long expectedSecurityRevision, long expectedConnectionRevision, CancellationToken ct = default)
    {
        var scope = TenantModelRules.Scope(tenant, connectionId, true);
        var state = await WriteState(human, tenant, scope, "model-connections.test", expectedSecurityRevision, ct);
        await Require(state, human, "model-connections.read", scope, ct);
        if (discover) await Require(state, human, "models.manage", AccessScope.ForTenant(tenant), ct);
        var connection = (await store.ReadTenantModelsAsync(tenant, ct)).Connections.SingleOrDefault(c => c.ConnectionId == connectionId)
            ?? throw new AccessDeniedException("resource_unavailable");
        if (!connection.Enabled) throw new SecurityConflictException("model_connection_disabled");
        RequireRevision(connection.Revision, expectedConnectionRevision);
        endpoints.Validate(connection.Kind, connection.Endpoint, connection.NoCredential);
        var currentCredential = connection.CredentialId is { } id ? await credentials.FindTenantCredentialAsync(tenant, id, ct) : null;
        TenantModelRules.RequireCredential(connection, currentCredential);
        var witness = new ModelCredentialWitness(connection.CredentialId, currentCredential?.Value.Revision, currentCredential?.Value.SecretRevision);
        async ValueTask Authorize(CancellationToken token)
        {
            var current = await WriteState(human, tenant, scope, "model-connections.test", expectedSecurityRevision, token);
            await Require(current, human, "model-connections.read", scope, token);
            if (discover) await Require(current, human, "models.manage", AccessScope.ForTenant(tenant), token);
            var row = (await store.ReadTenantModelsAsync(tenant, token)).Connections.SingleOrDefault(c => c.ConnectionId == connectionId);
            if (row is null || row != connection || !row.Enabled) throw new SecurityConflictException("model_connection_changed");
            var secret = row.CredentialId is { } key ? await credentials.FindTenantCredentialAsync(tenant, key, token) : null;
            TenantModelRules.RequireCredential(row, secret);
            if (witness != new ModelCredentialWitness(row.CredentialId, secret?.Value.Revision, secret?.Value.SecretRevision))
                throw new SecurityConflictException("model_credential_changed");
            await Complete(human, tenant, expectedSecurityRevision, token);
        }
        await Authorize(ct);
        if (metadata is null) throw AgentCoreErrors.Validation("Model metadata transport is not configured.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(30));
        ModelMetadataResult result;
        try { result = await metadata.ReadAsync(connection, witness, discover, Authorize, timeout.Token); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        { result = new(false, "metadata_unavailable", []); }
        var observed = ModelDiscoveryReconciliation.Freeze(result);
        await Authorize(ct);
        var at = DateTimeOffset.FromUnixTimeMilliseconds(time.GetUtcNow().ToUnixTimeMilliseconds());
        var audit = Audit(human, tenant, "model-connections.test", connectionId) with { ReasonCode = discover ? "model_discovery_completed" : "model_metadata_tested" };
        if (!await store.TryApplyModelMetadataAsync(Advance(state), expectedSecurityRevision, connectionId, connection.Revision,
            witness, at, result with { Models = observed }, discover, human, audit, ct)) throw new SecurityConflictException("stale_revision");
        return new(result.Success, result.Result, discover ? observed.Count : 0, at);
    }
}
