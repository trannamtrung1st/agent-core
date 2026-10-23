using System.Text.Json;
using AgentCore.Application.Tools;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Security;

public static partial class TenantApprovalRules
{
    public static PreparedTenantOperation PrepareHttpRequest(Guid id,Guid instance,PreparedHttpRequest value,long revision,long personaRevision)
    {
        _=TenantHttpRequestContract.Arguments(value);
        if(id==Guid.Empty || instance==Guid.Empty || revision<1 || personaRevision<1)throw new ArgumentException("Invalid exact HTTP request.");
        var json=JsonSerializer.Serialize(new { operation="http.request",request=value,expectedInstanceRevision=revision,expectedPersonaRevision=personaRevision });
        return new(id,instance,null,revision,personaRevision,json,TenantWorkspaceMutationContract.Hash(json),HttpRequest:value);
    }
}
