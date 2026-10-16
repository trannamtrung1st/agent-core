using AgentCore.Application.Ports;
using AgentCore.Domain.Security;
using Microsoft.EntityFrameworkCore;

namespace AgentCore.Infrastructure.Security;

public sealed record TenantCredentialRecord(string CredentialId, Guid TenantId, DateTimeOffset CreatedAt, DateTimeOffset? DeletedAt = null)
{
    internal static void Configure(ModelBuilder model)
    {
        var entity = model.Entity<TenantCredentialRecord>();
        entity.ToTable("TenantCredentials"); entity.HasKey(c => c.CredentialId);
        entity.HasAlternateKey(c => new { c.TenantId, c.CredentialId });
        entity.HasOne<Tenant>().WithMany().HasForeignKey(c => c.TenantId).OnDelete(DeleteBehavior.Restrict);
    }
    internal static TenantCredentialRecord Row(TenantCredentialRoot owner) => new(owner.CredentialId.ToString("D"), owner.TenantId, owner.CreatedAt, owner.DeletedAt);
    internal TenantCredentialRoot Domain() => new(TenantId, Guid.Parse(CredentialId), CreatedAt, DeletedAt);
}
