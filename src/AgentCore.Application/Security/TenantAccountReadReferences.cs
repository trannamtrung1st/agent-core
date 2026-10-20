using System.Text.Json;
using AgentCore.Application.Execution;
using AgentCore.Application.Ports;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Security;

public sealed record TenantAccountReadWitness(Guid BindingId, long BindingRevision, Guid CredentialId, long CredentialRevision, long SecretRevision, long AuthEpoch);
public sealed record TenantAccountReadReference(Guid RequestId, Guid OperationId, string ToolName, TenantAccountReadWitness Witness);
public static class TenantAccountReadReferences
{
    public static TenantAccountReadWitness Witness(TenantCredentialBinding bound) => new(bound.Binding.BindingId, bound.Binding.Revision, bound.Credential.Value.CredentialId,
        bound.Credential.Value.Revision, bound.Credential.Value.SecretRevision, TenantCredentialOAuthRules.Epoch(bound.Credential.Value));
    public static IReadOnlyList<TenantAccountReadReference> Read(AgentRun run)
    {
        if (run.Checkpoint is null || !AgentRunToolCallCheckpoint.TryRead(run.Checkpoint, out var messages)) return [];
        var result = new List<TenantAccountReadReference>();
        foreach (var message in messages!.Where(m => m.Role == ModelRole.Tool && m.Name == ToolCatalog.RequestAuthorization))
        {
            using var json = JsonDocument.Parse(message.Text); var root = json.RootElement;
            if (!root.TryGetProperty("accountRead", out var data) || data.ValueKind != JsonValueKind.Object
                || !data.TryGetProperty("tenantAccountRead", out var marker) || marker.ValueKind != JsonValueKind.True) continue;
            result.Add(new(data.GetProperty("accountReadRequestId").GetGuid(), data.GetProperty("accountReadOperationId").GetGuid(),
                data.GetProperty("accountReadTool").GetString() ?? "", data.GetProperty("accountReadWitness").Deserialize<TenantAccountReadWitness>(new JsonSerializerOptions(JsonSerializerDefaults.Web))
                    ?? throw new AccessDeniedException("account_source_changed")));
            if (result.Count > 100) throw new AccessDeniedException("account_source_changed");
        }
        return result;
    }
    public static void Require(TenantAccountReadReference reference, TenantApprovalRequest request, TenantCredentialBinding bound, TenantAgent agent)
    {
        if (request.RequestId != reference.RequestId || request.Operation.OperationId != reference.OperationId || request.Operation.AccountRead?.ToolName != reference.ToolName
            || request.Operation.ExpectedInstanceRevision != agent.Instance.Revision || request.Operation.ExpectedPersonaRevision != agent.Instance.PersonaRevision
            || !SameSource(reference.Witness, bound)) throw new AccessDeniedException("account_source_changed");
    }
    // A normalized OAuth refresh rotates only the protected secret. It does not
    // change the account, logical Credential revision or authorization epoch of
    // data already retrieved. New provider I/O still compares the exact secret.
    public static bool SameSource(TenantAccountReadWitness witness, TenantCredentialBinding bound)
    {
        var actual = Witness(bound);
        return (TenantCredentialOAuthRules.IsOAuth(bound.Credential.Value) ? witness with { SecretRevision = actual.SecretRevision } : witness) == actual;
    }
}
