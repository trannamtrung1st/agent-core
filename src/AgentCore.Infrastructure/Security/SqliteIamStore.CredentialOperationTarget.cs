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
        return await CredentialOperationTarget(db, request.TenantId, request.Operation.InstanceId, c.BindingId, c.CredentialId, ct).ConfigureAwait(false);
    }
    private static async ValueTask<TenantCredentialBinding> CredentialOperationTarget(AgentCoreDbContext db, Guid tenant, Guid instance,
        Guid bindingId, Guid credentialId, CancellationToken ct)
    {
        var binding = await OwnedCredentialBindings(db, tenant, instance).AsNoTracking()
            .SingleOrDefaultAsync(b => b.BindingId == bindingId.ToString("D"), ct).ConfigureAwait(false)
            ?? throw new AccessDeniedException("credential_binding_changed");
        var row = await OwnedCredentials(db, tenant).Include(v => v.EmailAccount).Include(v => v.DriveAccount).AsNoTracking()
            .SingleOrDefaultAsync(v => v.CredentialId == credentialId.ToString("D"), ct).ConfigureAwait(false)
            ?? throw new AccessDeniedException("credential_unavailable");
        var credential = await CredentialView(db, tenant, row, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("credential_unavailable");
        return new(MapBinding(binding), credential);
    }
}
