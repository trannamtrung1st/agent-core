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
        entity.HasAlternateKey(s => new { s.TenantId, s.SessionId, s.AgentInstanceId, s.ExecutorPrincipalId });
        entity.HasIndex(s => new { s.TenantId, s.AgentInstanceId, s.SessionId });
        entity.Property(s => s.CreatedAt).HasConversion(v => v.ToUnixTimeMilliseconds(), v => DateTimeOffset.FromUnixTimeMilliseconds(v));
        entity.HasOne<SessionRecord>().WithOne().HasForeignKey<TenantSessionRecord>(s => new { s.SessionId, s.AgentInstanceId })
            .HasPrincipalKey<SessionRecord>(s => new { s.SessionId, s.AgentInstanceId }).OnDelete(DeleteBehavior.Restrict);
        entity.HasOne<IamSystemIdentityRow>().WithMany().HasForeignKey(s => new { s.TenantId, s.AgentInstanceId, s.ExecutorPrincipalId })
            .HasPrincipalKey(i => new { i.TenantId, i.AgentInstanceId, i.PrincipalId }).OnDelete(DeleteBehavior.Restrict);
        entity.HasOne<Principal>().WithMany().HasForeignKey(s => s.CreatedByPrincipalId).OnDelete(DeleteBehavior.Restrict);
        var activations = model.Entity<ActivationRecord>();
        activations.HasOne<TenantSessionRecord>().WithMany().HasForeignKey(a => new { a.TenantId, a.SessionId, a.AgentInstanceId, a.ExecutorPrincipalId })
            .HasPrincipalKey(s => new { s.TenantId, s.SessionId, s.AgentInstanceId, s.ExecutorPrincipalId }).OnDelete(DeleteBehavior.Restrict);
        activations.HasOne<TenantUser>().WithMany().HasForeignKey(a => new { a.TenantId, a.OriginalInitiatorPrincipalId })
            .HasPrincipalKey(u => new { u.TenantId, u.PrincipalId }).OnDelete(DeleteBehavior.Restrict);
        activations.HasIndex(a => new { a.TenantId, a.RootActivationId, a.ActivationId });
        activations.ToTable("Activations", t => t.HasCheckConstraint("CK_Activations_ExecutionOwner", ExecutionOwnerCheck));
        var runs = model.Entity<AgentRunRecord>();
        runs.HasOne<TenantSessionRecord>().WithMany().HasForeignKey(a => new { a.TenantId, a.SessionId, a.AgentInstanceId, a.ExecutorPrincipalId })
            .HasPrincipalKey(s => new { s.TenantId, s.SessionId, s.AgentInstanceId, s.ExecutorPrincipalId }).OnDelete(DeleteBehavior.Restrict);
        runs.HasOne<TenantUser>().WithMany().HasForeignKey(a => new { a.TenantId, a.OriginalInitiatorPrincipalId })
            .HasPrincipalKey(u => new { u.TenantId, u.PrincipalId }).OnDelete(DeleteBehavior.Restrict);
        runs.HasIndex(a => new { a.TenantId, a.SessionId, a.CreatedAtUtc, a.AgentRunId });
        runs.ToTable("AgentRuns", t => t.HasCheckConstraint("CK_AgentRuns_ExecutionOwner", ExecutionOwnerCheck));
    }
    private const string ExecutionOwnerCheck = "(TenantId IS NULL AND ExecutorPrincipalId IS NULL AND OriginalInitiatorPrincipalId IS NULL AND ProfileId IS NOT NULL) OR (TenantId IS NOT NULL AND ExecutorPrincipalId IS NOT NULL AND ProfileId IS NULL)";
    internal static TenantSessionRecord Row(TenantSessionRoot value) => new(value.SessionId.ToString("D"), value.TenantId, value.AgentInstanceId.ToString("D"), value.ExecutorPrincipalId, value.CreatedByPrincipalId, value.CreatedAt);
    internal TenantSessionRoot Domain() => new(TenantId, Guid.Parse(SessionId), Guid.Parse(AgentInstanceId), ExecutorPrincipalId, CreatedByPrincipalId, CreatedAt);
}
