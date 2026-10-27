using System.Text;
using System.Text.Json;
using AgentCore.Domain.Security;
using AgentCore.Domain.Tasks;

namespace AgentCore.Application.Security;

public static partial class TenantApprovalRules
{
    public static void MatchTaskDelegationRequest(TenantApprovalRequest saved, TaskRequest request)
    {
        if (saved.Operation.TaskDelegation is not { } delegation || delegation.Task.Request.RequestKey != request.RequestKey
            || delegation.Task.Request.Fingerprint() != request.Fingerprint()) throw new SecurityConflictException("task_request_conflict");
    }
    public static PreparedTenantOperation PrepareTaskDelegation(Guid operation, Guid instance, PreparedTaskDelegation value, long revision, long personaRevision)
    {
        value.Validate();
        if (operation == Guid.Empty || instance != value.Task.Source.InstanceId || revision < 1 || personaRevision < 1)
            throw new ArgumentException("Task consent requires its actual source Agent and current source witnesses.");
        var json = JsonSerializer.Serialize(new { operation = "tasks.delegate", permission = "tasks.delegate", resourceType = "session",
            resourceId = value.Task.Source.SessionId.ToString("D"), expectedInstanceRevision = revision, expectedPersonaRevision = personaRevision,
            delegation = value });
        if (Encoding.UTF8.GetByteCount(json) > 65_536) throw new ArgumentException("Task consent exceeds its bounded size.");
        return new(operation, instance, null, revision, personaRevision, json, Tools.TenantWorkspaceMutationContract.Hash(json), TaskDelegation: value);
    }
}
