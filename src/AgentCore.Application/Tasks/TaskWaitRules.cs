using AgentCore.Application.Security;
using AgentCore.Domain.Security;
using AgentCore.Domain.Tasks;

namespace AgentCore.Application.Tasks;

public static class TaskWaitRules
{
    public static async ValueTask<bool> CanReadMessagesAsync(TenantSecurityState state, Guid actor, DelegatedTask task, IamAuthorizer iam, CancellationToken ct, Guid? requester = null)
        => (await iam.CheckOwnedTenantAsync(state, actor, PermissionCatalog.Get("agent-messages.read"), TaskAuthority.Scope(task.Source.TenantId, task.TaskId), ct)).Allowed
            && !await TenantSandboxReadOperationRules.NeedsReviewAsync(state, requester, "agent-messages.read", TaskAuthority.Scope(task.Source.TenantId, task.TaskId), iam, ct);
}
