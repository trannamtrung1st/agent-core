using AgentCore.Application.Ports;
using AgentCore.Domain.Security;
using Microsoft.EntityFrameworkCore;

namespace AgentCore.Infrastructure.Security;

public sealed record TenantDefinitionRecord(string DefinitionId, Guid TenantId, DateTimeOffset CreatedAt, DateTimeOffset? DeletedAt = null)
{
    internal static void Configure(ModelBuilder model)
    {
        var entity = model.Entity<TenantDefinitionRecord>();
        entity.ToTable("TenantAgentDefinitions"); entity.HasKey(d => d.DefinitionId);
        entity.HasIndex(d => new { d.TenantId, d.DefinitionId }).IsUnique();
        entity.HasOne<Tenant>().WithMany().HasForeignKey(d => d.TenantId).OnDelete(DeleteBehavior.Restrict);
    }
    internal static TenantDefinitionRecord Row(TenantDefinition definition) => new(definition.DefinitionId.ToString("D"), definition.TenantId, definition.CreatedAt, definition.DeletedAt);
    internal TenantDefinition Domain() => new(TenantId, Guid.Parse(DefinitionId), CreatedAt, DeletedAt);
}
