using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;
using AgentCore.Domain.Spaces;

namespace AgentCore.Application.Security;

public static partial class TenantApprovalRules
{
    public static PreparedTenantOperation ForRun(PreparedTenantOperation operation, AgentRun run)
    {
        var capture = run.Admission.Configuration?.SpaceContext ?? throw new AccessDeniedException("space_context_required");
        capture.Validate();
        if (capture.TenantId != run.Owner.TenantId) throw new AccessDeniedException("tenant_context_mismatch");
        var witness = capture.Witness();
        if (operation.SpaceContext is { } previous && previous != witness) throw new AccessDeniedException("operation_context_changed");
        return BindSpace(RebuildCore(operation), witness);
    }

    private static PreparedTenantOperation BindSpace(PreparedTenantOperation operation, RunSpaceWitness? witness)
    {
        if (witness is null) return operation;
        witness.Validate();
        var action = JsonNode.Parse(operation.CanonicalActionJson)!.AsObject();
        action.Add("spaceContext", JsonSerializer.SerializeToNode(witness));
        var json = action.ToJsonString();
        if (Encoding.UTF8.GetByteCount(json) > 1048576) throw new ArgumentException("Prepared operation exceeds its content bound.");
        return operation with { SpaceContext = witness, CanonicalActionJson = json,
            ActionHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(json))) };
    }
}
