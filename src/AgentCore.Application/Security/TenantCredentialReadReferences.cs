using System.Text.Json;
using AgentCore.Application.Execution;
using AgentCore.Application.Ports;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Security;

public sealed record TenantCredentialReadWitness(Guid BindingId, Guid CredentialId, long BindingRevision, long CredentialRevision);
public sealed record TenantCredentialReadSource(string? Family, IReadOnlyList<TenantCredentialReadWitness> Witnesses, Guid? RequestId = null, Guid? OperationId = null);
public static class TenantCredentialReadReferences
{
    public static string? Family(JsonElement root)
    {
        if (!root.TryGetProperty("credentialReadFamily", out var value) || value.ValueKind == JsonValueKind.Null) return null;
        return value.ValueKind == JsonValueKind.String && value.GetString() is "email" or "drive" ? value.GetString() : throw new AccessDeniedException("credential_source_changed");
    }
    public static IReadOnlyList<TenantCredentialReadSource> Read(AgentRun run)
    {
        if (!AgentRunToolCallCheckpoint.TryRead(run.Checkpoint, out var messages)) return [];
        var results = new List<TenantCredentialReadSource>();
        foreach (var message in messages!.Where(m => TenantCredentialDiscoveryContract.IsTool(m.Name ?? "") || m.Name == ToolCatalog.RequestAuthorization))
        {
            using var doc = JsonDocument.Parse(message.Text); var root = doc.RootElement;
            Guid? request = null, operation = null;
            if (message.Name == ToolCatalog.RequestAuthorization)
            {
                if (!root.TryGetProperty("credentialMetadata", out var data) || data.ValueKind == JsonValueKind.Null) continue;
                request = root.GetProperty("requestId").GetGuid(); operation = root.GetProperty("operationId").GetGuid(); root = data;
            }
            if (!root.TryGetProperty("tenantCredentialRead", out var marker) || marker.ValueKind != JsonValueKind.True)
            { if (request is not null) throw new AccessDeniedException("credential_source_changed"); continue; }
            var family = Family(root);
            if (request is null && TenantCredentialDiscoveryContract.Family(message.Name ?? "") != family) throw new AccessDeniedException("credential_source_changed");
            var witnesses = root.GetProperty("credentialReads").Deserialize<TenantCredentialReadWitness[]>() ?? throw new AccessDeniedException("credential_source_changed");
            if (witnesses.Length > 101 || witnesses.Any(w => w is null || w.BindingId == Guid.Empty || w.CredentialId == Guid.Empty || w.BindingRevision < 1 || w.CredentialRevision < 1)
                || witnesses.Select(w => w.BindingId).Distinct().Count() != witnesses.Length || results.Count >= 100)
                throw new AccessDeniedException("credential_source_changed");
            results.Add(new(family, witnesses, request, operation));
        }
        return results;
    }
    public static PreparedCredentialRead Direct(AgentRun run, TenantCredentialReadSource source, IReadOnlyList<TenantCredentialBinding> bindings)
    {
        var refs = new List<CredentialReadReference>();
        foreach (var witness in source.Witnesses)
        {
            var bound = bindings.SingleOrDefault(b => b.Binding.BindingId == witness.BindingId && b.Credential.Value.CredentialId == witness.CredentialId);
            if (bound is null || bound.Binding.Revision != witness.BindingRevision || bound.Credential.Value.Revision != witness.CredentialRevision)
                throw new AccessDeniedException("credential_source_changed");
            refs.Add(TenantCredentialReadOperationRules.Reference(bound));
        }
        refs = refs.OrderBy(r => r.Reference, StringComparer.Ordinal).ToList();
        var arguments = source.Family is null ? "{\"cursor\":null,\"limit\":100}" : JsonSerializer.Serialize(new { cursor = (string?)null, limit = 100, family = source.Family });
        return new(run.SessionId, refs.FirstOrDefault()?.CredentialId, arguments, JsonSerializer.Serialize(refs));
    }
    public static void Receipt(TenantCredentialReadSource source, TenantApprovalRequest request, TenantOperationReceipt receipt, TenantApprovalDecision decision, AgentRun run)
    {
        if (request.Operation.CredentialRead is not { } read || request.Kind != ApprovalKind.Review || request.Status != TenantApprovalStatus.Approved
            || source.RequestId != request.RequestId || source.OperationId != request.Operation.OperationId
            || source.Family != TenantCredentialDiscoveryContract.Parse(read.ArgumentsJson).Family
            || request.TenantId != run.Owner.TenantId || request.SessionId != run.SessionId || request.Operation.InstanceId != run.AgentInstanceId
            || request.ExecutorPrincipalId != run.Owner.ExecutorPrincipalId || request.RootActivationId != run.Admission.ExecutionContext?.RootActivationId
            || request.OriginalInitiatorPrincipalId != run.Admission.ExecutionContext.OriginalInitiatorPrincipalId
            || receipt.Outcome != TenantOperationOutcome.Completed || receipt.ReasonCode != TenantCredentialReadOperationRules.Reason
            || receipt.TenantId != request.TenantId || receipt.RequestId != request.RequestId || receipt.OperationId != request.Operation.OperationId
            || receipt.ActionHash != request.Operation.ActionHash || receipt.InstanceId != run.AgentInstanceId || receipt.SessionId != run.SessionId
            || receipt.ExecutorPrincipalId != request.ExecutorPrincipalId || receipt.RootActivationId != request.RootActivationId
            || decision.TenantId != request.TenantId || decision.RequestId != request.RequestId || decision.Status != TenantApprovalStatus.Approved
            || decision.ActionHash != request.Operation.ActionHash || decision.RequestRevision != request.Revision)
            throw new AccessDeniedException("credential_source_changed");
        var saved = TenantApprovalRules.CredentialReadReferences(read);
        foreach (var w in source.Witnesses)
            if (!saved.Any(r => r.BindingId == w.BindingId && r.CredentialId == w.CredentialId && r.BindingRevision == w.BindingRevision && r.CredentialRevision == w.CredentialRevision))
                throw new AccessDeniedException("credential_source_changed");
    }
}
