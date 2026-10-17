using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Security;
using AgentCore.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AgentCore.Infrastructure.Security;

public sealed partial class SqliteIamStore
{
    private static async ValueTask<TenantCredentialBinding> CredentialOperationTarget(AgentCoreDbContext db, TenantApprovalRequest request, CancellationToken ct)
    {
        var c = request.Operation.CredentialCheck ?? throw new ArgumentException("Missing credential target.");
        var binding = await OwnedCredentialBindings(db, request.TenantId, request.Operation.InstanceId).AsNoTracking()
            .SingleOrDefaultAsync(b => b.BindingId == c.BindingId.ToString("D"), ct).ConfigureAwait(false)
            ?? throw new AccessDeniedException("credential_binding_changed");
        var row = await OwnedCredentials(db, request.TenantId).Include(v => v.EmailAccount).Include(v => v.DriveAccount).AsNoTracking()
            .SingleOrDefaultAsync(v => v.CredentialId == c.CredentialId.ToString("D"), ct).ConfigureAwait(false)
            ?? throw new AccessDeniedException("credential_unavailable");
        var credential = await CredentialView(db, request.TenantId, row, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("credential_unavailable");
        return new(MapBinding(binding), credential);
    }
}
