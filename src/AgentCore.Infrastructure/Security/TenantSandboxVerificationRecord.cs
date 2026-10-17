using System.Text.Json;
using AgentCore.Domain.Conversation;
using AgentCore.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AgentCore.Infrastructure.Security;

public sealed class TenantSandboxVerificationRecord
{
    public Guid AttemptId { get; set; }
    public Guid TenantId { get; set; }
    public string ProfileId { get; set; } = "";
    public long ProfileRevision { get; set; }
    public long Revision { get; set; }
    public long SecurityRevision { get; set; }
    public bool Active { get; set; }
    public long LeaseExpiresAt { get; set; }
    public long? CleanupClaimExpiresAt { get; set; }
    public long StartedAt { get; set; }
    public string Json { get; set; } = "";
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
    internal static TenantSandboxVerificationRecord Row(SandboxVerificationAttempt a) => new() { AttemptId = a.AttemptId, TenantId = a.TenantId,
        ProfileId = a.ProfileId, ProfileRevision = a.ProfileRevision, Revision = a.Revision, SecurityRevision = a.SecurityRevision, Active = a.Active,
        LeaseExpiresAt = a.LeaseExpiresAt.ToUnixTimeMilliseconds(), CleanupClaimExpiresAt = a.CleanupClaimExpiresAt?.ToUnixTimeMilliseconds(),
        StartedAt = a.StartedAt.ToUnixTimeMilliseconds(), Json = JsonSerializer.Serialize(a, Options) };
    internal SandboxVerificationAttempt Domain()
    {
        var value = JsonSerializer.Deserialize<SandboxVerificationAttempt>(Json, Options);
        if (value is null || value.AttemptId != AttemptId || value.TenantId != TenantId || value.ProfileId != ProfileId || value.ProfileRevision != ProfileRevision
            || value.SecurityRevision != SecurityRevision || value.Revision != Revision || value.Active != Active || value.LeaseExpiresAt.ToUnixTimeMilliseconds() != LeaseExpiresAt
            || value.CleanupClaimExpiresAt?.ToUnixTimeMilliseconds() != CleanupClaimExpiresAt || value.StartedAt.ToUnixTimeMilliseconds() != StartedAt)
            throw AgentCore.Application.Sessions.AgentCoreErrors.Persistence("Verification ownership is invalid.");
        return value;
    }
    internal static void Configure(ModelBuilder model)
    {
        var e = model.Entity<TenantSandboxVerificationRecord>(); e.ToTable("TenantSandboxVerificationAttempts"); e.HasKey(x => x.AttemptId);
        e.HasAlternateKey(x => new { x.TenantId, x.ProfileId, x.ProfileRevision, x.AttemptId }); e.Property(x => x.Revision).IsConcurrencyToken();
        e.HasIndex(x => new { x.TenantId, x.ProfileId }).IsUnique().HasFilter("Active = 1");
        e.HasIndex(x => new { x.Active, x.LeaseExpiresAt }); e.HasIndex(x => new { x.TenantId, x.ProfileId, x.SecurityRevision });
        e.HasOne<TenantSandboxProfileRecord>().WithMany().HasForeignKey(x => new { x.TenantId, x.ProfileId })
            .HasPrincipalKey(x => new { x.TenantId, x.ProfileId }).OnDelete(DeleteBehavior.Restrict);
        e.HasOne<SandboxProfileRow>().WithMany().HasForeignKey(x => new { x.ProfileId, x.ProfileRevision })
            .HasPrincipalKey(x => new { x.Id, x.Revision }).OnDelete(DeleteBehavior.Restrict);
    }
}
