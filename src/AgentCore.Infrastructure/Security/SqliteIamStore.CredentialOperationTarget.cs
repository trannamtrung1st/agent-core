using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Security;
using AgentCore.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AgentCore.Infrastructure.Security;

public sealed partial class SqliteIamStore
{
    private async ValueTask<TenantCredentialBinding> CredentialOperationTarget(AgentCoreDbContext db, TenantApprovalRequest request, CancellationToken ct)
    {
        var c = request.Operation.CredentialCheck ?? throw new ArgumentException("Missing credential target.");
        var row = await OwnedRuns(db, request.TenantId).AsNoTracking().SingleOrDefaultAsync(r => r.AgentRunId == request.RunId.ToString("D"), ct).ConfigureAwait(false)
            ?? throw new AccessDeniedException("run_unavailable");
        return await ContextCredentialTarget(db, AgentRunStoreMapping.ToDomain(row), c.BindingId, c.CredentialId, ct).ConfigureAwait(false);
    }
}
