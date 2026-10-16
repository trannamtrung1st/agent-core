using AgentCore.Application.Ports;
using AgentCore.Infrastructure.Persistence;
using AgentCore.Domain.Security;
using Microsoft.EntityFrameworkCore;

namespace AgentCore.Infrastructure.Security;

public sealed record TenantSessionRecord(string SessionId, Guid TenantId, string AgentInstanceId, Guid ExecutorPrincipalId, Guid CreatedByPrincipalId, DateTimeOffset CreatedAt)
{
    internal static void Configure(ModelBuilder model)
    {
        var identity = model.Entity<IamSystemIdentityRow>();
        identity.HasAlternateKey(i => new { i.TenantId, i.AgentInstanceId, i.PrincipalId });
        var entity = model.Entity<TenantSessionRecord>();
        entity.ToTable("TenantSessions"); entity.HasKey(s => s.SessionId);
        entity.HasAlternateKey(s => new { s.TenantId, s.SessionId });
        entity.HasIndex(s => new { s.TenantId, s.AgentInstanceId, s.SessionId });
        entity.Property(s => s.CreatedAt).HasConversion(v => v.ToUnixTimeMilliseconds(), v => DateTimeOffset.FromUnixTimeMilliseconds(v));
        entity.HasOne<SessionRecord>().WithOne().HasForeignKey<TenantSessionRecord>(s => new { s.SessionId, s.AgentInstanceId })
            .HasPrincipalKey<SessionRecord>(s => new { s.SessionId, s.AgentInstanceId }).OnDelete(DeleteBehavior.Restrict);
        entity.HasOne<IamSystemIdentityRow>().WithMany().HasForeignKey(s => new { s.TenantId, s.AgentInstanceId, s.ExecutorPrincipalId })
            .HasPrincipalKey(i => new { i.TenantId, i.AgentInstanceId, i.PrincipalId }).OnDelete(DeleteBehavior.Restrict);
        entity.HasOne<TenantUser>().WithMany().HasForeignKey(s => new { s.TenantId, s.CreatedByPrincipalId })
            .HasPrincipalKey(p => new { p.TenantId, p.PrincipalId }).OnDelete(DeleteBehavior.Restrict);
    }
    internal static TenantSessionRecord Row(TenantSessionRoot value) => new(value.SessionId.ToString("D"), value.TenantId, value.AgentInstanceId.ToString("D"), value.ExecutorPrincipalId, value.CreatedByPrincipalId, value.CreatedAt);
    internal TenantSessionRoot Domain() => new(TenantId, Guid.Parse(SessionId), Guid.Parse(AgentInstanceId), ExecutorPrincipalId, CreatedByPrincipalId, CreatedAt);
}
