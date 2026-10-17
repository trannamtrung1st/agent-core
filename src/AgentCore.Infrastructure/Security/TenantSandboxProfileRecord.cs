using AgentCore.Application.Ports;
using AgentCore.Domain.Security;
using Microsoft.EntityFrameworkCore;

namespace AgentCore.Infrastructure.Security;

public sealed record TenantSandboxProfileRecord(string ProfileId, Guid TenantId, DateTimeOffset CreatedAt)
{
    internal static void Configure(ModelBuilder model)
    {
        var e = model.Entity<TenantSandboxProfileRecord>();
        e.ToTable("TenantSandboxProfiles"); e.HasKey(x => x.ProfileId);
        e.Property(x => x.CreatedAt).HasConversion(x => x.ToUnixTimeMilliseconds(), x => DateTimeOffset.FromUnixTimeMilliseconds(x));
        e.HasAlternateKey(x => new { x.TenantId, x.ProfileId });
        e.HasOne<Tenant>().WithMany().HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Restrict);
    }
    internal TenantSandboxProfileRoot Domain() => new(TenantId, ProfileId, CreatedAt);
}
